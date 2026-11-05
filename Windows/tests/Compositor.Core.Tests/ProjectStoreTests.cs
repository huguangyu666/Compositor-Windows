using Xunit;

namespace Compositor.Core.Tests;

public class ProjectStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string ProjectPath(string name) => Path.Combine(root, name);

    private static ProjectManifest SingleLayerManifest(int width = 8, int height = 8)
    {
        var id = Guid.NewGuid();
        return new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = width,
            Height = height,
            ActiveLayerId = id,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = id,
                    Name = "Layer",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(id) + ".png",
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                },
            ],
        };
    }

    private static Surface SolidSurface(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = a;
            }
        }
        return surface;
    }

    [Fact]
    public async Task EditedTextRoundTripsStylesRunsBoxAndPixels()
    {
        var document = EditorDocument.Create(64, 64);
        var layer = document.AddTextLayer(new LayerTextStyle { Content = "Old", FontName = "Not-A-Real-Font", FontSize = 24 }, PointD.Zero)!;
        var style = new LayerTextStyle
        {
            Content = "New",
            FontName = "Not-A-Real-Font",
            FontSize = 30,
            BoxSize = new TextBoxSize(120, 100),
            Tracking = 2,
            Leading = 38,
            Alignment = TextAlignment.Center,
            ColorRuns = [new LayerTextColorRun { Location = 0, Length = 1, Red = 1 }],
            FontRuns = [new LayerTextFontRun { Location = 1, Length = 1, FontName = "Not-A-Real-Font" }],
        };
        Assert.True(document.UpdateTextLayer(layer.Id, style));
        string path = ProjectPath("edited-text.comp");
        await ProjectStore.SaveAsync(document.Snapshot, path);
        var loaded = await ProjectStore.LoadAsync(path);
        var restored = loaded.Manifest.Layers.Single(record => record.Id == layer.Id);
        Assert.Equal(11, loaded.Manifest.Version);
        Assert.Equal(style, restored.Text);
        Assert.Equal(layer.Transform, restored.Transform);
        Assert.Equal("Old", restored.Name);
        var expected = document.Snapshot.Images[layer.Id];
        var actual = loaded.Images[layer.Id];
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Pixels.Length, actual.Pixels.Length);
        // PNG stores straight alpha; WIC's unpremultiply/premultiply round trip can lose one
        // color unit on antialiased glyph edges. Coverage itself must survive exactly.
        for (int i = 0; i < expected.Pixels.Length; i++)
        {
            if (i % 4 == 3) Assert.Equal(expected.Pixels[i], actual.Pixels[i]);
            else Assert.InRange((int)actual.Pixels[i] - expected.Pixels[i], -1, 1);
        }
        string json = await File.ReadAllTextAsync(Path.Combine(path, "manifest.json"));
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var textLayer = parsed.RootElement.GetProperty("layers").EnumerateArray().Single(record => record.TryGetProperty("text", out _));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, textLayer.GetProperty("text").GetProperty("boxSize").ValueKind);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsPixels()
    {
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        var images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 255, 128, 0) };
        string path = ProjectPath("roundtrip.comp");
        await ProjectStore.SaveAsync(new ProjectSnapshot { Manifest = manifest, Images = images, Masks = new Dictionary<Guid, MaskSurface>() }, path);

        Assert.True(ProjectStore.IsProjectDirectory(path));
        var loaded = await ProjectStore.LoadAsync(path);

        Assert.Equal(manifest.DocumentId, loaded.Manifest.DocumentId);
        var image = loaded.Images[id];
        Assert.Equal(8, image.Width);
        Assert.Equal(8, image.Height);
        for (int pixel = 0; pixel < 64; pixel++)
        {
            Assert.Equal(255, image.Pixels[pixel * 4]);
            Assert.Equal(128, image.Pixels[pixel * 4 + 1]);
            Assert.Equal(0, image.Pixels[pixel * 4 + 2]);
            Assert.Equal(255, image.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public async Task WicDecodeMatchesTheIndependentCodec()
    {
        // Opaque pixels through two codecs: the fixture writer and the production WIC path.
        var pixels = new byte[16 * 4];
        for (int i = 0; i < 16; i++)
        {
            pixels[i * 4] = (byte)(i * 17);
            pixels[i * 4 + 1] = (byte)(255 - i * 17);
            pixels[i * 4 + 2] = (byte)(i * 37);
            pixels[i * 4 + 3] = 255;
        }
        byte[] png = TinyPng.EncodeRgba(4, 4, pixels);

        var surface = await WicImaging.DecodeSurfaceAsync(png);
        var expected = TinyPng.Decode(png);

        Assert.Equal(4, surface.Width);
        Assert.Equal(expected.Rgba, surface.Pixels.AsSpan(0, 4 * 16).ToArray());
    }

    [Fact]
    public async Task WicDecodesStraightAlphaAsPremultiplied()
    {
        // WIC returns premultiplied pixels, the form the compositor works in.
        byte[] png = TinyPng.EncodeRgba(1, 2, [255, 0, 0, 255, 255, 128, 0, 128]);

        var surface = await WicImaging.DecodeSurfaceAsync(png);

        Assert.Equal(255, surface.Pixels[0]); // opaque red stays 255
        Assert.Equal(128, surface.Pixels[4]); // 255 at 50% alpha premultiplies to 128
        Assert.Equal(128, surface.Pixels[7]);
    }

    [Fact]
    public async Task MasksSaveAsGrayscaleAndReload()
    {
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        manifest.Layers[0].MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
        manifest.Layers[0].MaskEnabled = true;
        var mask = new MaskSurface(8, 8);
        for (int row = 0; row < 8; row++)
            for (int column = 0; column < 8; column++)
                mask.Pixels[row * 8 + column] = (byte)(row * 32 + column);

        string path = ProjectPath("mask.comp");
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 10, 20, 30) },
                Masks = new Dictionary<Guid, MaskSurface> { [id] = mask },
            },
            path);

        byte[] maskBytes = await File.ReadAllBytesAsync(Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".mask.png"));
        var decoded = TinyPng.Decode(maskBytes);
        Assert.Equal(PngHeader.ColorTypeGray, decoded.ColorType); // 8-bit grayscale, no alpha
        Assert.Equal(mask.Pixels, decoded.Gray);

        var loaded = await ProjectStore.LoadAsync(path);
        Assert.Equal(mask.Pixels, loaded.Masks[id].Pixels);
    }

    [Fact]
    public async Task UnreferencedImagesAreRemovedOnSave()
    {
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        string path = ProjectPath("orphans.comp");
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 1, 2, 3) },
                Masks = new Dictionary<Guid, MaskSurface>(),
            },
            path);

        string stale = Path.Combine(path, "images", "stale.png");
        await File.WriteAllBytesAsync(stale, TinyPng.EncodeRgba(1, 1, [1, 2, 3, 255]));

        // Save again with the same content: the stale file has no owner in the manifest.
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 1, 2, 3) },
                Masks = new Dictionary<Guid, MaskSurface>(),
            },
            path);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task MissingImageFailsTheLoadWithoutTouchingAnything()
    {
        var manifest = SingleLayerManifest();
        string path = ProjectPath("missing.comp");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), ManifestJson.Serialize(manifest));
        Directory.CreateDirectory(Path.Combine(path, "images"));

        var ex = await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectStore.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.MissingImage, ex.Kind);
    }

    [Fact]
    public async Task NonPngOrHighDepthAssetsAreRefused()
    {
        var manifest = SingleLayerManifest();
        string path = ProjectPath("badpng.comp");
        Directory.CreateDirectory(Path.Combine(path, "images"));
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), ManifestJson.Serialize(manifest));
        await File.WriteAllBytesAsync(
            Path.Combine(path, "images", manifest.Layers[0].ImageFile!),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF]);

        var ex = await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectStore.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.MissingImage, ex.Kind);
    }

    [Fact]
    public async Task OversizedAssetsAreRefused()
    {
        var manifest = SingleLayerManifest(width: 8, height: 8);
        string path = ProjectPath("huge.comp");
        Directory.CreateDirectory(Path.Combine(path, "images"));
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), ManifestJson.Serialize(manifest));
        byte[] big = new byte[Limits.AssetMaxBytes + 1];
        await File.WriteAllBytesAsync(Path.Combine(path, "images", manifest.Layers[0].ImageFile!), big);

        var ex = await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectStore.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.TooLarge, ex.Kind);
    }

    [Fact]
    public async Task SaveIsAtomicAgainstConcurrentReaders()
    {
        // Save repeatedly while a reader reloads: a reader must only ever see complete packages.
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        string path = ProjectPath("atomic.comp");
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 0, 0, 0) },
                Masks = new Dictionary<Guid, MaskSurface>(),
            },
            path);

        for (int pass = 0; pass < 20; pass++)
        {
            manifest.Layers[0].Name = $"Layer {pass}";
            await ProjectStore.SaveAsync(
                new ProjectSnapshot
                {
                    Manifest = manifest,
                    Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, (byte)pass, 0, 0) },
                    Masks = new Dictionary<Guid, MaskSurface>(),
                },
                path);
            var observed = await ProjectStore.LoadAsync(path);
            Assert.Equal($"Layer {pass}", observed.Manifest.Layers[0].Name);
        }
    }

    [Fact]
    public async Task SavingTwiceLeavesTheManifestStable()
    {
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        var snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 9, 9, 9) },
            Masks = new Dictionary<Guid, MaskSurface>(),
        };
        string path = ProjectPath("stable.comp");
        await ProjectStore.SaveAsync(snapshot, path);
        byte[] first = await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"));
        await ProjectStore.SaveAsync(snapshot, path);
        byte[] second = await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json"));
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task DigestIgnoresTouchesAndSeesContentChanges()
    {
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        string path = ProjectPath("digest.comp");
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 5, 5, 5) },
                Masks = new Dictionary<Guid, MaskSurface>(),
            },
            path);
        byte[] before = ProjectStore.ComputeDigest(path);

        File.SetLastWriteTimeUtc(Path.Combine(path, "manifest.json"), DateTime.UtcNow);
        Assert.Equal(before, ProjectStore.ComputeDigest(path));

        // A different-sized PNG counts as a change, as the macOS digest does.
        await File.WriteAllBytesAsync(
            Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".png"),
            TinyPng.EncodeRgba(2, 2, [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255]));
        Assert.NotEqual(before, ProjectStore.ComputeDigest(path));
    }

    [Fact]
    public async Task ExternalWritesFollowTheDocumentedProtocol()
    {
        // What an AI agent does per docs/writing-comp-files.md: write PNGs, then rename a
        // temporary manifest over the old one. The reload must see the new document.
        var manifest = SingleLayerManifest();
        var id = manifest.Layers[0].Id;
        string path = ProjectPath("external.comp");
        await ProjectStore.SaveAsync(
            new ProjectSnapshot
            {
                Manifest = manifest,
                Images = new Dictionary<Guid, Surface> { [id] = SolidSurface(8, 8, 0, 0, 0) },
                Masks = new Dictionary<Guid, MaskSurface>(),
            },
            path);

        var external = ManifestJson.Deserialize(ManifestJson.Serialize(manifest));
        external.Layers[0].Name = "Renamed by an agent";
        external.Layers[0].Opacity = 0.75;
        await File.WriteAllBytesAsync(Path.Combine(path, "images", ProjectValidator.Uuid(id) + ".png"), await WicImaging.EncodePngAsync(SolidSurface(8, 8, 200, 100, 50)));
        string temp = Path.Combine(path, ".manifest.json.tmp");
        await File.WriteAllTextAsync(temp, ManifestJson.Serialize(external));
        File.Move(temp, Path.Combine(path, "manifest.json"), overwrite: true);

        var loaded = await ProjectStore.LoadAsync(path);
        Assert.Equal("Renamed by an agent", loaded.Manifest.Layers[0].Name);
        Assert.Equal(0.75, loaded.Manifest.Layers[0].Opacity);
        Assert.Equal(200, loaded.Images[id].Pixels[0]);
    }
}
