using System.Net;
using System.Text;
using System.Text.Json;
using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class ImageGenerationTests
{
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");

    internal static HttpResponseMessage ImageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = new[] { new { b64_json = Convert.ToBase64String(Png) } }, output_format = "png"
        }), Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task UrlResponseDownloadsPngWithoutApiCredentials()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"data\":[{\"url\":\"https://cdn.example.test/image.png?signature=test\",\"revised_prompt\":\"a boat\"}]}") });
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Chatgpt-Account-Id"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        }));
        var result = await new ImageGenerationClient(http, "api-test-key", imageHttp: http)
            .GenerateAsync(new("prompt"));
        Assert.Equal(Png, result.Png);
        Assert.Equal("a boat", result.RevisedPrompt);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlResponseUsesIndependentClientForGenerationAndEditing(bool editing)
    {
        using var api = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.NotNull(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"data\":[{\"b64_json\":null,\"url\":\"https://cdn.example.test/image.png\"}]}") });
        }));
        using var download = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Chatgpt-Account-Id"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) });
        }));
        var result = await new ImageGenerationClient(api, "api-test-key", imageHttp: download)
            .GenerateAsync(new("prompt") { Image = editing ? new(Png) : null });
        Assert.Equal(Png, result.Png);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Base64TakesPrecedenceAndInvalidBase64DoesNotTriggerDownload(bool valid)
    {
        using var api = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new
            { b64_json = valid ? Convert.ToBase64String(Png) : "invalid!", url = "https://cdn.example.test/image.png" } } })) })));
        using var download = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Download called")));
        var client = new ImageGenerationClient(api, "api-test-key", imageHttp: download);
        if (valid) Assert.Equal(Png, (await client.GenerateAsync(new("prompt"))).Png);
        else await Assert.ThrowsAsync<AiServiceException>(() => client.GenerateAsync(new("prompt")));
    }

    [Fact]
    public async Task DownloadedNonPngCannotBecomeSuccessfulResult()
    {
        using var api = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"data\":[{\"url\":\"https://cdn.example.test/image.png\"}]}") })));
        using var download = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("not a PNG") })));
        var error = await Assert.ThrowsAsync<AiServiceException>(() =>
            new ImageGenerationClient(api, "api-test-key", imageHttp: download).GenerateAsync(new("prompt")));
        Assert.Contains("PNG", error.Message);
    }

    [Fact]
    public async Task ApiGenerationPreservesModelAndUsesOnlyApiCredentials()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://api.openai.com/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer api-test-key", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("Chatgpt-Account-Id"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("gpt-image-2.5-flare", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("a blue boat", json.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("n").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("response_format", out _));
            return ImageResponse();
        }));
        var result = await new ImageGenerationClient(http, "api-test-key")
            .GenerateAsync(new("a blue boat", "gpt-image-2.5-flare"));
        Assert.Equal(Png, result.Png);
        Assert.Equal("gpt-image-2.5-flare", result.Model);
    }

    [Fact]
    public async Task CustomApiGenerationRequestsBase64ToAvoidExternalDownload()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("b64_json", json.RootElement.GetProperty("response_format").GetString());
            return ImageResponse();
        }));
        var result = await new ImageGenerationClient(http, "api-test-key", new("https://images.example.test/v1/"))
            .GenerateAsync(new("prompt"));
        Assert.Equal(Png, result.Png);
    }

    [Fact]
    public async Task AccountEditingSendsAnInputAndMaskOnTheSameJsonContract()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.EndsWith("/images/edits", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(json.RootElement.TryGetProperty("response_format", out _));
            var input = json.RootElement.GetProperty("images")[0].GetProperty("image_url").GetString();
            Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), input);
            Assert.Equal(input, json.RootElement.GetProperty("mask").GetProperty("image_url").GetString());
            return ImageResponse();
        }));
        var result = await new ImageGenerationClient(http, _ => Task.FromResult(
            new ChatGptTokens("account-test-token", "refresh-test-token", "account-test-id", DateTimeOffset.UtcNow.AddHours(1))))
            .GenerateAsync(new("change the sky")
        { Image = new(Png), Mask = new(Png) });
        Assert.Equal(Png, result.Png);
    }

    [Fact]
    public async Task ApiEditingUploadsMultipartImageAndMaskToTheSelectedEndpoint()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("https://images.example.test/v1/images/edits", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer api-test-key", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("Chatgpt-Account-Id"));
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToDictionary(part => part.Headers.ContentDisposition!.Name!.Trim('"'));
            Assert.Equal("gpt-image-2.5-flare", await parts["model"].ReadAsStringAsync(ct));
            Assert.Equal("change the sky", await parts["prompt"].ReadAsStringAsync(ct));
            Assert.Equal("1", await parts["n"].ReadAsStringAsync(ct));
            Assert.Equal("png", await parts["output_format"].ReadAsStringAsync(ct));
            Assert.Equal("b64_json", await parts["response_format"].ReadAsStringAsync(ct));
            foreach (string name in new[] { "image[]", "mask" })
            {
                Assert.Equal("image/png", parts[name].Headers.ContentType!.MediaType);
                Assert.False(string.IsNullOrEmpty(parts[name].Headers.ContentDisposition!.FileName));
                Assert.Equal(Png, await parts[name].ReadAsByteArrayAsync(ct));
            }
            return ImageResponse();
        }));
        var result = await new ImageGenerationClient(http, "api-test-key", new("https://images.example.test/v1"))
            .GenerateAsync(new("change the sky", ImageModels.Flare) { Image = new(Png), Mask = new(Png) });
        Assert.Equal(Png, result.Png);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task FailuresAreNotRetriedAndDoNotExposeUpstreamSecrets(int status)
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("echoed-access-token and private prompt") });
        }));
        var error = await Assert.ThrowsAsync<AiServiceException>(() =>
            new ImageGenerationClient(http, "api-test-key").GenerateAsync(new("private prompt")));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("echoed-access-token", error.ToString());
        Assert.DoesNotContain("private prompt", error.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"not-base64!\"}]}")]
    [InlineData("{\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}")]
    [InlineData("not json")]
    public async Task MalformedOrUnsupportedImagesCannotBecomeSuccessfulResults(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body) })));
        await Assert.ThrowsAsync<AiServiceException>(() =>
            new ImageGenerationClient(http, "api-test-key").GenerateAsync(new("prompt")));
    }

    [Fact]
    public async Task BodyLimitAlsoAppliesWithoutContentLength()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new UnknownLengthContent(new byte[65]) })));
        await Assert.ThrowsAsync<AiServiceException>(() =>
            new ImageGenerationClient(http, "api-test-key", responseByteLimit: 64).GenerateAsync(new("prompt")));
    }

    [Fact]
    public async Task CancellationReachesTheNetworkWithoutRetry()
    {
        int calls = 0;
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            calls++;
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return ImageResponse();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageGenerationClient(http, "api-test-key").GenerateAsync(new("prompt"), cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AMaskWithoutAnInputIsRefusedBeforeAnyRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Network called")));
        await Assert.ThrowsAsync<ArgumentException>(() => new ImageGenerationClient(http, "api-test-key")
            .GenerateAsync(new("prompt") { Mask = new(Png) }));
    }

    [Fact]
    public void CredentialsCannotInsertNewHttpHeaders()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Network called")));
        var error = Assert.Throws<ArgumentException>(() => new ImageGenerationClient(http, "secret\r\nInjected: value"));
        Assert.DoesNotContain("secret", error.ToString());
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
