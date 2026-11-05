namespace Compositor.Core;

/// <summary>
/// One entry in the workspace's tab list, like the original's <c>ProjectTab</c> in
/// <c>ProjectWorkspace.swift</c>: a document, the project path it was read from or saved to when it has
/// one, and the name it shows before it does.
/// </summary>
public sealed class ProjectWorkspaceTab
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The document this tab holds. Fresh tabs start without one, as the original's session does;
    /// the window's launch document is adopted into the first tab instead.</summary>
    public EditorDocument? Document { get; set; }

    /// <summary>The project directory on disk, when this tab has one. Titles follow the original's rule:
    /// the file name wins while a path exists, the default name shows otherwise.</summary>
    public string? ProjectPath { get; set; }

    public string DefaultName { get; set; } = "Untitled";

    /// <summary>Original title rule: <c>session.projectURL?.deletingPathExtension().lastPathComponent ??
    /// defaultName</c>. Project folders carry no extension, so deleting one is a no-op like the original's.</summary>
    public string Title => ProjectPath is { } path
        ? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)) switch
        {
            { Length: > 0 } name => name,
            _ => DefaultName,
        }
        : DefaultName;

    /// <summary>The state the original's <c>reuseEmpty</c> tests for with a nil-document session. This port
    /// launches with a live blank canvas rather than a nil session (its window creates one at startup), so
    /// the same user-visible state — nothing opened and nothing changed — is the document being pristine and
    /// unpathed. Recorded deliberately: it is an adaptation, not the original's literal check.</summary>
    public bool IsPristineUntitled => ProjectPath is null && (Document is null || !Document.IsDirty);
}

/// <summary>
/// The tab registry the original's <c>ProjectWorkspace</c> keeps: the list of projects, which one is on
/// screen, and the rules for adding, dragging, selecting, closing and quitting them. It holds no view code;
/// the window supplies the switch guard the original reads from its session (<c>canSwitch</c>) and drives
/// this class from its commands. Multi-document porting step 1; the window hosts it in the following step.
/// </summary>
public sealed class ProjectWorkspace
{
    private int nextNumber = 2;

    public IList<ProjectWorkspaceTab> Tabs { get; } = new List<ProjectWorkspaceTab>();

    public Guid SelectedId { get; private set; }

    /// <summary>True while a management command (open, close, quit) runs, exactly as the original's
    /// <c>isManaging</c> blocks re-entry.</summary>
    public bool IsManaging { get; private set; }

    /// <summary>The original's <c>canSwitch</c> minus the parts this registry already owns: the window
    /// answers true while a draft dialog, a pending move or any other in-progress edit would be lost by
    /// switching — the Mac tests those drafts for nil before it lets select run. Null means nothing
    /// blocks, which is also the state before the window wires it.</summary>
    public Func<bool>? SwitchGuard { get; set; }

    /// <summary>The tab on screen. The window reads this through its document property on the render path,
    /// so it walks the list directly instead of allocating an enumerator; a stale selection falls back to
    /// the first tab, as the original's <c>current</c> does.</summary>
    public ProjectWorkspaceTab Current
    {
        get
        {
            for (int i = 0; i < Tabs.Count; i++) if (Tabs[i].Id == SelectedId) return Tabs[i];
            return Tabs[0];
        }
    }

    public bool CanSwitch => !IsManaging && !(SwitchGuard?.Invoke() ?? false);

    public ProjectWorkspace()
    {
        var first = new ProjectWorkspaceTab();
        Tabs.Add(first);
        SelectedId = first.Id;
    }

    /// <summary>Original rule: with only one tab on screen and that tab still pristine, a new canvas or a
    /// dropped image reuses it instead of stacking empty tabs.</summary>
    public ProjectWorkspaceTab AddTab(bool reuseEmpty = true)
    {
        if (reuseEmpty && Tabs.Count == 1 && Current.IsPristineUntitled) return Current;
        var tab = new ProjectWorkspaceTab { DefaultName = "Untitled " + nextNumber };
        nextNumber += 1;
        Tabs.Add(tab);
        SelectedId = tab.Id;
        return tab;
    }

    /// <summary>Reorders a tab by dragging it in the strip. Chrome, not a document edit, so it never
    /// touches undo; <c>index</c> is where the tab should land in the final order, clamped to the list's
    /// bounds — the original's <c>moveTab</c> verbatim.</summary>
    public void MoveTab(Guid id, int index)
    {
        int from = IndexOf(id);
        if (from < 0) return;
        int target = Math.Min(Math.Max(0, index), Tabs.Count - 1);
        if (target == from) return;
        var tab = Tabs[from];
        Tabs.RemoveAt(from);
        Tabs.Insert(target, tab);
    }

    /// <summary>Switching asks the original's way: refuse unless <see cref="CanSwitch"/>, let the window
    /// commit what it must first through <paramref name="commitPending"/>, then move on.</summary>
    public bool Select(Guid id, Action? commitPending = null)
    {
        if (id == SelectedId || !CanSwitch || IndexOf(id) < 0) return false;
        commitPending?.Invoke();
        SelectedId = id;
        return true;
    }

