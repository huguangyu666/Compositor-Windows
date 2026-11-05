using Xunit;

namespace Compositor.Core.Tests;

/// <summary>Guide add/move/remove/clear with undo, and crop displacement interplay.</summary>
public class GuideTests
{
    [Fact]
    public void AddGuideThenUndoRemovesIt()
    {
        var document = EditorDocument.Create(64, 64);

        document.AddGuide(GuideAxis.Vertical, 32);

        Assert.Single(document.Snapshot.Manifest.Guides!);
        Assert.Equal(32, document.Snapshot.Manifest.Guides![0].Position);

        document.Undo();
        // The memento round-trip omits an empty guides list entirely.
        Assert.True(document.Snapshot.Manifest.Guides is null or { Count: 0 });
    }

    [Fact]
    public void MoveGuideIsUndoable()
    {
        var document = EditorDocument.Create(64, 64);
        var guide = document.AddGuide(GuideAxis.Horizontal, 10);

        document.MoveGuide(guide.Id, 40);

        Assert.Equal(40, document.Snapshot.Manifest.Guides![0].Position);

        document.Undo();
        Assert.Equal(10, document.Snapshot.Manifest.Guides![0].Position);
    }

    [Fact]
    public void DragGuideCoalescesIntoOneUndoStep()
    {
        var document = EditorDocument.Create(64, 64);
        var guide = document.AddGuide(GuideAxis.Vertical, 10);

        var key = new object();
        for (int x = 12; x <= 40; x += 4)
        {
            document.MoveGuide(guide.Id, x, key);
        }

        Assert.Equal(40, document.Snapshot.Manifest.Guides![0].Position);

        document.Undo();
        Assert.Equal(10, document.Snapshot.Manifest.Guides![0].Position);
    }

    [Fact]
    public void RemoveGuideAndClear()
    {
        var document = EditorDocument.Create(64, 64);
        var first = document.AddGuide(GuideAxis.Horizontal, 10);
        document.AddGuide(GuideAxis.Horizontal, 20);

        Assert.True(document.RemoveGuide(first.Id));
        Assert.Single(document.Snapshot.Manifest.Guides!);

        document.ClearGuides();
        Assert.Empty(document.Snapshot.Manifest.Guides!);

        // Clearing when already empty reports false.
        Assert.False(document.ClearGuides());
    }

    [Fact]
    public void RemoveGuideIsUndoable()
    {
        var document = EditorDocument.Create(64, 64);
        var guide = document.AddGuide(GuideAxis.Horizontal, 10);

        Assert.True(document.RemoveGuide(guide.Id));
        Assert.True(document.Snapshot.Manifest.Guides is null or { Count: 0 });

        // The removal must be recorded AFTER BeginChange, or undo restores an already-empty list.
        Assert.True(document.Undo());
        Assert.Single(document.Snapshot.Manifest.Guides!);
        Assert.Equal(10, document.Snapshot.Manifest.Guides![0].Position);
    }

    [Fact]
    public void CropShiftsGuidePositions()
    {
        var document = EditorDocument.Create(64, 64);
        document.AddGuide(GuideAxis.Vertical, 32);

        document.SetCanvasBounds(12, 0, 40, 64);

        Assert.Equal(20, document.Snapshot.Manifest.Guides![0].Position); // 32 − 12
    }
}
