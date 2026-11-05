namespace Compositor.App.Ai;

internal static class ImageModels
{
    public const string Sunburst = "gpt-image-2.5-sunburst";
    public const string Flare = "gpt-image-2.5-flare";
}

// Callers capture settled input pixels before starting a request. These are encoded files,
// not live Surface buffers; the returned PNG must be decoded before adding an editor layer.
internal sealed record ImageInput(byte[] Bytes, string MediaType = "image/png");
internal sealed record ImageReference(ImageInput Image, string Role = "general");
internal sealed record AiReferenceFile(string Path, string Role = "general");

internal sealed record ImageGenerationRequest(string Prompt, string Model = ImageModels.Sunburst)
{
    public ImageInput? Image { get; init; }
    public ImageInput? Mask { get; init; }
    public IReadOnlyList<ImageReference> References { get; init; } = [];
    public string Preserve { get; init; } = "";
    public string Size { get; init; } = "auto";
    public string Quality { get; init; } = "auto";
    public string Background { get; init; } = "auto";
}

internal sealed record ImageGenerationResult(byte[] Png, string Model, string? RevisedPrompt);

internal sealed class ChatGptTokens(string accessToken, string refreshToken, string accountId, DateTimeOffset expiresAt)
{
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public string AccountId { get; } = accountId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
    public override string ToString() => "ChatGPT credentials (redacted)";
}
