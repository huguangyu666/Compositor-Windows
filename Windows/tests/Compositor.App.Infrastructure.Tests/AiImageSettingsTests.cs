using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class AiImageSettingsTests
{
    [Theory]
    [InlineData("api")]
    [InlineData("account")]
    public void SelectedConnectionAndModelSurviveReopen(string connection)
    {
        string directory = Path.Combine(Path.GetTempPath(), "compositor-ai-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AiImageSettings(connection, ImageModels.Flare);
            settings.Save(directory);
            Assert.Equal(settings, AiImageSettings.Load(directory));
            Assert.DoesNotContain("key", File.ReadAllText(Path.Combine(directory, "ai-image.json")), StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RetiredProviderSettingsFallBackToApiAndPreserveCustomApiCredentials()
    {
        string directory = Path.Combine(Path.GetTempPath(), "compositor-ai-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ai-image.json"), "{\"Connection\":\"kie\",\"Model\":\"nano-banana-2\",\"Resolution\":\"4K\"}");
            var store = new AiCredentialStore(directory);
            var connection = new AiApiConnection("retained-api-test-key", new Uri("https://images.example.test/v1/"));
            connection.Save(store);
            var settings = AiImageSettings.Load(directory);
            Assert.Equal("api", settings.Connection);
            Assert.Equal(ImageModels.Sunburst, settings.Model);
            var reopened = AiApiConnection.Load(store);
            Assert.NotNull(reopened);
            Assert.Equal(connection.ApiBase, reopened.ApiBase);
            Assert.Equal("retained-api-test-key", reopened.KeyFor(connection.ApiBase));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
