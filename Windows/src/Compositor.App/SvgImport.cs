using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Compositor.App;

/// <summary>Admits a bounded, self-contained subset of Direct2D SVG, before native rendering.</summary>
internal static class SvgImport
{
    public const int MaxBytes = 128 * 1024;
    public const int MaxSide = 8192;
    public const long MaxPixels = 16 * 1024 * 1024;
    private static readonly XNamespace svgNamespace = "http://www.w3.org/2000/svg";
    private static readonly XNamespace xlinkNamespace = "http://www.w3.org/1999/xlink";
    private static readonly HashSet<string> elements = new(StringComparer.Ordinal)
    { "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "linearGradient", "radialGradient", "stop", "clipPath", "use", "title", "desc" };
    private static readonly HashSet<string> presentation = new(StringComparer.Ordinal)
    { "clip-path", "clip-rule", "color", "display", "fill", "fill-opacity", "fill-rule", "opacity", "overflow", "stop-color", "stop-opacity", "stroke", "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-opacity", "stroke-width", "visibility" };
    private static readonly HashSet<string> attributes = new(StringComparer.Ordinal)
    { "id", "style", "transform", "x", "y", "width", "height", "viewBox", "preserveAspectRatio", "cx", "cy", "r", "rx", "ry", "x1", "y1", "x2", "y2", "d", "points", "fx", "fy", "gradientUnits", "gradientTransform", "spreadMethod", "offset", "clipPathUnits", "href", "version", "class", "role" };
    private static readonly Regex localUrl = new("^url\\(\\s*['\"]?#([^\\s'\"()]+)['\"]?\\s*\\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    public static string Prepare(string? source, int width, int height, long remainingPixels)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || (long)width * height > MaxPixels)
            throw new ArgumentException("SVG output must be 1–8192 pixels per side and at most 16 megapixels.");
        if ((long)width * height > remainingPixels) throw new ArgumentException("The SVG exceeds the document's remaining pixel budget.");
        if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > MaxBytes)
            throw new ArgumentException("Supply SVG code or a local .svg file of at most 128 KiB UTF-8.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(source), new() { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = MaxBytes });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex) { throw new ArgumentException("Invalid SVG XML; DTDs and entities are not allowed.", ex); }
        var root = document.Root;
        if (root?.Name.LocalName != "svg" || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new ArgumentException("Supply a standalone <svg> document without processing instructions.");
        var viewBox = Regex.Split(root.Attribute("viewBox")?.Value.Trim() ?? "", @"[\s,]+");
        var bounds = new double[4];
        if (viewBox.Length != 4 || viewBox.Where((part, i) => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out bounds[i])
            || !double.IsFinite(bounds[i]) || Math.Abs(bounds[i]) > 1000000).Any() || bounds[2] <= 0 || bounds[3] <= 0)
            throw new ArgumentException("SVG needs a finite positive viewBox, for example viewBox='0 0 100 100'.");
        var nodes = root.DescendantsAndSelf().ToArray();
        if (nodes.Length > 4096 || nodes.Any(node => node.Ancestors().Take(64).Count() >= 64))
            throw new ArgumentException("SVG exceeds 4096 elements or 64 levels of nesting.");
        var ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var references = new List<(XElement Element, string Id)>();
        foreach (var element in nodes)
        {
            if (element.Name.Namespace != XNamespace.None && element.Name.Namespace != svgNamespace || !elements.Contains(element.Name.LocalName))
                throw new ArgumentException("Unsupported SVG element: " + element.Name.LocalName + ". Use paths/basic shapes; create editable text with editor_text. Filters, embedded images and CSS stylesheets are not supported.");
            element.Name = svgNamespace + element.Name.LocalName;
            foreach (var attribute in element.Attributes().ToArray())
            {
                if (attribute.IsNamespaceDeclaration || attribute.Name.Namespace == XNamespace.Xml && attribute.Name.LocalName is "space" or "lang") continue;
                string name = attribute.Name.LocalName, value = attribute.Value.Trim();
                if (attribute.Name.Namespace != XNamespace.None && !(attribute.Name.Namespace == xlinkNamespace && name == "href")
                    || !attributes.Contains(name) && !presentation.Contains(name) && !name.StartsWith("aria-", StringComparison.Ordinal) && !name.StartsWith("data-", StringComparison.Ordinal))
                    throw new ArgumentException("Unsupported SVG attribute: " + name + ". Use supported geometry and inline presentation attributes.");
                if (name == "id" && (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || !ids.TryAdd(value, element)))
                    throw new ArgumentException("SVG IDs must be nonempty, unique and contain no whitespace.");
                if (name == "href")
                {
                    if (value.Length < 2 || value[0] != '#' || element.Name.LocalName is not ("use" or "linearGradient" or "radialGradient"))
                        throw new ArgumentException("SVG references must be local #ids; external files and URLs are not allowed.");
                    references.Add((element, value[1..]));
                    if (attribute.Name.Namespace == XNamespace.None) { attribute.Remove(); element.SetAttributeValue(xlinkNamespace + "href", value); }
                }
                else if (name == "style")
                {
                    foreach (string declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int colon = declaration.IndexOf(':');
                        if (colon <= 0 || !presentation.Contains(declaration[..colon].Trim()))
                            throw new ArgumentException("SVG style supports inline presentation properties only; use editor_text for text.");
                        AddPaintReference(element, declaration[(colon + 1)..].Trim());
                    }
                }
                else if (presentation.Contains(name)) AddPaintReference(element, value);
            }
        }
        var edges = nodes.ToDictionary(node => node, node => node.Elements().ToList());
        foreach (var reference in references)
        {
            if (!ids.TryGetValue(reference.Id, out var target)) throw new ArgumentException("SVG references an unknown #id: " + reference.Id);
            edges[reference.Element].Add(target);
        }
        var visiting = new HashSet<XElement>(); var costs = new Dictionary<XElement, long>();
        Count(root, 0);
        root.SetAttributeValue("width", width.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", height.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue(XNamespace.Xmlns + "xlink", xlinkNamespace.NamespaceName);
        return root.ToString(SaveOptions.DisableFormatting);

        void AddPaintReference(XElement element, string value)
        {
            if (!value.StartsWith("url", StringComparison.OrdinalIgnoreCase)) return;
            var match = localUrl.Match(value);
            if (!match.Success) throw new ArgumentException("SVG paint and clip references must use url(#id), without external URLs.");
            references.Add((element, match.Groups[1].Value));
        }
        long Count(XElement element, int depth)
        {
            if (depth >= 64 || !visiting.Add(element)) throw new ArgumentException("SVG contains a recursive or excessively deep reference.");
            if (costs.TryGetValue(element, out long cached)) { visiting.Remove(element); return cached; }
            long cost = 1;
            foreach (var child in edges[element])
            {
                cost += Count(child, depth + 1);
                if (cost > 100000) throw new ArgumentException("SVG expands to too many repeated elements; simplify its <use> references.");
            }
            visiting.Remove(element); costs[element] = cost; return cost;
        }
    }
}
