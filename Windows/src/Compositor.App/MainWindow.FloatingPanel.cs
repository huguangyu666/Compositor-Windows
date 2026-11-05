using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Compositor.App;

/// <summary>
/// The window's non-modal panel host (#37), the counterpart of the Mac's `FloatingPanelController`:
/// `filterPanel`, `effectsPanel`, `colorPickerPanel`, `colorRangePanel` and `keyboardShortcuts` are floating
/// panels there, which is why a live preview is worth anything — the canvas stays visible and stays workable
/// underneath the sliders.
///
/// A <see cref="ContentDialog"/> cannot do that job: it dims the window and holds the pointer, so the picture
/// the panel exists to show sits behind a mask. Being inside the window tree costs one thing the dialog gave
/// for free — keystrokes reach the canvas as well — and that is what <see cref="FloatingPanelHoldsFocus"/> is
/// for: while one of these panels has the focus, the canvas's own key handling stands down, exactly as the
/// responder-chain guard in <see cref="TypingInField"/> already stands down for a text field.
///
/// Several panels may be open at once, as on the Mac. Each answers to a key, and opening a key that is already
/// open closes the first rather than stacking copies — the Mac has one controller per panel for the same reason.
/// </summary>
/// <summary>Where a panel opens, named for the Mac's `FloatingPanelPlacement` (FloatingPanel.swift:5-10):
/// either the one a person can drag anywhere, or the one that lives against the window's right edge.</summary>
internal enum FloatingPanelPlacement
{
    Automatic,
    DockedToMainWindowRight,
}

internal enum FloatingPanelAnswers
{
    OkCancel,
    SaveCancel,
}

public sealed partial class MainWindow
{
    private sealed class FloatingPanel
    {
        public required string Key;
        public required Border Root;
        public required StackPanel Body;
        /// <summary>Which host grid this panel's root sits in. Docked panels live in the one that spans the
        /// layers pane, so taking one down has to ask the panel where it is rather than assume.</summary>
        public required Grid Host;
        public bool Docked;
        public FloatingPanelAnswers Answers;
        public bool KeepOpenWhileFinishing;
        public bool Finishing;
        public required ScrollViewer Editor;
        public required Button Dismiss;
        public required StackPanel Buttons;
        public required StackPanel Progress;
        public required ProgressRing Spinner;
        public Func<bool, Task>? Finish;
        /// <summary>What to do when the panel is taken down without an answer — a replaced document, a closed
        /// window. A prompt waiting on a dialog result has nobody to answer it then, and an owner that never
        /// resolves it leaves its caller hung for the rest of the session.</summary>
        public Action? OnAbandon;
        public required Button Confirm;
        public required Button Cancel;
        public double ShiftX, ShiftY;
        public bool Dragging;
        public Windows.Foundation.Point DragFrom;
    }

    private readonly Dictionary<string, FloatingPanel> floatingPanels = new();

    /// <summary>Opens a panel with a body somebody else built. Filter commits may keep the disabled panel visible
    /// while `finish` runs; other panels hand their answer back once the panel is out of the way:
    /// false for Cancel (the session hands back what it touched), true for OK (the session keeps it) — the same
    /// two answers the ContentDialog's Primary/Close buttons gave.</summary>
    private void ShowFloatingPanel(string key, string title, UIElement body, Func<bool, Task> finish, Action? onAbandon = null,
        (string Label, Action Action)? extra = null, FloatingPanelPlacement placement = FloatingPanelPlacement.Automatic,
        bool keepOpenWhileFinishing = false, FloatingPanelAnswers answers = FloatingPanelAnswers.OkCancel)
    {
        if (FloatingPanelIsFinishing(key)) return;
        if (floatingPanels.ContainsKey(key)) CloseFloatingPanel(key, commit: false);
        var panel = BuildFloatingPanel(key, title, body, finish, extra, placement, answers);
        panel.KeepOpenWhileFinishing = keepOpenWhileFinishing;
        panel.OnAbandon = onAbandon;
        // Staggered so a second panel is visibly a second panel, as the Mac's cascade is. A docked panel keeps
        // no position at all: FloatingPanel.swift:100-103 refuses to remember one precisely so the next filter
        // that shares this controller does not open on the right edge.
        if (!panel.Docked)
        {
            panel.ShiftX = 26 * floatingPanels.Count;
            panel.ShiftY = 26 * floatingPanels.Count;
        }
        floatingPanels[key] = panel;
        panel.Host.Children.Add(panel.Root);
        PlaceFloatingPanel(panel);
    }

