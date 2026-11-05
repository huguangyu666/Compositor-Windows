using Compositor.App;
using Windows.System;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// The stored form, the printed form and the save rules of a rebindable shortcut, tested without a window.
/// The window side is what puts a chord into a live menu item; these are the three things that must stay
/// true however it does that: a saved chord comes back as the same chord, the label the user read is the
/// label the menu shows, and a set the rules refuse cannot be written at all.
/// </summary>
public sealed class ShortcutChordTests
{
    private const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control, Alt = VirtualKeyModifiers.Menu,
        Shift = VirtualKeyModifiers.Shift;

    [Fact]
    public void AStoredChordComesBackAsTheSameChord()
    {
        foreach (var chord in new[]
        {
            new ShortcutChord(VirtualKey.Z, Ctrl),
            new ShortcutChord(VirtualKey.Z, Ctrl | Shift),
            new ShortcutChord((VirtualKey)ShortcutVK.OemPlusCode, Ctrl),
            new ShortcutChord(VirtualKey.Number0, Ctrl),
            new ShortcutChord(VirtualKey.Escape, Alt | Ctrl),
        })
        {
            Assert.True(ShortcutChord.TryLoad(chord.Store, out var again), chord.Store);
            Assert.Equal(chord, again);
        }
    }

    [Fact]
    public void ANonChordIsRefusedRatherThanReadAsSomethingElse()
    {
        // A file that names no key, or a modifier set outside the four bits, is not a chord: reading it as
        // one would silently rebind a command to whatever the enum happened to mean.
        ShortcutChord chord = ShortcutChord.Empty;
        foreach (var text in new[] { "", "0|2", "90", "90|", "90|16", "90|-1", "Z|2", "abc|def" })
            Assert.False(ShortcutChord.TryLoad(text, out chord), text);
        Assert.True(chord.IsEmpty);
    }

    [Fact]
    public void TheLabelIsWhatTheMenuUserReads()
    {
        Assert.Equal("Ctrl+Z", new ShortcutChord(VirtualKey.Z, Ctrl).Label);
        Assert.Equal("Ctrl+Shift+Z", new ShortcutChord(VirtualKey.Z, Ctrl | Shift).Label);
        Assert.Equal("Ctrl+Alt+Shift+J", new ShortcutChord(VirtualKey.J, Alt | Ctrl | Shift).Label);
        // The main-row keys the projection gives no name to, and the digits, print as the keyboard shows them.
        Assert.Equal("Ctrl+=", new ShortcutChord((VirtualKey)ShortcutVK.OemPlusCode, Ctrl).Label);
        // The doubled hint is what the menu has always shown for these two: the character already carries
        // the Shift, and the modifier is printed too rather than leaving "Ctrl++" looking like a plus key.
        Assert.Equal("Ctrl+Shift++", new ShortcutChord((VirtualKey)ShortcutVK.OemPlusCode, Ctrl | Shift).Label);
        Assert.Equal("Ctrl+-", new ShortcutChord((VirtualKey)ShortcutVK.OemMinusCode, Ctrl).Label);
        Assert.Equal("Ctrl+0", new ShortcutChord(VirtualKey.Number0, Ctrl).Label);
        Assert.Equal("Ctrl+Num+", new ShortcutChord(VirtualKey.Add, Ctrl).Label);
    }

    [Fact]
    public void ASetOfDistinctChordsSaves()
    {
        Assert.Null(ShortcutRules.Check(new[]
        {
            ("Undo", new ShortcutChord(VirtualKey.Z, Ctrl), true, (VirtualKeyModifiers)15),
            ("Redo", new ShortcutChord(VirtualKey.Z, Ctrl | Shift), true, (VirtualKeyModifiers)15),
            ("Zoom In", new ShortcutChord((VirtualKey)ShortcutVK.OemPlusCode, Ctrl), true, (VirtualKeyModifiers)15),
        }));
    }

