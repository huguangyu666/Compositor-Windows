using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private NumberBox quickBrushSize = null!, quickBrushHardness = null!, quickBrushOpacity = null!;
    private WriteableBitmap? quickBrushBitmap;
    private bool quickBrushSync;

    private void InitializeQuickBrushControls()
    {
        quickBrushBitmap = new(40, 40);
        var image = NameQuickControl(new Image { Width = 36, Height = 36, Source = quickBrushBitmap }, "QuickBrushTip", "QT_BrushTip");
        BrushControls.Children.Insert(0, image);
        NumberBox Pair(Slider slider, string name, string label)
        {
            slider.Width = 66;
            var box = QuickNumber(name, label, slider.Minimum, slider.Maximum, slider.Value);
            BrushControls.Children.Insert(BrushControls.Children.IndexOf(slider) + 1, box);
            slider.ValueChanged += (_, _) => SyncQuickBrush();
            box.ValueChanged += (_, args) =>
            {
                if (quickBrushSync) return;
                if (double.IsFinite(args.NewValue)) slider.Value = Math.Clamp(args.NewValue, slider.Minimum, slider.Maximum);
                SyncQuickBrush();
            };
            return box;
        }
        quickBrushSize = Pair(BrushSizeSlider, "QuickBrushSize", "QT_BrushSize");
        quickBrushHardness = Pair(BrushHardnessSlider, "QuickBrushHardness", "QT_BrushHardness");
        quickBrushOpacity = Pair(BrushOpacitySlider, "QuickBrushOpacity", "QT_BrushOpacity");
        SyncQuickBrush();
    }

    private void SyncQuickBrush()
    {
        if (quickBrushSync || quickBrushOpacity is null) return;
        quickBrushSync = true;
        try { quickBrushSize.Value = BrushSizeSlider.Value; quickBrushHardness.Value = BrushHardnessSlider.Value; quickBrushOpacity.Value = BrushOpacitySlider.Value; }
        finally { quickBrushSync = false; }
        RefreshQuickBrushTip();
    }

    private void RefreshQuickBrushTip()
    {
        if (quickBrushBitmap is null) return;
        Span<byte> pixels = stackalloc byte[40 * 40 * 4];
        double radius = 3 + 15 * Math.Sqrt(BrushSizeSlider.Value / BrushSizeSlider.Maximum);
        double hardness = BrushHardnessSlider.Value / 100, opacity = BrushOpacitySlider.Value / 100;
        for (int y = 0; y < 40; y++) for (int x = 0; x < 40; x++)
        {
            double distance = Math.Sqrt(Math.Pow(x + .5 - 20, 2) + Math.Pow(y + .5 - 20, 2)) / radius;
            double coverage = distance >= 1 ? 0 : distance <= hardness ? opacity : opacity * (1 - distance) / Math.Max(.001, 1 - hardness);
            int background = ((x / 5 + y / 5) & 1) == 0 ? 145 : 185, p = (y * 40 + x) * 4;
            pixels[p] = (byte)Math.Round(background * (1 - coverage) + foregroundColor.B * coverage);
            pixels[p + 1] = (byte)Math.Round(background * (1 - coverage) + foregroundColor.G * coverage);
            pixels[p + 2] = (byte)Math.Round(background * (1 - coverage) + foregroundColor.R * coverage); pixels[p + 3] = 255;
        }
        using var stream = quickBrushBitmap.PixelBuffer.AsStream(); stream.Write(pixels); quickBrushBitmap.Invalidate();
    }
}
