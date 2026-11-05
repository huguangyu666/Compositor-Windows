using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Compositor.App;

/// <summary>
/// Rebindable menu shortcuts, the counterpart of the Mac's <c>ShortcutSettings</c>
/// (KeyboardShortcuts.swift:130-214) and its editor sheet (:229-282).
///
/// The live <c>MenuItem.KeyboardAccelerators</c> collections stay the one fact, as they were for the
/// read-only table this replaces: the built-in chord of a command is
/// whatever that collection holds the first time the window is walked, an override is written back into
/// the same collection, and the menu, the help table and the editor therefore never disagree. Commands
/// bound in code because XAML cannot name the key go through the same walk, since
/// <see cref="AddCodeOnlyAccelerators"/> fills the same collections before this runs.
///
/// Canvas keys handled in KeyDown have no accelerator collection to rewrite, so they are remapped by
/// translation instead: <see cref="CanvasTranslate"/> hands a press to the matcher as the key that matcher
/// was written with, which keeps one table the source of truth for every chord on the canvas.
/// </summary>
public sealed partial class MainWindow
{
    private const string ShortcutsPreference = "keyboard-shortcuts";

    /// <summary>The canvas tool letters, in the order the Mac lists them. Their built-in chord is the bare
    /// key matched by <see cref="HandleWorkspaceToolKey"/>, and an override does not rewrite anything into
    /// the framework: the matcher asks <see cref="CanvasKeyFor"/> which key to treat the press as.</summary>
    internal static readonly (string Id, string TitleKey, VirtualKey Key, CanvasTool Tool)[] CanvasToolKeys =
    [
        ("canvas:Tool_Move", "Tool_Move", VirtualKey.V, CanvasTool.Move),
        ("canvas:Tool_RectSelect", "Tool_RectSelect", VirtualKey.M, CanvasTool.RectSelect),
        ("canvas:Tool_Wand", "Tool_Wand", VirtualKey.W, CanvasTool.Wand),
        ("canvas:Tool_Crop", "Tool_Crop", VirtualKey.C, CanvasTool.Crop),
        ("canvas:Tool_Brush", "Tool_Brush", VirtualKey.B, CanvasTool.Brush),
        ("canvas:Tool_Eraser", "Tool_Eraser", VirtualKey.E, CanvasTool.Eraser),
        ("canvas:Tool_Clone", "Tool_Clone", VirtualKey.S, CanvasTool.Clone),
        ("canvas:Tool_Blur", "Tool_Blur", VirtualKey.R, CanvasTool.Blur),
        ("canvas:Tool_Text", "Tool_Text", VirtualKey.T, CanvasTool.Text),
        ("canvas:Tool_Shape", "Tool_Shape", VirtualKey.U, CanvasTool.Shape),
        ("canvas:Tool_Eyedropper", "Tool_Eyedropper", VirtualKey.I, CanvasTool.Eyedropper),
        ("canvas:Tool_Hand", "Tool_Hand", VirtualKey.H, CanvasTool.Hand),
        ("canvas:Tool_Zoom", "Tool_Zoom", VirtualKey.Z, CanvasTool.Zoom),
        // These three used to be selected inside their own tool handlers. Keeping the letter there meant a
        // rebind had to be honoured in four places, so they joined the table and the handlers kept only the
        // keys that mean something while that tool is already active.
        ("canvas:Tool_Lasso", "Tool_Lasso", VirtualKey.L, CanvasTool.Lasso),
        ("canvas:Tool_Gradient", "Tool_Gradient", VirtualKey.G, CanvasTool.Gradient),
        ("canvas:Tool_Healing", "Tool_Healing", VirtualKey.J, CanvasTool.Healing),
    ];
    private readonly Dictionary<string, List<ShortcutChord>> shortcutBuiltins = new();
    private readonly Dictionary<string, ShortcutChord> shortcutChords = new();
    private readonly HashSet<string> shortcutApplied = new();

    /// <summary>Every menu command that carries a chord, in menu order, keyed by its resource identity —
    /// the <c>x:Uid</c> the label is loaded from, which a language switch does not move.</summary>
    private List<(string Id, string Title, string Group, MenuFlyoutItemBase Item)> RemappableShortcuts()
    {
        var rows = new List<(string Id, string Title, string Group, MenuFlyoutItemBase Item)>();
        foreach (var bar in MenuBarRoot.Items.OfType<MenuBarItem>())
            foreach (var item in bar.Items.OfType<MenuFlyoutItemBase>())
            {
                if (item.KeyboardAccelerators.Count == 0) continue;
                if (item is not MenuFlyoutItem command || string.IsNullOrEmpty(command.Text)) continue;
                // The name is the identity: it is what a stored line says, it does not move when the label
                // is translated, and the walk above refuses to list a command that has none.
                string id = command.Name;
                if (string.IsNullOrEmpty(id)) continue;
                rows.Add((id, command.Text, bar.Title, item));
            }
        return rows;
    }

