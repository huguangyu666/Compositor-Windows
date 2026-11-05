using System.Text.Json.Serialization;

namespace Compositor.Core;

/// <summary>Letters painted in a color other than the text's own, in UTF-16 offsets into the content.</summary>
public sealed class LayerTextColorRun : IEquatable<LayerTextColorRun>
{
    [JsonRequired]
    public int Location { get; set; }

    [JsonRequired]
    public int Length { get; set; }

    [JsonRequired]
    public double Red { get; set; }

    [JsonRequired]
    public double Green { get; set; }

    [JsonRequired]
    public double Blue { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Red) && Red is >= 0 and <= 1
        && double.IsFinite(Green) && Green is >= 0 and <= 1
        && double.IsFinite(Blue) && Blue is >= 0 and <= 1;

    public bool Equals(LayerTextColorRun? other) => other is not null
        && Location == other.Location && Length == other.Length
        && Red == other.Red && Green == other.Green && Blue == other.Blue;

    public override bool Equals(object? obj) => Equals(obj as LayerTextColorRun);

    public override int GetHashCode() => HashCode.Combine(Location, Length, Red, Green, Blue);
}

/// <summary>Letters set in a face other than the text's own, in the same offsets.</summary>
public sealed class LayerTextFontRun : IEquatable<LayerTextFontRun>
{
    [JsonRequired]
    public int Location { get; set; }

    [JsonRequired]
    public int Length { get; set; }

    [JsonRequired]
    public string FontName { get; set; } = "";

    [JsonIgnore]
    public bool IsValid => FontName.Length > 0 && FontName.Length <= 200 && !FontName.ContainsNewline();

    public bool Equals(LayerTextFontRun? other) => other is not null
        && Location == other.Location && Length == other.Length && FontName == other.FontName;

    public override bool Equals(object? obj) => Equals(obj as LayerTextFontRun);

    public override int GetHashCode() => HashCode.Combine(Location, Length, FontName);
}

/// <summary>
/// A pixel layer's editable text. The PNG stays the display fallback; this metadata only matters
/// when the text is edited. Validated the same way the macOS ProjectStore does.
/// </summary>
public sealed class LayerTextStyle : IEquatable<LayerTextStyle>
{
    [JsonRequired]
    public string Content { get; set; } = "Text";

    [JsonRequired]
    public string FontName { get; set; } = "Helvetica";

    [JsonRequired]
    public double FontSize { get; set; } = 72;

    [JsonRequired]
    public double Red { get; set; }

    [JsonRequired]
    public double Green { get; set; }

    [JsonRequired]
    public double Blue { get; set; }

    [JsonRequired]
    [JsonConverter(typeof(RawStringEnumConverter<TextAlignment>))]
    public TextAlignment Alignment { get; set; }

    [JsonRequired]
    public double Tracking { get; set; }

    /// <summary>Baseline to baseline, in layer pixels. 0 is Auto: 120% of the font size.</summary>
    [JsonRequired]
    public double Leading { get; set; }

    /// <summary>What Leading 0 means, the same 120% of the size as the Mac's autoLeading.</summary>
    [JsonIgnore]
    public double AutoLeading => FontSize * 1.2;

    /// <summary>Baseline to baseline as drawn: the leading, or 120% of the size when it is Auto.</summary>
    [JsonIgnore]
    public double LineHeight => Leading > 0 ? Leading : AutoLeading;

    /// <summary>
    /// The gap between the text and its box, in layer pixels — the same for point text and a fixed box,
    /// so turning one into the other doesn't move the text (the Mac's LayerTextStyle.padding).
    /// </summary>
    [JsonIgnore]
    public const double Padding = 12;

    /// <summary>Fixed paragraph bounds in layer pixels; absent on older point-text layers. Saved
    /// as the {"width":…,"height":…} object Swift's CGSize encodes to — the port's SizeD array
    /// shape would corrupt cross-compatibility, so the style carries its own record.</summary>
    public TextBoxSize? BoxSize { get; set; }

    public List<LayerTextColorRun>? ColorRuns { get; set; }

    public List<LayerTextFontRun>? FontRuns { get; set; }

