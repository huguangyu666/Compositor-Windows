using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<LayerLocks, ToggleButton> layerLockButtons = [];
    private bool syncingLayerLocks;
    private bool changingLayerLocks;
    private TextBlock layerLockHint = null!;

    private void InitializeLayerProtection()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        bar.Children.Add(new TextBlock { Text = Loc.T("LP_Lock"), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) });
        foreach (var (flag, key, glyph) in new[] { (LayerLocks.Transparency, "LP_Transparency", "\uE739"),
            (LayerLocks.Pixels, "LP_Pixels", "\uE790"), (LayerLocks.Position, "LP_Position", "\uE759"), (LayerLocks.All, "LP_All", "\uE72E") })
        {
            var button = NameQuickControl(new ToggleButton { Width = 32, Height = 30, MinWidth = 0, Padding = new(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 14 } }, "LayerLock" + flag, key);
            button.Checked += (_, _) => ChangeLayerLock(flag, true);
            button.Unchecked += (_, _) => ChangeLayerLock(flag, false);
            layerLockButtons[flag] = button; bar.Children.Add(button);
        }
        layerLockHint = new TextBlock { FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        LayerAppearance.Children.Add(bar); LayerAppearance.Children.Add(layerLockHint);
        CanvasStage.ContextRequested += (_, args) =>
        {
            args.Handled = true;
            if (document is null || !CanSelectLayerTarget || ProjectEditsInFlight) return;
            var at = args.TryGetPosition(CanvasStage, out var position) ? position : new Windows.Foundation.Point(CanvasStage.ActualWidth / 2, CanvasStage.ActualHeight / 2);
            BuildCanvasLayerPicker(viewport.ToDocument(new(at.X, at.Y))).ShowAt(CanvasStage, new FlyoutShowOptions { Position = at });
        };
    }

    private void LoadLayerProtection(EditorDocument owner)
    {
        if (owner.LayerLockSettings.Count != 0) return;
        string key = "layer-locks-" + owner.Snapshot.Manifest.DocumentId.ToString("N");
        try
        {
            var path = Path.Combine(PreferencesDirectory, key + ".txt");
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return;
            var saved = JsonSerializer.Deserialize<Dictionary<Guid, LayerLocks>>(File.ReadAllText(path));
            if (saved is not null) foreach (var pair in saved.Take(10_000)) owner.SetLayerLocks(pair.Key, pair.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { App.Log("Layer protection load failed: " + ex.Message); }
    }

    private void OnLayerProtectionChanged()
    {
        if (changingLayerLocks || document is not { } owner) return;
        WriteText("layer-locks-" + owner.Snapshot.Manifest.DocumentId.ToString("N"), JsonSerializer.Serialize(owner.LayerLockSettings));
        transformAvailability = null;
        RefreshLayerPanel(); RefreshTransformInspector(); UpdateTransformHandles();
    }

    private async void ChangeLayerLock(LayerLocks flag, bool locked)
    {
        if (syncingLayerLocks || document is not { } owner || selectedLayerIds.Count == 0) return;
        var ids = selectedLayerIds.ToArray();
        CommitInlineText(); await FinishTransformBeforeOutputAsync();
        if (!ReferenceEquals(document, owner) || !selectedLayerIds.SetEquals(ids) || !CanEditSelectionState) { RefreshLayerProtection(); return; }
        changingLayerLocks = true;
        try
        {
            foreach (var id in ids)
            {
                var old = owner.GetLayerLocks(id, inherited: false);
                owner.SetLayerLocks(id, locked ? old | flag : old & ~flag);
            }
        }
        finally { changingLayerLocks = false; }
        OnLayerProtectionChanged();
    }

    private void RefreshLayerProtection()
    {
        if (layerLockButtons.Count == 0) return;
        var owner = document; var ids = selectedLayerIds.ToArray();
        syncingLayerLocks = true;
        try
        {
            foreach (var (flag, button) in layerLockButtons)
            {
                int count = owner is null ? 0 : ids.Count(id => (owner.GetLayerLocks(id) & (flag | LayerLocks.All)) != 0);
                button.IsChecked = count == 0 ? false : count == ids.Length ? true : null;
                button.IsEnabled = owner is not null && ids.Length > 0 && CanEditSelectionState
                    && (flag != LayerLocks.Transparency || ids.All(id => owner.Snapshot.Manifest.Layers.Any(l => l.Id == id && !l.IsGroupOrFalse && l.Adjustment is null)));
            }
            bool inherited = owner is not null && ids.Any(id => owner.GetLayerLocks(id) != owner.GetLayerLocks(id, inherited: false));
            layerLockHint.Text = Loc.T(inherited ? "LP_Inherited" : "LP_Local");
            layerLockHint.Visibility = owner is not null && ids.Any(id => owner.GetLayerLocks(id) != LayerLocks.None) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { syncingLayerLocks = false; }
    }

    private MenuFlyout BuildCanvasLayerPicker(PointD point)
    {
        var menu = new MenuFlyout();
        if (document is not { } owner) return menu;
        var source = owner.Snapshot; TrackEditorDocument(owner); long revision = editorRevision;
        var hits = TransformPress.LayersAt(source, point);
        var byId = source.Manifest.Layers.ToDictionary(l => l.Id);
        if (hits.Count == 0) { menu.Items.Add(new MenuFlyoutItem { Text = Loc.T("LP_NoHits"), IsEnabled = false }); return menu; }
        foreach (var id in hits.Take(100))
        {
            var layer = byId[id]; string name = layer.ParentId is { } parent && byId.TryGetValue(parent, out var folder) ? layer.Name + " — " + folder.Name : layer.Name;
            if (owner.GetLayerLocks(id) != LayerLocks.None) name = "\U0001F512 " + name;
            var entry = new MenuFlyoutItem { Name = "CanvasLayer_" + id.ToString("N"), Text = name };
            if (selectedLayerIds.Contains(id)) entry.Icon = new FontIcon { Glyph = "\uE73E" };
            entry.Click += async (_, _) =>
            {
                if (windowClosed || !ReferenceEquals(document, owner) || !ReferenceEquals(owner.Snapshot, source) || editorRevision != revision) return;
                await PrepareLayerContextAsync(id, mask: false);
            };
            menu.Items.Add(entry);
        }
        if (hits.Count > 100) menu.Items.Add(new MenuFlyoutItem { Text = Loc.F("LP_MoreHits", hits.Count - 100), IsEnabled = false });
        return menu;
    }
}
