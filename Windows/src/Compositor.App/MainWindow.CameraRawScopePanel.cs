using Compositor.Core;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace Compositor.App;

/// <summary>
/// Camera Raw's histogram and vectorscope (macOS UI/CameraRawControls.swift:31-118). The numbers come from
/// <see cref="CameraRawScope"/>, which is counted on the grade with nothing painted over it; this half draws
/// them and holds the mode, which — like the Mac's — lives on the session and never reaches the project file.
/// </summary>
public sealed partial class MainWindow
{
    internal const int ScopeHistogram = 0;
    internal const int ScopeVectorscope = 1;

    /// <summary>The last scope the preview published. A tick that produced none leaves this alone, as on the
    /// Mac (<c>Filters.swift:564</c> stores only a non-nil answer).</summary>
    private CameraRawScope? cameraRawScope;
    private int cameraRawScopeMode;
    private CameraRawScopeCanvas? cameraRawScopeCanvas;

    /// <summary>Redrawn with every preview and every mode change.</summary>
    private Action? cameraRawScopeRefresh;

    internal CameraRawScope? CameraRawLastScope => cameraRawScope;
    internal int CameraRawCurrentScopeMode => cameraRawScopeMode;

    internal void SetCameraRawScopeMode(int mode)
    {
        cameraRawScopeMode = mode;
        cameraRawScopeRefresh?.Invoke();
    }

    /// <summary>The two permanent clipping indicators (Filters.swift:141-143): they paint on top of the grade and
    /// are never part of it, so OK cannot carry them and neither does the project file.</summary>
    private bool cameraRawShowsShadowClipping;
    private bool cameraRawShowsHighlightClipping;

    internal bool CameraRawCurrentShadowClipping => cameraRawShowsShadowClipping;
    internal bool CameraRawCurrentHighlightClipping => cameraRawShowsHighlightClipping;

    /// <summary>The triangles' own row: their colour is a fact about these two bits, not about the frame that
    /// is about to arrive, so it is synced on the press rather than on the publish (CR-i's eye refresh learned
    /// this the hard way).</summary>
    private Action? cameraRawClipRefresh;

    /// <summary>Flipping one indicator re-renders, which is what the Mac's button does: the settings go back
    /// unchanged and only the boolean moved (<c>CameraRawControls.swift:69-72</c>).</summary>
    internal void ToggleCameraRawClipping(bool highlights)
    {
        if (highlights) cameraRawShowsHighlightClipping = !cameraRawShowsHighlightClipping;
        else cameraRawShowsShadowClipping = !cameraRawShowsShadowClipping;
        cameraRawClipRefresh?.Invoke();
        if (document is { } doc && cameraRawSessionKey is not null) PreviewCameraRaw(doc, cameraRawSessionLayer);
    }

    /// <summary>
    /// The 110-tall frame: three ribbons over a shared vertical scale, or the vectorscope's cells. Both branches
    /// copy the Mac's drawing laws, including the two it does not hedge: nothing is drawn at all while the peak
    /// is zero, and the vectorscope's cell size comes from the width, so on a frame wider than it is tall the
    /// upper cells fall outside the clip.
    /// </summary>
    private sealed class CameraRawScopeCanvas : Canvas
    {
        public CameraRawScopeCanvas()
        {
            Width = 256;
            Height = 110;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Clip = new RectangleGeometry { Rect = new(0, 0, Width, Height) };
        }

        public void Show(CameraRawScope? scope, bool vectorscope)
        {
            Children.Clear();
            if (scope is null) return;
            if (vectorscope)
            {
                double peak = scope.VectorPeak;
                if (peak <= 0) return;
                double cell = Width / CameraRawScope.ScopeSide;
                for (int index = 0; index < scope.Vectorscope.Length; index++)
                {
                    double value = scope.Vectorscope[index];
                    if (value <= 0) continue;
                    double amount = Math.Min(1.0, value / peak);
                    Children.Add(new Rectangle
                    {
                        Width = cell + 0.2,
                        Height = cell + 0.2,
                        Fill = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.15 + 0.85 * amount },
                        IsHitTestVisible = false,
                    });
                    SetLeft(Children[^1], (index % CameraRawScope.ScopeSide) * cell);
                    SetTop(Children[^1], Height - (index / CameraRawScope.ScopeSide + 1) * cell);
                }
                return;
            }
            double shared = scope.Peak;
            if (shared <= 0) return;
            Children.Add(Ribbon(scope.Red, shared, Microsoft.UI.Colors.Red));
            Children.Add(Ribbon(scope.Green, shared, Microsoft.UI.Colors.LimeGreen));
            Children.Add(Ribbon(scope.Blue, shared, Microsoft.UI.Colors.DodgerBlue));
        }

        private Microsoft.UI.Xaml.Shapes.Path Ribbon(double[] bins, double peak, Windows.UI.Color color)
        {
            var ribbon = new PolyLineSegment();
            for (int index = 0; index < bins.Length; index++)
            {
                double height = Height * Math.Clamp(bins[index] / peak, 0, 1);
                ribbon.Points.Add(new Point(index * Width / bins.Length, Height - height));
            }
            ribbon.Points.Add(new Point(Width, Height));
            var figure = new PathFigure { StartPoint = new Point(0, Height), IsClosed = true };
            figure.Segments.Add(ribbon);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(color),
                Opacity = 0.55,
                IsHitTestVisible = false,
            };
        }
    }
}