    /// <summary>File &gt; New on the original: commit the on-screen transform and always take a fresh tab —
    /// a new canvas never reuses an untouched one silently the way a drop does.</summary>
    public ProjectWorkspaceTab NewCanvas(Action? commitPending = null)
    {
        if (!CanSwitch) return Current;
        commitPending?.Invoke();
        return AddTab(reuseEmpty: false);
    }

    /// <summary>Finds the tab already showing a project path, the original's <c>loadProject</c> dedupe
    /// lookup; the host follows its own switch rules to move there.</summary>
    public ProjectWorkspaceTab? OpenedTab(string path)
        => Tabs.FirstOrDefault(tab => SamePath(tab.ProjectPath, path));

    /// <summary>Opening a path that is already a tab selects it and changes nothing else.</summary>
    public bool TrySelectOpened(string path)
    {
        var existing = OpenedTab(path);
        if (existing is null) return false;
        SelectedId = existing.Id;
        return true;
    }

    /// <summary>Adopts a loaded document into a new tab, like the original appending a tab whose controller
    /// opened successfully. With a single pristine tab on screen it is cleared away first — the original
    /// removes the placeholder tab rather than leaving it behind the real project.</summary>
    public ProjectWorkspaceTab AdoptOpened(EditorDocument document, string path)
    {
        if (Tabs.Count == 1 && Current.IsPristineUntitled) Tabs.Clear();
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        var tab = new ProjectWorkspaceTab
        {
            Document = document,
            ProjectPath = path,
            DefaultName = name.Length > 0 ? name : "Untitled",
        };
        Tabs.Add(tab);
        SelectedId = tab.Id;
        return tab;
    }

    /// <summary>Removes a closed tab: closing the only project leaves a fresh untitled one, as the
    /// original's <c>removeTab</c> does; closing the selected neighbour selects the tab that took its
    /// place. Whether a close is allowed (the unsaved-changes question) is the window's call before it
    /// gets here — the original routes that through its controller.</summary>
    public void RemoveTab(Guid id)
    {
        int index = IndexOf(id);
        if (index < 0) return;
        bool wasSelected = id == SelectedId;
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            var replacement = AddTab(reuseEmpty: false);
            SelectedId = replacement.Id;
            return;
        }
        if (wasSelected) SelectedId = Tabs[Math.Min(index, Tabs.Count - 1)].Id;
    }

    /// <summary>The order Quit asks about unsaved projects: the tab on screen first, then the rest left to
    /// right, so it never jumps to another project before the one you are viewing.</summary>
    public IReadOnlyList<ProjectWorkspaceTab> QuitOrder()
    {
        var order = new List<ProjectWorkspaceTab> { Current };
        order.AddRange(Tabs.Where(tab => tab.Id != SelectedId));
        return order;
    }

    /// <summary>Runs a management command with the original's re-entry guard. The window wraps its open,
    /// close and quit flows in this; inside the action <see cref="CanSwitch"/> reads false.</summary>
    public void Managing(Action body)
    {
        if (IsManaging) return;
        IsManaging = true;
        try { body(); }
        finally { IsManaging = false; }
    }

    /// <summary>The same guard for flows that await (the window's close and quit loops ask the user
    /// between tabs). Try first, and only the caller that got true may call End; while managed,
    /// <see cref="CanSwitch"/> reads false, as in the original.</summary>
    public bool TryBeginManaging()
    {
        if (IsManaging) return false;
        IsManaging = true;
        return true;
    }

    public void EndManaging() => IsManaging = false;

    /// <summary>The original's closeWindow reset: every project goes and a fresh Untitled starts the
    /// list over, numbering from two again.</summary>
    public void ResetForNewWindow()
    {
        Tabs.Clear();
        nextNumber = 2;
        var first = new ProjectWorkspaceTab();
        Tabs.Add(first);
        SelectedId = first.Id;
    }

    /// <summary>The original's direct move inside its management flows (confirmQuit assigns selectedID
    /// while isManaging holds): select without the canSwitch gate, for hosts already inside
    /// <see cref="TryBeginManaging"/>. Unknown ids change nothing and answer false.</summary>
    public bool MoveSelection(Guid id)
    {
        if (id == SelectedId) return true;
        if (IndexOf(id) < 0) return false;
        SelectedId = id;
        return true;
    }

    private int IndexOf(Guid id)
    {
        for (int i = 0; i < Tabs.Count; i++) if (Tabs[i].Id == id) return i;
        return -1;
    }

    /// <summary>Compares two project locations the way the original compares resolved URLs: the same path
    /// in the same casing-insensitive filesystem form is the same project, however it was spelled.</summary>
    private static bool SamePath(string? left, string right)
    {
        if (left is null) return false;
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(left.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                System.IO.Path.GetFullPath(right.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
