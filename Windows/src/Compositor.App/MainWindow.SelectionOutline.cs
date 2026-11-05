using System.Globalization;
using System.Text;
using Compositor.Core;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private const int SelectionOutlineRunsPerBatch = 256;

    private static string[] PrepareSelectionOutlinePaths(SelectionEdge[] edges, CancellationToken token)
    {
        var paths = new string[(edges.Length + SelectionOutlineRunsPerBatch - 1) / SelectionOutlineRunsPerBatch];
        var path = new StringBuilder();
        for (int batch = 0; batch < paths.Length; batch++)
        {
            token.ThrowIfCancellationRequested(); path.Clear();
            int first = batch * SelectionOutlineRunsPerBatch, end = Math.Min(edges.Length, first + SelectionOutlineRunsPerBatch);
            for (int i = first; i < end; i++)
            {
                var edge = edges[i];
                path.Append(CultureInfo.InvariantCulture, $"M{edge.X0},{edge.Y0}L{edge.X1},{edge.Y1} ");
            }
            paths[batch] = path.ToString();
        }
        return paths;
    }

    private static IEnumerable<PathFigure> SelectionOutlineFigures(Geometry? geometry)
    {
        if (geometry is PathGeometry path)
        {
            foreach (var figure in path.Figures) yield return figure;
        }
        else if (geometry is GeometryGroup group)
        {
            foreach (var child in group.Children)
                foreach (var figure in SelectionOutlineFigures(child)) yield return figure;
        }
    }

    private static string[] PrepareProjectedSelectionOutlinePaths(SelectionEdge[] edges, LayerTransform original,
        LayerTransform value, PointD offset, PointD[] corners, CancellationToken token)
    {
        var carry = DistortWarp.DocumentMap(original, corners);
        PointD Map(int x, int y)
        {
            var unit = BrushEngine.DocumentToPixel(original, 1, 1, new(x + offset.X, y + offset.Y));
            return carry(original.Point(new(value.FlipX ? 1 - unit.X : unit.X, value.FlipY ? 1 - unit.Y : unit.Y)));
        }
        var paths = new string[(edges.Length + SelectionOutlineRunsPerBatch - 1) / SelectionOutlineRunsPerBatch];
        var path = new StringBuilder();
        for (int batch = 0; batch < paths.Length; batch++)
        {
            token.ThrowIfCancellationRequested(); path.Clear();
            int first = batch * SelectionOutlineRunsPerBatch, end = Math.Min(edges.Length, first + SelectionOutlineRunsPerBatch);
            for (int i = first; i < end; i++)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                var edge = edges[i]; var start = Map(edge.X0, edge.Y0); var finish = Map(edge.X1, edge.Y1);
                float x0 = (float)start.X, y0 = (float)start.Y, x1 = (float)finish.X, y1 = (float)finish.Y;
                if (!float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1))
                    throw new InvalidOperationException("Selection outline mapping produced a nonfinite point.");
                // WinUI stores geometry coordinates as floats. Round-trip those coordinates
                // explicitly instead of letting a decimal shortening change the native point.
                path.Append(CultureInfo.InvariantCulture, $"M{x0:R},{y0:R}L{x1:R},{y1:R} ");
            }
            paths[batch] = path.ToString();
        }
        return paths;
    }
}
