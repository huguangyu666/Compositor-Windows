using Compositor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.Segmentation;

public sealed record ObjectSelectionRequest(SelectionPoint[] Points, SelectionBox? Box, bool SoftEdges)
{
    public static ObjectSelectionRequest Parse(JsonObject args, int width, int height)
    {
        var points = new List<SelectionPoint>();
        ReadPoints("foregroundPoints", true); ReadPoints("backgroundPoints", false);
        SelectionBox? box = null;
        if (args["box"] is { } bounds)
        {
            if (bounds is not JsonObject area) throw new ArgumentException("box must contain x, y, width and height.");
            double x = Number(area["x"]), y = Number(area["y"]), w = Number(area["width"]), h = Number(area["height"]);
            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > width || y + h > height)
                throw new ArgumentException("Use a nonempty box inside the canvas.");
            box = new(x, y, w, h);
        }
        if (points.Count > 64 || box is null && !points.Any(point => point.Include))
            throw new ArgumentException("Use a box or at least one foreground point, with at most 64 points in total.");
        bool soft = true;
        if (args["softEdges"] is { } edge)
        {
            if (edge is not JsonValue value || !value.TryGetValue<bool>(out soft)) throw new ArgumentException("softEdges must be a boolean.");
        }
        return new(points.ToArray(), box, soft);

        void ReadPoints(string key, bool include)
        {
            if (args[key] is not { } node) return;
            if (node is not JsonArray array || array.Count > 64) throw new ArgumentException($"{key} must contain at most 64 [x,y] points.");
            foreach (var entry in array)
            {
                if (entry is not JsonArray { Count: 2 } pair) throw new ArgumentException($"{key} points must be [x,y].");
                double x = Number(pair[0]), y = Number(pair[1]);
                if (x < 0 || y < 0 || x >= width || y >= height) throw new ArgumentException("Points must lie inside the canvas.");
                points.Add(new(new PointD(x, y), include));
            }
        }
        static double Number(JsonNode? node)
        {
            if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
                throw new ArgumentException("Coordinates must be finite numbers.");
            double number = value.Deserialize<double>();
            if (!double.IsFinite(number)) throw new ArgumentException("Coordinates must be finite numbers.");
            return number;
        }
    }
}
