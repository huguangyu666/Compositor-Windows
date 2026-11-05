using System.Net;
using System.Text.Json;

namespace Compositor.App.Ai;

internal sealed class AiServiceException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

internal static class AiHttp
{
    public static bool IsCredentialValue(string? value) => !string.IsNullOrEmpty(value) &&
        !value.Any(character => character <= ' ' || character > '~');

    // One client can serve both adapters; credentials are set on each request, never DefaultRequestHeaders.
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = TimeSpan.FromMinutes(5) };

    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, int byteLimit,
        CancellationToken cancellation)
    {
        byte[] bytes = await ReadBytesAsync(http, request, byteLimit, cancellation).ConfigureAwait(false);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new AiServiceException("The AI service returned invalid JSON."); }
    }

    public static async Task<byte[]> ReadBytesAsync(HttpClient http, HttpRequestMessage request, int byteLimit,
        CancellationToken cancellation)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        // Do not include response bodies: providers can echo prompts, image data and credentials.
        if (!response.IsSuccessStatusCode)
            throw new AiServiceException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "The selected AI connection needs valid credentials or a new sign-in (401).",
                HttpStatusCode.Forbidden => "The selected AI connection does not have access to this operation (403).",
                HttpStatusCode.TooManyRequests => "The selected AI connection has reached its quota or rate limit (429).",
                _ => $"The selected AI connection returned HTTP {(int)response.StatusCode}."
            }, response.StatusCode);
        if (response.Content.Headers.ContentLength > byteLimit)
            throw new AiServiceException("The AI response exceeds the local size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[Math.Min(81920, byteLimit + 1)];
        while (true)
        {
            int count = await source.ReadAsync(chunk, cancellation).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > byteLimit)
                throw new AiServiceException("The AI response exceeds the local size limit.");
            buffer.Write(chunk, 0, count);
        }
        cancellation.ThrowIfCancellationRequested();
        return buffer.ToArray();
    }
}
