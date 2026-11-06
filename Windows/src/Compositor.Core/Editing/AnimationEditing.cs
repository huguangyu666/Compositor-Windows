using System.Text.Json;

namespace Compositor.Core;

public sealed partial class EditorDocument
{
    public bool SetAnimationSettings(int durationFrames, int framesPerSecond, bool loop)
    {
        if (durationFrames is < 1 or > ProjectTimeline.MaxFrames || framesPerSecond is < 1 or > 120) return false;
        var original = Snapshot.Manifest.Timeline;
        var timeline = original?.Clone() ?? new();
        double rate = original is null ? 1 : (double)framesPerSecond / original.FramesPerSecond;
        int Remap(int frame) => (int)Math.Round(frame * rate, MidpointRounding.AwayFromZero);
        foreach (var track in timeline.Tracks)
        {
            foreach (var property in Enum.GetValues<AnimationProperty>())
            {
                var remapped = track.Keys(property).Select(key => key with { Frame = Remap(key.Frame) }).GroupBy(key => key.Frame).Select(group => group.Last()).ToArray();
                if (remapped.Any(key => key.Frame >= durationFrames)) return false;
                track.Keys(property).Clear(); track.Keys(property).AddRange(remapped);
            }
            track.InFrame = Math.Min(Remap(track.InFrame), durationFrames - 1);
            track.OutFrame = Math.Clamp(original is not null && track.OutFrame == original.DurationFrames ? durationFrames : Remap(track.OutFrame), track.InFrame + 1, durationFrames);
        }
        timeline.DurationFrames = durationFrames; timeline.FramesPerSecond = framesPerSecond; timeline.Loop = loop;
        return CommitTimeline(timeline, "AnimationSettings");
    }

    public bool SetAnimationClip(Guid layerId, int inFrame, int outFrame)
    {
        if (Snapshot.Manifest.Timeline is not { } original || !CanEditLayer(layerId, LayerLocks.None)
            || inFrame < 0 || outFrame > original.DurationFrames || inFrame >= outFrame) return false;
        var timeline = original.Clone(); var track = AnimationTrack(timeline, layerId);
        track.InFrame = inFrame; track.OutFrame = outFrame;
        return CommitTimeline(timeline, "AnimationClip");
    }

    public bool SetAnimationKeys(Guid layerId, int frame, IReadOnlyDictionary<AnimationProperty, double> values, string interpolation = "linear")
    {
        if (Snapshot.Manifest.Timeline is not { } original || frame < 0 || frame >= original.DurationFrames || values.Count is < 1 or > 6
            || !ProjectTimeline.ValidInterpolation(interpolation) || values.Any(pair => !ProjectTimeline.ValidValue(pair.Key, pair.Value)
                || !CanEditLayer(layerId, pair.Key == AnimationProperty.Opacity ? LayerLocks.None : LayerLocks.Position))) return false;
        var timeline = original.Clone(); var track = AnimationTrack(timeline, layerId);
        foreach (var (property, value) in values)
        {
            var keys = track.Keys(property); keys.RemoveAll(key => key.Frame == frame);
            keys.Add(new() { Frame = frame, Value = value, Interpolation = interpolation }); keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
        }
        return CommitTimeline(timeline, "AnimationKeyframe");
    }

    public bool RemoveAnimationKeys(Guid layerId, int frame, IReadOnlyCollection<AnimationProperty> properties)
    {
        if (Snapshot.Manifest.Timeline is not { } original || !CanEditLayer(layerId, LayerLocks.None)
            || properties.Any(property => !Enum.IsDefined(property)
                || !CanEditLayer(layerId, property == AnimationProperty.Opacity ? LayerLocks.None : LayerLocks.Position))) return false;
        var timeline = original.Clone(); var track = timeline.Tracks.FirstOrDefault(track => track.LayerId == layerId);
        if (track is null) return false;
        foreach (var property in properties) track.Keys(property).RemoveAll(key => key.Frame == frame);
        return CommitTimeline(timeline, "AnimationDeleteKeyframe");
    }

