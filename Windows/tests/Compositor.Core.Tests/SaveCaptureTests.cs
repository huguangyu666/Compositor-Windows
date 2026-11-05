using Xunit;

namespace Compositor.Core.Tests;

public class SaveCaptureTests
{
    [Fact]
    public void SettledSaveFreezesMetadataAndSharesImmutableAssets()
    {
        var owner = EditorDocument.Create(32, 24); var layer = owner.Snapshot.Manifest.Layers[0];
        var mask = new MaskSurface(32, 24); mask.Pixels.AsSpan().Fill(255); owner.AddRasterMask(layer.Id, mask);
        var capture = owner.CaptureSave();
        Assert.NotSame(owner.Snapshot.Manifest, capture.Snapshot.Manifest);
        Assert.Same(owner.Snapshot.Images[layer.Id], capture.Snapshot.Images[layer.Id]);
        Assert.Same(mask, capture.Snapshot.Masks[layer.Id]);
        owner.RenameLayer(layer.Id, "Later"); owner.RemoveMask(layer.Id);
        Assert.Equal("Background", capture.Snapshot.Manifest.Layers[0].Name);
        Assert.NotNull(capture.Snapshot.Manifest.Layers[0].MaskFile);
        Assert.Same(mask, capture.Snapshot.Masks[layer.Id]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyLiveStrokePixelsNeedCopies(bool onMask)
    {
        var owner = EditorDocument.Create(32, 24); var id = owner.Snapshot.Manifest.Layers[0].Id;
        if (onMask) { var mask = new MaskSurface(32, 24); mask.Pixels.AsSpan().Fill(255); owner.AddRasterMask(id, mask); }
        var key = new object(); Assert.True(owner.BeginStroke(key, id, new(3, 1, 1, 0, 0, 0), new(5, 12), onMask, onMask));
        var capture = owner.CaptureSave();
        var frozen = onMask ? capture.Snapshot.Masks[id].Pixels : capture.Snapshot.Images[id].Pixels;
        var before = frozen.ToArray();
        Assert.NotSame(onMask ? owner.Snapshot.Masks[id].Pixels : owner.Snapshot.Images[id].Pixels, frozen);
        if (onMask) Assert.Same(owner.Snapshot.Images[id], capture.Snapshot.Images[id]);
        owner.ContinueStroke(key, new(24, 12));
        Assert.Equal(before, frozen);
        Assert.False(frozen.AsSpan().SequenceEqual(onMask ? owner.Snapshot.Masks[id].Pixels : owner.Snapshot.Images[id].Pixels));
        owner.EndStroke(key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveCloneAndBlurBuffersAreFrozen(bool blur)
    {
        var owner = EditorDocument.Create(32, 24); var id = owner.Snapshot.Manifest.Layers[0].Id;
        new Random(7301).NextBytes(owner.Snapshot.Images[id].Pixels);
        var key = new object();
        if (blur) { Assert.True(owner.BeginBlurStroke(id, 2, key)); owner.BlurStampApply(id, new(5, 12), 3, 1); }
        else Assert.True(owner.BeginCloneStroke(id, new(5, 5), new(15, 12), 3, 1, false, key));
        var capture = owner.CaptureSave(); var frozen = capture.Snapshot.Images[id].Pixels; var before = frozen.ToArray();
        Assert.NotSame(owner.Snapshot.Images[id].Pixels, frozen);
        if (blur) { owner.BlurStampApply(id, new(24, 12), 3, 1); owner.EndBlurStroke(); }
        else { owner.CloneStampAppend(id, new(5, 5), new(24, 12), 3, 1); owner.EndCloneStroke(); }
        Assert.Equal(before, frozen);
    }

    [Fact]
    public async Task CompletedCaptureTracksDiskWithoutMarkingLaterMetadataSaved()
    {
        using var fixture = new SaveFolder();
        var owner = EditorDocument.Create(32, 24); var id = owner.Snapshot.Manifest.Layers[0].Id;
        owner.RenameLayer(id, "Captured"); var capture = owner.CaptureSave();
        owner.RenameLayer(id, "Later"); await ProjectStore.SaveAsync(capture.Snapshot, fixture.Path);
        Assert.False(owner.CompleteSave(capture, fixture.Path)); Assert.True(owner.IsDirty);
        Assert.Equal(ProjectStore.ComputeDigest(fixture.Path), owner.SavedDiskDigest);
        Assert.Contains(owner.Snapshot.Manifest.Layers[0].ImageFile!, owner.UnchangedAssetNames(fixture.Path));
        Assert.Equal("Captured", (await ProjectStore.LoadAsync(fixture.Path)).Manifest.Layers[0].Name);
        var next = owner.CaptureSave();
        await ProjectStore.SaveAsync(next.Snapshot, fixture.Path, owner.UnchangedAssetNames(fixture.Path), expectedDiskDigest: owner.SavedDiskDigest);
        Assert.True(owner.CompleteSave(next, fixture.Path)); Assert.False(owner.IsDirty);
    }

    [Fact]
    public async Task SessionSelectionDoesNotInvalidateSavedContent()
    {
        using var fixture = new SaveFolder(); var owner = EditorDocument.Create(32, 24);
        var capture = owner.CaptureSave(); owner.SetSelection(Selection.Rectangle(32, 24, 2, 3, 8, 9));
        await ProjectStore.SaveAsync(capture.Snapshot, fixture.Path);
        Assert.True(owner.CompleteSave(capture, fixture.Path)); Assert.False(owner.IsDirty);
        Assert.NotNull(owner.ActiveSelection);
    }

    [Fact]
    public void AnotherDocumentCannotAcknowledgeTheCapture()
    {
        var owner = EditorDocument.Create(32, 24); var other = EditorDocument.Create(32, 24);
        Assert.Throws<ArgumentException>(() => other.CompleteSave(owner.CaptureSave(), "unused.comp"));
    }

    [Fact]
    public async Task AcknowledgingDiskStateDoesNotInvalidateContentOrEditingDrafts()
    {
        using var fixture = new SaveFolder(); var owner = EditorDocument.Create(32, 24);
        var capture = owner.CaptureSave(); await ProjectStore.SaveAsync(capture.Snapshot, fixture.Path);
        int notifications = 0; owner.Changed += () => notifications++;
        Assert.True(owner.CompleteSave(capture, fixture.Path));
        Assert.Equal(0, notifications);
    }

    private sealed class SaveFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "compositor-save-capture-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Path);
            if (target.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()) + "compositor-save-capture-", StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
