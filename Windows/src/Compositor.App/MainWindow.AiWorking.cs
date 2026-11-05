using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private Storyboard? aiWorkingSweep, aiWorkingPulse, aiWorkingFade;
    private bool aiWorkingEffect;
    private bool aiWorkingAnimations;
    private double aiWorkingSweepWidth;

    private void StartAiWorkingEffect()
    {
        StopAiWorkingEffect(immediate: true);
        aiWorkingEffect = true;
        aiWorkingAnimations = new UISettings().AnimationsEnabled;
        AiWorkingHost.Opacity = 1;
        AiWorkingHost.Visibility = Visibility.Visible;
        AiWorkingScan.Visibility = aiWorkingAnimations ? Visibility.Visible : Visibility.Collapsed;
        AiWorkingRegion.Opacity = .8;
        RefreshAiWorkingRegion();
        if (!aiWorkingAnimations) return;
        aiWorkingPulse = new Storyboard();
        var pulse = new DoubleAnimation
        {
            From = .5, To = 1, Duration = new(TimeSpan.FromSeconds(1.4)),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(pulse, AiWorkingRegion); Storyboard.SetTargetProperty(pulse, "Opacity");
        aiWorkingPulse.Children.Add(pulse); aiWorkingPulse.Begin();
    }

    private void RefreshAiWorkingRegion()
    {
        if (!aiWorkingEffect || document is null || aiEditor?.Edit is not { } edit) return;
        var manifest = document.Snapshot.Manifest;
        var bounds = !edit.Expands && document.ActiveSelection is { } selected
            ? selected.Bounds : new RectI(0, 0, manifest.Width, manifest.Height);
        double scale = viewport.PointsPerPixel;
        double originX = viewport.Origin.X, originY = viewport.Origin.Y;
        if (AiPreviewHost.Visibility == Visibility.Visible)
        {
            scale = Math.Max(0, Math.Min((CanvasStage.ActualWidth - 48) / edit.Width, (CanvasStage.ActualHeight - 48) / edit.Height));
            originX = (CanvasStage.ActualWidth - edit.Width * scale) / 2;
            originY = (CanvasStage.ActualHeight - edit.Height * scale) / 2;
            if (edit.Expands) bounds = new RectI(0, 0, edit.Width, edit.Height);
        }
        double x = originX + bounds.X * scale, y = originY + bounds.Y * scale;
        double x0 = Math.Clamp(x, 0, CanvasStage.ActualWidth), y0 = Math.Clamp(y, 0, CanvasStage.ActualHeight);
        double x1 = Math.Clamp(x + bounds.Width * scale, 0, CanvasStage.ActualWidth);
        double y1 = Math.Clamp(y + bounds.Height * scale, 0, CanvasStage.ActualHeight);
        double width = Math.Max(0, x1 - x0), height = Math.Max(0, y1 - y0);
        AiWorkingRegion.Visibility = width > 0 && height > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiWorkingRegion.Margin = new Thickness(x0, y0, 0, 0);
        AiWorkingRegion.Width = Math.Max(1, width); AiWorkingRegion.Height = Math.Max(1, height);
        AiWorkingClip.Rect = new(0, 0, Math.Max(0, width - 4), Math.Max(0, height - 4));
        AiWorkingBadge.Margin = new Thickness(Math.Clamp(x0 + 10, 8, Math.Max(8, CanvasStage.ActualWidth - 200)),
            Math.Clamp(y0 + 10, 8, Math.Max(8, CanvasStage.ActualHeight - 42)), 0, 0);
        if (!aiWorkingAnimations || aiWorkingSweep is not null && Math.Abs(aiWorkingSweepWidth - width) < 1) return;
        aiWorkingSweep?.Stop();
        aiWorkingSweepWidth = width;
        aiWorkingSweep = new Storyboard();
        var sweep = new DoubleAnimation
        {
            From = -180, To = Math.Max(1, width), Duration = new(TimeSpan.FromSeconds(2.4)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(sweep, AiWorkingScanShift); Storyboard.SetTargetProperty(sweep, "X");
        aiWorkingSweep.Children.Add(sweep); aiWorkingSweep.Begin();
    }

    private void StopAiWorkingEffect(bool immediate = false)
    {
        aiWorkingEffect = false;
        aiWorkingSweep?.Stop(); aiWorkingSweep = null;
        aiWorkingPulse?.Stop(); aiWorkingPulse = null;
        aiWorkingFade?.Stop(); aiWorkingFade = null;
        if (AiWorkingHost is null || AiWorkingHost.Visibility != Visibility.Visible) return;
        if (immediate || !aiWorkingAnimations)
        {
            AiWorkingHost.Visibility = Visibility.Collapsed;
            return;
        }
        aiWorkingFade = new Storyboard();
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = new(TimeSpan.FromMilliseconds(180)) };
        Storyboard.SetTarget(fade, AiWorkingHost); Storyboard.SetTargetProperty(fade, "Opacity");
        aiWorkingFade.Children.Add(fade);
        aiWorkingFade.Completed += (_, _) => { if (!aiWorkingEffect) AiWorkingHost.Visibility = Visibility.Collapsed; };
        aiWorkingFade.Begin();
    }
}