    [Fact]
    public void TwoCommandsOnOneChordAreNamedInOneMessage()
    {
        var finding = ShortcutRules.Check(new[]
        {
            ("Undo", new ShortcutChord(VirtualKey.Z, Ctrl), true, (VirtualKeyModifiers)15),
            ("Copy", new ShortcutChord(VirtualKey.Z, Ctrl), true, (VirtualKeyModifiers)15),
        });
        Assert.NotNull(finding);
        Assert.Equal("S_ShortcutConflict", finding!.Value.Key);
        Assert.Equal(new[] { "Ctrl+Z", "Undo", "Copy" }, finding.Value.Args);
    }

    [Fact]
    public void AChordWithoutAModifierIsRefused()
    {
        // The Mac's rule is that a menu shortcut carries a modifier (KeyboardShortcuts.swift:170-172); the
        // same reason holds here, where a bare letter would be swallowed from every text field.
        var finding = ShortcutRules.Check(new[] { ("Brush", new ShortcutChord(VirtualKey.B, 0), true, (VirtualKeyModifiers)15) });
        Assert.Equal("S_ShortcutNeedsModifier", finding!.Value.Key);
        Assert.NotNull(ShortcutRules.Check(new[] { ("Nothing", ShortcutChord.Empty, true, (VirtualKeyModifiers)15) }));
    }

    [Fact]
    public void AChordTheShellOwnsIsRefused()
    {
        Assert.Equal("S_ShortcutReserved",
            ShortcutRules.Check(new[] { ("Close", new ShortcutChord(VirtualKey.F4, Alt), true, (VirtualKeyModifiers)15) })!.Value.Key);
        Assert.Equal("S_ShortcutReserved",
            ShortcutRules.Check(new[] { ("Task Manager", new ShortcutChord(VirtualKey.Escape, Ctrl | Shift), true, (VirtualKeyModifiers)15) })!.Value.Key);
    }

    [Fact]
    public void TheCanvasBoundaryTakesBareKeysAndShiftButNotCtrl()
    {
        // The canvas matcher hands Ctrl and Alt to the menus before it looks at a letter
        // (MainWindow.Workspace.cs:421-426), so a canvas chord carrying them could never arrive.
        Assert.Null(ShortcutRules.Check(new[] { ("Brush", new ShortcutChord(VirtualKey.B, 0) , false, VirtualKeyModifiers.Shift) }));
        Assert.Null(ShortcutRules.Check(new[] { ("Marquee", new ShortcutChord(VirtualKey.M, Shift) , false, VirtualKeyModifiers.Shift) }));
        Assert.Equal("S_ShortcutCanvasPlain",
            ShortcutRules.Check(new[] { ("Brush", new ShortcutChord(VirtualKey.B, Ctrl) , false, VirtualKeyModifiers.Shift) })!.Value.Key);
        // And the two boundaries are judged as one set: a letter may not take a chord a menu already answers to.
        Assert.Equal("S_ShortcutConflict", ShortcutRules.Check(new[]
        {
            ("Fill With Foreground", new ShortcutChord(VirtualKey.F5, Shift), true, (VirtualKeyModifiers)15),
            ("Crop", new ShortcutChord(VirtualKey.F5, Shift), false, VirtualKeyModifiers.Shift),
        })!.Value.Key);
    }

