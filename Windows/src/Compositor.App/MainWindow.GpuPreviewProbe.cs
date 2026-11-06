#if DEBUG
using Compositor.Core;
using Microsoft.Graphics.Canvas;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private async Task VerifyGpuTransitionsAsync(EditorDocument scene, Dictionary<string, object> report)
    {
        var value = scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == scene.Snapshot.Manifest.ActiveLayerId).Transform;
        var origin = value.Origin; var start = value.Center;
        BeginTransformPointer(start, Windows.System.VirtualKeyModifiers.Control); await WaitForFramesAsync();
        MoveTransformPointer(new(start.X - 19, start.Y + 17), Windows.System.VirtualKeyModifiers.Control); await WaitForFramesAsync();
        Require(lastFrameUsedGpu, "Transition fixture did not enter GPU preview.");
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(lastFrameUsedGpu && scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == scene.Snapshot.Manifest.ActiveLayerId).Transform.Origin == origin,
            "GPU cancellation changed the placement or retained a stale GPU frame.");
        report["gpuCancel"] = "passed";

        BeginTransformPointer(start, Windows.System.VirtualKeyModifiers.Control); await WaitForFramesAsync();
        MoveTransformPointer(new(start.X - 11, start.Y + 13), Windows.System.VirtualKeyModifiers.Control); await WaitForFramesAsync();
        Require(lastFrameUsedGpu, "Device fixture did not enter GPU preview.");
        var previous = gpuPreviewRenderer;
        HandleCanvasResourceChange(Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.DpiChanged);
        Require(ReferenceEquals(previous, gpuPreviewRenderer), "DPI-only change discarded GPU pixels.");
        HandleCanvasResourceChange(Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice);
        Require(gpuPreviewRenderer is null && presented is null, "Device change retained GPU resources.");
        await WaitForFramesAsync(); AssertTransformUpload(transformRenderedPreview!.Snapshot);
        Require(lastFrameUsedGpu && gpuPreviewRenderer is not null && !ReferenceEquals(previous, gpuPreviewRenderer), "GPU draft was not reconstructed.");
        report["gpuDeviceReconstruction"] = "passed";

        int failures = renderFailureEpoch;
        try
        {
            beforeGpuPreviewRender = () => throw new NotSupportedException("Injected GPU capability loss.");
            MoveTransformPointer(new(start.X - 23, start.Y + 29), Windows.System.VirtualKeyModifiers.Control);
            await WaitForFramesAsync(); AssertTransformUpload(transformRenderedPreview!.Snapshot);
            Require(gpuPreviewUnavailable && !lastFrameUsedGpu && renderFailureEpoch == failures, "GPU capability loss failed to recover a CPU frame.");
        }
        finally { beforeGpuPreviewRender = null; gpuPreviewUnavailable = false; }
        report["gpuCapabilityFallback"] = "passed";

        var rotated = CopyTransform(transformInspectorValue!); rotated.Rotation = 17;
        QueueCanvasInput(scene, () => SetTransformDraft(rotated)); await WaitForFramesAsync(); AssertTransformUpload(transformRenderedPreview!.Snapshot);
        Require(lastFrameUsedGpu, "Rotation did not use GPU sampling.");
        QueueTransformCancel(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
        report["gpuRotation"] = "passed";

        var beforeToggle = gpuPreviewRenderer;
        GpuRenderingToggle.IsChecked = false; OnToggleGpuRendering(GpuRenderingToggle, new());
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(!lastFrameUsedGpu && beforeToggle is { IsDisposed: true } && !ReadFlag("gpu-rendering", true),
            "Disabling GPU did not retire its resources, persist the preference and present CPU pixels.");
        GpuRenderingToggle.IsChecked = true; OnToggleGpuRendering(GpuRenderingToggle, new());
        await WaitForFramesAsync(); AssertUploadedFrame(scene);
        Require(lastFrameUsedGpu && ReadFlag("gpu-rendering", false), "GPU did not recover after enabling it.");
        report["gpuUserSwitch"] = "passed";

        try
        {
            beforeGpuPreviewRender = () =>
            {
                using var software = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(true), 61, 37);
            };
            RenderCanvas(); await WaitForFramesAsync(); AssertUploadedFrame(scene);
            Require(gpuPreviewUnavailable && !lastFrameUsedGpu && renderFailureEpoch == failures,
                "A real software device did not recover a complete CPU frame.");
        }
        finally { beforeGpuPreviewRender = null; gpuPreviewUnavailable = false; }
        report["gpuSoftwareFallback"] = "passed";
        RenderCanvas(); await WaitForFramesAsync();

        var retired = gpuPreviewRenderer;
        OpenDocument(EditorDocument.Create(32, 24)); await WaitForFramesAsync();
        Require(retired is { IsDisposed: true } && !ReferenceEquals(retired, gpuPreviewRenderer), "Document replacement retained GPU resources.");
        report["gpuOwnerRetirement"] = "passed";
    }

    private static void VerifyGpuFixtures(Dictionary<string, object> report)
    {
        var results = new List<object>(); report["gpuFixtures"] = results;
        foreach (var sampling in Enum.GetValues<LayerSampling>())
        foreach (bool flip in new[] { false, true })
        foreach (bool alpha in new[] { false, true })
        {
            var snapshot = GpuFixture(alpha);
            var photo = snapshot.Manifest.Layers[1];
            photo.Transform = new() { Origin = new(-2.25, 3.75), Size = new(47.25, 31.75), Sampling = sampling, FlipX = flip, FlipY = !flip };
            Check($"{sampling}-flip{flip}-alpha{alpha}", snapshot);
            photo.Transform.Origin = new(31.5, -4.25);
            Check($"{sampling}-edge-flip{flip}-alpha{alpha}", snapshot);
        }
        foreach (var kind in Enum.GetValues<AdjustmentKind>())
        foreach (bool alpha in new[] { false, true })
        foreach (bool masked in new[] { false, true })
        {
            var snapshot = GpuFixture(alpha);
            var adjustment = new LayerAdjustment { Kind = kind, Hue = 27, Saturation = 19, Lightness = -7,
                GradientMapSettings = new() { Shadows = new(.09, .17, .24), Highlights = new(.95, .71, .33), Reversed = true },
                BlackWhiteSettings = new() { Reds = 51, Yellows = 72, Greens = 38, Cyans = 91, Blues = -17, Magentas = 121, Tint = true, TintHue = 197.3, TintSaturation = 33 },
                GrainSettings = new() { Amount = 37, Roughness = 61, Size = 2.3, Seed = 9127 },
                NoiseAmount = 37, NoiseSeed = 98111, MotionAngle = -23.7, MotionDistance = 17.3, BlurRadius = 2.7,
                ExposureSettings = new() { Exposure = .73, Offset = -.013, Gamma = 1.17 },
                ColorBalanceSettings = new() { ShadowCyanRed = -17, MidMagentaGreen = 9, HighlightYellowBlue = -31, PreserveLuminosity = true } };
            adjustment.Levels.Ranges[0] = new() { Black = 7, White = 239, Gamma = 1.31, OutputBlack = 3, OutputWhite = 247 };
            adjustment.Levels.Ranges[2].Gamma = .77;
            adjustment.Curves.Channels[0] = [new(0, 3), new(71, 58), new(173, 191), new(255, 250)];
            adjustment.Curves.Channels[3] = [new(0, 7), new(113, 101), new(255, 253)];
            var layer = new ProjectLayerRecord { Id = Guid.NewGuid(), Name = kind.ToString(), IsVisible = true,
                Transform = new() { Size = new(61, 37) }, Adjustment = adjustment };
            if (masked)
            {
                layer.MaskFile = "coverage.mask.png";
                var mask = new MaskSurface(61, 37);
                for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(i % 256);
                ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = mask;
            }
            snapshot.Manifest.Layers.Add(layer);
            Check($"{kind}-alpha{alpha}-masked{masked}", snapshot);
            if (masked && alpha)
            {
                var masks = (Dictionary<Guid, MaskSurface>)snapshot.Masks;
                var full = masks[layer.Id]; var small = new MaskSurface(17, 13);
                for (int i = 0; i < small.Pixels.Length; i++) small.Pixels[i] = (byte)(i * 71);
                masks[layer.Id] = small;
                Check($"{kind}-small-aligned-mask", snapshot);
                masks[layer.Id] = full;
            }
            if (alpha)
            {
                var placed = new LayerTransform { Origin = new(13.25, 11.75), Size = new(21.5, 9.5), Rotation = 17.25 };
                layer.Transform = placed; layer.MaskPlacement = new() { Origin = new(-2.25, 1.75), Size = new(53.5, 41.25), Rotation = -13, FlipX = true };
                layer.Opacity = .73;
                Check($"{kind}-placed-masked{masked}", snapshot);
                layer.BlendMode = LayerBlendMode.Multiply;
                Check($"{kind}-placed-blend-masked{masked}", snapshot);
                layer.Transform = new() { Size = new(61, 37) }; layer.MaskPlacement = null;
                layer.Opacity = null; layer.BlendMode = null;
            }
            if (kind == AdjustmentKind.ColorBalance)
            {
                adjustment.ColorBalanceSettings!.PreserveLuminosity = false; adjustment.MarkModified();
                Check($"{kind}-no-preserve-alpha{alpha}-masked{masked}", snapshot);
            }
            if (kind == AdjustmentKind.AddNoise)
            {
                adjustment.NoiseGaussian = true; adjustment.NoiseMonochromatic = masked; adjustment.MarkModified();
                Check($"{kind}-gaussian-alpha{alpha}-masked{masked}", snapshot);
            }
        }
        foreach (var kind in new[] { AdjustmentKind.Levels, AdjustmentKind.Curves, AdjustmentKind.Exposure,
            AdjustmentKind.HueSaturation, AdjustmentKind.BlackWhite, AdjustmentKind.GradientMap, AdjustmentKind.Grain, AdjustmentKind.AddNoise, AdjustmentKind.ColorBalance })
        {
            var snapshot = GpuFixture(false); snapshot.Manifest.Width = snapshot.Manifest.Height = 256;
            snapshot.Manifest.Layers.RemoveAt(1);
            var layer = snapshot.Manifest.Layers[0]; layer.Transform.Size = new(256, 256);
            var image = new Surface(256, 256);
            for (int a = 0; a <= 255; a++) for (int v = 0; v <= 255; v++)
            {
                int pixel = (a * 256 + v) * 4;
                image.Pixels[pixel] = (byte)Math.Min(a, v);
                image.Pixels[pixel + 1] = (byte)Math.Min(a, 255 - v);
                image.Pixels[pixel + 2] = (byte)Math.Min(a, v * 137 % 256); image.Pixels[pixel + 3] = (byte)a;
            }
            ((Dictionary<Guid, Surface>)snapshot.Images)[layer.Id] = image;
            var adjustment = new LayerAdjustment { Kind = kind, Hue = -37, Saturation = 31, Lightness = 13, NoiseGaussian = true,
                NoiseAmount = 113.7, NoiseSeed = 71923, GrainSettings = new() { Amount = 81, Size = .7, Roughness = 29, Seed = 112 },
                BlackWhiteSettings = new() { Tint = true, TintHue = 53.1, TintSaturation = 61 },
                ColorBalanceSettings = new() { ShadowCyanRed = -17, MidMagentaGreen = 9, HighlightYellowBlue = -31, PreserveLuminosity = true },
                ExposureSettings = new() { Exposure = .73, Offset = -.013, Gamma = 1.17 } };
            adjustment.Levels.Ranges[0].Gamma = .77;
            adjustment.Curves.Channels[0] = [new(0, 3), new(71, 58), new(173, 191), new(255, 250)];
            snapshot.Manifest.Layers.Add(new() { Id = Guid.NewGuid(), Name = kind.ToString(), IsVisible = true,
                Transform = new() { Size = new(256, 256) }, Adjustment = adjustment });
            Check($"{kind}-every-byte-alpha", snapshot);
        }
        foreach (var sampling in Enum.GetValues<LayerSampling>())
        foreach (double angle in new[] { 17.25, -39.5, 90.0, 180.0 })
        foreach (bool alpha in new[] { false, true })
        {
            var snapshot = GpuFixture(alpha); var layer = snapshot.Manifest.Layers[1];
            layer.Transform = new() { Origin = new(-2.25, 3.75), Size = new(47.25, 31.75), Sampling = sampling,
                Rotation = angle, FlipX = true, FlipY = false };
            Check($"rotate-{angle}-{sampling}-alpha{alpha}", snapshot);
        }
        foreach (var mode in Enum.GetValues<LayerBlendMode>())
        foreach (bool alpha in new[] { false, true })
        foreach (double opacity in new[] { 1.0, .37 })
        {
            var snapshot = GpuFixture(alpha); var layer = snapshot.Manifest.Layers[1];
            layer.BlendMode = mode; layer.Opacity = opacity;
            Check($"blend-{mode}-alpha{alpha}-opacity{opacity}", snapshot);
        }
        foreach (var mode in new[] { LayerBlendMode.Normal, LayerBlendMode.Hue })
        foreach (bool folderMask in new[] { false, true })
        foreach (double angle in new[] { 0.0, 17.25, 90.0, 180.0 })
        {
            var snapshot = GpuFixture(true); var layer = snapshot.Manifest.Layers[1];
            layer.BlendMode = mode; layer.Opacity = .73;
            var folder = new ProjectLayerRecord { Id = Guid.NewGuid(), IsGroup = true, Name = "Masked folder", IsVisible = true,
                Opacity = .81, Transform = new() { Size = new(61, 37) } };
            snapshot.Manifest.Layers.Insert(1, folder); layer.ParentId = folder.Id;
            var record = folderMask ? folder : layer;
            record.MaskFile = "plate.png"; record.MaskPlacement = new() { Origin = new(-2.25, 1.75), Size = new(53.5, 41.25), Rotation = angle, FlipX = true };
            var mask = new MaskSurface(17, 13); for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(i * 71);
            ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[record.Id] = mask;
            Check($"mask-{mode}-folder{folderMask}-angle{angle}", snapshot);
        }
        foreach (var mode in new[] { LayerBlendMode.Normal, LayerBlendMode.Hue })
        foreach (double angle in new[] { 0.0, 17.25, 90.0, 180.0 })
        {
            var snapshot = GpuFixture(true); var layer = snapshot.Manifest.Layers[1]; var source = snapshot.Manifest.Layers[0];
            layer.MaskSourceId = source.Id; layer.BlendMode = mode; layer.Opacity = .63;
            source.Opacity = .79; source.Transform = new() { Origin = new(3.5, -2.75), Size = new(53.5, 41.25), Rotation = angle, FlipX = true };
            source.MaskFile = "source-mask.png"; source.MaskPlacement = new() { Size = new(61, 37), Rotation = 17 };
            var mask = new MaskSurface(17,13); for (int i=0;i<mask.Pixels.Length;i++) mask.Pixels[i]=(byte)(i*73);
            ((Dictionary<Guid,MaskSurface>)snapshot.Masks)[source.Id]=mask;
            Check($"clipping-{mode}-angle{angle}",snapshot);
        }
        foreach (var kind in Enum.GetValues<LayerEffectKind>())
        foreach (int maskKind in new[] { 0, 1, 2 })
        foreach (double blur in new[] { 3.3, 9.7 })
        {
            var snapshot = GpuFixture(true); var layer = snapshot.Manifest.Layers[1];
            var effects = new LayerEffects();
            switch (kind)
            {
                case LayerEffectKind.Shadow: effects.Shadow = new() { Blur = blur, Distance = 4.5, Angle = 37, Red = .3, Blue = .7, Opacity = .53 }; break;
                case LayerEffectKind.Stroke: effects.Stroke = new() { Size = 3.5, Red = .37, Green = .81, Opacity = .63, Inside = blur > 4 }; break;
                case LayerEffectKind.ColorOverlay: effects.ColorOverlay = new() { Red = .71, Green = .27, Blue = .33, Opacity = .37 }; break;
                case LayerEffectKind.InnerShadow: effects.InnerShadow = new() { Blur = blur, Distance = 3.7, Angle = -17, Opacity = .79 }; break;
                case LayerEffectKind.OuterGlow: effects.OuterGlow = new() { Size = blur, Red = .71, Blue = .17, Opacity = .41 }; break;
                case LayerEffectKind.InnerGlow: effects.InnerGlow = new() { Size = blur, Red = .13, Green = .73, Opacity = .67 }; break;
            }
            layer.Effects = effects.Serialize(); layer.Transform.Rotation = 17.25;
            if (maskKind != 0)
            {
                var mask = new MaskSurface(17,13); for (int i=0;i<mask.Pixels.Length;i++) mask.Pixels[i]=(byte)(i*71);
                layer.MaskFile = "effect-mask.png"; ((Dictionary<Guid,MaskSurface>)snapshot.Masks)[layer.Id] = mask;
                if (maskKind == 2) layer.MaskPlacement = new() { Origin = new(9.5, 8.25), Size = new(21.5, 23.5), Rotation = -13.25, FlipX = true };
            }
            Check($"effect-{kind}-mask{maskKind}-blur{blur}", snapshot);
            snapshot.Manifest.Layers[0].MaskSourceId = layer.Id;
            Check($"effect-clipping-{kind}-mask{maskKind}-blur{blur}", snapshot);
        }
        report["gpuFixturePixels"] = "passed";
        VerifyGpuBudget(report);
        VerifyGpuIncrementalUploads(report);

        void Check(string name, ProjectSnapshot snapshot)
        {
            var plan = GpuPreviewScene.TryCreate(snapshot) ?? throw new InvalidOperationException(name + " was rejected.");
            using var renderer = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(), snapshot.Manifest.Width, snapshot.Manifest.Height);
            var comparison = CompareGpuFrame(snapshot, renderer.Render(plan));
            results.Add(new { name, comparison });
            Require(comparison.Differences == 0, $"GPU fixture {name}: {comparison}");
        }
    }

    private static void VerifyGpuBudget(Dictionary<string, object> report)
    {
        var snapshot = GpuFixture(true);
        for (int i = 0; i < 48; i++)
        {
            var id = Guid.NewGuid(); var image = new Surface(32, 32);
            for (int p = 0; p < image.Pixels.Length; p += 4) { image.Pixels[p] = (byte)(i * 3); image.Pixels[p + 3] = 173; }
            snapshot.Manifest.Layers.Add(new() { Id = id, Name = "Eviction " + i, ImageFile = "tile.png", IsVisible = true,
                Transform = new() { Origin = new(i % 23, i % 11), Size = new(32, 32) } });
            ((Dictionary<Guid, Surface>)snapshot.Images)[id] = image;
        }
        var plan = GpuPreviewScene.TryCreate(snapshot)!;
        using var renderer = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(), 61, 37, 64 << 10);
        for (int pass = 0; pass < 2; pass++)
        {
            Require(CompareGpuFrame(snapshot, renderer.Render(plan)).Differences == 0, "LRU eviction changed GPU pixels.");
            Require(renderer.RetainedTextureBytes <= 64 << 10, "LRU eviction exceeded the texture budget.");
        }
        Require(renderer.AssetUploads > plan.Layers.Count, "The constrained fixture did not exercise eviction.");
        report["gpuBudgetEviction"] = new { layers = plan.Layers.Count, budget = 64 << 10, retained = renderer.RetainedTextureBytes, uploads = renderer.AssetUploads };
    }

    private static void VerifyGpuIncrementalUploads(Dictionary<string, object> report)
    {
        var snapshot = GpuFixture(true); var layer = snapshot.Manifest.Layers[1];
        using var renderer = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(), 61, 37);
        var plan = GpuPreviewScene.TryCreate(snapshot)!;
        _ = renderer.Render(plan);
        var image = snapshot.Images[layer.Id]; long before = renderer.UploadedTextureBytes;
        for (int y = 3; y < 6; y++) for (int x = 2; x < 6; x++)
        { int p = y * image.Stride + x * 4; image.Pixels[p] = image.Pixels[p + 1] = 0; }
        image.MarkModified(new(2, 3, 4, 3));
        Require(CompareGpuFrame(snapshot, renderer.Render(plan)).Differences == 0, "Incremental raster upload changed pixels.");
        long rasterBytes = renderer.UploadedTextureBytes - before;
        Require(rasterBytes == 4 * 3 * 4, $"A small raster edit uploaded {rasterBytes} bytes.");
        layer.MaskFile = "incremental-mask.png";
        var mask = new MaskSurface(17, 13); Array.Fill(mask.Pixels, (byte)173);
        ((Dictionary<Guid, MaskSurface>)snapshot.Masks)[layer.Id] = mask;
        _ = renderer.Render(plan); before = renderer.UploadedTextureBytes;
        mask.Pixels.AsSpan(3 * 17 + 2, 5).Clear(); mask.MarkModified(new(2, 3, 5, 1));
        Require(CompareGpuFrame(snapshot, renderer.Render(plan)).Differences == 0, "Incremental mask upload changed pixels.");
        long maskBytes = renderer.UploadedTextureBytes - before;
        Require(maskBytes == 5, $"A small mask edit uploaded {maskBytes} bytes.");
        report["gpuIncrementalUpload"] = new { rasterBytes, maskBytes };

        var paddedScene = GpuFixture(true); var paddedLayer = paddedScene.Manifest.Layers[1];
        var original = paddedScene.Images[paddedLayer.Id]; int stride = original.Stride + 12;
        var bytes = new byte[stride * original.Height + 17];
        for (int y = 0; y < original.Height; y++) original.Pixels.AsSpan(y * original.Stride, original.Stride).CopyTo(bytes.AsSpan(y * stride));
        var padded = new Surface(original.Width, original.Height, bytes, stride);
        ((Dictionary<Guid, Surface>)paddedScene.Images)[paddedLayer.Id] = padded;
        using var paddedRenderer = new GpuCanvasRenderer(CanvasDevice.GetSharedDevice(), 61, 37);
        var paddedPlan = GpuPreviewScene.TryCreate(paddedScene)!;
        Require(CompareGpuFrame(paddedScene, paddedRenderer.Render(paddedPlan)).Differences == 0, "Padded raster upload changed pixels.");
        padded.Pixels[4 * stride + 5 * 4] = 0; padded.MarkModified(new(5, 4, 1, 1));
        Require(CompareGpuFrame(paddedScene, paddedRenderer.Render(paddedPlan)).Differences == 0, "Padded incremental upload changed pixels.");
        report["gpuPaddedUpload"] = "passed";
    }

    private static ProjectSnapshot GpuFixture(bool alpha)
    {
        var background = new Surface(61, 37); var photo = new Surface(29, 17);
        var random = new Random(9127);
        foreach (var image in new[] { background, photo })
            for (int i = 0; i < image.Pixels.Length; i += 4)
            {
                int a = alpha ? random.Next(256) : 255;
                image.Pixels[i] = (byte)random.Next(a + 1); image.Pixels[i + 1] = (byte)random.Next(a + 1);
                image.Pixels[i + 2] = (byte)random.Next(a + 1); image.Pixels[i + 3] = (byte)a;
            }
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        return new() { Manifest = new() { Width = 61, Height = 37, ActiveLayerId = second, Layers = [
            new() { Id = first, Name = "Background", ImageFile = "background.png", IsVisible = true, Transform = new() { Size = new(61, 37) } },
            new() { Id = second, Name = "Photo", ImageFile = "photo.png", IsVisible = true, Transform = new() { Origin = new(7, 9), Size = new(29, 17) } } ] },
            Images = new Dictionary<Guid, Surface> { [first] = background, [second] = photo }, Masks = new Dictionary<Guid, MaskSurface>() };
    }
}
#endif
