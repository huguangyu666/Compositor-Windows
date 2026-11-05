using Compositor.Core;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Pins the tab registry's rules to the original's ProjectWorkspace.swift: reuse of a still-empty first
/// tab, numbered untitled names, the switch guard, drag reordering with clamping, path-deduped opens,
/// neighbour selection on close and the order Quit asks about. Step 1 of the multi-document port; the
/// window hosting it is the following step.
/// </summary>
public sealed class ProjectWorkspaceTests
{
    [Fact]
    public void ANewWorkspaceStartsWithOneUntitledSelectedTab()
    {
        var workspace = new ProjectWorkspace();
        Assert.Single(workspace.Tabs);
        Assert.Equal("Untitled", workspace.Current.Title);
        Assert.Equal(workspace.Tabs[0].Id, workspace.SelectedId);
        Assert.True(workspace.CanSwitch);
    }

    [Fact]
    public void ASecondNewCanvasNamesUntitledTwoAndThenThree()
    {
        var workspace = new ProjectWorkspace();
        var second = workspace.NewCanvas();
        var third = workspace.NewCanvas();
        Assert.Equal(3, workspace.Tabs.Count);
        Assert.Equal("Untitled 2", second.Title);
        Assert.Equal("Untitled 3", third.Title);
        Assert.Equal(third.Id, workspace.SelectedId);
    }

    [Fact]
    public void ADropOntoASingleUntouchedTabReusesItInsteadOfStacking()
    {
        var workspace = new ProjectWorkspace();
        var reused = workspace.AddTab();
        Assert.Single(workspace.Tabs);
        Assert.Equal(workspace.Tabs[0].Id, reused.Id);
    }

    [Fact]
    public void ADropDoesNotReuseATabSomeoneAlreadyChanged()
    {
        var workspace = new ProjectWorkspace();
        var document = EditorDocument.Create(8, 8);
        document.AddRasterMask(document.Snapshot.Manifest.Layers[0].Id, MaskSurface.Solid(revealing: true));
        Assert.True(document.IsDirty);
        workspace.Current.Document = document;
        var fresh = workspace.AddTab();
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.NotEqual(workspace.Tabs[0].Id, fresh.Id);
        Assert.Equal("Untitled 2", fresh.Title);
    }

    [Fact]
    public void ADropDoesNotReuseASavedFirstTabEvenUnedited()
    {
        var workspace = new ProjectWorkspace();
        workspace.Current.Document = EditorDocument.Create(8, 8);
        workspace.Current.ProjectPath = @"C:\projects\kept.comp";
        Assert.False(workspace.Current.IsPristineUntitled);
        var added = workspace.AddTab();
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.NotSame(workspace.Tabs[0], added);
    }

    [Fact]
    public void DraggingATabLandsAtTheClampedIndexAndNeverMovesTheWrongOne()
    {
        var workspace = new ProjectWorkspace();
        var a = workspace.Tabs[0].Id;
        var b = workspace.NewCanvas().Id;
        var c = workspace.NewCanvas().Id;
        workspace.MoveTab(a, 99);                                     // clamps to the last slot
        Assert.Equal(new[] { b, c, a }, workspace.Tabs.Select(tab => tab.Id));
        workspace.MoveTab(a, 0);                                      // drags back across the strip
        Assert.Equal(new[] { a, b, c }, workspace.Tabs.Select(tab => tab.Id));
        workspace.MoveTab(Guid.NewGuid(), 1);                         // unknown tab changes nothing
        workspace.MoveTab(a, 0);                                      // landing where it already is
        Assert.Equal(new[] { a, b, c }, workspace.Tabs.Select(tab => tab.Id));
    }

    [Fact]
    public void SwitchingAsksTheGuardAndCommitsBeforeItMovesSelection()
    {
        var workspace = new ProjectWorkspace();
        var second = workspace.NewCanvas();
        var first = workspace.Tabs[0].Id;
        Assert.False(workspace.Select(second.Id));                    // already selected
        bool committed = false;
        workspace.SwitchGuard = () => true;                           // a draft dialog is open
        Assert.False(workspace.Select(first, () => committed = true));
        Assert.False(committed);
        Assert.Equal(second.Id, workspace.SelectedId);
        workspace.SwitchGuard = null;                                 // the draft closed
        Assert.True(workspace.Select(first, () => committed = true));
        Assert.True(committed);
        Assert.Equal(first, workspace.SelectedId);
        Assert.False(workspace.Select(Guid.NewGuid()));               // unknown tab
    }

    [Fact]
    public void ANewProjectRefusesWhileTheGuardIsDown()
    {
        var workspace = new ProjectWorkspace();
        workspace.SwitchGuard = () => true;
        Assert.Single(workspace.Tabs);
        Assert.Same(workspace.Tabs[0], workspace.NewCanvas());
    }

