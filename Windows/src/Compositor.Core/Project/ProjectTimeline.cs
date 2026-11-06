using System.Text.Json.Serialization;

namespace Compositor.Core;

public enum AnimationProperty { PositionX, PositionY, ScaleX, ScaleY, Rotation, Opacity }

public sealed record AnimationKeyframe
{
    [JsonRequired] public int Frame { get; init; }
    [JsonRequired] public double Value { get; init; }
    public string Interpolation { get; init; } = "linear";
}

public sealed class LayerAnimationTrack
{
    [JsonRequired] public Guid LayerId { get; init; }
    [JsonRequired] public int InFrame { get; set; }
    [JsonRequired] public int OutFrame { get; set; }
    public List<AnimationKeyframe> PositionX { get; set; } = [];
    public List<AnimationKeyframe> PositionY { get; set; } = [];
    public List<AnimationKeyframe> ScaleX { get; set; } = [];
    public List<AnimationKeyframe> ScaleY { get; set; } = [];
    public List<AnimationKeyframe> Rotation { get; set; } = [];
    public List<AnimationKeyframe> Opacity { get; set; } = [];

    public List<AnimationKeyframe> Keys(AnimationProperty property) => property switch
    {
        AnimationProperty.PositionX => PositionX, AnimationProperty.PositionY => PositionY,
        AnimationProperty.ScaleX => ScaleX, AnimationProperty.ScaleY => ScaleY,
        AnimationProperty.Rotation => Rotation, AnimationProperty.Opacity => Opacity,
        _ => throw new ArgumentOutOfRangeException(nameof(property)),
    };

    public LayerAnimationTrack Clone(Guid? layerId = null) => new()
    {
        LayerId = layerId ?? LayerId, InFrame = InFrame, OutFrame = OutFrame,
        PositionX = [.. PositionX], PositionY = [.. PositionY], ScaleX = [.. ScaleX], ScaleY = [.. ScaleY],
        Rotation = [.. Rotation], Opacity = [.. Opacity],
    };
}

public sealed class ProjectTimeline
{
    public const int MaxFrames = 216_000;
    public const int MaxKeyframes = 40_000;
    [JsonRequired] public int DurationFrames { get; set; } = 120;
    [JsonRequired] public int FramesPerSecond { get; set; } = 24;
    public bool Loop { get; set; } = true;
    public List<LayerAnimationTrack> Tracks { get; set; } = [];

    public ProjectTimeline Clone() => new()
    { DurationFrames = DurationFrames, FramesPerSecond = FramesPerSecond, Loop = Loop, Tracks = Tracks.Select(track => track.Clone()).ToList() };

    public bool IsValidFor(IReadOnlyList<ProjectLayerRecord> layers)
    {
        if (DurationFrames is < 1 or > MaxFrames || FramesPerSecond is < 1 or > 120 || Tracks is null || Tracks.Count > layers.Count) return false;
        var byId = layers.ToDictionary(layer => layer.Id); var ids = new HashSet<Guid>(); int total = 0;
        foreach (var track in Tracks)
        {
            if (track is null || !ids.Add(track.LayerId) || !byId.TryGetValue(track.LayerId, out var layer)
                || track.InFrame < 0 || track.OutFrame > DurationFrames || track.OutFrame <= track.InFrame) return false;
            foreach (var property in Enum.GetValues<AnimationProperty>())
            {
                var keys = track.Keys(property);
                if (keys is null || (total += keys.Count) > MaxKeyframes) return false;
                if (property != AnimationProperty.Opacity && (layer.IsGroupOrFalse || layer.Adjustment is not null) && keys.Count > 0) return false;
                int previous = -1;
                foreach (var key in keys)
                {
                    if (key is null || key.Frame <= previous || key.Frame >= DurationFrames || !ValidValue(property, key.Value)
                        || !ValidInterpolation(key.Interpolation)) return false;
                    previous = key.Frame;
                }
            }
        }
        return true;
    }

    public static bool ValidInterpolation(string? value) => value is "linear" or "hold" or "smooth";
    public static bool ValidValue(AnimationProperty property, double value) => double.IsFinite(value) && property switch
    {
        AnimationProperty.PositionX or AnimationProperty.PositionY or AnimationProperty.Rotation => Math.Abs(value) <= 1_000_000,
        AnimationProperty.ScaleX or AnimationProperty.ScaleY => value is >= .001 and <= 1000,
        AnimationProperty.Opacity => value is >= 0 and <= 1,
        _ => false,
    };
}
