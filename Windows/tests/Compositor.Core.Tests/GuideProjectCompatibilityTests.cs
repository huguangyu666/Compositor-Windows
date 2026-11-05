using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Compositor.Core.Tests;

public sealed class GuideProjectCompatibilityTests : IDisposable
{
    private static readonly Guid LayerId = Guid.Parse("6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F");
    private readonly string root = Path.Combine(Path.GetTempPath(), "compositor-guide-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task LoadsCanonicalGuidesAndSavesTheirExactRawStrings(int version)
    {
        string path = await WriteFixtureAsync(version);
        var snapshot = await ProjectStore.LoadAsync(path);
        Assert.Equal(version, snapshot.Manifest.Version);
        AssertGuides(snapshot);
        Assert.Equal(FixturePixels(), snapshot.Images[LayerId].Pixels);

        string saved = Path.Combine(root, "saved.comp");
        await ProjectStore.SaveAsync(snapshot, saved);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(saved, "manifest.json")));
        var guides = json.RootElement.GetProperty("guides");
        Assert.Equal("horizontal", guides[0].GetProperty("axis").GetString());
        Assert.Equal("vertical", guides[1].GetProperty("axis").GetString());
        var restored = await ProjectStore.LoadAsync(saved);
        AssertGuides(restored);
        Assert.Equal(snapshot.Images[LayerId].Pixels, restored.Images[LayerId].Pixels);
    }

    [Theory]
    [InlineData(GuideAxis.Horizontal, "horizontal")]
    [InlineData(GuideAxis.Vertical, "vertical")]
    public async Task SavesNewGuidesUsingTheMacRawValue(GuideAxis axis, string expected)
    {
        var document = EditorDocument.Create(8, 8);
        document.AddGuide(axis, 3.25);
        string path = Path.Combine(root, "new.comp");
        await ProjectStore.SaveAsync(document.Snapshot, path);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "manifest.json")));
        Assert.Equal(expected, json.RootElement.GetProperty("guides")[0].GetProperty("axis").GetString());
    }

    [Theory]
    [InlineData("horizontal", "Horizontal")]
    [InlineData("vertical", "Vertical")]
    public async Task OpensTheExactLegacyWindowsSpellingAndNormalizesOnSave(string canonical, string legacy)
    {
        string path = await WriteFixtureAsync();
        string manifestPath = Path.Combine(path, "manifest.json");
        string original = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, original.Replace($"\"{canonical}\"", $"\"{legacy}\"", StringComparison.Ordinal));
        var snapshot = await ProjectStore.LoadAsync(path);
        AssertGuides(snapshot);
        await ProjectStore.SaveAsync(snapshot, path);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var guides = json.RootElement.GetProperty("guides");
        Assert.Equal("horizontal", guides[0].GetProperty("axis").GetString());
        Assert.Equal("vertical", guides[1].GetProperty("axis").GetString());
    }

    [Theory]
    [InlineData("\"HORIZONTAL\"")]
    [InlineData("\"Horizontal \"")]
    [InlineData("\"diagonal\"")]
    [InlineData("\"verticalExtra\"")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task InvalidGuideAxesReportAProjectError(string token)
    {
        string path = await WriteFixtureAsync();
        string manifestPath = Path.Combine(path, "manifest.json");
        string original = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, original.Replace("\"horizontal\"", token, StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectStore.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.Invalid, error.Kind);
    }

    [Fact]
    public async Task LegacyGuideSupportDoesNotMakeOtherEnumsCaseInsensitive()
    {
        string path = await WriteFixtureAsync();
        string manifestPath = Path.Combine(path, "manifest.json");
        string original = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, original.Replace("High quality", "high quality", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProjectFormatException>(() => ProjectStore.LoadAsync(path));
        Assert.Equal(ProjectErrorKind.Invalid, error.Kind);
    }

    private async Task<string> WriteFixtureAsync(int version = 8)
    {
        // Hand-authored against project-format.md and CanvasGuide.Axis in Guides.swift.
        // This is contract JSON, not a Windows serialization round trip or a Mac-generated package.
        using var stream = typeof(GuideProjectCompatibilityTests).Assembly.GetManifestResourceStream(
            "Compositor.Core.Tests.Fixtures.guides-v8-manifest.json")!;
        using var reader = new StreamReader(stream);
        string json = (await reader.ReadToEndAsync()).Replace("\"version\": 8",
            "\"version\": " + version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        string path = Path.Combine(root, "input.comp");
        string images = Path.Combine(path, "images");
        Directory.CreateDirectory(images);
        await File.WriteAllTextAsync(Path.Combine(path, "manifest.json"), json);
        await File.WriteAllBytesAsync(Path.Combine(images, "6F1D3C2A-0B7E-4E8A-9C4D-2A1B3C4D5E6F.png"), TinyPng.EncodeRgba(8, 8, FixturePixels()));
        return path;
    }

    private static byte[] FixturePixels()
    {
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)i;
            pixels[i + 1] = 128;
            pixels[i + 2] = 17;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static void AssertGuides(ProjectSnapshot snapshot)
    {
        Assert.Equal(new[]
        {
            new CanvasGuide { Id = Guid.Parse("FF741F57-F9DE-49FD-9322-A45E17E96C71"), Axis = GuideAxis.Horizontal, Position = 3.25 },
            new CanvasGuide { Id = Guid.Parse("36A276FD-7183-42A2-B213-58047D1F91E5"), Axis = GuideAxis.Vertical, Position = -4.5 },
        }, snapshot.Manifest.Guides);
    }
}