    /// <summary>Records the built-in chord of every command. Called once, after the code-only accelerators
    /// have been added and before any stored override is applied.</summary>
    private void CaptureBuiltinShortcuts()
    {
        shortcutBuiltins.Clear();
        foreach (var (id, _, _, item) in RemappableShortcuts())
            shortcutBuiltins[id] = item.KeyboardAccelerators.Select(chord => new ShortcutChord(chord.Key, chord.Modifiers)).ToList();
    }

    /// <summary>The chords a command answers to right now, read off the live menu item itself.</summary>
    private List<ShortcutChord> LiveChords(string id) =>
        RemappableShortcuts().Where(row => row.Id == id).SelectMany(row =>
            row.Item.KeyboardAccelerators.Select(each => new ShortcutChord(each.Key, each.Modifiers))).ToList();

    private ShortcutChord DefaultChord(string id) =>
        shortcutBuiltins.TryGetValue(id, out var chords) && chords.Count > 0 ? chords[0] : ShortcutChord.Empty;

    /// <summary>The arrow nudges, the Mac's sixteen rows (KeyboardShortcuts.swift:115-118). Rebinding one works
    /// because the matchers read the <em>translated</em> chord: binding "nudge left" to a free letter makes that
    /// letter the nudge and leaves the arrow key swallowed. What a mask constrains is the modifiers a boundary
    /// can see — the outline treats Control as "not for me", the pixel move requires it — never which key may
    /// carry the command.</summary>
    internal static readonly (string Id, string TitleKey, string DirectionKey, int Pixels, VirtualKey Key,
        VirtualKeyModifiers Default, VirtualKeyModifiers Allowed)[] CanvasArrowKeys = BuildArrowRows();

