using System.Text.Json;

namespace Compositor.App.Ai;

// Store the API address with the key in the same encrypted value. Reopening a custom connection
// must not silently reuse its credential at the default service or another edited address.
internal sealed class AiApiConnection
{
    public static Uri DefaultBase { get; } = new("https://api.openai.com/v1/");
    public Uri ApiBase { get; }
    private readonly string key;

    public AiApiConnection(string key, Uri apiBase)
    {
        if (!AiHttp.IsCredentialValue(key)) throw new ArgumentException("The API key contains invalid characters.");
        this.key = key;
        ApiBase = ImageGenerationClient.NormalizeApiBase(apiBase);
    }

    public string KeyFor(Uri apiBase)
    {
        if (ImageGenerationClient.NormalizeApiBase(apiBase) != ApiBase)
            throw new AiServiceException("The saved API key belongs to another API address. Enter and save a key for this address.");
        return key;
    }

    public void Save(IAiCredentialStore store) =>
        store.Write("api-key", JsonSerializer.Serialize(new SavedConnection(1, ApiBase.AbsoluteUri, key)));

    public static AiApiConnection? Load(IAiCredentialStore store)
    {
        string? saved = store.Read("api-key");
        if (saved is null) return null;
        try
        {
            // Older builds saved a raw key and only ever reopened the official address.
            if (!saved.StartsWith('{')) return new(saved, DefaultBase);
            var data = JsonSerializer.Deserialize<SavedConnection>(saved);
            if (data is null || data.Version != 1 || string.IsNullOrEmpty(data.ApiKey) || string.IsNullOrEmpty(data.ApiBase))
                throw new ArgumentException("Invalid saved connection.");
            return new(data.ApiKey, new Uri(data.ApiBase, UriKind.Absolute));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new AiServiceException("The saved API connection is invalid. Enter and save the API key again.");
        }
    }

    public override string ToString() => "API connection (credentials redacted)";
    private sealed record SavedConnection(int Version, string ApiBase, string ApiKey);
}