    /// <summary>The same check as the macOS LayerTextStyle.isValid.</summary>
    [JsonIgnore]
    public bool IsValid
    {
        get
        {
            if (Content.Length > 100_000) return false;
            if (!(double.IsFinite(FontSize) && FontSize is >= 1 and <= 2000)) return false;
            if (!(double.IsFinite(Red) && Red is >= 0 and <= 1
                && double.IsFinite(Green) && Green is >= 0 and <= 1
                && double.IsFinite(Blue) && Blue is >= 0 and <= 1)) return false;
            if (!(double.IsFinite(Tracking) && Tracking is >= -100 and <= 1000)) return false;
            if (!(double.IsFinite(Leading) && Leading is >= 0 and <= 5000)) return false;
            if (BoxSize is { } box && !box.IsValid) return false;
            return ColorRunsAreValid && FontRunsAreValid;
        }
    }

    private bool ColorRunsAreValid
    {
        get
        {
            if (ColorRuns is not { } runs) return true;
            if (runs.Count == 0) return false;
            int end = 0;
            foreach (var run in runs)
            {
                if (run.Location < end || run.Length <= 0 || !run.IsValid) return false;
                end = run.Location + run.Length;
            }
            return end <= Content.Length;
        }
    }

    private bool FontRunsAreValid
    {
        get
        {
            if (FontRuns is not { } runs) return true;
            if (runs.Count == 0) return false;
            int end = 0;
            foreach (var run in runs)
            {
                if (run.Location < end || run.Length <= 0 || !run.IsValid) return false;
                end = run.Location + run.Length;
            }
            return end <= Content.Length;
        }
    }

    /// <summary>
    /// The color each letter is painted in: the text's own, with every colour run painted over it. Indices are
    /// UTF-16 units, which is how <see cref="LayerTextColorRun.Location"/> counts the content on both platforms.
    /// </summary>
    private (double Red, double Green, double Blue)[] UnitColors()
    {
        var units = new (double Red, double Green, double Blue)[Content.Length];
        for (int index = 0; index < units.Length; index++) units[index] = (Red, Green, Blue);
        foreach (var run in ColorRuns ?? [])
            for (int index = Math.Max(0, run.Location); index < Math.Min(units.Length, run.Location + run.Length); index++)
                units[index] = (run.Red, run.Green, run.Blue);
        return units;
    }

    /// <summary>Writes the runs back from per-letter colors, dropping the letters that agree with the text's own
    /// color and merging neighbours that agree with each other — the shape the manifest saves and the validator
    /// accepts: sorted, not overlapping, and nothing at all when every letter is the base color.</summary>
    private void SetUnitColors((double Red, double Green, double Blue)[] units)
    {
        var runs = new List<LayerTextColorRun>();
        for (int index = 0; index < units.Length; index++)
        {
            var color = units[index];
            if (color.Red == Red && color.Green == Green && color.Blue == Blue) continue;
            if (runs.Count > 0 && runs[^1].Location + runs[^1].Length == index
                && runs[^1].Red == color.Red && runs[^1].Green == color.Green && runs[^1].Blue == color.Blue)
                runs[^1].Length++;
            else runs.Add(new LayerTextColorRun { Location = index, Length = 1, Red = color.Red, Green = color.Green, Blue = color.Blue });
        }
        ColorRuns = runs.Count == 0 ? null : runs;
    }

    /// <summary>The face of each letter, the same expansion as the colors: the text's own face with the runs over it.</summary>
    private string[] UnitFaces()
    {
        var faces = new string[Content.Length];
        Array.Fill(faces, FontName);
        foreach (var run in FontRuns ?? [])
            for (int index = Math.Max(0, run.Location); index < Math.Min(faces.Length, run.Location + run.Length); index++)
                faces[index] = run.FontName;
        return faces;
    }

    private void SetUnitFaces(string[] faces)
    {
        // One face across the whole text is not a run, it is the text's face.
        if (faces.Length > 0 && Array.TrueForAll(faces, face => face == faces[0]))
        {
            FontName = faces[0];
            FontRuns = null;
            return;
        }
        var runs = new List<LayerTextFontRun>();
        for (int index = 0; index < faces.Length; index++)
        {
            if (faces[index] == FontName) continue;
            if (runs.Count > 0 && runs[^1].Location + runs[^1].Length == index && runs[^1].FontName == faces[index]) runs[^1].Length++;
            else runs.Add(new LayerTextFontRun { Location = index, Length = 1, FontName = faces[index] });
        }
        FontRuns = runs.Count == 0 ? null : runs;
    }