    static (string Id, string TitleKey, string DirectionKey, int Pixels, VirtualKey Key, VirtualKeyModifiers Default,
        VirtualKeyModifiers Allowed)[] BuildArrowRows()
    {
        var rows = new List<(string Id, string TitleKey, string DirectionKey, int Pixels, VirtualKey Key,
            VirtualKeyModifiers Default, VirtualKeyModifiers Allowed)>();
        foreach (var (direction, key) in new[]
        {
            ("SC_Left", VirtualKey.Left), ("SC_Right", VirtualKey.Right), ("SC_Up", VirtualKey.Up), ("SC_Down", VirtualKey.Down),
        })
            foreach (int step in new[] { 1, 10 })
            {
                var shift = step == 10 ? VirtualKeyModifiers.Shift : (VirtualKeyModifiers)0;
                rows.Add(($"canvas:NudgeOutline.{direction}.{step}", "SC_ArrowNudgeOutline", direction, step, key,
                    shift, VirtualKeyModifiers.Shift));
                rows.Add(($"canvas:MovePixels.{direction}.{step}", "SC_ArrowMovePixels", direction, step, key,
                    VirtualKeyModifiers.Control | shift, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
            }
        return rows.ToArray();
    }

    /// <summary>The spacing chords the inline text editor answers: the Mac's eight "Text Editing" rows
    /// (KeyboardShortcuts.swift:119-124) — Option with an arrow, and Option+Shift for the step of ten. Left and
    /// right take the tracking, up and down the leading. What a mask constrains here is again what the boundary
    /// can see: the text handler reads Alt and Shift and nothing else on these keys.</summary>
    internal static readonly (string Id, string TitleKey, int Pixels, VirtualKey Key, VirtualKeyModifiers Default,
        VirtualKeyModifiers Allowed)[] CanvasSpacingKeys = BuildSpacingRows();

    static (string Id, string TitleKey, int Pixels, VirtualKey Key, VirtualKeyModifiers Default,
        VirtualKeyModifiers Allowed)[] BuildSpacingRows()
    {
        var rows = new List<(string, string, int, VirtualKey, VirtualKeyModifiers, VirtualKeyModifiers)>();
        foreach (var (id, titleKey, key) in new[]
        {
            ("TrackingDecrease", "SC_TrackingDecrease", VirtualKey.Left),
            ("TrackingIncrease", "SC_TrackingIncrease", VirtualKey.Right),
            ("LeadingDecrease", "SC_LeadingDecrease", VirtualKey.Up),
            ("LeadingIncrease", "SC_LeadingIncrease", VirtualKey.Down),
        })
            foreach (int step in new[] { 1, 10 })
                rows.Add(($"canvas:Spacing.{id}.{step}", titleKey, step, key,
                    step == 10 ? VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift : VirtualKeyModifiers.Menu,
                    VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift));
        return rows.ToArray();
    }

    /// <summary>The brush tip brackets. The tail of <see cref="OnContentKeyDown"/> reads these two keys plus
    /// Alt, so Alt is the only modifier a bracket chord can carry and still mean what it says; Control belongs
    /// to the layer step (C05c) and Shift is not read here at all.</summary>
    internal static readonly (string Id, string TitleKey, VirtualKey Key, VirtualKeyModifiers Default)[] CanvasBracketKeys =
    [
        ("canvas:BrushSizeDecrease", "SC_BrushSizeDecrease", (VirtualKey)ShortcutVK.OpenBracketCode, 0),
        ("canvas:BrushSizeIncrease", "SC_BrushSizeIncrease", (VirtualKey)ShortcutVK.CloseBracketCode, 0),
        ("canvas:BrushHardnessDecrease", "SC_BrushHardnessDecrease", (VirtualKey)ShortcutVK.OpenBracketCode,
            VirtualKeyModifiers.Menu),
        ("canvas:BrushHardnessIncrease", "SC_BrushHardnessIncrease", (VirtualKey)ShortcutVK.CloseBracketCode,
            VirtualKeyModifiers.Menu),
    ];

    /// <summary>The palette's two modifier-less chords. They live on the root grid as
    /// <c>KeyboardAccelerator</c> objects that carry their own <c>Invoked</c> handler, so a rebind rewrites
    /// those objects in place instead of translating a press: the framework resolves them before the canvas
    /// ever sees the key, which is also why <see cref="TypingInField"/> exists.</summary>
    internal static readonly (string Id, string TitleKey, VirtualKey Key)[] PaletteKeys =
    [
        ("palette:SwapColors", "SC_SwapColors", VirtualKey.X),
        ("palette:ResetColors", "SC_ResetColors", VirtualKey.D),
    ];

    /// <summary>The keys that finish or throw away whatever is happening on the canvas. Every matcher below
    /// reads the key alone and ignores the modifier state, so these rows allow no modifier at all: a chord
    /// carrying one could not be told apart from the bare key by the code that decides whether to apply a
    /// lasso. <c>Alias</c> is a second physical key that means the same command only while nobody has moved
    /// it — Backspace and Delete are one command on the Mac (KeyboardShortcuts.swift:104) and two keys here.</summary>
    internal static readonly (string Id, string TitleKey, VirtualKey Key, VirtualKey Alias)[] CanvasStructuralKeys =
    [
        ("canvas:ApplyOperation", "SC_ApplyOperation", VirtualKey.Enter, (VirtualKey)0),
        ("canvas:CancelOperation", "SC_CancelOperation", VirtualKey.Escape, (VirtualKey)0),
        ("canvas:TemporaryPan", "SC_TemporaryPan", VirtualKey.Space, (VirtualKey)0),
        ("canvas:DeleteTarget", "SC_DeleteTarget", VirtualKey.Delete, VirtualKey.Back),
    ];

    /// <summary>The ten opacity digits, the Mac's "Opacity digit N (type two for exact %)" rows
    /// (KeyboardShortcuts.swift:119). The shared handler reads the bare key and ignores Shift; other
    /// modifiers are refused by its guard, so like the structural keys these can only ever
    /// be bound to another unmodified key.</summary>
    internal static readonly (string Id, string TitleKey, int Digit, VirtualKey Key)[] CanvasOpacityDigits =
        Enumerable.Range(0, 10).Select(digit => ($"canvas:OpacityDigit.{digit}", "SC_OpacityDigit", digit,
            VirtualKey.Number0 + digit)).ToArray();

    /// <summary>Everything on the canvas side that can be rebound. A row's <c>Allowed</c> is the modifier mask
    /// its boundary can observe, not a taste: the tool letters sit behind a guard that hands Ctrl and Alt to
    /// the menus, the brackets only ever read Alt, the arrows read what their own matcher checks, the spacing
    /// rows read Alt and Shift through the text editor, and the structural keys read the key alone.</summary>
    internal List<(string Id, string Title, ShortcutChord Default, VirtualKeyModifiers Allowed, bool NeedsModifier)> CanvasCommands() =>
        CanvasToolKeys.Select(entry => (entry.Id, Loc.T(entry.TitleKey), new ShortcutChord(entry.Key, 0),
            VirtualKeyModifiers.Shift, false))
            .Concat(CanvasBracketKeys.Select(row => (row.Id, Loc.T(row.TitleKey), new ShortcutChord(row.Key, row.Default),
                VirtualKeyModifiers.Menu, false)))
            .Concat(CanvasArrowKeys.Select(row => (row.Id, Loc.F(row.TitleKey, Loc.T(row.DirectionKey), row.Pixels),
                new ShortcutChord(row.Key, row.Default), row.Allowed, false)))
            .Concat(PaletteKeys.Select(row => (row.Id, Loc.T(row.TitleKey), new ShortcutChord(row.Key, 0),
                (VirtualKeyModifiers)15, false)))
            .Concat(CanvasStructuralKeys.Select(row => (row.Id, Loc.T(row.TitleKey), new ShortcutChord(row.Key, 0),
                (VirtualKeyModifiers)0, false)))
            .Concat(CanvasOpacityDigits.Select(row => (row.Id, Loc.F(row.TitleKey, row.Digit), new ShortcutChord(row.Key, 0),
                (VirtualKeyModifiers)0, false)))
            // Last, so the sheet's own group heading for them comes after the canvas keys instead of splitting
            // that group in two: the panel starts a heading whenever the row's group differs from the one before.
            .Concat(CanvasSpacingKeys.Select(row => (row.Id, Loc.F(row.TitleKey, row.Pixels),
                new ShortcutChord(row.Key, row.Default), row.Allowed, false)))
            .ToList();

    /// <summary>Refreshes the two chip tooltips from the chords that will actually fire. The tooltips used to
    /// bake "(X)" and "(D)" into the resource file, which made them a second source of truth: rebind the key
    /// and the tooltip kept promising a chord that did nothing.</summary>
    private void RefreshPaletteTooltips()
    {
        if (SwapColorsChip is null || ResetColorsChip is null) return;
        string SwapChord = CanvasChord(shortcutChords, "palette:SwapColors").Label;
        string ResetChord = CanvasChord(shortcutChords, "palette:ResetColors").Label;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(SwapColorsChip, Loc.F("S_ChipShortcut", Loc.T("L_SwapColorsTip"), SwapChord));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(ResetColorsChip, Loc.F("S_ChipShortcut", Loc.T("L_ResetColorsTip"), ResetChord));
    }

    /// <summary>Every id the stored file may legitimately name: the menu commands and the canvas keys.</summary>
    private string[] KnownShortcutIds() =>
        shortcutBuiltins.Keys.Concat(CanvasCommands().Select(command => command.Id)).ToArray();

    /// <summary>The chord a command was declared with, on either boundary.</summary>
    private ShortcutChord BuiltinChord(string id) =>
        id.StartsWith("canvas:", StringComparison.Ordinal) ? DefaultCanvasChord(id) : DefaultChord(id);

    /// <summary>Loads the stored overrides into the live set, then writes the menu half into the menu items.
    /// A command whose stored chord is exactly its built-in chord is dropped, so the file only ever holds what
    /// the user changed, and a canvas chord that the matcher could never receive (it carries Ctrl or Alt, which
    /// the canvas guard hands to the menus) is dropped for the same reason.</summary>
    private void LoadAndApplyShortcuts()
    {
        shortcutChords.Clear();
        foreach (var (id, chord) in ShortcutRules.Load(ReadText(ShortcutsPreference), KnownShortcutIds()))
        {
            if (chord == ShortcutChord.Empty || chord == BuiltinChord(id)) continue;
            // A chord the boundary cannot observe is dropped rather than applied: the file may have been edited
            // by hand, and a binding that can never fire is worse than no binding at all.
            var command = CanvasCommands().FirstOrDefault(row => row.Id == id);
            if (command.Id is not null && (chord.Modifiers & ~command.Allowed) != 0) continue;
            shortcutChords[id] = chord;
        }
        ApplyShortcuts(shortcutChords);
    }

    /// <summary>Puts chords into the menu items themselves, which is what makes them fire and what makes
    /// the sheet print them. A command the user remapped keeps the one chord they set — the Mac's recorder
    /// replaces the binding the same way — and a command whose override was removed gets all of its
    /// built-in chords back, so saving can never leave a stale binding behind.</summary>
    private void ApplyShortcuts(IReadOnlyDictionary<string, ShortcutChord> chords)
    {
        foreach (var (id, _, _, item) in RemappableShortcuts())
        {
            bool overridden = chords.TryGetValue(id, out var chord);
            if (!overridden && !shortcutApplied.Contains(id)) continue;
            item.KeyboardAccelerators.Clear();
            List<ShortcutChord> set = overridden ? new List<ShortcutChord> { chord! }
                : shortcutBuiltins.TryGetValue(id, out var built) ? built : new List<ShortcutChord>();
            foreach (var each in set)
                item.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = each.Key, Modifiers = each.Modifiers });
            if (overridden) shortcutApplied.Add(id); else shortcutApplied.Remove(id);
        }
        var palette = new System.Collections.Generic.Dictionary<string, Microsoft.UI.Xaml.Input.KeyboardAccelerator>
        {
            ["palette:SwapColors"] = SwapColorsAccelerator, ["palette:ResetColors"] = ResetColorsAccelerator,
        };
        foreach (var (id, accelerator) in palette)
        {
            var chord = CanvasChord(shortcutChords, id);
            accelerator.Key = chord.Key;
            accelerator.Modifiers = chord.Modifiers;
        }
        RefreshPaletteTooltips();
    }

