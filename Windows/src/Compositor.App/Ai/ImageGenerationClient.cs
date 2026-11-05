// Open-source attribution: account image routing and edit JSON adapted from router-for-me/CLIProxyAPI,
// internal/runtime/executor/codex_openai_images.go and codex_executor_request.go at
// 1f917e77c4562623aa49e9804d999524151a2f96. MIT; see Windows/third-party/CLIProxyAPI-LICENSE.txt.
// https://github.com/router-for-me/CLIProxyAPI -- native C# HTTP adapter without the proxy server.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Compositor.App.Ai;

internal sealed class ImageGenerationClient
{
    private readonly HttpClient http;
    private readonly string? apiKey;
    private readonly Func<CancellationToken, Task<ChatGptTokens>>? accountTokens;
    private readonly Uri baseUri;
    private readonly int responseByteLimit;
    private const int MaxInputBytes = 14 * 1024 * 1024;
    private const int MaxOutputBytes = 64 * 1024 * 1024;

    public ImageGenerationClient(HttpClient http, string apiKey, Uri? apiBase = null,
        int responseByteLimit = 96 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!AiHttp.IsCredentialValue(apiKey)) throw new ArgumentException("The API key contains invalid characters.", nameof(apiKey));
        this.http = http;
        this.apiKey = apiKey;
        baseUri = NormalizeApiBase(apiBase ?? new("https://api.openai.com/v1/"));
        this.responseByteLimit = CheckLimit(responseByteLimit);
    }

    // Pass ChatGptAccount.GetTokensAsync. There is no API-key fallback in this adapter.
    public ImageGenerationClient(HttpClient http, Func<CancellationToken, Task<ChatGptTokens>> accountTokens,
        int responseByteLimit = 96 * 1024 * 1024)
    {
        this.http = http;
        this.accountTokens = accountTokens;
        baseUri = new("https://chatgpt.com/backend-api/codex/");
        this.responseByteLimit = CheckLimit(responseByteLimit);
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest input, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Model);
        if (input.Prompt.Length > 32000) throw new ArgumentException("The prompt exceeds 32000 characters.");
        if (input.Mask is not null && input.Image is null) throw new ArgumentException("An edit mask requires an input image.");
        AiReferenceInputs.Validate(input);
        string prompt = AiReferenceInputs.Prompt(input);
        if (prompt.Length > 32000) throw new ArgumentException("Shorten the prompt and reference instructions to 32000 characters.");
        cancellation.ThrowIfCancellationRequested();
        var body = new Dictionary<string, object>
        {
            ["model"] = input.Model, ["prompt"] = prompt, ["n"] = 1,
            ["size"] = input.Size, ["quality"] = input.Quality, ["background"] = input.Background,
            ["output_format"] = "png"
        };
        if (input.Mask is not null && input.Mask.MediaType != "image/png") throw new ArgumentException("An edit mask must be a PNG.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(baseUri, input.Image is null ? "images/generations" : "images/edits"));
        if (input.Image is not null && accountTokens is null)
        {
            ValidateImage(input.Image);
            if (input.Mask is not null) ValidateImage(input.Mask);
            var form = new MultipartFormDataContent();
            request.Content = form;
            foreach (var field in body)
                form.Add(new StringContent(Convert.ToString(field.Value, System.Globalization.CultureInfo.InvariantCulture)!), field.Key);
            void AddImage(ImageInput image, string name, string filename)
            {
                var content = new ByteArrayContent(image.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(image.MediaType);
                form.Add(content, name, filename);
            }
            AddImage(input.Image, "image[]", "image" + (input.Image.MediaType == "image/jpeg" ? ".jpg" : input.Image.MediaType == "image/webp" ? ".webp" : ".png"));
            int referenceIndex = 0;
            foreach (var reference in input.References) AddImage(reference.Image, "image[]", "reference-" + ++referenceIndex + (reference.Image.MediaType == "image/jpeg" ? ".jpg" : reference.Image.MediaType == "image/webp" ? ".webp" : ".png"));
            if (input.Mask is not null) AddImage(input.Mask, "mask", "mask.png");
        }
        else
        {
            if (input.Image is not null) body["images"] = new[] { input.Image }.Concat(input.References.Select(reference => reference.Image)).Select(image => new { image_url = DataUrl(image) }).ToArray();
            if (input.Mask is not null) body["mask"] = new { image_url = DataUrl(input.Mask) };
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Accept.Add(new("application/json"));
        if (accountTokens is not null)
        {
            var tokens = await accountTokens(cancellation).ConfigureAwait(false);
            if (!AiHttp.IsCredentialValue(tokens.AccessToken) || !AiHttp.IsCredentialValue(tokens.AccountId))
                throw new AiServiceException("The selected ChatGPT account contains invalid credentials. Sign in again.");
            request.Headers.Authorization = new("Bearer", tokens.AccessToken);
            request.Headers.Add("Chatgpt-Account-Id", tokens.AccountId);
            request.Headers.Add("Originator", "codex-tui");
            // Keep the upstream compatibility identity explicit and confined to the experimental account route.
            request.Headers.TryAddWithoutValidation("User-Agent",
                "codex-tui/0.154.0 (Mac OS 26.5.2; arm64) iTerm.app/3.6.11 (codex-tui; 0.154.0)");
            request.Headers.TryAddWithoutValidation("Session_id", Guid.NewGuid().ToString());
        }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var json = await AiHttp.SendAsync(http, request, responseByteLimit, cancellation).ConfigureAwait(false);
        return ReadImage(json.RootElement, input.Model);
    }

    private static ImageGenerationResult ReadImage(JsonElement root, string model)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var items) ||
            items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1)
            throw new AiServiceException("The AI service did not return exactly one image.");
        var image = items[0];
        if (image.ValueKind != JsonValueKind.Object || !image.TryGetProperty("b64_json", out var b64) ||
            b64.ValueKind != JsonValueKind.String || b64.GetString() is not { Length: > 0 } text ||
            text.Length > (MaxOutputBytes + 2L) / 3 * 4)
            throw new AiServiceException("The AI service did not return a supported image payload.");
        byte[] png;
        try { png = Convert.FromBase64String(text); }
        catch (FormatException) { throw new AiServiceException("The AI service returned invalid image data."); }
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 8 || png.Length > MaxOutputBytes || !png.AsSpan(0, 8).SequenceEqual(signature))
            throw new AiServiceException("The AI service did not return the requested PNG format.");
        string? revised = image.TryGetProperty("revised_prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String
            ? prompt.GetString() : null;
        return new(png, model, revised);
    }

    private static string DataUrl(ImageInput input)
    {
        ValidateImage(input);
        return $"data:{input.MediaType};base64,{Convert.ToBase64String(input.Bytes)}";
    }

    private static void ValidateImage(ImageInput input)
    {
        if (input.Bytes.Length == 0 || input.Bytes.Length > MaxInputBytes)
            throw new ArgumentException("Each input image must contain 1 to 14 MiB of encoded data.");
        if (input.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ArgumentException("Input images must use PNG, JPEG or WebP.");
    }

    private static int CheckLimit(int limit) => limit is > 0 and <= 96 * 1024 * 1024
        ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    internal static Uri NormalizeApiBase(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps ||
            address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("The API base must be an absolute HTTPS URL without credentials, query or fragment.");
        return new(address.AbsoluteUri.TrimEnd('/') + "/");
    }
}
