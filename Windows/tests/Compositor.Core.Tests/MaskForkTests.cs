using System.Runtime.CompilerServices;
using Xunit;

namespace Compositor.Core.Tests;

public class MaskForkTests
{
    [Fact]
    public void JournalMergesSeveralUnrenderedCopiesAndKeepsOriginalPixels()
    {
        var original = new MaskSurface(80, 60); original.MarkModified();
        var first = original.CloneForEditing(); first.MarkModified(new(10, 12, 3, 4));
        var second = first.CloneForEditing(); second.MarkModified(new(40, 35, 5, 6));
        Assert.True(second.TryGetChangesFrom(original, original.Revision, out var dirty));
        Assert.Equal(new RectI(10, 12, 35, 29), dirty);
        Assert.Equal(original.Pixels, first.Pixels); Assert.NotSame(original.Pixels, first.Pixels);
        Assert.False(original.Clone().TryGetChangesFrom(original, original.Revision, out _));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("parent-changed")]
    [InlineData("depth")]
    public void MissingAncestryOrRevisionProofRequiresFullFallback(string change)
    {
        var original = new MaskSurface(80, 60); var first = original.CloneForEditing();
        first.MarkModified(new(1, 2, 3, 4));
        var second = first.CloneForEditing(); second.MarkModified(new(5, 6, 7, 8));
        switch (change)
        {
            case "unknown": second.MarkModified(); break;
            case "expired": for (int i = 0; i < 64; i++) second.MarkModified(new(1, 1, 1, 1)); break;
            case "parent-changed": first.MarkModified(new(50, 40, 1, 1)); break;
            default:
                var owners = new List<MaskSurface> { first, second };
                for (int i = 0; i < 65; i++) { second = second.CloneForEditing(); owners.Add(second); }
                Assert.False(second.TryGetChangesFrom(original, 0, out _)); GC.KeepAlive(owners); return;
        }
        Assert.False(second.TryGetChangesFrom(original, 0, out _));
    }

    [Fact]
    public void CopyOnlyProvesItsExactCapturedHistoricalRevision()
    {
        var original = new MaskSurface(80, 60); original.MarkModified(new(10, 10, 3, 3));
        var copy = original.CloneForEditing(); copy.MarkModified(new(20, 20, 3, 3));
        Assert.True(copy.TryGetChangesFrom(original, 0, out var dirty));
        Assert.Equal(new RectI(10, 10, 13, 13), dirty);
        original.MarkModified(new(70, 50, 3, 3));
        Assert.False(copy.TryGetChangesFrom(original, 0, out _));
        Assert.True(copy.TryGetChangesFrom(original, 1, out dirty)); Assert.Equal(new RectI(20, 20, 3, 3), dirty);
        Assert.False(copy.TryGetChangesFrom(original, 2, out _));
    }

    [Fact]
    public void WorkingCopyDoesNotRetainThePreviousMask()
    {
        var (copy, previous) = CopyWithoutOtherOwners(); GC.Collect();
        Assert.False(previous.TryGetTarget(out _)); GC.KeepAlive(copy);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (MaskSurface Copy, WeakReference<MaskSurface> Previous) CopyWithoutOtherOwners()
    {
        var original = new MaskSurface(80, 60);
        return (original.CloneForEditing(), new(original));
    }
}
