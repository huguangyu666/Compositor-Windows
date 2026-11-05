using Compositor.App.Ai;
using Xunit;

namespace Compositor.App.Tests;

public sealed class AiReferenceTests
{
    [Fact]
    public async Task ApiPreservesImageOrderMaskAndReferenceRoles()
    {
        using var http = new HttpClient(new ImageGenerationTests.Handler(async (request, ct) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var images = form.Where(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "image[]").ToArray();
            Assert.Equal(3, images.Length); Assert.Single(form, p => p.Headers.ContentDisposition?.Name?.Trim('"') == "mask");
            string prompt = await form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "prompt").ReadAsStringAsync(ct);
            Assert.Contains("Image 2", prompt); Assert.Contains("identity", prompt); Assert.Contains("clothing", prompt); Assert.Contains("same face", prompt);
            return ImageGenerationTests.ImageResponse();
        }));
        await new ImageGenerationClient(http, "test-key").GenerateAsync(new("change the scene")
        { Image = new(ImageGenerationTests.Png), Mask = new(ImageGenerationTests.Png), Preserve = "same face",
            References = [new(new(ImageGenerationTests.Png), "identity"), new(new(ImageGenerationTests.Png), "clothing")] });
    }

    [Fact]
    public async Task InvalidReferencesFailBeforeAnyUpload()
    {
        int calls = 0;
        using var http = new HttpClient(new ImageGenerationTests.Handler((_, _) => { calls++; return Task.FromResult(ImageGenerationTests.ImageResponse()); }));
        await Assert.ThrowsAsync<ArgumentException>(() => new ImageGenerationClient(http, "test-key").GenerateAsync(new("edit")
        { Image = new(ImageGenerationTests.Png), Size = "1K", References = [new(new(ImageGenerationTests.Png), "invalid")] }));
        Assert.Equal(0, calls);
    }

}