    [Fact]
    public void EachArrowBoundaryIsJudgedByTheModifiersItCanSee()
    {
        // Moving the outline reads Control as "not for me"; moving pixels inside the selection requires it.
        // One canvas rule for both would either refuse a chord that works or accept one that can never fire.
        var outline = VirtualKeyModifiers.Shift;
        var pixels = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        Assert.Null(ShortcutRules.Check(new[] { ("Nudge", new ShortcutChord(VirtualKey.Left, Shift), false, outline) }));
        Assert.Equal("S_ShortcutCanvasPlain",
            ShortcutRules.Check(new[] { ("Nudge", new ShortcutChord(VirtualKey.Left, Ctrl), false, outline) })!.Value.Key);
        Assert.Equal("S_ShortcutNeedsModifier",
            ShortcutRules.Check(new[] { ("Move pixels", new ShortcutChord(VirtualKey.Left, 0), true, pixels) })!.Value.Key);
        Assert.Null(ShortcutRules.Check(new[] { ("Move pixels", new ShortcutChord(VirtualKey.Left, Ctrl | Shift), true, pixels) }));
        // The two arrow kinds live in one set, so the same chord offered to both is a conflict, not two bindings.
        Assert.Equal("S_ShortcutConflict", ShortcutRules.Check(new[]
        {
            ("Nudge left", new ShortcutChord(VirtualKey.Left, Ctrl), true, pixels),
            ("Move pixels left", new ShortcutChord(VirtualKey.Left, Ctrl), true, pixels),
        })!.Value.Key);
    }

    [Fact]
    public void AKeyMatchedWithoutModifiersIsRefusedWithADifferentSentence()
    {
        // Enter, Escape, Space and Delete are matched by code that never looks at the modifier state, so any
        // chord carrying one cannot be observed. Refusing it is the same rule as for the tool letters, but the
        // sentence is not: "Ctrl and Alt go to the menus" would be a lie about Shift+Enter.
        var none = (VirtualKeyModifiers)0;
        Assert.Null(ShortcutRules.Check(new[] { ("Apply", new ShortcutChord(VirtualKey.Enter, none), false, none) }));
        Assert.Equal("S_ShortcutFixedKey",
            ShortcutRules.Check(new[] { ("Apply", new ShortcutChord(VirtualKey.Enter, Shift), false, none) })!.Value.Key);
        Assert.Equal("S_ShortcutFixedKey",
            ShortcutRules.Check(new[] { ("Apply", new ShortcutChord(VirtualKey.Q, Ctrl), false, none) })!.Value.Key);
        // A bare letter is still refused by the canvas guard that hands Ctrl to the menus, and that boundary
        // keeps its own sentence.
        Assert.Equal("S_ShortcutCanvasPlain",
            ShortcutRules.Check(new[] { ("Brush", new ShortcutChord(VirtualKey.B, Ctrl), false, VirtualKeyModifiers.Shift) })!.Value.Key);
    }

    [Fact]
    public void OnlyWhatTheUserChangedIsStored()
    {
        string text = ShortcutRules.Save(new Dictionary<string, ShortcutChord>
        {
            ["M_Undo"] = new ShortcutChord(VirtualKey.Z, Ctrl | Shift),
        });
        var lines = text.Split('\n');
        Assert.Equal(ShortcutRules.Format, lines[0]);
        Assert.Equal("M_Undo=" + new ShortcutChord(VirtualKey.Z, Ctrl | Shift).Store, lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void AFileIsReadBackAsChordsOrAsNothing()
    {
        string text = ShortcutRules.Save(new Dictionary<string, ShortcutChord>
        {
            ["M_Undo"] = new ShortcutChord(VirtualKey.Z, Ctrl | Shift),
            ["M_Gone"] = new ShortcutChord(VirtualKey.Q, Ctrl),
        });
        var known = new[] { "M_Undo" };
        var loaded = ShortcutRules.Load(text, known);
        Assert.Equal(new ShortcutChord(VirtualKey.Z, Ctrl | Shift), loaded["M_Undo"]);
        // A command the app no longer has is dropped rather than applied to a different item, and a file
        // from another format is a first launch rather than a crash.
        Assert.DoesNotContain("M_Gone", loaded.Keys);
        Assert.Empty(ShortcutRules.Load("not a chord file\nM_Undo=90|10", known));
        Assert.Empty(ShortcutRules.Load(null, known));
        Assert.Empty(ShortcutRules.Load(ShortcutRules.Format + "\nM_Undo=nonsense\n", known));
    }
}
