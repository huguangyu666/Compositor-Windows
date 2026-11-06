using Xunit;

namespace Compositor.Core.Tests;

public sealed class AnimationTimelineTests
{
    private static EditorDocument Scene()
    {
        var scene = EditorDocument.Create(64, 48);
        Assert.True(scene.SetAnimationSettings(120, 24, true));
        return scene;
    }

    private static Guid Layer(EditorDocument scene) => scene.Snapshot.Manifest.Layers[0].Id;
    private static Dictionary<AnimationProperty, double> Values(params (AnimationProperty Property, double Value)[] items)
        => items.ToDictionary(item => item.Property, item => item.Value);

    [Fact]
    public void StaticProjectsKeepTheCrossPlatformVersionUntilAnimationIsCreated()
    {
        var scene = EditorDocument.Create(32, 24);
        Assert.Equal(11, scene.Snapshot.Manifest.Version);
        Assert.Null(scene.Snapshot.Manifest.Timeline);
        scene.SetAnimationSettings(48, 24, true);
        Assert.Equal(12, scene.Snapshot.Manifest.Version);
    }

    [Fact]
    public void ShiftingATrackMovesItsClipAndKeysAtomicallyAndRejectsOverflow()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationClip(id, 12, 60); scene.SetAnimationKeys(id, 24, Values((AnimationProperty.PositionX, 50)));
        var before = scene.CurrentHistoryId;
        Assert.True(scene.ShiftAnimationTrack(id, 10));
        var track = scene.Snapshot.Manifest.Timeline!.Tracks[0];
        Assert.Equal((22, 70, 34), (track.InFrame, track.OutFrame, track.PositionX[0].Frame));
        Assert.False(scene.ShiftAnimationTrack(id, int.MaxValue));
        Assert.Equal(34, scene.Snapshot.Manifest.Timeline.Tracks[0].PositionX[0].Frame);
        Assert.True(scene.Undo()); Assert.Equal(before, scene.CurrentHistoryId);
    }

    [Fact]
    public void PositionProtectionAlsoProtectsRemovingTransformKeys()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.PositionX, 50), (AnimationProperty.Opacity, .5)));
        scene.SetLayerLocks(id, LayerLocks.Position);
        Assert.False(scene.RemoveAnimationKeys(id, 0, [AnimationProperty.PositionX]));
        Assert.True(scene.RemoveAnimationKeys(id, 0, [AnimationProperty.Opacity]));
        Assert.Single(scene.Snapshot.Manifest.Timeline!.Tracks[0].PositionX);
    }

    [Fact]
    public async Task SavedAnimationReopensWithIdenticalFramesAndSourcePixels()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.PositionX, 10), (AnimationProperty.Opacity, 0)));
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.PositionX, 40), (AnimationProperty.Opacity, 1)));
        string root = Path.Combine(Path.GetTempPath(), "compositor-timeline-" + Guid.NewGuid().ToString("N"));
        try
        {
            await ProjectStore.SaveAsync(scene.CaptureSave().Snapshot, Path.Combine(root, "Animation.comp"));
            var restored = await ProjectStore.LoadAsync(Path.Combine(root, "Animation.comp"));
            Assert.Equal(ManifestJson.Serialize(scene.Snapshot.Manifest), ManifestJson.Serialize(restored.Manifest));
            Assert.Equal(scene.Snapshot.Images[id].Pixels, restored.Images[id].Pixels);
            Assert.Equal(new CanvasCompositor(AnimationEvaluator.Evaluate(scene.Snapshot, 12)).Render().Pixels,
                new CanvasCompositor(AnimationEvaluator.Evaluate(restored, 12)).Render().Pixels);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LinkedMaskFollowsAnimatedCenterScaleAndRotationWhileIndependentMaskStaysPut()
    {
        var scene = Scene(); var id = Layer(scene); var layer = scene.Snapshot.Manifest.Layers[0];
        layer.MaskPlacement = new() { Origin = new(0, 0), Size = new(32, 24) };
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.PositionX, 64), (AnimationProperty.ScaleX, 2), (AnimationProperty.Rotation, 90)));
        var animated = AnimationEvaluator.Evaluate(scene.Snapshot, 0).Manifest.Layers[0];
        Assert.Equal(76, animated.MaskPlacement!.Center.X, 6);
        Assert.Equal(-8, animated.MaskPlacement.Center.Y, 6);
        Assert.Equal(90, animated.MaskPlacement.Rotation, 6);
        Assert.Equal(new SizeD(64, 24), animated.MaskPlacement.Size);
        layer.MaskLinked = false;
        Assert.Equal(layer.MaskPlacement, AnimationEvaluator.Evaluate(scene.Snapshot, 0).Manifest.Layers[0].MaskPlacement);
    }

    [Theory]
    [InlineData("crop", 36, 14, 30)]
    [InlineData("resize", 80, 40, 30)]
    [InlineData("canvas", 72, 44, 30)]
    [InlineData("flip", 24, 20, -30)]
    public void CanvasCoordinateChangesCarryAnimationAndUndo(string operation, double x, double y, double rotation)
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 12, Values((AnimationProperty.PositionX, 40), (AnimationProperty.PositionY, 20), (AnimationProperty.Rotation, 30)));
        string before = ManifestJson.Serialize(scene.Snapshot.Manifest);
        Assert.True(operation switch { "crop" => scene.SetCanvasBounds(4, 6, 32, 24), "resize" => scene.ResizeImage(128, 96),
            "canvas" => scene.SetCanvasSize(128, 96), _ => scene.FlipCanvas(true) });
        var layer = AnimationEvaluator.Evaluate(scene.Snapshot, 12).Manifest.Layers[0];
        Assert.Equal(x, layer.Transform.Center.X, 6); Assert.Equal(y, layer.Transform.Center.Y, 6);
        Assert.Equal(rotation, layer.Transform.Rotation, 6);
        Assert.True(scene.Undo()); Assert.Equal(before, ManifestJson.Serialize(scene.Snapshot.Manifest));
    }

    [Theory]
    [InlineData("linear", 5, 50)]
    [InlineData("hold", 5, 0)]
    [InlineData("smooth", 2, 10.4)]
    [InlineData("linear", -2, 0)]
    [InlineData("linear", 14, 100)]
    public void InterpolationAndEndpointHolds(string interpolation, int frame, double expected)
    {
        AnimationKeyframe[] keys = [new() { Frame = 0, Value = 0, Interpolation = interpolation }, new() { Frame = 10, Value = 100 }];
        Assert.Equal(expected, AnimationEvaluator.Sample(keys, frame, 700), 6);
    }

    [Fact]
    public void PreviewUsesCenterPositionScaleRotationAndOpacityWithoutChangingTheDocument()
    {
        var scene = Scene(); var id = Layer(scene); var source = scene.Snapshot.Images[id];
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.PositionX, 32), (AnimationProperty.ScaleX, 1),
            (AnimationProperty.Rotation, 0), (AnimationProperty.Opacity, 1)));
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.PositionX, 80), (AnimationProperty.ScaleX, 2),
            (AnimationProperty.Rotation, 180), (AnimationProperty.Opacity, 0)));
        string original = ManifestJson.Serialize(scene.Snapshot.Manifest); var history = scene.CurrentHistoryId;
        var preview = AnimationEvaluator.Evaluate(scene.Snapshot, 12); var layer = preview.Manifest.Layers[0];
        Assert.Equal(56, layer.Transform.Center.X, 6);
        Assert.Equal(96, layer.Transform.Size.Width, 6);
        Assert.Equal(90, layer.Transform.Rotation, 6);
        Assert.Equal(.5, layer.OpacityOrOne, 6);
        Assert.Same(source, preview.Images[id]); Assert.Same(scene.Snapshot.Masks, preview.Masks);
        Assert.Equal(original, ManifestJson.Serialize(scene.Snapshot.Manifest)); Assert.Equal(history, scene.CurrentHistoryId);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(23, true)]
    [InlineData(24, false)]
    public void ClipUsesAnInclusiveInAndExclusiveOutFrame(int frame, bool visible)
    {
        var scene = Scene(); scene.SetAnimationClip(Layer(scene), 12, 24);
        Assert.Equal(visible, AnimationEvaluator.Evaluate(scene.Snapshot, frame).Manifest.Layers[0].IsVisible);
    }

    [Fact]
    public void EditingAndRemovingKeysAreSingleUndoStepsAndUpgradeLegacyProjects()
    {
        var scene = EditorDocument.Create(); scene.Snapshot.Manifest.Version = 11;
        Assert.True(scene.SetAnimationSettings(120, 24, true));
        Assert.Equal(12, scene.Snapshot.Manifest.Version); Assert.True(scene.Undo());
        Assert.Equal(11, scene.Snapshot.Manifest.Version); Assert.Null(scene.Snapshot.Manifest.Timeline);
        Assert.True(scene.Redo()); var id = Layer(scene);
        var before = scene.CurrentHistoryId;
        scene.SetAnimationKeys(id, 12, Values((AnimationProperty.PositionX, 100), (AnimationProperty.PositionY, 80)));
        Assert.Single(scene.Snapshot.Manifest.Timeline!.Tracks); Assert.True(scene.Undo()); Assert.Equal(before, scene.CurrentHistoryId);
        Assert.Empty(scene.Snapshot.Manifest.Timeline!.Tracks); Assert.True(scene.Redo());
        Assert.True(scene.RemoveAnimationKeys(id, 12, [AnimationProperty.PositionX, AnimationProperty.PositionY]));
        Assert.Empty(scene.Snapshot.Manifest.Timeline!.Tracks[0].PositionX); Assert.True(scene.Undo());
        Assert.Single(scene.Snapshot.Manifest.Timeline!.Tracks[0].PositionX);
    }

    [Fact]
    public void ReplacingAndMovingAKeyDoesNotCreateDuplicateFrames()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 2, Values((AnimationProperty.Opacity, .3)));
        scene.SetAnimationKeys(id, 2, Values((AnimationProperty.Opacity, .7)), "smooth");
        scene.SetAnimationKeys(id, 8, Values((AnimationProperty.Opacity, .1)));
        Assert.True(scene.MoveAnimationKeys(id, 2, 8, [AnimationProperty.Opacity]));
        var key = Assert.Single(scene.Snapshot.Manifest.Timeline!.Tracks[0].Opacity);
        Assert.Equal(8, key.Frame); Assert.Equal(.7, key.Value); Assert.Equal("smooth", key.Interpolation);
        Assert.True(scene.Undo()); Assert.Equal(2, scene.Snapshot.Manifest.Timeline!.Tracks[0].Opacity.Count);
    }

    [Fact]
    public void ChangingFrameRatePreservesSecondsAndRejectsDestructiveShortening()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationClip(id, 12, 96); scene.SetAnimationKeys(id, 48, Values((AnimationProperty.Rotation, 360)));
        Assert.True(scene.SetAnimationSettings(150, 30, true));
        var track = scene.Snapshot.Manifest.Timeline!.Tracks[0];
        Assert.Equal(15, track.InFrame); Assert.Equal(120, track.OutFrame); Assert.Equal(60, track.Rotation[0].Frame);
        string original = ManifestJson.Serialize(scene.Snapshot.Manifest);
        Assert.False(scene.SetAnimationSettings(30, 30, false)); Assert.Equal(original, ManifestJson.Serialize(scene.Snapshot.Manifest));
    }

    [Theory]
    [InlineData(-1, 1, "linear")]
    [InlineData(120, 1, "linear")]
    [InlineData(24, -1, "linear")]
    [InlineData(24, .5, "bogus")]
    public void InvalidEditsDoNotChangeHistory(int frame, double opacity, string interpolation)
    {
        var scene = Scene(); var history = scene.CurrentHistoryId;
        Assert.False(scene.SetAnimationKeys(Layer(scene), frame, Values((AnimationProperty.Opacity, opacity)), interpolation));
        Assert.Equal(history, scene.CurrentHistoryId); Assert.Empty(scene.Snapshot.Manifest.Timeline!.Tracks);
    }

    [Fact]
    public void SaveCaptureAndJsonRoundTripKeepTheWholeTimeline()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.ScaleY, 1)), "hold");
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.ScaleY, 2)), "smooth");
        var captured = scene.CaptureSave(); string json = ManifestJson.Serialize(captured.Snapshot.Manifest);
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.ScaleY, 3)));
        var restored = ManifestJson.Deserialize(json); ProjectValidator.Validate(restored);
        Assert.Equal(2, restored.Timeline!.Tracks[0].ScaleY[1].Value); Assert.Equal("hold", restored.Timeline.Tracks[0].ScaleY[0].Interpolation);
        Assert.Same(scene.Snapshot.Images[id], captured.Snapshot.Images[id]);
    }

    [Fact]
    public void ValidationRejectsDuplicateTracksUnsortedKeysAndLegacyTimelineFields()
    {
        var scene = Scene(); scene.SetAnimationKeys(Layer(scene), 0, Values((AnimationProperty.Opacity, 1)));
        var manifest = scene.Snapshot.Manifest; var track = manifest.Timeline!.Tracks[0];
        manifest.Timeline.Tracks.Add(track); Assert.Throws<ProjectFormatException>(() => ProjectValidator.Validate(manifest));
        manifest.Timeline.Tracks.RemoveAt(1); track.Opacity.Add(new() { Frame = 0, Value = .4 });
        Assert.Throws<ProjectFormatException>(() => ProjectValidator.Validate(manifest));
        track.Opacity.RemoveAt(1); manifest.Version = 11; Assert.Throws<ProjectFormatException>(() => ProjectValidator.Validate(manifest));
    }

    [Fact]
    public void FrameRenderingKeepsVisibilityAndTransparency()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 0, Values((AnimationProperty.Opacity, 1)));
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.Opacity, 0)));
        Assert.Equal(255, new CanvasCompositor(AnimationEvaluator.Evaluate(scene.Snapshot, 0)).Render().Pixels[3]);
        Assert.InRange(new CanvasCompositor(AnimationEvaluator.Evaluate(scene.Snapshot, 12)).Render().Pixels[3], (byte)127, (byte)128);
        Assert.Equal(0, new CanvasCompositor(AnimationEvaluator.Evaluate(scene.Snapshot, 24)).Render().Pixels[3]);
        scene.SetAnimationClip(id, 12, 24);
        Assert.Equal(0, new CanvasCompositor(AnimationEvaluator.Evaluate(scene.Snapshot, 0)).Render().Pixels[3]);
    }

    [Fact]
    public void DuplicationCopiesTracksAndDeletingALayerPrunesOnlyItsTrack()
    {
        var scene = Scene(); var id = Layer(scene);
        scene.SetAnimationKeys(id, 24, Values((AnimationProperty.Rotation, 90)));
        var duplicate = Assert.Single(scene.DuplicateLayers([id]));
        Assert.Equal(2, scene.Snapshot.Manifest.Timeline!.Tracks.Count);
        Assert.Equal(90, scene.Snapshot.Manifest.Timeline.Tracks.Single(track => track.LayerId == duplicate.Id).Rotation[0].Value);
        Assert.True(scene.DeleteLayer(id));
        Assert.Equal(duplicate.Id, Assert.Single(scene.Snapshot.Manifest.Timeline.Tracks).LayerId);
        Assert.True(scene.Undo()); Assert.Equal(2, scene.Snapshot.Manifest.Timeline!.Tracks.Count);
        ProjectValidator.Validate(scene.Snapshot.Manifest);
    }
}