    /// <summary>The whole set as the rules see it: every command's current chord, overrides included.</summary>
    private List<(string Title, ShortcutChord Chord, bool NeedsModifier, VirtualKeyModifiers Allowed)> ShortcutBindings(
        IReadOnlyDictionary<string, ShortcutChord> draft) =>
        RemappableShortcuts().Select(row => (row.Title, draft.TryGetValue(row.Id, out var chord) ? chord : row.Item.KeyboardAccelerators
            .Select(built => new ShortcutChord(built.Key, built.Modifiers)).FirstOrDefault(), true, (VirtualKeyModifiers)15))
            .Concat(CanvasCommands().Select(command => (command.Title,
                draft.TryGetValue(command.Id, out var bound) ? bound : command.Default, command.NeedsModifier, command.Allowed))).ToList();



    /// <summary>The chord a canvas key answers to in <paramref name="chords"/>: its override when the set carries
    /// one, the bare letter it was declared with otherwise.</summary>
    internal static ShortcutChord CanvasChord(IReadOnlyDictionary<string, ShortcutChord> chords, string id) =>
        chords.TryGetValue(id, out var chord) ? chord : DefaultCanvasChord(id);

    /// <summary>The chord a canvas command was declared with, before any user changed it.</summary>
    internal static ShortcutChord DefaultCanvasChord(string id)
    {
        var tool = CanvasToolKeys.FirstOrDefault(entry => entry.Id == id);
        if (tool.Id is not null) return new ShortcutChord(tool.Key, 0);
        var bracket = CanvasBracketKeys.FirstOrDefault(row => row.Id == id);
        if (bracket.Id is not null) return new ShortcutChord(bracket.Key, bracket.Default);
        var arrow = CanvasArrowKeys.FirstOrDefault(row => row.Id == id);
        if (arrow.Id is not null) return new ShortcutChord(arrow.Key, arrow.Default);
        var spacing = CanvasSpacingKeys.FirstOrDefault(row => row.Id == id);
        if (spacing.Id is not null) return new ShortcutChord(spacing.Key, spacing.Default);
        var structural = CanvasStructuralKeys.FirstOrDefault(row => row.Id == id);
        if (structural.Id is not null) return new ShortcutChord(structural.Key, 0);
        var digit = CanvasOpacityDigits.FirstOrDefault(row => row.Id == id);
        if (digit.Id is not null) return new ShortcutChord(digit.Key, 0);
        return new ShortcutChord(PaletteKeys.First(row => row.Id == id).Key, 0);
    }

