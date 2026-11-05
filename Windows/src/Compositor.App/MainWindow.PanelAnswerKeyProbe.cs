#if DEBUG
using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyPanelAnswerKeysAsync(Dictionary<string, object> report)
    {
        var saved = shortcutChords.ToDictionary(entry => entry.Key, entry => entry.Value);
        try
        {
            shortcutChords["canvas:ApplyOperation"] = new(VirtualKey.Enter, 0);
            shortcutChords["canvas:CancelOperation"] = new(VirtualKey.Escape, 0);
            var scene = LensCorrectionScene();
            OpenDocument(scene); await WaitForFramesAsync();
            var id = scene.Snapshot.Manifest.ActiveLayerId!.Value;
            var capture = scene.Snapshot.Images[id].Clone();
            OnLensCorrection(this, new RoutedEventArgs());
            var slider = SliderNamed(FloatingPanelBody("lensCorrection")!, Loc.T("L_RemoveDistortion"))!;
            slider.Value = 100;
            Require(await WaitUntilAsync(() => lensPreview is { Busy: false, Queued: false }, 8000), "Key fixture preview did not settle.");
            Require(!HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Enter, VirtualKeyModifiers.Control), slider)
                    && !HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Enter, 0), RootGrid),
                "A modified or outside-panel key reached the answer row.");
            Require(HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Escape, 0), slider),
                "Escape did not reach the open panel's Cancel action.");
            Require(await WaitUntilAsync(() => lensPreview is null, 8000), "Escape did not finish Cancel.");
            Require(capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && !scene.CanUndo,
                "Escape left filter pixels or history behind.");

            OnLensCorrection(this, new RoutedEventArgs());
            slider = SliderNamed(FloatingPanelBody("lensCorrection")!, Loc.T("L_RemoveDistortion"))!;
            slider.Value = 100;
            Require(await WaitUntilAsync(() => lensPreview is { Busy: false, Queued: false }, 8000), "Second key fixture did not settle.");
            SetFloatingPanelConfirmEnabled("lensCorrection", false);
            Require(HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Enter, 0), slider)
                    && FloatingPanelIsOpen("lensCorrection"), "Enter bypassed a disabled confirmation.");
            SetFloatingPanelConfirmEnabled("lensCorrection", true);
            var panel = floatingPanels["lensCorrection"];
            var finish = panel.Finish!;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            panel.Finish = async commit =>
            {
                calls++;
                try { await release.Task; await finish(commit); }
                finally { done.TrySetResult(); }
            };
            try
            {
                Require(HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Enter, 0), slider), "Enter did not confirm the filter.");
                Require(HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Escape, 0), slider)
                        && HandleFloatingPanelAnswerKey("lensCorrection", new(VirtualKey.Enter, 0), slider)
                        && calls == 1 && FloatingPanelIsOpen("lensCorrection"), "Answer keys interrupted or repeated a pending commit.");
            }
            finally { release.TrySetResult(); await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            Require(await WaitUntilAsync(() => lensPreview is null && !FloatingPanelIsOpen("lensCorrection"), 8000), "Enter commit did not finish.");
            Require(!capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && scene.Undo()
                    && capture.Pixels.AsSpan().SequenceEqual(scene.Snapshot.Images[id].Pixels) && !scene.CanUndo,
                "Enter did not apply a single undoable filter.");
            report["panelAnswerKeysApplyAndCancel"] = "passed";

            var answers = new List<bool>();
            const string key = "answerKeysProbe";
            (TextBox Text, TextBox Multiline, ComboBox Combo, Slider Slider) Open(FloatingPanelAnswers kind = FloatingPanelAnswers.OkCancel)
            {
                var text = new TextBox { Text = "draft" };
                var multiline = new TextBox { Text = "two lines", AcceptsReturn = true };
                var combo = new ComboBox { Items = { "One", "Two" }, SelectedIndex = 0 };
                var control = new Slider { Minimum = 0, Maximum = 10, Value = 5 };
                var body = new StackPanel { Children = { text, multiline, combo, control } };
                ShowFloatingPanel(key, "Answer key probe", body, commit => { answers.Add(commit); return Task.CompletedTask; }, answers: kind);
                // Canvas idleness does not lay out a newly opened XAML panel. A real key cannot originate
                // from its slider until that slider has entered the visual tree.
                RootGrid.UpdateLayout();
                Require(control.ActualWidth > 0 && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(control) is not null,
                    "The answer-key fixture did not lay out its input control.");
                return (text, multiline, combo, control);
            }
            var fields = Open(); await WaitForFramesAsync();
            Require(!HandleFloatingPanelAnswerKey(key, new(VirtualKey.Enter, 0), fields.Multiline)
                    && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.Tab, 0), fields.Text)
                    && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.Space, 0), fields.Slider),
                "Panel answers swallowed a multiline newline or a field/navigation key.");
            fields.Combo.IsDropDownOpen = true; await WaitForFramesAsync();
            Require(fields.Combo.IsDropDownOpen && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.Escape, 0), fields.Combo),
                "Escape bypassed the open picker instead of belonging to it.");
            fields.Combo.IsDropDownOpen = false;
            shortcutChords["canvas:ApplyOperation"] = new(VirtualKey.O, 0);
            shortcutChords["canvas:CancelOperation"] = new(VirtualKey.P, 0);
            Require(!HandleFloatingPanelAnswerKey(key, new(VirtualKey.Enter, 0), fields.Slider)
                    && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.Escape, 0), fields.Slider),
                "The old answer keys remained active after rebinding.");
            Require(!HandleFloatingPanelAnswerKey(key, new(VirtualKey.O, 0), fields.Text)
                    && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.P, 0), fields.Text),
                "A rebound letter interrupted text entry.");
            Require(HandleFloatingPanelAnswerKey(key, new(VirtualKey.O, 0), fields.Slider)
                    && answers.SequenceEqual(new[] { true }), "The rebound Apply key did not confirm.");
            var oldSource = fields.Slider;
            fields = Open(); await WaitForFramesAsync();
            Require(!HandleFloatingPanelAnswerKey(key, new(VirtualKey.P, 0), oldSource), "A retired control answered a replacement panel.");
            bool reboundCancelHandled = HandleFloatingPanelAnswerKey(key, new(VirtualKey.P, 0), fields.Slider);
            report["reboundPanelAnswerTrace"] = new { reboundCancelHandled, answers = answers.ToArray(),
                cancelChord = CanvasChord(shortcutChords, "canvas:CancelOperation").Store,
                stillOpen = FloatingPanelIsOpen(key) };
            Require(reboundCancelHandled
                    && answers.SequenceEqual(new[] { true, false }), "The rebound Cancel key did not cancel.");
            report["panelAnswerKeysRespectFieldsAndBindings"] = "passed";

            fields = Open(FloatingPanelAnswers.SaveCancel); await WaitForFramesAsync();
            Require(!HandleFloatingPanelAnswerKey(key, new(VirtualKey.O, 0), fields.Slider)
                    && !HandleFloatingPanelAnswerKey(key, new(VirtualKey.P, 0), fields.Slider),
                "The shortcut editor's fixed answers followed the draft bindings.");
            Require(HandleFloatingPanelAnswerKey(key, new(VirtualKey.Enter, 0), fields.Slider), "The fixed Return key did not save.");
            fields = Open(FloatingPanelAnswers.SaveCancel); await WaitForFramesAsync();
            Require(HandleFloatingPanelAnswerKey(key, new(VirtualKey.Escape, 0), fields.Slider)
                    && answers.SequenceEqual(new[] { true, false, true, false }), "The fixed Escape key did not cancel.");
            report["shortcutEditorAnswerKeysStayFixed"] = "passed";
        }
        finally
        {
            AbandonFloatingPanel("answerKeysProbe");
            shortcutChords.Clear();
            foreach (var pair in saved) shortcutChords.Add(pair.Key, pair.Value);
        }
    }
}
#endif
