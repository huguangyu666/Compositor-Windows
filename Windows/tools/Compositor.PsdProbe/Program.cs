using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Compositor.Core.Psd;

// Each measurement runs in a fresh process. OS peak working set includes mapped pages;
// managed allocation alone would hide those pages and cannot prove an import's peak.
if (args.Length != 2) throw new ArgumentException("Usage: Compositor.PsdProbe create|mapped|copy|full <fixture.psd>");
string mode = args[0], path = Path.GetFullPath(args[1]);
if (mode == "create") { Create(path); return; }
if (mode is not ("mapped" or "copy" or "full")) throw new ArgumentException("Unknown probe mode.");
// Warm shared parse/JIT paths with a tiny independent composite; no large file pages.
using (var warm = new MemoryStream())
{
    Header(warm, 1, 1); U32(warm, 0); U32(warm, 0); U16(warm, 0); warm.Write([1, 2, 3]);
    PsdReader.Read(warm.ToArray(), parallel: false);
}
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
using var process = Process.GetCurrentProcess(); process.Refresh();
long baselineWorkingSet = process.WorkingSet64, baselinePrivate = process.PrivateMemorySize64;
long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
var timer = Stopwatch.StartNew();
long budget = mode == "full" ? 32_000_000 : 2048;
var document = mode == "copy" ? PsdReader.Read(File.ReadAllBytes(path), false, budget) : PsdReader.ReadFile(path, false, budget);
timer.Stop();
if (document.Layers.Count != 2) throw new InvalidDataException("Expected two layers.");
using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
for (int layer = 0; layer < 2; layer++)
{
    var pixels = document.Layers[layer].Pixels!;
    int side = mode == "full" ? 4000 : 32;
    if (pixels.Width != side || pixels.Height != side) throw new InvalidDataException("Wrong crop size.");
    for (int row = 0; row < 32; row++)
    {
        var span = pixels.Pixels.AsSpan(row * pixels.Stride, 32 * 4);
        for (int x = 0; x < 32; x++)
            if (span[x * 4] != layer + 11 || span[x * 4 + 1] != layer + 31 || span[x * 4 + 2] != layer + 51 || span[x * 4 + 3] != 255)
                throw new InvalidDataException("Imported pixels differ from the written RGB fixture.");
        hash.AppendData(span);
    }
}
process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new
{
    mode, fileBytes = new FileInfo(path).Length, budget, elapsedMs = timer.Elapsed.TotalMilliseconds,
    allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
    baselineWorkingSet, baselinePrivate, workingSetBytes = process.WorkingSet64,
    privateBytes = process.PrivateMemorySize64, peakWorkingSetBytes = process.PeakWorkingSet64,
    cropSha256 = Convert.ToHexString(hash.GetHashAndReset()),
    decodedPixels = document.Layers.Sum(layer => (long)layer.Pixels!.Width * layer.Pixels.Height),
    status = "passed"
}));
GC.KeepAlive(document);

static void Create(string path)
{
    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    if (!Native.DeviceIoControl(file.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0)) throw new IOException("Probe requires sparse-file support.");
    const int plane = 4000 * 4000;
    Header(file, 32, 32); U32(file, 32 * 1024 * 1024); file.Seek(32 * 1024 * 1024, SeekOrigin.Current);
    using var records = new MemoryStream();
    for (int layer = 0; layer < 2; layer++)
    {
        U32(records, 0); U32(records, 0); U32(records, 4000); U32(records, 4000); U16(records, 3);
        for (int channel = 0; channel < 3; channel++) { U16(records, channel); U32(records, plane + 2); }
        records.Write("8BIMnorm"u8); records.Write([255, 0, 0, 0]); U32(records, 12);
        records.Write(new byte[8]); records.Write([1, (byte)('A' + layer), 0, 0]);
    }
    uint info = (uint)(2 + records.Length + 6L * (plane + 2)); U32(file, info + 4); U32(file, info); U16(file, 2); file.Write(records.ToArray());
    for (int layer = 0; layer < 2; layer++) for (int channel = 0; channel < 3; channel++)
    {
        U16(file, 0); long start = file.Position;
        byte[] row = new byte[32]; Array.Fill(row, (byte)(layer + 11 + channel * 20));
        for (int y = 0; y < 32; y++) { file.Position = start + y * 4000; file.Write(row); }
        file.Position = start + plane;
    }
    file.SetLength(file.Position);
    Console.WriteLine(JsonSerializer.Serialize(new { file = path, fileBytes = file.Length, status = "created" }));
}
static void Header(Stream file, int width, int height)
{ file.Write("8BPS"u8); U16(file, 1); file.Write(new byte[6]); U16(file, 3); U32(file, (uint)height); U32(file, (uint)width); U16(file, 8); U16(file, 3); U32(file, 0); }
static void U16(Stream file, int value) => file.Write([(byte)(value >> 8), (byte)value]);
static void U32(Stream file, uint value) => file.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