    /// <summary>Translate a canvas press the way the Mac's <c>canvasEvent</c> does
    /// (KeyboardShortcuts.swift:185-202): a chord the user bound to a canvas command is handed to the matcher
    /// as that command's built-in key, and a key whose command has moved elsewhere is swallowed rather than
    /// reaching whatever now sits at the old position. Everything else, including the keys this table does not
    /// own, goes back unchanged.</summary>
    internal (VirtualKey Key, bool Swallowed) CanvasKeyFor(VirtualKey key, bool shift)
    {
        var translated = CanvasTranslate(new ShortcutChord(key, shift ? VirtualKeyModifiers.Shift : (VirtualKeyModifiers)0));
        return (translated.Chord.Key, translated.Swallowed);
    }

    /// <summary>Translate a chord the way the Mac's <c>canvasEvent</c> does
    /// (KeyboardShortcuts.swift:185-202): a chord the user bound to a canvas command comes back as that
    /// command's declared chord, so the matcher keeps reading what it was written with, and a chord whose
    /// command has moved elsewhere is swallowed rather than reaching whatever now sits at the old position.
    /// Only declared commands take part; Tab and other unbound editing keys are left alone.</summary>
    internal (ShortcutChord Chord, bool Swallowed) CanvasTranslate(ShortcutChord input)
    {
        foreach (var command in CanvasCommands())
        {
            if (CanvasChord(shortcutChords, command.Id) == input) return (command.Default, false);
        }
        // Nothing owns this chord any more. If some command was declared with it, that command has moved
        // elsewhere and the old key is swallowed rather than reaching whatever the table now puts there —
        // which is the half of the Mac's rule that keeps a rebind from leaving a stray tool behind.
        // (A Shift combination nobody declared never gets here as anybody's default, so ⇧U and ⇧M still reach
        // the matchers that read them.)
        foreach (var command in CanvasCommands())
            if (command.Default == input) return (input, true);
        // An alias is a convenience for a command nobody has touched: Backspace keeps meaning Delete while
        // that row sits on its built-in chord, and stops the moment the user moves it, so the sheet never
        // advertises a key whose command answers somewhere else.
        foreach (var row in CanvasStructuralKeys)
            if (row.Alias != (VirtualKey)0 && CanvasChord(shortcutChords, row.Id) != DefaultCanvasChord(row.Id)
                && input == new ShortcutChord(row.Alias, 0)) return (input, true);
        return (input, false);
    }

