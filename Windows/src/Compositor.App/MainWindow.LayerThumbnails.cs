using Compositor.Core;
using Compositor.Renderer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private void RefreshLayerThumbnail(LayerRow row, Guid? primaryId = null, ProjectSnapshot? preview = null)
    {
        if (document is null) return;
        var snapshot = preview ?? LayerDisplaySnapshot!;
        var layer = preview?.Manifest.Layers.FirstOrDefault(layer => layer.Id == row.Layer.Id) ?? row.Layer;
        var canvas = new SizeD(snapshot.Manifest.Width, snapshot.Manifest.Height);
        bool pixels = snapshot.Images.TryGetValue(layer.Id, out var source);
        bool framed = pixels && layer.Text is null;
        row.Thumbnail.Visibility = framed ? Visibility.Visible : Visibility.Collapsed;
        row.KindIcon.Visibility = framed ? Visibility.Collapsed : Visibility.Visible;
        row.KindIcon.Glyph = layer.IsGroupOrFalse ? "\uE8B7" : layer.Text is not null ? "\uE8D2" : "\uE91F";
        var size = framed ? CanvasThumbnail.FittedSize(snapshot.Manifest.Width, snapshot.Manifest.Height, 36) : (Width: 36, Height: 36);
        row.Thumbnail.Width = row.PixelTarget.Width = size.Width;
        row.Thumbnail.Height = row.PixelTarget.Height = size.Height;
        if (source is not null && framed && (Changed(row.ThumbnailSource, row.ThumbnailRevision, source, source.Revision)
            || row.ThumbnailCanvas != canvas || !layer.Transform.Equals(row.ThumbnailPlacement)))
        {
            row.Thumbnail.Source = LayerThumbnail(snapshot, layer.Transform, 36, source, null);
            row.ThumbnailSource = new WeakReference<object>(source); row.ThumbnailRevision = source.Revision;
            row.ThumbnailPlacement = CopyTransform(layer.Transform); row.ThumbnailCanvas = canvas;
        }
        bool hasMask = snapshot.Masks.TryGetValue(layer.Id, out var mask);
        row.MaskTarget.Visibility = hasMask ? Visibility.Visible : Visibility.Collapsed;
        row.MaskLink.Visibility = hasMask && pixels && !layer.IsGroupOrFalse ? Visibility.Visible : Visibility.Collapsed;
        row.MaskLink.IsEnabled = transformInspectorEdit is null;
        ((Microsoft.UI.Xaml.Controls.FontIcon)row.MaskLink.Content).Opacity = layer.MaskLinked == false ? 0 : 1;
        row.MaskDisabledMark.Visibility = layer.MaskEnabled == false ? Visibility.Visible : Visibility.Collapsed;
        primaryId ??= SelectedLayer?.Id;
        bool selected = primaryId == layer.Id && selectedLayerIds.Count <= 1 && effectSelection is null;
        row.PixelTarget.BorderThickness = new(selected && !paintOnMask ? 2 : 1);
        row.MaskTarget.BorderThickness = new(selected && paintOnMask ? 2 : 1);
        row.PixelTarget.BorderBrush = selected && !paintOnMask ? layerTargetAccent : layerTargetEdge;
        row.MaskTarget.BorderBrush = hasMask && paintOnMask && primaryId == layer.Id && maskAloneLayerId == layer.Id
            ? layerTargetSolo : selected && paintOnMask ? layerTargetAccent : layerTargetEdge;
        row.MaskThumbnail.Visibility = hasMask ? Visibility.Visible : Visibility.Collapsed;
        row.MaskThumbnail.Opacity = 1;
        if (hasMask)
        {
            var maskSize = CanvasThumbnail.FittedSize(snapshot.Manifest.Width, snapshot.Manifest.Height, 30);
            row.MaskThumbnail.Width = row.MaskTarget.Width = maskSize.Width;
            row.MaskThumbnail.Height = row.MaskTarget.Height = maskSize.Height;
            row.MaskDisabledMark.X1 = 1; row.MaskDisabledMark.Y1 = maskSize.Height - 1;
            row.MaskDisabledMark.X2 = maskSize.Width - 1; row.MaskDisabledMark.Y2 = 1;
            var placement = layer.MaskPlacement ?? layer.Transform;
            if (Changed(row.MaskThumbnailSource, row.MaskThumbnailRevision, mask!, mask!.Revision)
                || row.MaskThumbnailCanvas != canvas || !placement.Equals(row.MaskThumbnailPlacement))
            {
                row.MaskThumbnail.Source = LayerThumbnail(snapshot, placement, 30, null, mask);
                row.MaskThumbnailSource = new WeakReference<object>(mask); row.MaskThumbnailRevision = mask.Revision;
                row.MaskThumbnailPlacement = CopyTransform(placement); row.MaskThumbnailCanvas = canvas;
            }
        }

        static bool Changed(WeakReference<object>? cached, int revision, object source, int current) =>
            revision != current || cached is null || !cached.TryGetTarget(out var previous) || !ReferenceEquals(source, previous);
    }

    private static WriteableBitmap LayerThumbnail(ProjectSnapshot snapshot, LayerTransform placement, int box, Surface? image, MaskSurface? mask)
    {
        var thumbnail = CanvasThumbnail.Draw(snapshot.Manifest.Width, snapshot.Manifest.Height, box, placement, image, mask);
        var bitmap = new WriteableBitmap(thumbnail.Width, thumbnail.Height);
        var bytes = new byte[thumbnail.Pixels.Length];
        SurfacePresentation.FillBgra(thumbnail, bytes, thumbnail.Width, 0, 0, thumbnail.Width, thumbnail.Height);
        using var stream = bitmap.PixelBuffer.AsStream(); stream.Write(bytes); bitmap.Invalidate();
        return bitmap;
    }
}
