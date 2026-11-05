using System.Security.Cryptography;
using Xunit;

namespace Compositor.Core.Tests;

public class GradientBatchTests
{
    // Captured from the independent serial implementation at fb1c66d, before row batching.
    [Theory]
    [InlineData("linear", "9BE1ADE7C9C5846FFF15987ACF4D011ED2C32BBDDDDB087825199AB8E722AF6E")]
    [InlineData("radial", "8F59C0523E3D4525E032486A8C12FD1F4906603C7B67D36A0CD9EF1F5AF43C91")]
    [InlineData("mask", "1C41779D6BE01D62803D0302D74C8D46221896AF2AFB876653CD129C631F0520")]
    [InlineData("rotated-selection", "E9DEAF51A7DA3AA807DB33A6629A9803B67097A97E7E43886BD776F065395EF2")]
    [InlineData("independent-mask", "2944F021114367591DAA1FFCE1F28639A21546A2803F024E0908B7BAD27D14F8")]
    [InlineData("clipped-flipped", "88273A342C1E434D016FC400DDA199129557FBB8A97A5CFFB5EB5004584975DE")]
    [InlineData("unchanged", null)]
    [InlineData("last-row", "EE6A65C2FA98C9513E1C760E0B3799B6EB16E4AA524BFA3C2D27860FF1A8E539")]
    [InlineData("small", "275F08A82B209C4AAE05C55915F8EB66F2DCA0270BE470E953A37AC5696C5F7F")]
    public void PreviewRetainsSerialBytesAndNoOpDetection(string name, string? expected)
    {
        var (document, id, mask) = GradientBatchScenario.Create(name);
        var source = document.Snapshot;
        var preview = document.BeginGradient(id, mask)!.Preview(new(3.12, 2.43),
            new(source.Manifest.Width - .38, source.Manifest.Height - .86), GradientBatchScenario.Settings(name));
        if (expected is null) Assert.Null(preview);
        else
        {
            Assert.NotNull(preview);
            var pixels = mask ? preview.Snapshot.Masks[id].Pixels : preview.Snapshot.Images[id].Pixels;
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(pixels)));
        }
        Assert.Same(source, document.Snapshot);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
    }

    [Theory]
    [InlineData("linear")]
    [InlineData("mask")]
    [InlineData("rotated-selection")]
    public async Task ConcurrentPreviewsOwnTheirPixelsAndCommitOneHistoryStep(string name)
    {
        var (document, id, mask) = GradientBatchScenario.Create(name);
        var source = document.Snapshot;
        var sourceBytes = (byte[])(mask ? source.Masks[id].Pixels : source.Images[id].Pixels).Clone();
        var edit = document.BeginGradient(id, mask)!;
        var previews = await Task.WhenAll(Enumerable.Range(0, 3).Select(frame => Task.Run(() =>
            edit.Preview(new(3.12, 2.43), new(700 - frame * 17, 370), GradientBatchScenario.Settings(name))!)));
        byte[] Pixels(GradientPreview preview) => mask ? preview.Snapshot.Masks[id].Pixels : preview.Snapshot.Images[id].Pixels;
        var retained = (byte[])Pixels(previews[0]).Clone();
        Assert.NotSame(Pixels(previews[0]), Pixels(previews[1]));
        Assert.NotSame(Pixels(previews[1]), Pixels(previews[2]));
        Assert.True(document.CommitGradient(previews[2]));
        Assert.Equal(retained, Pixels(previews[0]));
        Assert.Equal(sourceBytes, mask ? source.Masks[id].Pixels : source.Images[id].Pixels);
        Assert.True(document.Undo());
        Assert.False(document.CanUndo);
        Assert.Equal(sourceBytes, mask ? document.Snapshot.Masks[id].Pixels : document.Snapshot.Images[id].Pixels);
        Assert.True(document.Redo());
        Assert.Equal(Pixels(previews[2]), mask ? document.Snapshot.Masks[id].Pixels : document.Snapshot.Images[id].Pixels);
    }
}
