using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public class GradientVectorTests
{
    public static IEnumerable<object?[]> SerialFixtures()
    {
        // Frozen from 7b5e6d7 before adding vector rows, including rounding boundaries.
        using var stream = typeof(GradientVectorTests).Assembly.GetManifestResourceStream(
            "Compositor.Core.Tests.Fixtures.gradient-vector-reference.json")!;
        using var json = JsonDocument.Parse(stream);
        foreach (var fixture in json.RootElement.EnumerateArray())
            yield return new object?[] { fixture.GetProperty("scenario").GetInt32(), fixture.GetProperty("hash").GetString() };
    }

    [Theory]
    [MemberData(nameof(SerialFixtures))]
    public void PreviewRetainsScalarPixelsAndNoOpDetection(int scenario, string? expected)
    {
        var (document, id, mask, start, end, settings) = GradientVectorScenario.Create(scenario);
        var source = document.Snapshot;
        var sourcePixels = mask ? source.Masks[id].Pixels : source.Images[id].Pixels;
        var sourceHash = SHA256.HashData(sourcePixels);
        var preview = document.BeginGradient(id, mask)!.Preview(start, end, settings);
        if (expected is null) Assert.Null(preview);
        else
        {
            Assert.NotNull(preview);
            var pixels = mask ? preview.Snapshot.Masks[id].Pixels : preview.Snapshot.Images[id].Pixels;
            Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(pixels)));
        }
        Assert.Equal(sourceHash, SHA256.HashData(sourcePixels));
        Assert.Same(source, document.Snapshot);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
    }
}
