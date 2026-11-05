#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    /// <summary>
    /// Layer ▸ Rename Layer… — the keyboard's way into a row's rename field, which the panel previously only
    /// reached by double-clicking it (the Mac has both: CompositorApp.swift:318 and the row menu's
    /// renameLayerAction, and it scrolls the row into view first, NativeLayerList.swift:98-99).
    ///
    /// What this cannot drive is the Enter and Escape keys inside the field: a probe does not fabricate
    /// <see cref="KeyRoutedEventArgs"/>, so the commit runs through the other real exit the editor has —
    /// losing focus — and the Escape branch stays covered only by reading it.
    /// </summary>
    private async Task VerifyLayerRenameAsync(Dictionary<string, object> report)
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        var scene = EditorDocument.Create(64, 48);
        var top = scene.ImportImage(new Surface(16, 16), "Top plate");
        OpenDocument(scene); await WaitForFramesAsync();

        SelectLayerRow(top.Id);
        RenameSelectedLayer();
        Require(renamingLayer, "the rename command opened no editor in the row.");
        var row = (LayerRow)layerItems.Single(grid => ((LayerRow)grid.Tag).Layer.Id == top.Id).Tag;
        var editor = row.Label.Parent is Grid host
            ? host.Children.OfType<TextBox>().SingleOrDefault()
            : null;
        Require(editor is not null, "the row's name did not give way to a text field.");
        Require(editor!.Text == "Top plate" && row.Label.Visibility == Visibility.Collapsed,
            $"the field started from {editor.Text} instead of the layer's own name.");
        Require(await WaitUntilAsync(() => VisualTreeHelper.GetParent(editor) is not null && editor.ActualHeight > 0, 2000),
            "the rename field was never laid out, so the command had nothing to type into.");
        Require(ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), editor),
            $"focus went to {FocusManager.GetFocusedElement(Content.XamlRoot)?.GetType().Name ?? "nothing"} instead of the rename field.");
        // A second invocation must not stack a second field on the same row — the menu is reachable twice.
        RenameSelectedLayer();
        Require(row.Label.Parent is Grid again && again.Children.OfType<TextBox>().Count() == 1,
            "invoking the command twice put a second rename field in the same row.");
        report["theRenameCommandOpensTheRowsOwnField"] = "passed";

        // What this cannot claim: the two exits that finish a rename. Enter and Escape need a fabricated
        // KeyRoutedEventArgs, which no probe here does; losing focus was tried and measured — the field really
        // did lose focus (asserted by identity above and again below) and the row stayed in rename mode, so in
        // a window the OS has not activated the LostFocus handler never runs. Both exits stay a manual check.
        Require(BlendCombo.Focus(FocusState.Programmatic) && !ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), editor),
            "focus could not be moved off the field at all, so the row below cannot be laid out either.");

        // Out of rename mode: neither real exit is drivable here, and a row left renaming would freeze the
        // panel rebuild for anything that runs after this group.
        renamingLayer = false;
    }
}
#endif
