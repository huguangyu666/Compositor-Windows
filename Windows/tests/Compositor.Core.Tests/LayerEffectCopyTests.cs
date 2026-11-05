using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// The Mac's effect-row drag onto a layer (NativeLayerList.swift:381-390 → copyEffect,
/// LayerEffects.swift:322-348): one effect moves from the layer it was dragged off to the layer it was
/// dropped on, as its own undo step, without touching the rest of either layer's effects.
/// </summary>
public class LayerEffectCopyTests
{
    private static (EditorDocument Document, Guid Source, Guid Target) TwoLayers()
    {
        var document = EditorDocument.Create(16, 16);
        var source = document.ImportImage(new Surface(8, 8), "Source");
        var target = document.ImportImage(new Surface(8, 8), "Target");
        return (document, source.Id, target.Id);
    }

    private static LayerEffects? EffectsOf(EditorDocument document, Guid id)
        => LayerEffects.Parse(document.Snapshot.Manifest.Layers.Single(layer => layer.Id == id).Effects);

    [Fact]
    public void CopyingAnEffectPutsItOnTheTargetAndLeavesTheSourceAlone()
    {
        var (document, source, target) = TwoLayers();
        Assert.True(document.SetLayerEffects(source, new LayerEffects
        {
            Shadow = new ShadowEffect { Blur = 40, Distance = 7, Opacity = .8 },
        }));

        Assert.True(document.CanCopyEffect(LayerEffectKind.Shadow, source, target));
        Assert.True(document.CopyEffect(LayerEffectKind.Shadow, source, target));

        Assert.Equal(40, EffectsOf(document, target)!.Shadow!.Blur);
        Assert.Equal(40, EffectsOf(document, source)!.Shadow!.Blur);

        // Its own step: one Undo takes it off the target and leaves the source's shadow in place.
        Assert.True(document.Undo());
        Assert.Null(EffectsOf(document, target)?.Shadow);
        Assert.Equal(40, EffectsOf(document, source)!.Shadow!.Blur);
    }

    [Fact]
    public void CopyingAnEffectKeepsWhateverElseTheTargetCarried()
    {
        var (document, source, target) = TwoLayers();
        document.SetLayerEffects(source, new LayerEffects { Shadow = new() { Blur = 30 } });
        document.SetLayerEffects(target, new LayerEffects { Stroke = new() { Size = 5 } });

        Assert.True(document.CopyEffect(LayerEffectKind.Shadow, source, target));

        var after = EffectsOf(document, target)!;
        Assert.Equal(5, after.Stroke!.Size);
        Assert.Equal(30, after.Shadow!.Blur);
    }

    [Fact]
    public void TheSourceAndTheTargetDoNotShareOneEffect()
    {
        var (document, source, target) = TwoLayers();
        document.SetLayerEffects(source, new LayerEffects { Shadow = new() { Blur = 30 } });
        Assert.True(document.CopyEffect(LayerEffectKind.Shadow, source, target));

        // The copy is written into the target's own manifest entry, so changing it afterwards must not
        // reach back into the layer it came from — the Swift side gets this from value semantics for free.
        Assert.True(document.SetLayerEffects(target, new LayerEffects { Shadow = new() { Blur = 90 } }));
        Assert.Equal(30, EffectsOf(document, source)!.Shadow!.Blur);
        Assert.Equal(90, EffectsOf(document, target)!.Shadow!.Blur);
    }

    [Fact]
    public void AnEffectCannotBeCopiedOntoItselfAFolderOrALayerWithoutIt()
    {
        var (document, source, target) = TwoLayers();
        document.SetLayerEffects(source, new LayerEffects { Shadow = new() { Blur = 30 } });
        var folder = document.GroupLayers(target)!;

        Assert.False(document.CanCopyEffect(LayerEffectKind.Shadow, source, source)); // onto itself
        Assert.False(document.CanCopyEffect(LayerEffectKind.Shadow, source, folder.Id)); // onto a folder
        Assert.False(document.CanCopyEffect(LayerEffectKind.Stroke, source, target)); // the source has none
        Assert.False(document.CopyEffect(LayerEffectKind.Shadow, source, folder.Id));
        Assert.False(document.CopyEffect(LayerEffectKind.Stroke, source, target));

        // Nothing was written, so the next Undo is the grouping rather than a phantom step.
        Assert.True(document.Undo());
        Assert.Null(EffectsOf(document, target)?.Shadow);
    }

    [Fact]
    public void AnAdjustmentLayerTakesNoCopiedEffect()
    {
        var (document, source, target) = TwoLayers();
        document.SetLayerEffects(source, new LayerEffects { Shadow = new() { Blur = 30 } });
        var adjustment = document.AddAdjustment(AdjustmentKind.Curves);

        // The Mac's guard is "the target has an asset": an adjustment has no pixels of its own to shade.
        Assert.False(document.CanCopyEffect(LayerEffectKind.Shadow, source, adjustment.Id));
        Assert.True(document.CanCopyEffect(LayerEffectKind.Shadow, source, target));
    }
}
