using Windows.System;

namespace Compositor.App;

/// <summary>The virtual key codes the WinUI projection gives no name to, so a chord that uses one is
/// built from the code. Owned here because both the menu's own accelerators and the remapped chords
/// have to print the same character.</summary>
internal static class ShortcutVK
{
    internal const int OemPlusCode = 0xBB, OemMinusCode = 0xBD, OpenBracketCode = 0xDB, CloseBracketCode = 0xDD,
        SemicolonCode = 0xBA, QuoteCode = 0xDE;
}

/// <summary>
/// One key combination a command can be bound to. The Mac's <c>ShortcutChord</c>
/// (KeyboardShortcuts.swift:4-61) keeps Command/Option/Control/Shift as four stored bits so a saved
/// binding never depends on how the key is drawn; the Windows counterpart is the same two numbers —
/// a virtual key and the modifier flags — and the label is derived from them, never stored.
/// </summary>
internal readonly record struct ShortcutChord(VirtualKey Key, VirtualKeyModifiers Modifiers)
{
    public static readonly ShortcutChord Empty = new((VirtualKey)0, (VirtualKeyModifiers)0);

    public bool IsEmpty => Key == (VirtualKey)0;

    /// <summary>The chord the way the user reads it in the menu: modifiers, then the key.</summary>
    public string Label
    {
        get
        {
            bool shift = Modifiers.HasFlag(VirtualKeyModifiers.Shift);
            var parts = new List<string>();
            if (Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            parts.Add(Key switch
            {
                VirtualKey.Add => "Num+",
                VirtualKey.Subtract => "Num-",
                // The projection names neither main-row key, so they match by virtual keycode.
                var key when (int)key == ShortcutVK.OemPlusCode => shift ? "+" : "=",
                var key when (int)key == ShortcutVK.OemMinusCode => shift ? "_" : "-",
                var key when (int)key == ShortcutVK.SemicolonCode => shift ? ":" : ";",
                var key when (int)key == ShortcutVK.OpenBracketCode => "[",
                var key when (int)key == ShortcutVK.CloseBracketCode => "]",
                var key when (int)key == ShortcutVK.QuoteCode => "'",
                var key when key >= VirtualKey.Number0 && key <= VirtualKey.Number9
                    => ((char)('0' + key - VirtualKey.Number0)).ToString(),
                var key when key >= VirtualKey.A && key <= VirtualKey.Z
                    => ((char)('A' + key - VirtualKey.A)).ToString(),
                var other => other.ToString(),
            });
            return string.Join("+", parts);
        }
    }

    /// <summary>One stored chord: two integers, so a saved binding is immune to a rename, a relabel or a
    /// language switch — the three things a label would have depended on.</summary>
    public string Store => $"{(int)Key}|{(int)Modifiers}";

    public static bool TryLoad(string text, out ShortcutChord chord)
    {
        chord = Empty;
        var halves = text.Split('|');
        if (halves.Length != 2 || !int.TryParse(halves[0], out int key) || !int.TryParse(halves[1], out int modifiers)) return false;
        if (key == 0 || modifiers is < 0 or > 15) return false;
        chord = new ShortcutChord((VirtualKey)key, (VirtualKeyModifiers)modifiers);
        return true;
    }
}

/// <summary>Why a set of bindings cannot be saved, and how the set is written to the preference file.
/// Pure so the rules are testable without a window: the Mac keeps them in <c>ShortcutSettings</c>
/// (KeyboardShortcuts.swift:166-181) for the same reason. It names the rule that fired rather than a
/// sentence, because the sentence belongs to the resource file.</summary>
internal static class ShortcutRules
{
    /// <summary>The reason a set was refused: a resource key and the holes to fill it with.</summary>
    internal readonly record struct Problem(string Key, string[] Args);

    /// <summary>The version marker, because the file has no other schema and a future format must be
    /// able to tell an old file from an unreadable one.</summary>
    internal const string Format = "keyboard-shortcuts.v1";

    /// <summary>Chords the shell owns: binding one would only ever lose the key, so the editor refuses
    /// it instead of letting the user discover it. The Mac's list is ⌘Q, ⌘, and ⌃⌘F
    /// (KeyboardShortcuts.swift:174); these are their Windows counterparts.</summary>
    private static readonly (VirtualKey Key, VirtualKeyModifiers Modifiers)[] Reserved =
    [
        (VirtualKey.F4, VirtualKeyModifiers.Menu),
        (VirtualKey.Tab, VirtualKeyModifiers.Menu),
        (VirtualKey.Escape, VirtualKeyModifiers.Menu),
        (VirtualKey.Escape, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        (VirtualKey.F4, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift),
    ];

    /// <summary>The first reason this set cannot be saved, or null when it can. Titles are the localized
    /// command names, which is what the user needs in the sentence even though they are never stored.</summary>
    /// <summary>
    /// A row the rules judge: the command's name for the sentence, the chord it would get, whether the
    /// boundary needs a modifier at all, and which modifier bits that boundary can even see. The last one is
    /// not a preference — a menu chord goes through the framework's accelerator routing so a bare key would be
    /// taken out of every text field, while the tool letters are matched behind a guard that hands Ctrl and Alt
    /// to the menus (MainWindow.Workspace.cs:421-426) and the arrows are matched by code that only reads
    /// Control and Shift. Binding a chord the boundary cannot observe is a shortcut that never fires.
    /// </summary>
    internal static Problem? Check(IReadOnlyList<(string Title, ShortcutChord Chord, bool NeedsModifier, VirtualKeyModifiers Allowed)> bindings)
    {
        var assigned = new Dictionary<ShortcutChord, string>();
        foreach (var (title, chord, needsModifier, allowed) in bindings)
        {
            if (chord.IsEmpty || needsModifier && chord.Modifiers == 0) return new Problem("S_ShortcutNeedsModifier", new[] { title });
            if ((chord.Modifiers & ~allowed) != 0)
                return new Problem(allowed == 0 ? "S_ShortcutFixedKey" : "S_ShortcutCanvasPlain", new[] { chord.Label, title });
            foreach (var (key, modifiers) in Reserved)
                if (chord == new ShortcutChord(key, modifiers)) return new Problem("S_ShortcutReserved", new[] { chord.Label });
            if (assigned.TryGetValue(chord, out var other)) return new Problem("S_ShortcutConflict", new[] { chord.Label, other, title });
            assigned[chord] = title;
        }
        return null;
    }

    /// <summary>The whole override set as one preference file: a marker line, then one `id=key|modifiers`
    /// line per remapped command. Commands that keep their default never appear, so restoring one is a
    /// line deleted rather than a value copied back.</summary>
    internal static string Save(IReadOnlyDictionary<string, ShortcutChord> overrides) =>
        string.Join('\n', new[] { Format }.Concat(overrides.Select(entry => $"{entry.Key}={entry.Value.Store}")));

    /// <summary>The overrides stored in <paramref name="text"/>. A file that is not this format is read as
    /// "no overrides" — the same shape as every other preference here, where unparseable is a first
    /// launch rather than an error, and a line naming a command that no longer exists is simply ignored.</summary>
    internal static Dictionary<string, ShortcutChord> Load(string? text, IReadOnlyCollection<string> knownIds)
    {
        var overrides = new Dictionary<string, ShortcutChord>();
        if (text is not { Length: > 0 }) return overrides;
        var lines = text.Split('\n');
        if (lines[0].Trim() != Format) return overrides;
        foreach (var raw in lines[1..])
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int equals = line.IndexOf('=');
            if (equals <= 0 || !ShortcutChord.TryLoad(line[(equals + 1)..], out var chord)) continue;
            string id = line[..equals];
            if (knownIds.Contains(id)) overrides[id] = chord;
        }
        return overrides;
    }
}
