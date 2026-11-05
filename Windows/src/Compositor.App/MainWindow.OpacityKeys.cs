using Compositor.Core;
using Microsoft.UI.Xaml;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private (int Digit, long Time)? pendingOpacityDigit;
    private bool applyingLayerOpacityKeys;

    private bool HandleOpacityKey(VirtualKey raw, VirtualKeyModifiers modifiers, bool typing, long? at = null)
    {
        if (typing || FloatingPanelHoldsFocus || (modifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu | VirtualKeyModifiers.Windows)) != 0
            || tool is not (CanvasTool.Move or CanvasTool.Brush or CanvasTool.Eraser or CanvasTool.Clone or CanvasTool.Blur or CanvasTool.Healing or CanvasTool.Gradient)
            || pointerStroke is not null || healingStroke is not null || healingFinishing || IsFileImporting || IsDeletePending
            || IsGradientFinishing || StatusProgress.Visibility == Visibility.Visible || OpacityDigitPress(raw) is not { } digit) return false;
        if (tool == CanvasTool.Move && (!CanEditLayerCommands || selectedLayerIds.Count == 0)) return false;
        long now = at ?? Environment.TickCount64;
        int percent;
        if (pendingOpacityDigit is { } previous && now - previous.Time < 600)
        {
            percent = Math.Max(1, previous.Digit * 10 + digit); pendingOpacityDigit = null;
        }
        else { percent = digit == 0 ? 100 : digit * 10; pendingOpacityDigit = (digit, now); }
        if (tool == CanvasTool.Gradient) GradientOpacitySlider.Value = percent;
        else if (tool != CanvasTool.Move) BrushOpacitySlider.Value = percent;
        else if (document is { } owner)
        {
            EndOpacityEdit(); RetireBlendPreview(redraw: false);
            foreach (var id in selectedLayerIds) MarkLayerBandDirty(id);
            applyingLayerOpacityKeys = true;
            try { if (!owner.SetLayerOpacities(selectedLayerIds, percent / 100d)) PumpRender(); }
            finally { applyingLayerOpacityKeys = false; }
        }
        return true;
    }
}