    /// <summary>The color of one letter, or the text's own where no run reaches it.</summary>
    public (double Red, double Green, double Blue) ColorAt(int index)
    {
        foreach (var run in ColorRuns ?? [])
            if (run.Location <= index && index < run.Location + run.Length) return (run.Red, run.Green, run.Blue);
        return (Red, Green, Blue);
    }

    /// <summary>The face of one letter, or the text's own.</summary>
    public string FontNameAt(int index)
    {
        foreach (var run in FontRuns ?? [])
            if (run.Location <= index && index < run.Location + run.Length) return run.FontName;
        return FontName;
    }

    /// <summary>
    /// The one face covering the range, or null when it is empty or set in more than one face — what the
    /// character panel shows in its face field so a mixed selection does not pretend to be one thing.
    /// </summary>
    public string? UniformFontName(int location, int length)
    {
        int start = Math.Clamp(location, 0, Content.Length), end = Math.Max(start, Math.Clamp(location + length, 0, Content.Length));
        if (end <= start) return null;
        var face = FontNameAt(start);
        for (int index = start; index < end; index++) if (FontNameAt(index) != face) return null;
        return face;
    }

    /// <summary>
    /// Paints a range of letters with one color (the macOS setColor(_:in:), TypeTool.swift:71-82). An empty
    /// range changes nothing; the whole content painted at once becomes the text's own color, with no runs left
    /// behind — that is the difference between repainting a selection and repainting everything.
    /// </summary>
    public void SetColor(double red, double green, double blue, int location, int length)
    {
        if (!(double.IsFinite(red) && red is >= 0 and <= 1 && double.IsFinite(green) && green is >= 0 and <= 1
            && double.IsFinite(blue) && blue is >= 0 and <= 1)) return;
        // An empty range means the caret, and the caret's color is the text's own: both that and painting
        // everything set the base color and leave no runs, which is the macOS start == end || whole-content
        // branch (TypeTool.swift:74-78) rather than two special cases.
        int start = Math.Clamp(location, 0, Content.Length), end = Math.Max(start, Math.Clamp(location + length, 0, Content.Length));
        if (start == end || (start == 0 && end == Content.Length))
        {
            Red = red;
            Green = green;
            Blue = blue;
            ColorRuns = null;
            return;
        }
        var units = UnitColors();
        for (int index = start; index < end; index++) units[index] = (red, green, blue);
        SetUnitColors(units);
    }

    /// <summary>Sets one face over a range (the macOS setFont(_:in:), TypeTool.swift:107-119), with the same
    /// whole-content shortcut. A face the document would refuse is refused here instead.</summary>
    public void SetFont(string name, int location, int length)
    {
        if (name.Length == 0 || name.Length > 200 || name.ContainsNewline()) return;
        int start = Math.Clamp(location, 0, Content.Length), end = Math.Max(start, Math.Clamp(location + length, 0, Content.Length));
        if (start == end || (start == 0 && end == Content.Length))
        {
            FontName = name;
            FontRuns = null;
            return;
        }
        var faces = UnitFaces();
        for (int index = start; index < end; index++) faces[index] = name;
        SetUnitFaces(faces);
    }

    /// <summary>
    /// Keeps each letter's color and face when <paramref name="length"/> units at <paramref name="location"/>
    /// are replaced by <paramref name="withLength"/> new ones, which take theirs from the letter before, as
    /// typing does (the macOS replaceCharacters(in:withLength:), TypeTool.swift:123-139). Call it before the
    /// content itself changes.
    /// </summary>
    public void ReplaceCharacters(int location, int length, int withLength)
    {
        int count = Content.Length;
        int start = Math.Clamp(location, 0, count), end = Math.Max(start, Math.Clamp(location + length, 0, count));
        if (ColorRuns is not null)
        {
            var colors = new List<(double Red, double Green, double Blue)>(UnitColors());
            var inherited = start > 0 ? colors[start - 1] : start < colors.Count ? colors[start] : (Red, Green, Blue);
            colors.RemoveRange(start, end - start);
            colors.InsertRange(start, Enumerable.Repeat(inherited, Math.Max(0, withLength)));
            SetUnitColors([.. colors]);
        }
        if (FontRuns is not null)
        {
            var faces = new List<string>(UnitFaces());
            var inherited = start > 0 ? faces[start - 1] : start < faces.Count ? faces[start] : FontName;
            faces.RemoveRange(start, end - start);
            faces.InsertRange(start, Enumerable.Repeat(inherited, Math.Max(0, withLength)));
            SetUnitFaces([.. faces]);
        }
    }

