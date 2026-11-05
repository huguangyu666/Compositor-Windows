using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed class BlendPreviewFrame(EditorDocument owner, Guid id, LayerBlendMode mode, ProjectSnapshot source)
    {
        public EditorDocument Owner { get; } = owner;
        public Guid LayerId { get; } = id;
        public LayerBlendMode Mode { get; } = mode;
        // Frozen metadata is cloned on the render worker once per highlighted mode.
        public Lazy<ProjectSnapshot> Snapshot { get; } = new(() =>
        {
            var manifest = ManifestJson.Deserialize(ManifestJson.Serialize(source.Manifest));
            manifest.Layers.Single(layer => layer.Id == id).BlendMode = mode;
            return new() { Manifest = manifest, Images = source.Images, Masks = source.Masks };
        });
    }

    private sealed record BlendPickerSession(EditorDocument Owner, Guid Id);
    private BlendPickerSession? blendPickerSession;
    private BlendPreviewFrame? blendPreview;
    private bool applyingBlendMode;

    private void InitializeBlendPicker()
    {
        BlendCombo.SelectionChangedTrigger = ComboBoxSelectionChangedTrigger.Committed;
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        {
            var item = new ComboBoxItem { Content = Loc.Name("Blend", Enum.GetName(mode)!), Tag = mode };
            if (mode is LayerBlendMode.Darken or LayerBlendMode.Lighten or LayerBlendMode.Overlay or LayerBlendMode.Difference or LayerBlendMode.Hue)
            {
                item.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 75, 75, 75));
                item.BorderThickness = new(0, 1, 0, 0); item.Margin = new(0, 5, 0, 0);
            }
            item.PointerEntered += (_, _) => PreviewBlendItem(item);
            // Opening a popup assigns programmatic focus to its stored item after
            // DropDownOpened. That must not overwrite a newer pointer highlight.
            item.GotFocus += (_, _) => { if (item.FocusState == FocusState.Keyboard) PreviewBlendItem(item); };
            BlendCombo.Items.Add(item);
        }
        BlendCombo.SelectedIndex = 0;
        BlendCombo.DropDownOpened += (_, _) =>
        {
            EndOpacityEdit();
            if (CanEditLayerCommands && selectedLayerIds.Count == 1 && document is { } owner && SelectedLayer is { IsGroupOrFalse: false } layer)
                blendPickerSession = new(owner, layer.Id);
        };
        BlendCombo.DropDownClosed += (_, _) =>
        {
            var closing = blendPickerSession;
            // A chosen item's SelectionChanged may follow DropDownClosed. Let it replace
            // the preview before a canceled menu restores the document's current mode.
            DispatcherQueue.TryEnqueue(() => { if (!BlendCombo.IsDropDownOpen && ReferenceEquals(blendPickerSession, closing)) RetireBlendPreview(); });
        };
    }

    private void PreviewBlendItem(ComboBoxItem item)
    {
        if (item.Tag is not LayerBlendMode mode || blendPickerSession is not { } session
            || !ReferenceEquals(document, session.Owner) || SelectedLayer?.Id != session.Id
            || !CanEditLayerCommands || selectedLayerIds.Count != 1) return;
        if (blendPreview?.Mode == mode) return;
        if ((SelectedLayer.BlendMode ?? LayerBlendMode.Normal) == mode)
        {
            if (blendPreview is null) return;
            blendPreview = null;
        }
        else blendPreview = new(session.Owner, session.Id, mode, CaptureRenderSnapshot(session.Owner.Snapshot));
        MarkLayerBandDirty(session.Id); frameRevision++; PumpRender();
    }

    private void RetireBlendPreview(bool redraw = true)
    {
        var previous = blendPreview; blendPreview = null; blendPickerSession = null;
        if (BlendCombo.IsDropDownOpen) BlendCombo.IsDropDownOpen = false;
        if (previous is null || !ReferenceEquals(previous.Owner, document) || windowClosed) return;
        MarkLayerBandDirty(previous.LayerId); frameRevision++;
        if (redraw) PumpRender();
    }

    private void OnBlendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressPanelEvents || !CanEditLayerCommands || selectedLayerIds.Count != 1
            || document is not { } owner || SelectedLayer is not { IsGroupOrFalse: false } layer
            || BlendCombo.SelectedItem is not ComboBoxItem { Tag: LayerBlendMode mode }) return;
        if (blendPickerSession is { } session && (!ReferenceEquals(session.Owner, owner) || session.Id != layer.Id)) return;
        EndOpacityEdit(); RetireBlendPreview(redraw: false);
        MarkLayerBandDirty(layer.Id);
        applyingBlendMode = true;
        try
        {
            if (!owner.SetBlendMode(layer.Id, mode)) PumpRender();
        }
        finally { applyingBlendMode = false; }
    }
}
