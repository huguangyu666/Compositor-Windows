using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class LayerEffectVisibilityTests
{
    [Theory]
    [InlineData("stroke")]
    [InlineData("shadow")]
    [InlineData("colorOverlay")]
    [InlineData("innerShadow")]
    [InlineData("outerGlow")]
    [InlineData("innerGlow")]
    public void EditingAnotherEffectPreservesHiddenEffectParameters(string kind)
    {
        var raw = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [kind] = new { enabled = false, opacity = .37 },
        });
        var edited = LayerEffects.Parse(raw)!.WithKind(kind == "stroke" ? LayerEffectKind.Shadow : LayerEffectKind.Stroke, true);
        var saved = edited.Serialize().GetProperty(kind);
        Assert.True(saved.TryGetProperty("enabled", out var enabled), "Editing effects discarded their saved visibility.");
        Assert.False(enabled.GetBoolean());
        Assert.Equal(.37, saved.GetProperty("opacity").GetDouble());
    }

    [Theory]
    [InlineData(LayerEffectKind.Stroke)]
    [InlineData(LayerEffectKind.Shadow)]
    [InlineData(LayerEffectKind.ColorOverlay)]
    [InlineData(LayerEffectKind.InnerShadow)]
    [InlineData(LayerEffectKind.OuterGlow)]
    [InlineData(LayerEffectKind.InnerGlow)]
    public void VisibilityKeepsParametersCopiesAndUndoWithoutTouchingPixels(LayerEffectKind kind)
    {
        var original = new LayerEffects().WithKind(kind, true);
        Assert.True(original.IsEnabled(kind));
        var hidden = original.WithEnabled(kind, false);
        Assert.True(hidden.Contains(kind)); Assert.False(hidden.IsEnabled(kind)); Assert.True(original.IsEnabled(kind));
        Assert.Null(LayerEffectsRenderer.Visible(hidden.Serialize()));
        Assert.True(JsonElement.DeepEquals(original.Serialize(), hidden.WithKind(kind, false).WithKind(kind, true).Serialize()));
        var scene = EditorDocument.Create(16, 16); var id = scene.Snapshot.Manifest.Layers[0].Id;
        scene.SetLayerEffects(id, original); scene = new(scene.Snapshot);
        var pixels = scene.Snapshot.Images[id];
        Assert.True(scene.SetLayerEffects(id, hidden));
        Assert.Same(pixels, scene.Snapshot.Images[id]);
        Assert.True(scene.Undo()); Assert.False(scene.CanUndo);
        Assert.True(LayerEffects.Parse(scene.Snapshot.Manifest.Layers[0].Effects)!.IsEnabled(kind));
        Assert.True(scene.Redo());
        var copy = scene.ImportImage(new Surface(4, 4), "Copy").Id;
        Assert.True(scene.CopyEffect(kind, id, copy));
        Assert.False(LayerEffects.Parse(scene.Snapshot.Manifest.Layers.Single(layer => layer.Id == copy).Effects)!.IsEnabled(kind));
    }
}
