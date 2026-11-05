using System.Buffers.Binary;
using System.Text;
using Compositor.Core.Psd;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class PsdBlendImportTests
{
    // PSDTypes.swift:69-98 is the source mapping, including its four-character padding.
    public static TheoryData<string, LayerBlendMode> SupportedKeys => new()
    {
        { "norm", LayerBlendMode.Normal }, { "mul ", LayerBlendMode.Multiply },
        { "scrn", LayerBlendMode.Screen }, { "over", LayerBlendMode.Overlay },
        { "sLit", LayerBlendMode.SoftLight }, { "dark", LayerBlendMode.Darken },
        { "lite", LayerBlendMode.Lighten }, { "diff", LayerBlendMode.Difference },
        { "div ", LayerBlendMode.ColorDodge }, { "idiv", LayerBlendMode.ColorBurn },
        { "hue ", LayerBlendMode.Hue }, { "sat ", LayerBlendMode.Saturation },
        { "colr", LayerBlendMode.Color }, { "lum ", LayerBlendMode.Luminosity },
        { "lbrn", LayerBlendMode.LinearBurn }, { "lddg", LayerBlendMode.LinearDodge },
        { "hLit", LayerBlendMode.HardLight }, { "vLit", LayerBlendMode.VividLight },
        { "lLit", LayerBlendMode.LinearLight }, { "pLit", LayerBlendMode.PinLight },
        { "hMix", LayerBlendMode.HardMix }, { "smud", LayerBlendMode.Exclusion },
        { "fsub", LayerBlendMode.Subtract }, { "fdiv", LayerBlendMode.Divide },
    };

    [Theory]
    [MemberData(nameof(SupportedKeys))]
    public void ReaderAndBothImportPathsPreserveSupportedBlendKeys(string key, LayerBlendMode expected)
    {
        foreach (bool psb in new[] { false, true })
        {
            var parsed = PsdReader.Read(Build(key, psb));
            var direct = EditorDocument.Create(1, 1);
            Assert.Equal(expected, Assert.Single(direct.ImportPsd(parsed)).BlendMode);
            Assert.True(direct.Undo()); Assert.False(direct.CanUndo);
            Assert.True(direct.Redo()); Assert.Equal(expected, direct.Snapshot.Manifest.Layers[^1].BlendMode);

            var batchOwner = EditorDocument.Create(1, 1);
            using (var batch = batchOwner.BeginFileImport()) batch.AddPhotoshop(parsed, "Fixture");
            Assert.Equal(expected, batchOwner.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Blend").BlendMode);
            Assert.True(batchOwner.Undo()); Assert.False(batchOwner.CanUndo);
            Assert.True(batchOwner.Redo());
            Assert.Equal(expected, batchOwner.Snapshot.Manifest.Layers.Single(layer => layer.Name == "Blend").BlendMode);
            ProjectValidator.Validate(batchOwner.Snapshot.Manifest);
        }
    }

    [Fact]
    public void MultiplyImportRendersMultiplyPixelsAndSurvivesProjectRoundTrip()
    {
        var owner = EditorDocument.Create(1, 1);
        var background = owner.Snapshot.Images[owner.Snapshot.Manifest.ActiveLayerId!.Value];
        new byte[] { 200, 100, 50, 255 }.CopyTo(background.Pixels, 0);
        using (var batch = owner.BeginFileImport()) batch.AddPhotoshop(PsdReader.Read(Build("mul ")), "Fixture");
        // The imported pixel is (128,64,192). Normal would replace the background, whereas Multiply
        // must produce (100,25,38), rounded by the compositor's existing integer path.
        Assert.Equal(new byte[] { 100, 25, 38, 255 }, new CanvasCompositor(owner.Snapshot).Render().Pixels);
        var restored = ManifestJson.Deserialize(ManifestJson.Serialize(owner.Snapshot.Manifest));
        Assert.Equal(LayerBlendMode.Multiply, restored.Layers.Single(layer => layer.Name == "Blend").BlendMode);
    }

    [Theory]
    [InlineData("zzzz", false, true)]
    [InlineData("diss", false, true)]
    [InlineData("pass", false, false)]
    [InlineData("norm", true, false)]
    [InlineData("pass", true, false)]
    [InlineData("mul ", true, true)]
    public void UnsupportedModesRemainExplicitAndFoldersStayPassThrough(string key, bool group, bool warning)
    {
        var layer = new PsdLayer { Id = Guid.NewGuid(), Name = "Folder", IsGroup = group,
            Offset = new(0, 0), BlendKey = key };
        Assert.Equal(LayerBlendMode.Normal, layer.BlendMode);
        Assert.Equal(warning, layer.HasUnsupportedBlendMode);
        if (!group)
        {
            var parsed = Assert.Single(PsdReader.Read(Build(key)).Layers);
            Assert.Equal(key, parsed.BlendKey);
            Assert.Equal(warning, parsed.HasUnsupportedBlendMode);
        }
    }

    private static byte[] Build(string blendKey, bool psb = false)
    {
        using var record = new MemoryStream();
        I32(record, 0); I32(record, 0); I32(record, 1); I32(record, 1); U16(record, 4);
        foreach (int channel in new[] { 0, 1, 2, -1 }) { U16(record, channel); Length(record, 3, psb); }
        record.Write("8BIM"u8); record.Write(Encoding.ASCII.GetBytes(blendKey)); record.Write([255, 0, 0, 0]);
        I32(record, 16); I32(record, 0); I32(record, 0); record.Write([5, .. "Blend"u8, 0, 0]);
        using var info = new MemoryStream(); U16(info, 1); info.Write(record.ToArray());
        foreach (byte value in new byte[] { 128, 64, 192, 255 }) { U16(info, 0); info.WriteByte(value); }
        using var file = new MemoryStream();
        file.Write("8BPS"u8); U16(file, psb ? 2 : 1); file.Write(new byte[6]); U16(file, 4);
        I32(file, 1); I32(file, 1); U16(file, 8); U16(file, 3); I32(file, 0); I32(file, 0);
        Length(file, info.Length + (psb ? 8 : 4), psb); Length(file, info.Length, psb); file.Write(info.ToArray());
        return file.ToArray();
    }

    private static void U16(Stream stream, int value) => stream.Write([(byte)(value >> 8), (byte)value]);
    private static void I32(Stream stream, long value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)value)); stream.Write(bytes); }
    private static void Length(Stream stream, long value, bool psb) { if (psb) I32(stream, 0); I32(stream, value); }
}
