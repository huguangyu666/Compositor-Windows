using System.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
    private readonly Queue<Geometry> retiredOutlineRoots = new();
    private readonly HashSet<Geometry> retiredOutlineIdentities = new(ReferenceEqualityComparer.Instance);
    private bool retiringOutlines;
#if DEBUG
    /// <summary>How long a probe may wait for detached outline figures to drain. Teardown releases a
    /// bounded batch of figures per UI slice and yields at Low priority to whatever else the dispatcher
    /// wants, so on a loaded machine even the last 53k-figure root can cross twenty seconds while the
    /// diagnostics show it making steady progress (failed runs expired at 19k-53k of 53,148 figures
    /// removed, with 20s green passes on a quiet box). The longer budget still catches a true livelock:
    /// a queue that never empties never reaches zero at thirty seconds either.</summary>
    internal const int OutlineDrainBudgetMs = 30000;

    private int retiredOutlineFigures, retiredOutlineYields, retiredOutlinePeakRoots;
    private double retiredOutlineMaxSliceMs;
    private readonly List<object> retiredOutlineRootTimings = new();
    private string? retiredOutlineRootKind;
    private int retiredOutlineRootFigures, retiredOutlineRootFiguresBefore;
    private long retiredOutlineRootStarted, retiredOutlineRootWaitStarted;
    private double retiredOutlineRootWaitMs;

    private static int OutlineFigureCount(Geometry root) => root switch
    {
        PathGeometry path => path.Figures.Count,
        GeometryGroup group => group.Children.Sum(OutlineFigureCount),
        _ => 0,
    };

    private object OutlineRetirementDiagnosticState() => new
    {
        completed = retiredOutlineRootTimings.ToArray(),
        current = retiredOutlineRootKind is null ? null : new
        {
            kind = retiredOutlineRootKind, figures = retiredOutlineRootFigures,
            removed = retiredOutlineFigures - retiredOutlineRootFiguresBefore,
            elapsedMs = Stopwatch.GetElapsedTime(retiredOutlineRootStarted).TotalMilliseconds,
            waitMs = retiredOutlineRootWaitMs + (retiredOutlineRootWaitStarted == 0 ? 0
                : Stopwatch.GetElapsedTime(retiredOutlineRootWaitStarted).TotalMilliseconds),
        },
    };
#endif

    private void ReplaceSelectionOutlineData(Geometry? geometry)
    {
        var previous = SelectionOutline.Data;
        SelectionOutline.Data = geometry;
        if (!ReferenceEquals(previous, geometry) && !ReferenceEquals(previous, selectionOutlineGeometry)) RetireOutline(previous);
    }

    private void RetireOutline(Geometry? geometry)
    {
        if (geometry is null || ReferenceEquals(geometry, SelectionOutline.Data) || ReferenceEquals(geometry, selectionOutlineGeometry)
            || !retiredOutlineIdentities.Add(geometry)) return;
        // Keep the root alive until its native child collections have been emptied.
        // Releasing a large root wholesale makes WinUI delete every dependency object
        // in one UI-affinity cleanup callback, which can block input for seconds.
        retiredOutlineRoots.Enqueue(geometry);
#if DEBUG
        retiredOutlinePeakRoots = Math.Max(retiredOutlinePeakRoots, retiredOutlineRoots.Count);
#endif
        if (!retiringOutlines) _ = RetireOutlinesAsync();
    }

    private async Task RetireOutlinesAsync()
    {
        retiringOutlines = true;
        try
        {
            var slice = Stopwatch.StartNew();
            while (!windowClosed && retiredOutlineRoots.TryPeek(out var root))
            {
#if DEBUG
                retiredOutlineRootKind = root.GetType().Name;
                retiredOutlineRootFigures = OutlineFigureCount(root);
                retiredOutlineRootFiguresBefore = retiredOutlineFigures;
                retiredOutlineRootStarted = Stopwatch.GetTimestamp(); retiredOutlineRootWaitMs = 0; retiredOutlineRootWaitStarted = 0;
#endif
                var branches = new Stack<Geometry>(); branches.Push(root);
                while (!windowClosed && branches.TryPeek(out var current))
                {
                    if (current is GeometryGroup group && group.Children.Count > 0)
                    {
                        // Hold the child before detaching it so its native figures stay
                        // alive until the bounded figure loop can release them.
                        branches.Push(group.Children[^1]); group.Children.RemoveAt(group.Children.Count - 1);
                    }
                    else if (current is PathGeometry path && path.Figures.Count > 0)
                    {
                        path.Figures.RemoveAt(path.Figures.Count - 1);
#if DEBUG
                        retiredOutlineFigures++;
#endif
                    }
                    else branches.Pop();
                    if (slice.Elapsed.TotalMilliseconds >= 2)
                    {
#if DEBUG
                        retiredOutlineMaxSliceMs = Math.Max(retiredOutlineMaxSliceMs, slice.Elapsed.TotalMilliseconds);
                        retiredOutlineYields++;
#endif
                        // New contours and frame publication take priority over
                        // detached cleanup; a normal-priority yield interleaves
                        // retirement with every construction batch.
                        var resumed = new TaskCompletionSource();
                        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                            () => resumed.TrySetResult())) return;
#if DEBUG
                        retiredOutlineRootWaitStarted = Stopwatch.GetTimestamp();
#endif
                        await resumed.Task; slice.Restart();
#if DEBUG
                        retiredOutlineRootWaitMs += Stopwatch.GetElapsedTime(retiredOutlineRootWaitStarted).TotalMilliseconds;
                        retiredOutlineRootWaitStarted = 0;
#endif
                    }
                }
                if (windowClosed) return;
                retiredOutlineRoots.Dequeue(); retiredOutlineIdentities.Remove(root);
#if DEBUG
                if (retiredOutlineRootTimings.Count >= 32) retiredOutlineRootTimings.RemoveAt(0);
                double elapsedMs = Stopwatch.GetElapsedTime(retiredOutlineRootStarted).TotalMilliseconds;
                retiredOutlineRootTimings.Add(new { kind = retiredOutlineRootKind, figures = retiredOutlineRootFigures,
                    removed = retiredOutlineFigures - retiredOutlineRootFiguresBefore, elapsedMs,
                    waitMs = retiredOutlineRootWaitMs, activeApproxMs = elapsedMs - retiredOutlineRootWaitMs });
                retiredOutlineRootKind = null;
#endif
            }
        }
        catch (Exception ex) { App.Log($"Selection outline retirement failed: {ex}"); }
        finally { retiringOutlines = false; }
    }
}
