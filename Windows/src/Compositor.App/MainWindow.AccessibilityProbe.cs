#if DEBUG
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App;

public sealed partial class MainWindow
{
#if DEBUG
    // Counted by the window's own Activated handler (MainWindow.Navigation.cs). #33's hypothesis is that the
    // silent routed focus events correlate with the window not being in the foreground, which needs a number
    // beside the event stream rather than a memory of what the machine felt like. `Activated` answers for both
    // directions, so deactivations are counted apart from activations: the two numbers beside a red sample say
    // whether the window was ever pushed away during the run — they do not say in which order, which is what the
    // post-mortem wait in the floating focus group is for.
    internal int windowActivationCount;
    internal int windowDeactivationCount;
    internal string lastActivationState = "none";
#endif

    /// <summary>
    /// Asks every control under <paramref name="root"/> that a screen reader can operate what it reports as its
    /// name. Only controls whose own peer says it is in the control view are counted, because a template part a
    /// reader never walks to (<c>EditableText</c>, <c>UpSpinButton</c>) would otherwise be booked as work — the
    /// first pass counted 58 of 145 that way and the real number was 35 of 117. The offenders are named by their
    /// nearest labelled ancestor because rows are built in a loop and have no name of their own.
    /// A measurement for the report, not a gate: see <c>Windows/docs/c13b-accessibility-2026-10-17.md</c>.
    /// </summary>
    internal (int Named, List<string> Unnamed) AccessibleControls(DependencyObject root)
    {
        var unnamed = new List<string>();
        var named = 0;
        Walk(root);
        return (named, unnamed);

        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement fe
                && fe is ButtonBase or TextBox or ComboBox or CheckBox or Slider or ToggleSwitch
                    or RadioButton or NumberBox or AutoSuggestBox
                && FrameworkElementAutomationPeer.CreatePeerForElement(fe) is { } peer
                && peer.IsControlElement())
            {
                if (!string.IsNullOrWhiteSpace(peer.GetName())) named++;
                else
                {
                    var ancestor = "?";
                    for (var walk = VisualTreeHelper.GetParent(fe); walk is not null; walk = VisualTreeHelper.GetParent(walk))
                        if (walk is FrameworkElement step && !string.IsNullOrWhiteSpace(step.Name)) { ancestor = step.Name; break; }
                    unnamed.Add($"{(AutomationProperties.GetAutomationId(fe) is { Length: > 0 } id ? id : fe.Name)}"
                        + $":{fe.GetType().Name}@{ancestor}");
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>The same sweep as one report line.</summary>
    internal string AccessibilityInventory(DependencyObject root)
    {
        var (named, unnamed) = AccessibleControls(root);
        return FormatInventory(named, unnamed);
    }

    /// <summary>
    /// Records what one surface's controls report, then holds it to the only two claims that survive a sample
    /// whose size legitimately changes — a panel shows different rows by style, a draft expands on a click:
    /// nothing unnamed, and at least one control actually found. The second half is not decoration. C13g built
    /// the first version of this without it and a mutation that swept an empty tree passed it green, because an
    /// empty tree has nothing unnamed either.
    /// </summary>
    internal void RequireSurfaceControlsNamed(Dictionary<string, object> report, string key, DependencyObject? root)
    {
        var (named, unnamed) = root is null ? (0, new List<string>()) : AccessibleControls(root);
        report[key] = FormatInventory(named, unnamed);
        Require(unnamed.Count == 0,
            $"{key}: {unnamed.Count} controls report no name to a reader: [{string.Join(", ", unnamed)}]");
        Require(named + unnamed.Count >= 1,
            $"{key}: the sweep walked nothing, so its zero says nothing about the surface");
    }

    internal static string FormatInventory(int named, List<string> unnamed)
        => $"controls={named + unnamed.Count} named={named} unnamed={unnamed.Count} missing=[{string.Join(", ", unnamed)}]";
}
#endif
