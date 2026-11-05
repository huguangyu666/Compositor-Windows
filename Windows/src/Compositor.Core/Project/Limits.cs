using System.Runtime.InteropServices;

namespace Compositor.Core;

/// <summary>
/// The size and memory ceilings a document is held to, in one place. Mirrors the macOS
/// DocumentLimits: the pixel budget is a quarter of physical memory at 4 bytes a pixel,
/// never less than one surface and never more than 800 megapixels.
/// </summary>
public static class Limits
{
    /// <summary>Longest side, in pixels, of any canvas, layer, mask or generated surface.</summary>
    public const int MaxSide = 30_000;

    /// <summary>Largest single surface: a canvas, an export, a filter target, an adjustment or mask render.</summary>
    public const long MaxSurfacePixels = 200_000_000;

    /// <summary>Largest manifest.json, in bytes.</summary>
    public const long ManifestMaxBytes = 4 * 1024 * 1024;

    /// <summary>Largest single encoded asset, in bytes.</summary>
    public const long AssetMaxBytes = 512 * 1024 * 1024;

    /// <summary>Most layers a document may hold.</summary>
    public const int MaxLayers = 10_000;

    /// <summary>
    /// Total imported raster one document may hold, summed across every layer and mask,
    /// in pixels: physical memory / 16 clamped to [MaxSurfacePixels, 800 MP].
    /// </summary>
    public static long DocumentPixelBudget { get; } = ComputeBudget();

    private static long ComputeBudget()
    {
        long physicalMemory = PhysicalMemoryBytes();
        long scaled = physicalMemory / 16; // pixels, not bytes: a quarter of memory at 4 bytes a pixel
        return Math.Min(800_000_000, Math.Max(MaxSurfacePixels, scaled));
    }

    private static long PhysicalMemoryBytes()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status)) return unchecked((long)status.ullTotalPhys);
        }
        catch
        {
            // Fall through to the conservative default.
        }
        return 16L * 1024 * 1024 * 1024; // an assumed 16 GB machine: 800 MP budget
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