    /// <summary>
    /// The span that has to be replaced to get from one string to the other: whatever prefix and suffix they
    /// share is left alone, so an insertion, a deletion and a pasted selection each report what actually moved.
    /// </summary>
    public static (int Location, int Length, int WithLength) MinimalEdit(string from, string to)
    {
        if (from == to) return (from.Length, 0, 0);
        int prefix = 0;
        while (prefix < from.Length && prefix < to.Length && from[prefix] == to[prefix]) prefix++;
        int suffix = 0;
        while (suffix < from.Length - prefix && suffix < to.Length - prefix
            && from[from.Length - 1 - suffix] == to[to.Length - 1 - suffix]) suffix++;
        return (prefix, from.Length - prefix - suffix, to.Length - prefix - suffix);
    }

    public bool Equals(LayerTextStyle? other)
    {
        if (other is null) return false;
        if (Content != other.Content || FontName != other.FontName || FontSize != other.FontSize
            || Red != other.Red || Green != other.Green || Blue != other.Blue
            || Alignment != other.Alignment || Tracking != other.Tracking || Leading != other.Leading
            || !Same(BoxSize, other.BoxSize)) return false;
        return RunsEqual(ColorRuns, other.ColorRuns) && RunsEqual(FontRuns, other.FontRuns);
    }

    private static bool Same(TextBoxSize? left, TextBoxSize? right) =>
        left is not null ? right is not null && left.Equals(right) : right is null;

    private static bool RunsEqual<T>(List<T>? left, List<T>? right) where T : IEquatable<T>
    {
        if (left is null) return right is null;
        if (right is null || left.Count != right.Count) return false;
        return left.Zip(right).All(pair => pair.First.Equals(pair.Second));
    }

    public override bool Equals(object? obj) => Equals(obj as LayerTextStyle);

    public override int GetHashCode() => HashCode.Combine(Content, FontName, FontSize, Alignment, Tracking, Leading);
}

internal static class StringNewlineExtensions
{
    public static bool ContainsNewline(this string text) =>
        text.Contains('\n') || text.Contains('\r') || text.Contains('\u2028') || text.Contains('\u2029')
        || text.Contains('\u0085') || text.Contains('\u000B') || text.Contains('\u000C');
}

/// <summary>A text box's fixed bounds, in the {"width":…,"height":…} object shape Swift's CGSize
/// (the Mac's LayerTextStyle.boxSize) encodes to. The port's SizeD saves as an array, which the
/// Mac's decoder would refuse here.</summary>
public sealed class TextBoxSize : IEquatable<TextBoxSize>
{
    [JsonRequired]
    public double Width { get; set; }

    [JsonRequired]
    public double Height { get; set; }

    public TextBoxSize() { }

    public TextBoxSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>The macOS <c>LayerTextStyle.boxIsValid</c> (TypeTool.swift:25-29) for a box on its own: the
    /// 16-pixel floor the rasterizer needs on either side, the document's side ceiling, and its area ceiling.
    /// A handle drag checks this before showing a new box, so the rule lives with the box rather than only
    /// inside the whole style's <see cref="LayerTextStyle.IsValid"/>.</summary>
    [JsonIgnore]
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && Width is >= 16 and <= 30_000
        && Height is >= 16 and <= 30_000 && Width * Height <= Limits.MaxSurfacePixels;

    public bool Equals(TextBoxSize? other) => other is not null && Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => Equals(obj as TextBoxSize);

    public override int GetHashCode() => HashCode.Combine(Width, Height);
}
