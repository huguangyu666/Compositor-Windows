using Compositor.Segmentation;
using System.Security.Cryptography;
using Xunit;

public sealed class ModelStoreTests
{
    private sealed class ModelDownload(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Fact]
    public async Task DownloadRepairsACorruptInstalledFileAndStillVerifiesReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), "Compositor-model-repair-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root); byte[] bytes = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(Path.Combine(root, "test.onnx"), [4, 3, 2, 1]);
            var file = new ModelFile("test.onnx", new Uri("https://example.test/model"), 4, Convert.ToHexString(SHA256.HashData(bytes)));
            using var client = new HttpClient(new ModelDownload(bytes));
            var store = new ModelStore(root, [file], client);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureAsync(false, null, default));
            await store.EnsureAsync(true, null, default);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "test.onnx")));
            using var corrupt = new HttpClient(new ModelDownload([4, 3, 2, 1]));
            string bad = Path.Combine(root, "bad");
            await Assert.ThrowsAsync<InvalidDataException>(() => new ModelStore(bad, [file], corrupt).EnsureAsync(true, null, default));
            Assert.False(File.Exists(Path.Combine(bad, "test.onnx")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImportsVerifiedOfflineFilesAndRefusesCorruptionWithoutPublishing()
    {
        string root = Path.Combine(Path.GetTempPath(), "Compositor-model-test-" + Guid.NewGuid());
        try
        {
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            byte[] bytes = [1, 2, 3, 4]; await File.WriteAllBytesAsync(Path.Combine(source, "test.onnx"), bytes);
            var file = new ModelFile("test.onnx", new Uri("https://example.test/model"), 4, Convert.ToHexString(SHA256.HashData(bytes)));
            var store = new ModelStore(Path.Combine(root, "models"), [file]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.EnsureAsync(false, null, default));
            await store.ImportAsync(source, null, default);
            await store.EnsureAsync(false, null, default);
            Assert.True(store.IsInstalled);
            await File.WriteAllBytesAsync(Path.Combine(source, "test.onnx"), [4, 3, 2, 1]);
            var bad = new ModelStore(Path.Combine(root, "bad"), [file]);
            await Assert.ThrowsAsync<InvalidDataException>(() => bad.ImportAsync(source, null, default));
            Assert.False(File.Exists(Path.Combine(root, "bad", "test.onnx")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
