using System.Security.Cryptography;

namespace Compositor.Segmentation;

public sealed record ModelFile(string Name, Uri Url, long Bytes, string Sha256);

/// <summary>Optional local weights, pinned to a revision and verified before atomic publication.</summary>
public sealed class ModelStore(string directory, IReadOnlyList<ModelFile> files, HttpClient? downloadClient = null)
{
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(10) };
    public static readonly ModelFile[] MobileSamFiles =
    [
        new("mobile_sam_image_encoder.onnx", new("https://huggingface.co/Acly/MobileSAM/resolve/0d3b403339b4674a82493d5e97964dd78089ddc8/mobile_sam_image_encoder.onnx"),
            28157093, "580f5fb648ea1062c0aabc26217aed56921985f03f0cbbd852bba81d760cc749"),
        new("sam_mask_decoder_multi.onnx", new("https://huggingface.co/Acly/MobileSAM/resolve/0d3b403339b4674a82493d5e97964dd78089ddc8/sam_mask_decoder_multi.onnx"),
            16496559, "8976b90a87ba50a6a72217a5ff994f7d25ce16f2229fcc1ed259e1294c622ffe"),
    ];
    public static readonly ModelFile[] Sam2Files =
    [
        new("vision_encoder.onnx", Sam2Url("vision_encoder.onnx"), 354238,
            "4f30aacd3aaefbca81a0b7fe4c1fc96345570ea0a6f80ced599493d1b3be2e8c"),
        new("vision_encoder.onnx_data", Sam2Url("vision_encoder.onnx_data"), 134084864,
            "e83df9866a5afe68ea7f0f721f18f65137fc3acbf0da1c74e946d363e09c69cc"),
        new("prompt_encoder_mask_decoder.onnx", Sam2Url("prompt_encoder_mask_decoder.onnx"), 213114,
            "874414704c5d686db7d206a35f6e15d26563d50c8c4468fccc6739bd7e491dcf"),
        new("prompt_encoder_mask_decoder.onnx_data", Sam2Url("prompt_encoder_mask_decoder.onnx_data"), 20958208,
            "e9874d900dd4134ed60eab1e97910327c2419e0b2954485d8fd6e7f1a1470f47"),
    ];
    private static Uri Sam2Url(string name) => new("https://huggingface.co/onnx-community/sam2.1-hiera-tiny-ONNX/resolve/814a066640debee5a91e70aa401fb8e17e030503/onnx/" + name);
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, DateTime> verified = [];
    public string DirectoryPath => directory;
    public bool IsSam2 => files.Any(file => file.Name == Sam2Files[0].Name);
    public bool IsInstalled => files.All(file => File.Exists(Path.Combine(directory, file.Name)) && new FileInfo(Path.Combine(directory, file.Name)).Length == file.Bytes);

    public async Task EnsureAsync(bool download, IProgress<double>? progress, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i]; string path = Path.Combine(directory, file.Name);
                if (File.Exists(path) && new FileInfo(path).Length == file.Bytes)
                {
                    try
                    {
                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (!verified.TryGetValue(path, out var old) || old != stamp)
                        { await VerifyAsync(path, file, cancellation); verified[path] = stamp; }
                        continue;
                    }
                    catch (InvalidDataException) when (download) { verified.Remove(path); }
                }
                if (!download) throw new InvalidOperationException("Install the local Object Selection model in the editor first (Download model or Import model folder). No image is uploaded.");
                Directory.CreateDirectory(directory);
                using var response = await (downloadClient ?? client).GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await ReceiveAsync(input, file, value => progress?.Report((i + value) / files.Count), cancellation);
            }
            progress?.Report(1);
        }
        finally { gate.Release(); }
    }

    public async Task ImportAsync(string source, IProgress<double>? progress, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            Directory.CreateDirectory(directory);
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                await using var input = File.OpenRead(Path.Combine(source, file.Name));
                await ReceiveAsync(input, file, value => progress?.Report((i + value) / files.Count), cancellation);
            }
        }
        finally { gate.Release(); }
    }

    private async Task ReceiveAsync(Stream input, ModelFile file, Action<double> progress, CancellationToken cancellation)
    {
        string target = Path.Combine(directory, file.Name), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; long count = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    count += read;
                    if (count > file.Bytes) throw new InvalidDataException("The model size does not match the pinned release.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation); progress(count / (double)file.Bytes);
                }
                if (count != file.Bytes) throw new InvalidDataException("The model download is incomplete.");
            }
            await VerifyAsync(temporary, file, cancellation);
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, target, true);
            verified[target] = File.GetLastWriteTimeUtc(target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task VerifyAsync(string path, ModelFile file, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        if (input.Length != file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local model failed SHA-256 verification. Reinstall the pinned model files.");
    }
}