    /// <summary>The press the canvas's structural matchers read: what the physical key means after the user's
    /// own bindings are applied, or nothing at all once its command has moved. Swallowing is expressed as an
    /// unmatched key rather than a handled event, so Escape still closes a menu and Space still presses the
    /// focused control — the table decides what the canvas does with a key, not whether the framework sees it.</summary>
    internal VirtualKey CanvasPress(VirtualKey key)
    {
        var (chord, swallowed) = CanvasTranslate(new ShortcutChord(key, (VirtualKeyModifiers)0));
        return swallowed ? (VirtualKey)0 : chord.Key;
    }

    // The four structural boundaries as questions about the physical key, because each one is the call its
    // matcher makes: the probe drives the same predicate the KeyDown chain does, and neither of them has to
    // remember a translation some earlier line did.
    internal bool IsApplyPress(VirtualKey raw) => CanvasPress(raw) == VirtualKey.Enter;
    internal bool IsCancelPress(VirtualKey raw) => CanvasPress(raw) == VirtualKey.Escape;
    internal bool IsPanPress(VirtualKey raw) => CanvasPress(raw) == VirtualKey.Space;
    internal bool IsDeletePress(VirtualKey raw) => CanvasPress(raw) is VirtualKey.Delete or VirtualKey.Back;

    /// <summary>Which opacity percentage this press means, after the user's own bindings: the shared handler that
    /// type an opacity read the translated key, so binding "opacity digit 5" to a free key makes that key the 5
    /// and leaves the old 5 typing nothing. Null when the press answers to no digit — including a digit whose
    /// command has moved, which is swallowed rather than falling through to whatever sits at it.</summary>
    internal int? OpacityDigitPress(VirtualKey raw)
    {
        var pressed = CanvasPress(raw);
        return pressed is >= VirtualKey.Number0 and <= VirtualKey.Number9 ? pressed - VirtualKey.Number0 : null;
    }

    /// <summary>Space's two halves, together because the release has to be translated as well: move the
    /// temporary pan onto another key and a KeyUp of the old one would leave the pan latched on the canvas
    /// with nothing pressing it, which is the one failure mode a modifier key does not have.</summary>
    internal bool BeginTemporaryPan(VirtualKey raw)
    {
        if (!IsPanPress(raw)) return false;
        spaceHeld = true; RefreshNavigationCursor(); return true;
    }

    internal void ReleaseTemporaryPan(VirtualKey raw)
    {
        if (!IsPanPress(raw)) return;
        spaceHeld = false; RefreshNavigationCursor();
    }

    /// <summary>One live accelerator as the user reads it. The label is derived from the same model the
    /// editor prints, so a menu row and its editor row can never show different text.</summary>
    internal static string ChordLabel(Microsoft.UI.Xaml.Input.KeyboardAccelerator accelerator) =>
        new ShortcutChord(accelerator.Key, accelerator.Modifiers).Label;

    /// <summary>The editor. It opens on the live bindings, edits a draft, and only writes to the menu and
    /// to disk when the draft saves — the same shape as the grid settings sheet.</summary>
    /// <summary>Opens the editor in the window's floating host (#37), as the Mac keeps it in a panel of its
    /// own (`ShortcutSettings.show()` at KeyboardShortcuts.swift:156-158). The dialog had one advantage worth
    /// naming: it swallowed every key while it was up, which is what let the recorder read a chord. Here the
    /// same grab is re-put on the panel's own root, and only while that panel holds the focus — the canvas
    /// stays live, so rebinding a key does not cost the user the picture they are rebinding it from.</summary>
    private void ShowShortcutEditor(object? sender, RoutedEventArgs e)
    {
        var draft = shortcutChords.ToDictionary(entry => entry.Key, entry => entry.Value);
        var form = new ShortcutForm(this, draft);
#if DEBUG
        shortcutEditorProbe = form;
#endif
        ShowFloatingPanel("shortcuts", Loc.T("DlgShortcutsTitle"), form.BuildPanel(),
            finish: commit =>
            {
                ClearShortcutProbe();
                if (commit && form.Save()) SetStatus(Loc.T("S_ShortcutsSaved"));
                else if (form.Changed) SetStatus(Loc.T("S_ShortcutsCancelled"));
                return Task.CompletedTask;
            },
            // A panel whose document went out from under it must still let go of the probe, or the next group
            // would drive a form belonging to a session that no longer exists.
            onAbandon: ClearShortcutProbe,
            extra: (Loc.T("Dlg_RestoreDefaults"), () => form.RestoreDefaults()), answers: FloatingPanelAnswers.SaveCancel);
        form.GrabKeys(FloatingPanelRoot("shortcuts"));
    }

#if DEBUG
    /// <summary>The open editor, so a probe can drive recording and saving through the same object the
    /// panel does instead of reaching for a key event it cannot synthesize.</summary>
    internal ShortcutForm? shortcutEditorProbe;
    private void ClearShortcutProbe() => shortcutEditorProbe = null;
#else
    private void ClearShortcutProbe() { }
#endif

