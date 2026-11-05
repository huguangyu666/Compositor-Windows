#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    // These observations describe the renderer actually in use. A direct outline
    // deliberately has no XAML Data, so null Data cannot be its readiness signal.
    private object? OutlineDisplayIdentity => directOutlineEnabled ? directProjectedOutline ?? directSourceOutline : SelectionOutline.Data;
    private bool OutlineIsReady => OutlineDisplayIdentity is not null;
    private PointD OutlineDisplayOffset => directOutlineEnabled
        ? new(directOutlinePlacement.M31, directOutlinePlacement.M32) : new(outlineTranslation.X, outlineTranslation.Y);
    private bool OutlineIsVisible => directOutlineEnabled ? directOutlineVisible : SelectionOutline.Visibility == Visibility.Visible;

    private void RequireSourceOutlineMinimumX(double expected)
    {
        double actual = directOutlineEnabled ? directSourceOutline!.Points.Min(point => point.X)
            : SelectionOutlineFigures(SelectionOutline.Data).Min(figure => figure.StartPoint.X);
        Require(actual == expected, "Pending geometry changed its source coordinates.");
    }
}
#endif
