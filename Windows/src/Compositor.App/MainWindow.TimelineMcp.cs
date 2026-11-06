using Compositor.Core;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private static AnimationProperty TimelineProperty(string? name) => name switch
    {
        "positionX" => AnimationProperty.PositionX, "positionY" => AnimationProperty.PositionY,
        "scaleX" => AnimationProperty.ScaleX, "scaleY" => AnimationProperty.ScaleY,
        "rotation" => AnimationProperty.Rotation, "opacity" => AnimationProperty.Opacity,
        _ => throw new ArgumentException("Use positionX, positionY, scaleX, scaleY, rotation or opacity."),
    };

    private async Task<JsonObject> EditorTimelineAsync(JsonObject args, EditorDocument owner)
    {
        StopTimelinePlayback(); RequireEditorIdle(allowTimeline: true);
        if (pendingTimelineValues.Count > 0) throw new InvalidOperationException("Finish the active keyframe field before using MCP.");
        string operation = args["operation"]?.GetValue<string>() ?? "";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await QueueEditorActionAsync(owner, args, deadline.Token, () =>
        {
            applyingTimeline = true;
            try
            {
                bool changed;
                if (operation == "settings")
                {
                    var current = Timeline;
                    int fps = args["framesPerSecond"]?.GetValue<int>() ?? current?.FramesPerSecond ?? 24;
                    int frames = args["durationFrames"]?.GetValue<int>() ?? (current is null ? 120 : (int)Math.Round((double)current.DurationFrames * fps / current.FramesPerSecond));
                    changed = owner.SetAnimationSettings(frames, fps,
                        args["loop"]?.GetValue<bool>() ?? current?.Loop ?? true);
                    if (changed && current is not null) timelineFrame = (int)Math.Round((double)timelineFrame * fps / current.FramesPerSecond);
                }
                else if (operation == "clear") changed = owner.ClearAnimation();
                else if (operation is "seek" or "preview")
                {
                    if (Timeline is not { } timeline) throw new InvalidOperationException("Create a timeline with operation=settings first.");
                    if (operation == "seek")
                    {
                        int frame = EditorInt(args, "frame");
                        if (frame < 0 || frame >= timeline.DurationFrames) throw new ArgumentException("frame must be inside the timeline.");
                        timelineFrame = frame; timelinePreview.IsChecked = true;
                    }
                    else timelinePreview.IsChecked = args["enabled"]?.GetValue<bool>() ?? throw new ArgumentException("Missing enabled.");
                    changed = true;
                }
                else
                {
                    if (!Guid.TryParse(args["layerId"]?.GetValue<string>(), out var id)) throw new ArgumentException("Choose a layerId from editor_state.");
                    var properties = args["properties"] is JsonArray list ? list.Select(item => TimelineProperty(item?.GetValue<string>())).ToArray()
                        : Enum.GetValues<AnimationProperty>();
                    changed = operation switch
                    {
                        "keyframe" => owner.SetAnimationKeys(id, EditorInt(args, "frame"),
                            args["values"] is JsonObject values ? values.ToDictionary(pair => TimelineProperty(pair.Key),
                                pair => pair.Value?.Deserialize<double>() ?? throw new ArgumentException("A keyframe value must be numeric."))
                                : throw new ArgumentException("Missing values object."), args["interpolation"]?.GetValue<string>() ?? "linear"),
                        "clip" => owner.SetAnimationClip(id, EditorInt(args, "inFrame"), EditorInt(args, "outFrame")),
                        "deleteKeyframe" => owner.RemoveAnimationKeys(id, EditorInt(args, "frame"), properties),
                        "moveKeyframe" => owner.MoveAnimationKeys(id, EditorInt(args, "fromFrame"), EditorInt(args, "frame"), properties),
                        "shiftTrack" => owner.ShiftAnimationTrack(id, EditorInt(args, "deltaFrames")),
                        _ => throw new ArgumentException("Unknown timeline operation."),
                    };
                    if (changed) timelineLayer = id;
                }
                if (!changed) throw new ArgumentException("Invalid timeline edit or protected layer. Check frame bounds, key values and layer locks.");
            }
            finally { applyingTimeline = false; }
        }, allowTimeline: true);
        timelineVisible = true; TimelineToggle.IsChecked = true; TimelineHost.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        RefreshTimeline(); RefreshTimelineMode(); RenderCanvas();
        SetStatus("MCP · editor_timeline");
        return EditorResult(EditorState());
    }
}
