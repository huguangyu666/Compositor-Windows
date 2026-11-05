using Compositor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.UI.ViewManagement;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private sealed record McpActivityPlan(LayerTransform Target, string Action, bool Creating = false,
        LayerTransform? Source = null, int Commands = 1);

    private sealed class McpActivitySession(EditorDocument owner, long revision, McpActivityPlan plan, bool animated, bool compact)
    {
        public readonly EditorDocument Owner = owner;
        public long Revision = revision;
        public readonly McpActivityPlan Plan = plan;
        public readonly bool Animated = animated, Compact = compact;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly TaskCompletionSource Drawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double? Finished;
        public double Duration => !Animated ? 0 : Compact ? .12 : Plan.Creating ? .46 : Plan.Source is not null ? .32 : .16;
    }

    private McpActivitySession? mcpActivity;
    private long lastMcpActivity;
#if DEBUG
    private bool? mcpActivityAnimationsProbe;
#endif

    private static McpActivityPlan? DescribeMcpActivity(ProjectSnapshot before, ProjectSnapshot after, int commands = 1)
    {
        var previous = before.Manifest.Layers.ToDictionary(l => l.Id);
        var changed = after.Manifest.Layers.Where(l => !l.IsGroupOrFalse
            && (!previous.TryGetValue(l.Id, out var old) || !old.Equals(l))).ToArray();
        bool removed = false;
        if (changed.Length == 0)
        {
            var remaining = after.Manifest.Layers.Select(l => l.Id).ToHashSet();
            changed = before.Manifest.Layers.Where(l => !l.IsGroupOrFalse && !remaining.Contains(l.Id)).ToArray();
            removed = changed.Length > 0;
        }
        if (changed.Length == 0) return null;
        if (changed.Length == 1 && changed[0].Adjustment is null)
        {
            var layer = changed[0];
            bool creating = !removed && !previous.ContainsKey(layer.Id);
            var source = !creating && !removed && previous.TryGetValue(layer.Id, out var old) && !old.Transform.Equals(layer.Transform)
                ? CopyTransform(old.Transform) : null;
            return new(CopyTransform(layer.Transform), removed ? "Mcp_Deleting" : creating ? "Mcp_Creating" : source is not null ? "Mcp_Moving" : "Mcp_Editing",
                creating, source, commands);
        }
        if (changed.Any(l => l.Adjustment is not null))
            return new(new() { Size = new(after.Manifest.Width, after.Manifest.Height) }, "Mcp_Editing", Commands: commands);
        var points = changed.SelectMany(l => new[] { l.Transform.Point(new(0, 0)), l.Transform.Point(new(1, 0)),
            l.Transform.Point(new(1, 1)), l.Transform.Point(new(0, 1)) }).ToArray();
        double x = points.Min(p => p.X), y = points.Min(p => p.Y);
        return new(new() { Origin = new(x, y), Size = new(points.Max(p => p.X) - x, points.Max(p => p.Y) - y) },
            "Mcp_Editing", Commands: Math.Max(commands, changed.Length));
    }

    private async Task<McpActivitySession?> PreviewMcpActivityAsync(EditorDocument owner, McpActivityPlan? plan, CancellationToken cancellation)
    {
        if (plan is null || McpAnimationToggle.IsChecked != true || windowClosed || CanvasStage.ActualWidth <= 0 || CanvasStage.ActualHeight <= 0) return null;
        var corners = new[] { new PointD(0, 0), new(1, 0), new(1, 1), new(0, 1) }.Select(p => viewport.ToView(plan.Target.Point(p))).ToArray();
        if (corners.Max(p => p.X) < 0 || corners.Min(p => p.X) > CanvasStage.ActualWidth
            || corners.Max(p => p.Y) < 0 || corners.Min(p => p.Y) > CanvasStage.ActualHeight) return null;
        StopMcpActivity();
        long now = Stopwatch.GetTimestamp();
        bool animations = new UISettings().AnimationsEnabled;
#if DEBUG
        animations = mcpActivityAnimationsProbe ?? animations;
#endif
        var session = new McpActivitySession(owner, editorRevision, plan, animations,
            plan.Commands > 1 || lastMcpActivity != 0 && Stopwatch.GetElapsedTime(lastMcpActivity, now).TotalSeconds < .8);
        lastMcpActivity = now; mcpActivity = session;
        McpActivityHost.Visibility = Visibility.Visible; McpActivityHost.Opacity = 1;
        McpActivityLabel.Text = "AI · " + (plan.Commands > 1 ? Loc.F("Mcp_Batch", plan.Commands) : Loc.T(plan.Action));
        McpActivitySize.Text = $"{plan.Target.Size.Width:0.#} × {plan.Target.Size.Height:0.#} px";
        CompositionTarget.Rendering += OnMcpActivityFrame;
        OnMcpActivityFrame(null, EventArgs.Empty);
        try
        {
            // A minimized/background window need not hold an edit waiting for a display frame.
            await Task.WhenAny(session.Drawn.Task, Task.Delay(TimeSpan.FromSeconds(session.Duration + .2), cancellation));
            cancellation.ThrowIfCancellationRequested();
            return session;
        }
        catch { CompleteMcpActivity(session, false); throw; }
    }

    private void OnMcpActivityFrame(object? sender, object args)
    {
        if (mcpActivity is not { } session) return;
        if (windowClosed || !ReferenceEquals(document, session.Owner) || editorRevision != session.Revision)
        { StopMcpActivity(); return; }
        double elapsed = session.Clock.Elapsed.TotalSeconds;
        double t = session.Duration == 0 ? 1 : Math.Clamp(elapsed / session.Duration, 0, 1);
        double progress = t * t * (3 - 2 * t);
        var target = session.Plan.Target;
        McpActivityFrame.Points.Clear();
        foreach (var unit in new[] { new PointD(0, 0), new(1, 0), new(1, 1), new(0, 1) })
        {
            var point = target.Point(session.Plan.Creating && !session.Compact ? new(unit.X * progress, unit.Y * progress) : unit);
            if (session.Plan.Source is { } source && !session.Compact)
            {
                var old = source.Point(unit);
                point = new(old.X + (point.X - old.X) * progress, old.Y + (point.Y - old.Y) * progress);
            }
            var view = viewport.ToView(point);
            McpActivityFrame.Points.Add(new(view.X, view.Y));
        }
        var tip = McpActivityFrame.Points[2];
        bool cursorVisible = session.Animated && !session.Compact && session.Finished is null
            && tip.X >= 0 && tip.Y >= 0 && tip.X < CanvasStage.ActualWidth - 4 && tip.Y < CanvasStage.ActualHeight - 4;
        McpActivityCursor.Visibility = cursorVisible ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(McpActivityCursor, tip.X); Canvas.SetTop(McpActivityCursor, tip.Y);
        Canvas.SetLeft(McpActivityBadge, Math.Clamp(tip.X + 22, 8, Math.Max(8, CanvasStage.ActualWidth - 260)));
        Canvas.SetTop(McpActivityBadge, Math.Clamp(tip.Y + 25, 8, Math.Max(8, CanvasStage.ActualHeight - 68)));
        McpActivityFrame.Opacity = session.Finished is null ? .9 : .65;
        if (t >= 1) session.Drawn.TrySetResult();
        if (session.Finished is { } finished)
        {
            double fade = Math.Clamp((elapsed - finished - .14) / .22, 0, 1);
            McpActivityHost.Opacity = session.Animated ? 1 - fade : 1;
            if (fade >= 1) StopMcpActivity();
        }
    }

    private void CompleteMcpActivity(McpActivitySession? session, bool success)
    {
        if (session is null || !ReferenceEquals(mcpActivity, session)) return;
        if (!success) { StopMcpActivity(); return; }
        session.Revision = editorRevision;
        session.Finished = session.Clock.Elapsed.TotalSeconds;
        McpActivityLabel.Text = "AI · " + Loc.T("Mcp_Done");
        OnMcpActivityFrame(null, EventArgs.Empty);
    }

    private void StopMcpActivity()
    {
        CompositionTarget.Rendering -= OnMcpActivityFrame;
        var session = mcpActivity; mcpActivity = null;
        session?.Drawn.TrySetResult();
        if (McpActivityHost is not null) McpActivityHost.Visibility = Visibility.Collapsed;
    }

    private void OnToggleMcpAnimation(object sender, RoutedEventArgs args)
    {
        WriteFlag("mcp-operation-animation", McpAnimationToggle.IsChecked);
        if (!McpAnimationToggle.IsChecked) StopMcpActivity();
    }
}