    [Fact]
    public void OpeningAProjectThatIsAlreadyATabSelectsItInsteadOfLoadingTwice()
    {
        var workspace = new ProjectWorkspace();
        workspace.AdoptOpened(EditorDocument.Create(8, 8), @"C:\projects\Alpha.comp");
        workspace.AddTab(reuseEmpty: false);
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.True(workspace.TrySelectOpened(@"c:\projects\ALPHA.comp\"));
        Assert.Equal(2, workspace.Tabs.Count);
        Assert.Equal("Alpha.comp", workspace.Current.Title);
        Assert.False(workspace.TrySelectOpened(@"C:\projects\Beta.comp"));
    }

    [Fact]
    public void TheWindowResetStartsTheStripOverAndNumberingAgainFromTwo()
    {
        var workspace = new ProjectWorkspace();
        workspace.NewCanvas();
        workspace.NewCanvas();
        workspace.ResetForNewWindow();                              // the original's closeWindow reset
        Assert.Single(workspace.Tabs);
        Assert.Equal("Untitled", workspace.Current.Title);
        Assert.Equal("Untitled 2", workspace.NewCanvas().Title);    // numbering restarted
    }

    [Fact]
    public void TheOpenedTabLookupFindsThePathWithoutMovingTheSelection()
    {
        var workspace = new ProjectWorkspace();
        var opened = workspace.AdoptOpened(EditorDocument.Create(8, 8), @"C:\projects\Alpha.comp");
        workspace.AddTab(reuseEmpty: false);
        Assert.Null(workspace.OpenedTab(@"C:\projects\Missing.comp"));
        var found = workspace.OpenedTab(@"c:\projects\Alpha.comp\");
        Assert.Same(opened, found);                                        // the dedupe lookup, case-free
        Assert.NotEqual(opened.Id, workspace.SelectedId);                  // and it did not move there
    }

    [Fact]
    public void AFirstLoadedProjectTakesThePlaceOfTheUntouchedPlaceholder()
    {
        var workspace = new ProjectWorkspace();
        var loaded = workspace.AdoptOpened(EditorDocument.Create(8, 8), @"C:\projects\Gamma.comp");
        Assert.Single(workspace.Tabs);                                // the placeholder gave its slot up
        Assert.Same(loaded, workspace.Current);
        Assert.Equal("Gamma.comp", loaded.Title);
    }

    [Fact]
    public void ALoadedProjectJoinsOtherTabsWithoutDisturbingThem()
    {
        var workspace = new ProjectWorkspace();
        var kept = workspace.AddTab(reuseEmpty: false);               // two pristine tabs: no lone placeholder
        var loaded = workspace.AdoptOpened(EditorDocument.Create(8, 8), @"C:\projects\Delta.comp");
        Assert.Equal(3, workspace.Tabs.Count);                        // the Mac's count==1 guard: nothing removed
        Assert.Same(kept, workspace.Tabs[1]);
        Assert.Same(loaded, workspace.Current);
    }

    [Fact]
    public void ClosingTheSelectedTabSelectsTheNeighbourThatTookItsPlace()
    {
        var workspace = new ProjectWorkspace();
        var a = workspace.Tabs[0].Id;
        var b = workspace.NewCanvas().Id;
        var c = workspace.NewCanvas().Id;
        workspace.Select(b);
        workspace.RemoveTab(b);
        Assert.Equal(c, workspace.SelectedId);                        // index clamps to the new slot
        workspace.RemoveTab(Guid.NewGuid());                          // unknown id changes nothing
        Assert.Equal(2, workspace.Tabs.Count);
        workspace.RemoveTab(a);                                       // not the selected one
        Assert.Equal(1, workspace.Tabs.Count);
        Assert.Equal(c, workspace.SelectedId);
    }

    [Fact]
    public void ClosingTheOnlyTabLeavesAFreshUntitledProject()
    {
        var workspace = new ProjectWorkspace();
        var original = workspace.Tabs[0].Id;
        workspace.RemoveTab(original);
        Assert.Single(workspace.Tabs);
        Assert.NotEqual(original, workspace.Tabs[0].Id);
        Assert.Equal("Untitled 2", workspace.Current.Title);          // the original numbers forward too
        Assert.Null(workspace.Current.Document);
    }

    [Fact]
    public void QuitAsksAboutTheVisibleProjectFirstAndTheRestLeftToRight()
    {
        var workspace = new ProjectWorkspace();
        var a = workspace.Tabs[0];
        var b = workspace.NewCanvas();
        var c = workspace.NewCanvas();
        workspace.Select(a.Id);
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, workspace.QuitOrder().Select(tab => tab.Id));   // visible first, rest left to right
        workspace.Select(b.Id);
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, workspace.QuitOrder().Select(tab => tab.Id));
    }

    [Fact]
    public void ManagementBlocksReentryAndSuspendsSwitching()
    {
        var workspace = new ProjectWorkspace();
        var second = workspace.NewCanvas().Id;
        int bodies = 0;
        workspace.Managing(() =>
        {
            bodies += 1;
            Assert.False(workspace.CanSwitch);
            Assert.False(workspace.Select(second));                   // switching is barred mid-management
            workspace.Managing(() => bodies += 1);                    // the nested call must not run
        });
        Assert.Equal(1, bodies);
        Assert.True(workspace.CanSwitch);
    }

    [Fact]
    public void TheTitleFollowsTheProjectPathAndFallsBackToTheDefaultName()
    {
        var tab = new ProjectWorkspaceTab();
        Assert.Equal("Untitled", tab.Title);
        tab.ProjectPath = @"C:\projects\My Photo.comp";
        Assert.Equal("My Photo.comp", tab.Title);
        tab.ProjectPath = @"C:\projects\My Photo.comp\";
        Assert.Equal("My Photo.comp", tab.Title);                     // trailing separator reads the same
    }
}