    /// <summary>One row per remappable command with the chord it currently answers to, and a button that
    /// records the next key combination into it.</summary>
    internal sealed class ShortcutForm
    {
        private readonly MainWindow owner;
        private readonly Dictionary<string, ShortcutChord> draft;
        private readonly Dictionary<string, Button> recorders = new();
        private readonly Dictionary<string, string> titles = new();
        private string? recording;
        private TextBlock? problem;

        public ShortcutForm(MainWindow owner, Dictionary<string, ShortcutChord> draft) { this.owner = owner; this.draft = draft; }

        /// <summary>Whether the user touched anything, so closing the sheet can say so on the status bar.</summary>
        public bool Changed { get; private set; }

        /// <summary>The line the sheet shows under the rows — the sentence the user reads when a set is
        /// refused, kept readable so a probe can say which rule it tripped instead of only that it tripped.</summary>
        public string ProblemText => problem?.Text ?? string.Empty;

        public IReadOnlyDictionary<string, ShortcutChord> Draft => draft;

        /// <summary>Every chord one command answers to, as the menu prints them. A remapped command has
        /// the one chord the user set; a command still on its built-in can carry two (Redo is ⌘Y and
        /// ⌘⇧Z on the Mac, Ctrl+Y and Ctrl+Shift+Z here), and showing only the first would hide a binding
        /// the user can press.</summary>
        public string ChordOf(string id) => id.StartsWith("canvas:", StringComparison.Ordinal)
            ? CanvasChord(draft, id).Label
            : draft.TryGetValue(id, out var chord) ? chord.Label
                : string.Join(" / ", owner.LiveChords(id).Select(each => each.Label));

        public sealed record Row(string Id, string Title, string Group, string Keys, bool Remappable);

        /// <summary>The rows the sheet lists: every remappable menu command, then every canvas chord.</summary>
        public List<Row> Rows() =>
            owner.RemappableShortcuts().Select(row => new Row(row.Id, row.Title, row.Group, ChordOf(row.Id), true))
                .Concat(RebindableCanvasRows()).ToList();

        /// <summary>The canvas side, one row per command the canvas matches by hand. They are rebindable the
        /// same way as the menu rows, but saving them rewrites nothing in the framework — the matchers ask
        /// <see cref="CanvasTranslate"/> what a press means. The spacing rows answer the text editor rather than
        /// the canvas, and the original lists them under their own heading (KeyboardShortcuts.swift:119-124),
        /// so the sheet keeps them in a group of their own instead of folding them into the canvas keys.</summary>
        private List<Row> RebindableCanvasRows() =>
            owner.CanvasCommands().Select(command => new Row(command.Id, command.Title,
                command.Id.StartsWith("canvas:Spacing.", StringComparison.Ordinal)
                    ? Loc.T("DlgGroupTextEditing") : Loc.T("DlgGroupCanvas"),
                CanvasChord(draft, command.Id).Label, true)).ToList();

