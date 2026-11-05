using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class SpotHealingTests
{
    public static IEnumerable<object[]> ReferenceCases()
    {
        using var stream = typeof(SpotHealingTests).Assembly.GetManifestResourceStream("Compositor.Core.Tests.Fixtures.spot-healing-reference.json")!;
        using var json = JsonDocument.Parse(stream);
        foreach (var row in json.RootElement.EnumerateArray())
            yield return [row.GetProperty("name").GetString()!, row.GetProperty("mode").GetInt32(), row.GetProperty("seed").GetUInt32(),
                row.GetProperty("opacity").GetDouble(), row.GetProperty("input").GetString()!, row.GetProperty("output").GetString()!];
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void KernelMatchesOriginalC(string name, int mode, uint seed, double opacity, string inputHash, string outputHash)
    {
        var (image, coverage) = HealingTestImages.Create(name);
        Assert.Equal(inputHash, Hash(image));
        var original = (byte[])image.Pixels.Clone();
        var healed = SpotHealingEngine.Heal(image, coverage, opacity, (SpotHealingMode)mode, seed) ?? image;
        Assert.Equal(outputHash, Hash(healed));
        Assert.Equal(original, image.Pixels);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            int i = y * image.Stride + x * 4;
            if (coverage.Pixels[y * image.Width + x] == 0)
                Assert.Equal(original.AsSpan(i, 4).ToArray(), healed.Pixels.AsSpan(i, 4).ToArray());
            for (int c = 0; c < 3; c++) Assert.True(healed.Pixels[i + c] <= healed.Pixels[i + 3]);
        }
    }

    [Fact]
    public void InvalidInputsAndCancellationCannotMutateSource()
    {
        var (image, coverage) = HealingTestImages.Create("pattern");
        var original = (byte[])image.Pixels.Clone();
        Assert.Throws<ArgumentException>(() => SpotHealingEngine.Heal(image, new MaskSurface(1, 1), 1, SpotHealingMode.ContentAware, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpotHealingEngine.Heal(image, coverage, double.NaN, SpotHealingMode.ContentAware, 1));
        Assert.Throws<InvalidOperationException>(() => SpotHealingEngine.Heal(image, coverage, 1, SpotHealingMode.ContentAware, 1, workPixelBudget: 1));
        Assert.Throws<OperationCanceledException>(() => SpotHealingEngine.Heal(image, coverage, 1, SpotHealingMode.ContentAware, 1,
            cancellationToken: new CancellationToken(true)));
        Assert.Null(SpotHealingEngine.Heal(image, new MaskSurface(image.Width, image.Height), 1, SpotHealingMode.ContentAware, 1));
        Assert.Null(SpotHealingEngine.Heal(image, coverage, 0, SpotHealingMode.ContentAware, 1));
        Assert.Equal(original, image.Pixels);
    }

    [Fact]
    public void StrokePreviewDoesNotEditDocumentAndCommitHasOneUndo()
    {
        var doc = MakeDocument();
        var layer = doc.Snapshot.Manifest.Layers[0];
        var original = doc.Snapshot.Images[layer.Id];
        var stroke = doc.BeginHealingStroke(layer.Id, new BrushSettings(3, .6, .8, 255, 0, 0), SpotHealingMode.ContentAware)!;
        stroke.Append(new(16, 16));
        stroke.Append(new(18, 16));
        Assert.Same(original, doc.Snapshot.Images[layer.Id]);
        Assert.False(doc.IsDirty);
        Assert.False(doc.CanUndo);
        Assert.NotEqual(original.Pixels, stroke.PreviewSnapshot.Images[layer.Id].Pixels);
        var result = stroke.Render(seed: 123)!;
        Assert.True(doc.CommitHealingStroke(result));
        Assert.True(doc.Undo());
        Assert.Same(original, doc.Snapshot.Images[layer.Id]);
        Assert.False(doc.CanUndo);
        Assert.True(doc.Redo());
        Assert.Equal(result.Snapshot.Images[layer.Id].Pixels, doc.Snapshot.Images[layer.Id].Pixels);
    }

    [Fact]
    public void DiscardedOrStaleStrokePreservesHistoryAndSelectionLimitsCoverage()
    {
        var doc = MakeDocument();
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        doc.SetSelection(Selection.Rectangle(32, 32, 14, 14, 2, 2));
        var stroke = doc.BeginHealingStroke(id, new(5, 1, 1, 0, 0, 0), SpotHealingMode.CreateTexture)!;
        stroke.Append(new(16, 16));
        Assert.Equal(0, stroke.Coverage.Pixels[16 * stroke.Coverage.Width + 16]);
        Assert.True(stroke.Coverage.Pixels[14 * stroke.Coverage.Width + 14] > 0);
        var result = stroke.Render(3)!;
        doc.SetSelection(null);
        Assert.False(doc.CommitHealingStroke(result));
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("folder")]
    [InlineData("adjustment")]
    [InlineData("hidden")]
    [InlineData("empty")]
    public void RefusedTargetNeverOpensHistory(string reason)
    {
        var doc = MakeDocument();
        var layer = doc.Snapshot.Manifest.Layers[0];
        if (reason == "folder") layer.IsGroup = true;
        if (reason == "adjustment") layer.Adjustment = new() { Kind = AdjustmentKind.Invert };
        if (reason == "hidden") layer.IsVisible = false;
        if (reason == "empty") doc.SetSelection(new Selection(new MaskSurface(32, 32), new(0, 0, 0, 0)));
        Assert.Null(doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware, onMask: reason == "mask"));
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(SpotHealingMode.ContentAware)]
    [InlineData(SpotHealingMode.CreateTexture)]
    [InlineData(SpotHealingMode.ProximityMatch)]
    public void ConstantImageIsANoOp(SpotHealingMode mode)
    {
        var image = new Surface(32, 32);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        { image.Pixels[i] = 60; image.Pixels[i + 1] = 30; image.Pixels[i + 2] = 15; image.Pixels[i + 3] = 120; }
        var coverage = new MaskSurface(32, 32);
        coverage.Pixels[16 * 32 + 16] = 255;
        Assert.Null(SpotHealingEngine.Heal(image, coverage, 1, mode, 1));
    }

    [Fact]
    public void MalformedStrideAndDimensionsAreRejectedBeforeAllocation()
    {
        var coverage = new MaskSurface(32, 32);
        coverage.Pixels[16 * 32 + 16] = 255;
        Assert.Throws<ArgumentException>(() => SpotHealingEngine.Heal(new Surface(32, 32, new byte[4096], 4), coverage, 1, SpotHealingMode.ContentAware, 1));
        Assert.Throws<ArgumentException>(() => SpotHealingEngine.Heal(new Surface(-1, 32, [], -1), coverage, 1, SpotHealingMode.ContentAware, 1));
    }

    [Fact]
    public void CanceledRenderAndEmptyStrokeKeepRedoAndPixels()
    {
        var doc = MakeDocument();
        var layer = doc.Snapshot.Manifest.Layers[0];
        doc.RenameLayer(layer.Id, "Renamed");
        doc.Undo();
        var source = doc.Snapshot.Images[layer.Id];
        var empty = doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        Assert.Null(empty.Render(1));
        var canceled = doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        canceled.Append(new(16, 16));
        Assert.Throws<OperationCanceledException>(() => canceled.Render(1, new CancellationToken(true)));
        Assert.True(doc.CanRedo);
        Assert.False(doc.CanUndo);
        Assert.Same(source, doc.Snapshot.Images[layer.Id]);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void LateResultCannotOverwriteReplacedOrModifiedPixels()
    {
        var doc = MakeDocument();
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        var stroke = doc.BeginHealingStroke(id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        stroke.Append(new(16, 16));
        var result = stroke.Render(1)!;
        doc.Snapshot.Images[id].MarkModified();
        Assert.False(doc.CommitHealingStroke(result));
        Assert.False(doc.CanUndo);
        var replacement = new Surface(32, 32);
        doc.ReplaceLayerImage(id, replacement);
        Assert.False(doc.CommitHealingStroke(result));
        Assert.Same(replacement, doc.Snapshot.Images[id]);
    }

    [Fact]
    public void ExpandedImageAndAttachedMaskPreservePlacementAndUndo()
    {
        var doc = MakeDocument();
        doc.Snapshot.Manifest.Width = doc.Snapshot.Manifest.Height = 64;
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.Origin = new(16, 16);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = MaskSurface.Solid(true);
        var original = doc.Snapshot.Images[layer.Id];
        var stroke = doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.CreateTexture)!;
        stroke.Append(new(16, 32));
        var result = stroke.Render(1)!;
        Assert.NotNull(result);
        var healed = result.Snapshot.Images[layer.Id];
        var placed = result.Snapshot.Manifest.Layers[0].Transform;
        Assert.True(healed.Width > original.Width);
        Assert.Equal(original.Height, healed.Height);
        var anchor = BrushEngine.DocumentToPixel(placed, healed.Width, healed.Height, new(16.5, 16.5));
        int index = (int)anchor.Y * healed.Stride + (int)anchor.X * 4;
        Assert.Equal(original.Pixels.AsSpan(0, 4).ToArray(), healed.Pixels.AsSpan(index, 4).ToArray());
        var mask = result.Snapshot.Masks[layer.Id];
        Assert.Equal(healed.Width, mask.Width);
        Assert.Equal(healed.Height, mask.Height);
        Assert.All(mask.Pixels, value => Assert.Equal(255, value));
        Assert.True(doc.CommitHealingStroke(result));
        Assert.True(doc.Undo());
        Assert.Same(original, doc.Snapshot.Images[layer.Id]);
        Assert.Equal(1, doc.Snapshot.Masks[layer.Id].Width);
    }

    [Theory]
    [InlineData(45, false, false)]
    [InlineData(-45, true, false)]
    [InlineData(0, true, true)]
    public void RotatedFlippedScaledStrokeKeepsPixelMapping(double rotation, bool flipX, bool flipY)
    {
        var doc = MakeDocument();
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.Size = new(40, 40);
        layer.Transform.Origin = new(-4, -4);
        layer.Transform.Rotation = rotation;
        layer.Transform.FlipX = flipX; layer.Transform.FlipY = flipY;
        var source = doc.Snapshot.Images[layer.Id];
        var center = BrushEngine.PixelToDocument(layer.Transform, 32, 32, new(16, 16));
        var anchor = BrushEngine.PixelToDocument(layer.Transform, 32, 32, new(.5, .5));
        var stroke = doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        stroke.Append(center);
        var result = stroke.Render(1)!;
        var image = result.Snapshot.Images[layer.Id];
        var transform = result.Snapshot.Manifest.Layers[0].Transform;
        var pixel = BrushEngine.DocumentToPixel(transform, image.Width, image.Height, anchor);
        int index = (int)Math.Floor(pixel.Y) * image.Stride + (int)Math.Floor(pixel.X) * 4;
        Assert.Equal(source.Pixels.AsSpan(0, 4).ToArray(), image.Pixels.AsSpan(index, 4).ToArray());
        var mapped = BrushEngine.PixelToDocument(transform, image.Width, image.Height, pixel);
        Assert.Equal(anchor.X, mapped.X, 8); Assert.Equal(anchor.Y, mapped.Y, 8);
        Assert.Equal(rotation, transform.Rotation);
        Assert.Equal(flipX, transform.FlipX); Assert.Equal(flipY, transform.FlipY);
        Assert.Equal(40d / 32, transform.Size.Width / image.Width, 8);
    }

    [Fact]
    public void PreviewDoesNotEmitDocumentEventsAndCommitEmitsOnce()
    {
        var doc = MakeDocument();
        var id = doc.Snapshot.Manifest.Layers[0].Id;
        int changed = 0, pixels = 0;
        doc.Changed += () => changed++; doc.PixelsChanged += () => pixels++;
        var stroke = doc.BeginHealingStroke(id, new(3, .5, 1, 0, 0, 0), SpotHealingMode.ProximityMatch)!;
        stroke.Append(new(16, 16)); stroke.Append(new(17, 16));
        Assert.Equal(0, changed); Assert.Equal(0, pixels);
        Assert.True(doc.CommitHealingStroke(stroke.Render(1)!));
        Assert.Equal(1, changed); Assert.Equal(0, pixels);
    }

    [Fact]
    public void FeatheredSelectionClipsTheRepairAfterPatchSearch()
    {
        var unrestricted = MakeDocument();
        var id = unrestricted.Snapshot.Manifest.Layers[0].Id;
        var fullStroke = unrestricted.BeginHealingStroke(id, new(5, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        fullStroke.Append(new(16, 16));
        var full = fullStroke.Render(1)!.Snapshot.Images[id];
        var selected = MakeDocument();
        id = selected.Snapshot.Manifest.Layers[0].Id;
        var selection = Selection.Rectangle(32, 32, 14, 14, 4, 4).Feathered(2);
        selected.SetSelection(selection);
        var original = selected.Snapshot.Images[id];
        var stroke = selected.BeginHealingStroke(id, new(5, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware)!;
        stroke.Append(new(16, 16));
        var result = stroke.Render(1)!.Snapshot.Images[id];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
        for (int c = 0; c < 4; c++)
        {
            int i = y * original.Stride + x * 4 + c;
            double amount = selection.CoverageAt(x, y) / 255d;
            byte expected = (byte)Math.Round(original.Pixels[i] + (full.Pixels[i] - original.Pixels[i]) * amount, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, result.Pixels[i]);
        }
    }

    [Fact]
    public void OversizedWorkingGridIsRefusedWithoutHistory()
    {
        var doc = MakeDocument();
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.Size = new(.01, .01);
        Assert.Null(doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.ContentAware));
        Assert.False(doc.CanUndo);
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlacedMaskAndRetiredMetadataStayCorrectAcrossAssetGrowth(bool linked)
    {
        var doc = MakeDocument();
        doc.Snapshot.Manifest.Width = doc.Snapshot.Manifest.Height = 64;
        var layer = doc.Snapshot.Manifest.Layers[0];
        layer.Transform.Origin = new(16, 16);
        layer.MaskLinked = linked;
        layer.MaskPlacement = new() { Origin = new(2, 3), Size = new(10, 12), Rotation = 25 };
        layer.Text = new() { Content = "Text", FontName = "Segoe UI", FontSize = 12 };
        var mask = MaskSurface.Solid(true);
        ((Dictionary<Guid, MaskSurface>)doc.Snapshot.Masks)[layer.Id] = mask;
        var stroke = doc.BeginHealingStroke(layer.Id, new(3, 1, 1, 0, 0, 0), SpotHealingMode.CreateTexture)!;
        stroke.Append(new(16, 32));
        Assert.NotNull(stroke.PreviewSnapshot.Manifest.Layers[0].Text);
        var result = stroke.Render(1)!;
        var healed = result.Snapshot.Manifest.Layers[0];
        Assert.Null(healed.Text);
        Assert.Equal(layer.MaskPlacement, healed.MaskPlacement);
        Assert.Equal(linked, healed.MaskLinked);
        Assert.Same(mask, result.Snapshot.Masks[layer.Id]);
        Assert.True(doc.CommitHealingStroke(result));
        Assert.True(doc.Undo());
        Assert.Equal("Text", doc.Snapshot.Manifest.Layers[0].Text!.Content);
    }

    private static EditorDocument MakeDocument()
    {
        var doc = EditorDocument.Create(32, 32);
        var layer = doc.Snapshot.Manifest.Layers[0];
        ((Dictionary<Guid, Surface>)doc.Snapshot.Images)[layer.Id] = HealingTestImages.Create("pattern").Image;
        return doc;
    }
    private static string Hash(Surface surface) => Convert.ToHexString(SHA256.HashData(surface.Pixels));
}