    private FloatingPanel BuildFloatingPanel(string key, string title, UIElement body, Func<bool, Task> finish,
        (string Label, Action Action)? extra = null, FloatingPanelPlacement placement = FloatingPanelPlacement.Automatic,
        FloatingPanelAnswers answers = FloatingPanelAnswers.OkCancel)
    {
        var docked = placement == FloatingPanelPlacement.DockedToMainWindowRight;
        var bodyHost = new StackPanel { Spacing = 8 };
        bodyHost.Children.Add(body);
        var bar = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 53, 53, 53)),
            Padding = new Thickness(8, 5, 4, 5),
        };
        bar.Children.Add(new TextBlock
        {
            Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        var dismiss = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 }, Width = 24, Height = 22, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dismiss, Loc.T("A_ClosePanel"));
        dismiss.Click += (_, _) => CloseFloatingPanel(key, commit: false);
        bar.Children.Add(dismiss);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(10, 4, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = Loc.T("Dlg_SheetCancel") };
        cancel.Click += (_, _) => CloseFloatingPanel(key, commit: false);
        var confirm = new Button { Content = Loc.T(answers == FloatingPanelAnswers.SaveCancel ? "Dlg_Save" : "Dlg_OK") };
        confirm.Click += (_, _) => CloseFloatingPanel(key, commit: true);
        var spinner = new ProgressRing { Width = 16, Height = 16, IsActive = false };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(spinner, Loc.T("S_FilterApplying"));
        var progress = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        progress.Children.Add(spinner);
        progress.Children.Add(new TextBlock { Text = Loc.T("S_FilterApplying"), VerticalAlignment = VerticalAlignment.Center });
        buttons.Children.Add(progress);
        // The Mac's shortcut sheet keeps Restore Defaults in the same row as Cancel and Save, on the left;
        // a panel that needs a third answer says so, instead of the host inventing one for everybody.
        if (extra is { } third)
        {
            var extraButton = new Button { Content = third.Label };
            extraButton.Click += (_, _) => third.Action();
            buttons.Children.Add(extraButton);
        }
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(bar);
        var editor = new ScrollViewer
        {
            // A docked panel fills the window instead of capping itself: the Mac's host is told to stretch both
            // ways (FloatingPanel.swift:46-51) because a scroll view that measures zero must not collapse the
            // grading wheels it carries.
            MaxHeight = docked ? double.PositiveInfinity : 512,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(10, 4, 10, 4),
            Content = bodyHost,
            VerticalAlignment = docked ? VerticalAlignment.Stretch : VerticalAlignment.Top,
        };
        content.Children.Add(editor);
        content.Children.Add(buttons);
        var panel = new FloatingPanel
        {
            Key = key, Body = bodyHost, Finish = finish, Confirm = confirm, Cancel = cancel, OnAbandon = null,
            Editor = editor, Dismiss = dismiss, Buttons = buttons, Progress = progress, Spinner = spinner,
            Root = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(244, 40, 40, 40)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 95, 95, 95)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                // 440 is the Mac's own docked width, and it was reached from the inside: the three grading
                // wheels are the widest thing the panel holds, and FloatingPanel.swift:123-125 narrows those to
                // fit the dock rather than widening the dock to fit them.
                Width = docked ? 440 : 330,
                MaxHeight = docked ? double.PositiveInfinity : 640,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = docked ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                Margin = docked ? new Thickness(0) : new Thickness(0, 10, 12, 10),
                RenderTransform = new TranslateTransform(), Child = content, Tag = key,
            },
            Host = docked ? DockedPanelHosts : FloatingPanelHosts,
            Docked = docked,
            Answers = answers,
        };
        // Bubble after the field handles its editing keys. The shortcut recorder tunnels first and marks
        // its own keys handled, so recording Enter or Escape cannot also answer the sheet.
        panel.Root.KeyDown += (_, args) =>
        {
            if (args.Handled) return;
            var modifiers = (KeyHeld(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0)
                | (KeyHeld(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0)
                | (KeyHeld(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : 0)
                | (KeyHeld(VirtualKey.LeftWindows) || KeyHeld(VirtualKey.RightWindows) ? VirtualKeyModifiers.Windows : 0);
            if (HandleFloatingPanelAnswerKey(key, new(args.Key, modifiers), args.OriginalSource as DependencyObject))
                args.Handled = true;
        };
        // Dragging by the bar, as the Mac's floating panels move. The bar is the only part of the panel that
        // takes the pointer for itself: everywhere else the panel's own controls answer. A docked panel is
        // placed, not parked — its frame is copied from the window every time (FloatingPanel.swift:127-136),
        // so a drag would be undone by the next resize and the Mac does not offer one.
        if (docked) return panel;
        bar.PointerPressed += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
            panel.Dragging = bar.CapturePointer(args.Pointer);
            panel.DragFrom = args.GetCurrentPoint(panel.Root).Position;
        };
        bar.PointerMoved += (_, args) =>
        {
            if (!panel.Dragging) return;
            var point = args.GetCurrentPoint(panel.Root).Position;
            panel.ShiftX += point.X - panel.DragFrom.X;
            panel.ShiftY += point.Y - panel.DragFrom.Y;
            panel.DragFrom = point;
            PlaceFloatingPanel(panel);
        };
        bar.PointerReleased += (_, args) =>
        {
            if (!panel.Dragging) return;
            panel.Dragging = false;
            bar.ReleasePointerCapture(args.Pointer);
        };
        return panel;
    }

    private static void PlaceFloatingPanel(FloatingPanel panel)
    {
        if (panel.Root.RenderTransform is TranslateTransform shift)
        {
            shift.X = panel.ShiftX;
            shift.Y = panel.ShiftY;
        }
    }

    /// <summary>Closes a panel and asks its owner what to do about the work — that is all the two buttons and
    /// the cross do. A panel nobody opened is a no-op, so "close whatever this document owned" is one call per
    /// key. The owner's answer is awaited here rather than dropped: a panel closing is exactly when its session
    /// finishes writing, and an exception from that must not be swallowed by nobody holding the task.</summary>
    private async void CloseFloatingPanel(string key, bool commit)
    {
        if (!floatingPanels.TryGetValue(key, out var panel) || panel.Finishing) return;
        var finish = panel.Finish;
        panel.Finish = null;
        bool retained = commit && panel.KeepOpenWhileFinishing;
        if (retained)
        {
            panel.Finishing = true;
            panel.Editor.IsEnabled = false;
            panel.Dismiss.IsEnabled = false;
            foreach (var button in panel.Buttons.Children.OfType<Button>()) button.IsEnabled = false;
            panel.Progress.Visibility = Visibility.Visible;
            panel.Spinner.IsActive = true;
        }
        else RemoveFloatingPanel(panel);
        if (finish is null) return;
        try { await finish(commit); }
        catch (Exception ex) { App.Log($"floating panel {key} close failed: {ex}", LogLevel.Error); }
        finally { if (retained) RemoveFloatingPanel(panel); }
    }

    private void RemoveFloatingPanel(FloatingPanel panel)
    {
        panel.Spinner.IsActive = false;
        // A retired commit may finish after a replacement opened with the same key.
        if (!floatingPanels.TryGetValue(panel.Key, out var current) || !ReferenceEquals(current, panel)) return;
        floatingPanels.Remove(panel.Key);
        panel.Host.Children.Remove(panel.Root);
    }

    /// <summary>Takes a panel down without answering it, because its session has already been retired from
    /// under it — a replaced document, a closed window. Nothing is left to keep or hand back, and a panel on
    /// screen naming a layer of a document that no longer exists is worse than closing it silently.</summary>
    private void AbandonFloatingPanel(string key)
    {
        if (!floatingPanels.TryGetValue(key, out var panel)) return;
        panel.Finish = null;
        var onAbandon = panel.OnAbandon;
        panel.OnAbandon = null;
        RemoveFloatingPanel(panel);
        onAbandon?.Invoke();
    }

    /// <summary>Lets the owner put its own OK out of reach: the Mac's amount sheet disables it until the typed
    /// amount is a whole number inside the range (LassoControls.swift:228-232), which is the one judgement a
    /// dialog's PrimaryButton.IsEnabled used to make for it.</summary>
    internal bool FloatingPanelConfirmEnabled(string key) =>
        !floatingPanels.TryGetValue(key, out var panel) || panel.Confirm.IsEnabled;

    internal void SetFloatingPanelConfirmEnabled(string key, bool enabled)
    {
        if (floatingPanels.TryGetValue(key, out var panel)) panel.Confirm.IsEnabled = enabled;
    }

    internal bool FloatingPanelIsOpen(string key) => floatingPanels.ContainsKey(key);

    private bool FloatingPanelIsFinishing(string key) =>
        floatingPanels.TryGetValue(key, out var panel) && panel.Finishing;

    /// <summary>Shares the configured Apply/Cancel keys with the canvas, as configuredNativeShortcut does on
    /// the Mac. Save/Cancel is the shortcut editor's fixed Return/Escape pair. A field's own key wins first.</summary>
    private bool HandleFloatingPanelAnswerKey(string key, ShortcutChord input, DependencyObject? source)
    {
        if (!floatingPanels.TryGetValue(key, out var panel) || source is null) return false;
        bool inside = false;
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true }) return false;
            if (node is TextBox text && (input.Key is not (VirtualKey.Enter or VirtualKey.Escape)
                || (input.Key == VirtualKey.Enter && text.AcceptsReturn))) return false;
            if (ReferenceEquals(node, panel.Root)) { inside = true; break; }
        }
        if (!inside) return false;
        var apply = panel.Answers == FloatingPanelAnswers.SaveCancel ? new ShortcutChord(VirtualKey.Enter, 0)
            : CanvasChord(shortcutChords, "canvas:ApplyOperation");
        var cancel = panel.Answers == FloatingPanelAnswers.SaveCancel ? new ShortcutChord(VirtualKey.Escape, 0)
            : CanvasChord(shortcutChords, "canvas:CancelOperation");
        if (input != apply && input != cancel) return false;
        bool commit = input == apply;
        if (!panel.Finishing && (commit ? panel.Confirm : panel.Cancel).IsEnabled)
            CloseFloatingPanel(key, commit);
        return true;
    }

    /// <summary>The sheet's own answer row. A test drives the buttons a person sees rather than calling the
    /// owner's finish function, because which of the two closes the sheet is part of what is being claimed.</summary>
    internal Button? FloatingPanelButton(string key, bool confirm) =>
        floatingPanels.TryGetValue(key, out var panel) ? (confirm ? panel.Confirm : panel.Cancel) : null;

    /// <summary>The panel's own root, so an owner can put a key grab on the whole window rather than on one
    /// of its controls: the shortcut recorder has to see a chord before the focused row button does.</summary>
    internal FrameworkElement? FloatingPanelRoot(string key) =>
        floatingPanels.TryGetValue(key, out var panel) ? panel.Root : null;

    /// <summary>Puts the focus inside a panel, which is what the canvas's responder guard keys on. The host's
    /// own confirm button is the target: it is always there, always focusable, and no owner has to be asked to
    /// expose one of its controls for the sake of a test.</summary>
    internal bool FocusFloatingPanel(string key) =>
        floatingPanels.TryGetValue(key, out var panel) && panel.Confirm.Focus(FocusState.Programmatic);

    /// <summary>The body a probe drives — the object the owner handed in, not a copy of it, so a control read
    /// here is the control the user sees.</summary>
    internal UIElement? FloatingPanelBody(string key) =>
        floatingPanels.TryGetValue(key, out var panel) && panel.Body.Children.Count > 0 ? panel.Body.Children[0] : null;

    /// <summary>Whether the focus sits inside one of these panels, found by walking up from the focused element
    /// rather than by counting GotFocus/LostFocus: focus moving between two controls of the same panel raises a
    /// loss before the gain, and a counter would read that gap as "the canvas is the responder again".</summary>
    private bool FloatingPanelHoldsFocus
    {
        get
        {
            if (floatingPanels.Count == 0) return false;
            for (var node = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject; node is not null;)
            {
                if (node is FrameworkElement { Tag: string named } && floatingPanels.ContainsKey(named)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