        /// <summary>The editor's body, handed to the window's floating host. The row that used to be built as
        /// a ContentDialog had two things the host cannot copy for it: the recorder's key grab, re-put on the
        /// panel root by <see cref="GrabKeys"/>, and the disabled-OK state, which the host lets the owner set.</summary>
        public StackPanel BuildPanel()
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("L_ShortcutEditorHint"), Opacity = 0.7, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });
            string? shownGroup = null;
            foreach (var row in Rows())
            {
                if (row.Group != shownGroup)
                {
                    shownGroup = row.Group;
                    panel.Children.Add(new TextBlock
                    {
                        Text = row.Group, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2),
                    });
                }
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = new TextBlock { Text = row.Title, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 0);
                line.Children.Add(title);
                if (!row.Remappable)
                {
                    var fixedChord = new TextBlock
                    {
                        Text = row.Keys, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                    };
                    Grid.SetColumn(fixedChord, 1);
                    line.Children.Add(fixedChord);
                    panel.Children.Add(line);
                    continue;
                }
                var button = new Button
                {
                    Content = row.Keys, MinWidth = 130, Tag = row.Id,
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                };
                button.Click += (_, _) => BeginRecording(row.Id);
                recorders[row.Id] = button;
                titles[row.Id] = row.Title;
                Grid.SetColumn(button, 1);
                line.Children.Add(button);
                panel.Children.Add(line);
            }
            problem = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange), TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap };
            panel.Children.Add(problem);
            ShowProblem();
            return panel;
        }

        /// <summary>Re-puts the recorder's key grab where the dialog's used to be. PreviewKeyDown tunnels from
        /// the root of the tree down, so hooking the panel's own root sees a chord before the focused row button
        /// does — and sees nothing at all when some other window part has the focus, which is what keeps the
        /// canvas usable while the editor is open.</summary>
        public void GrabKeys(FrameworkElement? root)
        {
            if (root is null) return;
            root.PreviewKeyDown += OnKey;
        }

        private void OnKey(object sender, KeyRoutedEventArgs args)
        {
            if (recording is not { } id) return;
            args.Handled = true;
            if (args.Key == VirtualKey.Escape) { FinishRecording(); return; }
            Record(id, args.Key, Modifiers());
            FinishRecording();
        }

        private static VirtualKeyModifiers Modifiers() =>
            (Held(VirtualKey.Control) ? VirtualKeyModifiers.Control : 0)
            | (Held(VirtualKey.Menu) ? VirtualKeyModifiers.Menu : 0)
            | (Held(VirtualKey.Shift) ? VirtualKeyModifiers.Shift : 0);

        private static bool Held(VirtualKey key) =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        /// <summary>The production entry for "the user pressed this chord": the dialog's key handler calls
        /// it with what it read, and a probe can call it with the same three arguments. Recording does not
        /// write to the menu — only saving does.</summary>
        public void Record(string id, VirtualKey key, VirtualKeyModifiers modifiers)
        {
            var chord = new ShortcutChord(key, modifiers);
            bool canvas = id.StartsWith("canvas:", StringComparison.Ordinal);
            if (chord == (canvas ? DefaultCanvasChord(id) : owner.DefaultChord(id))) draft.Remove(id);
            else draft[id] = chord;
            Changed = true;
            if (recorders.TryGetValue(id, out var button)) button.Content = chord.Label;
            ShowProblem();
        }

        public void BeginRecording(string id)
        {
            FinishRecording();
            recording = id;
            if (recorders.TryGetValue(id, out var button)) button.Content = Loc.T("L_ShortcutPressKeys");
            // A command can be reachable by more than one chord (Redo is Ctrl+Y and Ctrl+Shift+Z, Zoom In is
            // Ctrl+= and Ctrl+Shift+=), and the stored override is one chord per command — recording therefore
            // retires the alias. Say it on the row the user is about to collapse, not only in the hint above.
            if (problem is not null && !id.StartsWith("canvas:", StringComparison.Ordinal)
                && owner.LiveChords(id).Count > 1)
                problem.Text = Loc.F("S_ShortcutAliasLost", titles.TryGetValue(id, out var title) ? title : id);
        }

        public void FinishRecording()
        {
            if (recording is { } id && recorders.TryGetValue(id, out var button)) button.Content = ChordOf(id);
            recording = null;
            ShowProblem();
        }

        /// <summary>Puts every command back on the chord its markup declares, in the draft only: the
        /// built-ins are what the menu goes back to when the draft saves.</summary>
        public void RestoreDefaults()
        {
            FinishRecording();
            draft.Clear();
            foreach (var (id, button) in recorders) button.Content = ChordOf(id);
            Changed = true;
            ShowProblem();
        }

        private void ShowProblem()
        {
            if (problem is null) return;
            var finding = ShortcutRules.Check(owner.ShortcutBindings(draft));
            problem.Text = finding is { } why ? Loc.F(why.Key, why.Args) : string.Empty;
            owner.SetFloatingPanelConfirmEnabled("shortcuts", finding is null);
        }

        /// <summary>Writes the draft to disk and into the menu. Refuses a set the rules reject, so a
        /// conflict cannot half-apply.</summary>
        public bool Save()
        {
            FinishRecording();
            if (ShortcutRules.Check(owner.ShortcutBindings(draft)) is not null) return false;
            owner.shortcutChords.Clear();
            foreach (var (id, chord) in draft) owner.shortcutChords[id] = chord;
            WriteText(ShortcutsPreference, ShortcutRules.Save(draft));
            owner.ApplyShortcuts(owner.shortcutChords);
            return true;
        }
    }
}
