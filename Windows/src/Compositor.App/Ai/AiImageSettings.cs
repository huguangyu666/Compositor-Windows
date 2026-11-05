using System.Text.Json;

namespace Compositor.App.Ai;

internal sealed record AiImageSettings(string Connection = "api", string Model = ImageModels.Sunburst)
{
    private static string Root(string? directory) => directory ?? Environment.GetEnvironmentVariable("COMPOSITOR_PREFERENCES_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compositor");

    public static AiImageSettings Load(string? directory = null)
    {
        try
        {
            var file = new FileInfo(Path.Combine(Root(directory), "ai-image.json"));
            if (!file.Exists || file.Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<AiImageSettings>(File.ReadAllText(file.FullName));
            if (settings is null || settings.Connection is not ("api" or "account")) return new();
            return settings.Model is ImageModels.Flare or ImageModels.Sunburst ? settings : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? directory = null)
    {
        string root = Root(directory), temporary = Path.Combine(root, "ai-image-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(root); File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, Path.Combine(root, "ai-image.json"), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
