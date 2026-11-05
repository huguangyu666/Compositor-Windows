using System.Net;
using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class ImageDownloadTests
{
    [Theory]
    [InlineData("http://example.com/image.png")]
    [InlineData("file:///image.png")]
    [InlineData("https://user:secret@example.com/image.png")]
    [InlineData("https://example.com:8443/image.png")]
    [InlineData("https://localhost/image.png")]
    [InlineData("https://127.0.0.1/image.png")]
    [InlineData("https://10.0.0.1/image.png")]
    [InlineData("https://[::ffff:127.0.0.1]/image.png")]
    [InlineData("/image.png")]
    public async Task UnsafeUrlsAreRejectedBeforeRequest(string url)
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => throw new Xunit.Sdk.XunitException("Network called")));
        await Assert.ThrowsAsync<AiServiceException>(() => ImageDownload.ReadAsync(http, url, 64, default));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2002:7f00:1::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void OnlyPublicAddressesAreEligibleForConnection(string address, bool expected) =>
        Assert.Equal(expected, ImageDownload.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData(302)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task DownloadFailuresAreNotRetriedOrExposed(int status)
    {
        int calls = 0;
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private response") };
            response.Headers.Location = new("http://127.0.0.1/secret");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<AiServiceException>(() => ImageDownload.ReadAsync(http,
            "https://cdn.example.test/image.png?signature=secret", 64, default));
        Assert.Equal(1, calls);
        Assert.Contains("could not be downloaded", error.Message);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("private response", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadLimitAppliesWithOrWithoutContentLength(bool knownLength)
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = knownLength ? new ByteArrayContent(new byte[65]) : new UnknownLengthContent(new byte[65]) })));
        await Assert.ThrowsAsync<AiServiceException>(() => ImageDownload.ReadAsync(http,
            "https://cdn.example.test/image.png", 64, default));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "DNS")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS")]
    [InlineData(HttpRequestError.ConnectionError, "connection")]
    [InlineData(HttpRequestError.ResponseEnded, "closed")]
    public void TransportClassificationUsesNoExceptionText(HttpRequestError kind, string expected)
    {
        var message = ImageDownload.FailureReason(new HttpRequestException(kind, "secret signed URL"));
        Assert.Contains(expected, message);
        Assert.DoesNotContain("secret", message);
    }

    [Fact]
    public void NestedAddressPolicyFailureKeepsItsSafeReason()
    {
        var error = new HttpRequestException("secret signed URL", new ImageDownload.ConnectionFailure("DNS policy blocked the host."));
        Assert.Equal("DNS policy blocked the host.", ImageDownload.FailureReason(error));
    }

    [Fact]
    public async Task TransportErrorsDoNotExposeSignedUrl()
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) =>
            throw new HttpRequestException("https://cdn.example.test/image.png?signature=secret")));
        var error = await Assert.ThrowsAsync<AiServiceException>(() => ImageDownload.ReadAsync(http,
            "https://cdn.example.test/image.png?signature=secret", 64, default));
        Assert.DoesNotContain("signature", error.ToString());
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task DownloadCancellationIsPreserved()
    {
        using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageDownload.ReadAsync(http,
            "https://cdn.example.test/image.png", 64, cancel.Token));
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
