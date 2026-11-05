using Compositor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private double brushSmoothing;
    private bool syncingBrushSmoothing;
    private BrushEndpoint? lastBrushEndpoint;

    private sealed class BrushEndpoint(EditorDocument owner, Guid layerId, bool onMask, PointD point, BrushEndpoint? previous)
    {
        public readonly EditorDocument Owner = owner;
        public readonly Guid LayerId = layerId;
        public readonly bool OnMask = onMask;
        public PointD Point = point;
        public bool Valid;
        public BrushEndpoint? Previous = previous;
    }

    private static BrushEndpoint? LastValidBrushEndpoint(BrushEndpoint? endpoint)
    {
        while (endpoint is { Valid: false }) endpoint = endpoint.Previous;
        return endpoint;
    }

    private void OnBrushSmoothingSliderChanged(object sender, RangeBaseValueChangedEventArgs args) => SetBrushSmoothing(args.NewValue);
    private void OnBrushSmoothingBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => SetBrushSmoothing(args.NewValue);

    private void SetBrushSmoothing(double value)
    {
        if (syncingBrushSmoothing || BrushSmoothingSlider is null || BrushSmoothingBox is null) return;
        brushSmoothing = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
        syncingBrushSmoothing = true;
        try { BrushSmoothingSlider.Value = BrushSmoothingBox.Value = brushSmoothing; }
        finally { syncingBrushSmoothing = false; }
    }
}