    public bool MoveAnimationKeys(Guid layerId, int fromFrame, int toFrame, IReadOnlyCollection<AnimationProperty> properties)
    {
        if (Snapshot.Manifest.Timeline is not { } original || toFrame < 0 || toFrame >= original.DurationFrames
            || !CanEditLayer(layerId, LayerLocks.None) || properties.Any(property => !Enum.IsDefined(property)
                || !CanEditLayer(layerId, property == AnimationProperty.Opacity ? LayerLocks.None : LayerLocks.Position))) return false;
        var timeline = original.Clone(); var track = timeline.Tracks.FirstOrDefault(track => track.LayerId == layerId);
        if (track is null) return false;
        foreach (var property in properties)
        {
            var keys = track.Keys(property); var key = keys.FirstOrDefault(key => key.Frame == fromFrame);
            if (key is null) continue;
            keys.RemoveAll(candidate => candidate.Frame == fromFrame || candidate.Frame == toFrame);
            keys.Add(key with { Frame = toFrame }); keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
        }
        return CommitTimeline(timeline, "AnimationMoveKeyframe");
    }

    public bool ClearAnimation() => CommitTimeline(null, "AnimationClear");

    public bool ShiftAnimationTrack(Guid layerId, int frames)
    {
        if (Snapshot.Manifest.Timeline is not { } original || !CanEditLayer(layerId, LayerLocks.Position)) return false;
        var timeline = original.Clone(); var track = AnimationTrack(timeline, layerId);
        if ((long)track.InFrame + frames < 0 || (long)track.OutFrame + frames > timeline.DurationFrames) return false;
        foreach (var property in Enum.GetValues<AnimationProperty>())
        {
            var keys = track.Keys(property);
            if (keys.Any(key => (long)key.Frame + frames < 0 || (long)key.Frame + frames >= timeline.DurationFrames)) return false;
            for (int i = 0; i < keys.Count; i++) keys[i] = keys[i] with { Frame = keys[i].Frame + frames };
        }
        track.InFrame += frames; track.OutFrame += frames;
        return CommitTimeline(timeline, "AnimationShiftTrack");
    }

    private static LayerAnimationTrack AnimationTrack(ProjectTimeline timeline, Guid layerId)
    {
        var track = timeline.Tracks.FirstOrDefault(candidate => candidate.LayerId == layerId);
        if (track is not null) return track;
        track = new() { LayerId = layerId, InFrame = 0, OutFrame = timeline.DurationFrames }; timeline.Tracks.Add(track); return track;
    }

    private bool CommitTimeline(ProjectTimeline? timeline, string operation)
    {
        if (ActiveTextDraft is not null || timeline is not null && !timeline.IsValidFor(Snapshot.Manifest.Layers)) return false;
        if (JsonSerializer.Serialize(timeline, ManifestJson.CompactOptions) == JsonSerializer.Serialize(Snapshot.Manifest.Timeline, ManifestJson.CompactOptions)) return true;
        BeginChange(operation: operation); Snapshot.Manifest.Timeline = timeline;
        if (timeline is not null) Snapshot.Manifest.Version = Math.Max(Snapshot.Manifest.Version, 12);
        EndChange(); return true;
    }

    private void PruneAnimationTracks()
    {
        if (Snapshot.Manifest.Timeline is not { } timeline) return;
        var ids = Snapshot.Manifest.Layers.Select(layer => layer.Id).ToHashSet();
        if (timeline.Tracks.All(track => ids.Contains(track.LayerId))) return;
        var pruned = timeline.Clone(); pruned.Tracks.RemoveAll(track => !ids.Contains(track.LayerId)); Snapshot.Manifest.Timeline = pruned;
    }

    private bool TryRemapAnimation(double scaleX, double scaleY, double offsetX, double offsetY, bool mirrorRotation, out ProjectTimeline? timeline)
    {
        timeline = Snapshot.Manifest.Timeline?.Clone();
        if (timeline is null) return true;
        foreach (var track in timeline.Tracks)
        {
            track.PositionX = track.PositionX.Select(key => key with { Value = key.Value * scaleX + offsetX }).ToList();
            track.PositionY = track.PositionY.Select(key => key with { Value = key.Value * scaleY + offsetY }).ToList();
            if (mirrorRotation) track.Rotation = track.Rotation.Select(key => key with { Value = -key.Value }).ToList();
        }
        return timeline.IsValidFor(Snapshot.Manifest.Layers);
    }
}
