using System.Security.Cryptography;

namespace Compositor.Core;

/// <summary>An open project's manifest plus its decoded layer images and masks.</summary>
public sealed class ProjectSnapshot
{
    public required ProjectManifest Manifest { get; init; }

    public required IReadOnlyDictionary<Guid, Surface> Images { get; init; }

    public required IReadOnlyDictionary<Guid, MaskSurface> Masks { get; init; }
}

/// <summary>
/// Reads and writes .comp packages: manifest.json plus an images directory of
/// &lt;layer UUID&gt;.png assets. Loading validates everything before handing a document over;
/// saving writes images first and then replaces the manifest in one atomic move, so an open
/// document never sees a half-written package.
/// </summary>
public static class ProjectStore
{
    public const string ManifestFileName = "manifest.json";
    public const string ImagesFolderName = "images";
    public const string ManifestTempFileName = ".manifest.json.tmp";

    public static bool IsProjectDirectory(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, ManifestFileName));

    public static async Task<ProjectSnapshot> LoadAsync(string path, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(path)) throw new ProjectFormatException(ProjectErrorKind.Invalid);

        string manifestPath = Path.Combine(path, ManifestFileName);
        var manifestBytes = await ReadBoundedFileAsync(manifestPath, Limits.ManifestMaxBytes, cancellation)
            ?? throw new ProjectFormatException(ProjectErrorKind.Invalid);

        // The header check first, so an unsupported version reports itself as a version problem.
        ProjectManifest manifest;
        try
        {
            manifest = ManifestJson.Deserialize(System.Text.Encoding.UTF8.GetString(manifestBytes));
        }
        catch (ProjectFormatException)
        {
            throw new ProjectFormatException(ProjectErrorKind.Invalid);
        }
        ProjectValidator.Validate(manifest);

        string imagesPath = Path.Combine(path, ImagesFolderName);
        if (!Directory.Exists(imagesPath)) throw new ProjectFormatException(ProjectErrorKind.MissingImage);

        var images = new Dictionary<Guid, Surface>();
        var masks = new Dictionary<Guid, MaskSurface>();
        long imagePixels = 0, maskPixels = 0;
        foreach (var layer in manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile)
            {
                var bytes = await ReadAssetAsync(imagesPath, imageFile, cancellation);
                if (!PngHeader.TryRead(bytes, out var header) || header.BitDepth > 8)
                    throw new ProjectFormatException(ProjectErrorKind.MissingImage);
                CheckSize(header.Width, header.Height, ref imagePixels);
                images[layer.Id] = await DecodeAsync(() => WicImaging.DecodeSurfaceAsync(bytes, cancellation));
            }
            if (layer.MaskFile is { } maskFile)
            {
                var bytes = await ReadAssetAsync(imagesPath, maskFile, cancellation);
                if (!PngHeader.TryRead(bytes, out var header)
                    || header.BitDepth != 8
                    || header.ColorType != PngHeader.ColorTypeGray)
                    throw new ProjectFormatException(ProjectErrorKind.Invalid);
                CheckSize(header.Width, header.Height, ref maskPixels);
                masks[layer.Id] = await DecodeAsync(() => WicImaging.DecodeMaskAsync(bytes, cancellation));
            }
        }
        return new ProjectSnapshot { Manifest = manifest, Images = images, Masks = masks };
    }

    /// <summary>
    /// Saves a project the way the file-format contract requires: PNGs land first, then the
    /// manifest is renamed over the old one — a reader sees the old project or the new one,
    /// never a mixture. Unreferenced PNGs are removed once the new manifest is in place.
    /// </summary>
    /// <param name="assetsCurrentOnDisk">File names whose bytes already on disk match their
    /// surfaces (EditorDocument.UnchangedAssetNames): they are skipped instead of
    /// re-encoded. Each asset is encoded and staged to a temporary file as it is visited, so
    /// the encoded bytes are never all held at once; the staged files move into place together.</param>
    /// <param name="expectedDiskDigest">The digest of the package as the caller last acknowledged it
    /// (EditorDocument.SavedDiskDigest). When the folder on disk no longer matches, the save is
    /// refused before anything is created or staged, so another writer's manifest survives.</param>
    public static async Task SaveAsync(ProjectSnapshot snapshot, string path, IReadOnlySet<string>? assetsCurrentOnDisk = null,
        CancellationToken cancellation = default, byte[]? expectedDiskDigest = null)
    {
        ProjectValidator.Validate(snapshot.Manifest);
        if (expectedDiskDigest is not null && Directory.Exists(path) && File.Exists(Path.Combine(path, ManifestFileName)))
        {
            var disk = ComputeDigest(path);
            if (!disk.AsSpan().SequenceEqual(expectedDiskDigest)) throw new ProjectConflictException(path, disk, expectedDiskDigest);
        }
        Directory.CreateDirectory(path);
        string imagesPath = Path.Combine(path, ImagesFolderName);
        Directory.CreateDirectory(imagesPath);

        long imagePixels = 0, maskPixels = 0;
        var staged = new List<(string Temporary, string Target)>();
        try
        {
            foreach (var layer in snapshot.Manifest.Layers)
            {
                if (layer.ImageFile is { } imageFile)
                {
                    if (!snapshot.Images.TryGetValue(layer.Id, out var image))
                        throw new ProjectFormatException(ProjectErrorKind.MissingImage);
                    CheckSize(image.Width, image.Height, ref imagePixels);
                    if (assetsCurrentOnDisk?.Contains(imageFile) != true)
                    {
                        await StageAssetAsync(imagesPath, imageFile,
                            await EncodeAsync(() => WicImaging.EncodePngAsync(image, cancellation)), staged, cancellation);
                    }
                }
                if (layer.MaskFile is { } maskFile)
                {
                    if (!snapshot.Masks.TryGetValue(layer.Id, out var mask))
                        throw new ProjectFormatException(ProjectErrorKind.MissingImage);
                    CheckSize(mask.Width, mask.Height, ref maskPixels);
                    if (assetsCurrentOnDisk?.Contains(maskFile) != true)
                    {
                        await StageAssetAsync(imagesPath, maskFile,
                            await EncodeAsync(() => WicImaging.EncodePngAsync(mask, cancellation)), staged, cancellation);
                    }
                }
            }

            byte[] manifestBytes = ManifestJson.SerializeToUtf8(snapshot.Manifest);
            if (manifestBytes.Length > Limits.ManifestMaxBytes) throw new ProjectFormatException(ProjectErrorKind.TooLarge);

            // Every staged PNG moves into place before the manifest names them: readers see
            // the old project or the new one, never a mixture.
            for (int i = staged.Count - 1; i >= 0; i--)
            {
                MoveAtomically(staged[i].Temporary, staged[i].Target);
            }
            staged.Clear();

            string manifestPath = Path.Combine(path, ManifestFileName);
            await WriteFileAtomicallyAsync(manifestPath, manifestBytes, cancellation);

            // Outdated previews and orphans go once the new manifest is in place.
            try
            {
                string quickLook = Path.Combine(path, "QuickLook");
                if (Directory.Exists(quickLook)) Directory.Delete(quickLook, recursive: true);
            }
            catch (IOException)
            {
                // A locked preview folder never blocks a save.
            }
            var referenced = new HashSet<string>();
            foreach (var layer in snapshot.Manifest.Layers)
            {
                if (layer.ImageFile is { } imageFile) referenced.Add(imageFile);
                if (layer.MaskFile is { } maskFile) referenced.Add(maskFile);
            }
            foreach (var file in Directory.EnumerateFiles(imagesPath, "*.png"))
            {
                if (!referenced.Contains(Path.GetFileName(file)))
                {
                    try { File.Delete(file); }
                    catch (IOException) { }
                }
            }
        }
        finally
        {
            foreach (var (temporary, _) in staged)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>Writes one encoded asset to a temporary file next to its target — the staged
    /// files all move into place together after every asset has been encoded, so the encoded
    /// bytes are never all held in memory at once.</summary>
    private static async Task StageAssetAsync(string imagesPath, string fileName, byte[] bytes, List<(string Temporary, string Target)> staged, CancellationToken cancellation)
    {
        string target = Path.Combine(imagesPath, fileName);
        string temporary = Path.Combine(imagesPath, $".{fileName}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temporary, bytes, cancellation);
        staged.Add((temporary, target));
    }

    /// <summary>Moves a staged temporary over its target atomically.</summary>
    private static void MoveAtomically(string temporary, string target)
    {
        if (File.Exists(target)) File.Replace(temporary, target, null);
        else File.Move(temporary, target);
    }

    /// <summary>
    /// A fingerprint of what a project package contains: the manifest byte for byte, and each
    /// asset's name and size. A package that was only touched has the same digest as before,
    /// so it is not treated as a change. Mirrors the macOS ProjectDigest.
    /// </summary>
    /// <summary>Whether a folder already holds a project package. The save-side conflict guard only has
    /// something to compare against when this is true, so callers that skipped their baseline check need it.</summary>
    public static bool HasPackage(string path) => File.Exists(Path.Combine(path, ManifestFileName));

    public static byte[] ComputeDigest(string path)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(File.ReadAllBytes(Path.Combine(path, ManifestFileName)));
        string imagesPath = Path.Combine(path, ImagesFolderName);
        var names = Directory.Exists(imagesPath)
            ? Directory.EnumerateFileSystemEntries(imagesPath).OrderBy(entry => entry, StringComparer.Ordinal).ToList()
            : new List<string>();
        foreach (var name in names)
        {
            var info = new FileInfo(name);
            if (info.Exists && !info.Attributes.HasFlag(FileAttributes.Directory) && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(name)));
                hasher.AppendData(BitConverter.GetBytes(unchecked((ulong)info.Length)));
            }
        }
        return hasher.GetHashAndReset();
    }

    private static async Task<byte[]> ReadAssetAsync(string imagesPath, string fileName, CancellationToken cancellation)
    {
        // Asset names are validated against "<UUID>.png" before this runs, so no traversal is possible.
        string file = Path.Combine(imagesPath, fileName);
        return await ReadBoundedFileAsync(file, Limits.AssetMaxBytes, cancellation)
            ?? throw new ProjectFormatException(ProjectErrorKind.MissingImage);
    }

    /// <summary>Reads a regular file inside the package, refusing directories, links and oversized files.</summary>
    private static async Task<byte[]?> ReadBoundedFileAsync(string file, long maximumBytes, CancellationToken cancellation)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return null;
        if (info.Length > maximumBytes) throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        using var stream = info.OpenRead();
        var bytes = new byte[info.Length];
        await stream.ReadExactlyAsync(bytes, cancellation);
        return bytes;
    }

    private static void CheckSize(int width, int height, ref long used)
    {
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide
            || (long)width * height > Limits.DocumentPixelBudget - used)
            throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        used += (long)width * height;
    }

    private static async Task WriteFileAtomicallyAsync(string target, byte[] bytes, CancellationToken cancellation)
    {
        // Write next to the target and move over it: readers see either the old file or the new one.
        string temporary = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellation);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static async Task<T> DecodeAsync<T>(Func<Task<T>> decode)
    {
        try
        {
            return await decode();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProjectFormatException(ProjectErrorKind.MissingImage, innerException: ex);
        }
    }

    private static async Task<byte[]> EncodeAsync(Func<Task<byte[]>> encode)
    {
        try
        {
            return await encode();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ProjectFormatException(ProjectErrorKind.Encode, innerException: ex);
        }
    }
}
