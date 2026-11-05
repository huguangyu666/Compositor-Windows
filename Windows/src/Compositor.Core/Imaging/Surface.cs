namespace Compositor.Core;

/// <summary>
/// An RGBA8 bitmap, premultiplied alpha, row-major with a 4-byte-aligned stride. This is the
/// pixel currency of the whole Windows port: decoders produce it, the compositor consumes and
/// produces it, encoders write it back out.
/// </summary>
public sealed class Surface
{
    public Surface(int width, int height)
    {
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), "Surface dimensions exceed the supported limits.");
        if ((long)width * height > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Surface exceeds the pixel budget.");
        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[checked((long)Stride * height)];
    }

    public Surface(int width, int height, byte[] pixels, int stride)
    {
        if (pixels.Length < (long)stride * height) throw new ArgumentException("Pixel buffer is too small.");
        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row. Rows are 4-byte aligned, so this can exceed Width * 4.</summary>
    public int Stride { get; }

    /// <summary>Premultiplied RGBA, one byte per channel, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>
    /// Bumped by whoever mutates the pixels in place (brush strokes). Render caches fold it
    /// into their validity fingerprints — the macOS equivalent is EditorSession.brushRevision.
    /// </summary>
    public int Revision { get; private set; }

    private RectI[]? changes;
    private int knownChanges;
    private WeakReference<Surface>? copiedFrom;
    private int copiedRevision;

    /// <summary>An unbounded edit invalidates incremental history.</summary>
    public void MarkModified() { Revision++; knownChanges = 0; }

    /// <summary>Records a conservative source-pixel rectangle. The last 64 revisions let
    /// a coalesced render consume several edits; older or unknown edits require a full pass.</summary>
    public void MarkModified(RectI region)
    {
        Revision++;
        changes ??= new RectI[64];
        changes[Revision & 63] = RectI.Normalize(region.X, region.Y, region.X + region.Width,
            region.Y + region.Height, Width, Height);
        knownChanges = Math.Min(64, knownChanges + 1);
    }

    public bool TryGetChangesSince(int revision, out RectI dirty)
    {
        dirty = default;
        long count = (long)Revision - revision;
        if (revision < 0 || count < 0 || count > knownChanges) return false;
        for (int back = 0; back < count; back++)
        {
            var rect = changes![(Revision - back) & 63];
            if (rect.Width == 0 || rect.Height == 0) continue;
            dirty = dirty.Width == 0 || dirty.Height == 0 ? rect : RectI.Union(dirty, rect);
        }
        return true;
    }

    /// <summary>Known edits across a stroke's byte-for-byte working copy. Every ancestor
    /// must still represent the captured revision; unknown/expired edits fall back. Weak
    /// ancestry does not pin pixel history outside the document's undo budget.</summary>
    internal bool TryGetChangesFrom(Surface? basis, int revision, out RectI dirty)
    {
        dirty = default;
        if (basis is null || revision < 0) return false;
        var current = this;
        int captured = Revision;
        RectI accumulated = default;
        for (int depth = 0; depth < 64; depth++)
        {
            if (ReferenceEquals(current, basis))
            {
                if (captured != current.Revision || !current.TryGetChangesSince(revision, out var edits)) return false;
                dirty = UnionEdits(accumulated, edits);
                return true;
            }
            if (current.Revision != captured || !current.TryGetChangesSince(0, out var changes)
                || current.copiedFrom is null || !current.copiedFrom.TryGetTarget(out var parent)) return false;
            accumulated = UnionEdits(accumulated, changes);
            // The copy contains this exact historical state even if the parent changed later.
            if (ReferenceEquals(parent, basis) && current.copiedRevision == revision)
            {
                dirty = accumulated;
                return true;
            }
            captured = current.copiedRevision;
            current = parent;
        }
        return false;

        static RectI UnionEdits(RectI left, RectI right) => left.Width == 0 || left.Height == 0 ? right
            : right.Width == 0 || right.Height == 0 ? left : RectI.Union(left, right);
    }

    public long PixelCount => (long)Width * Height;

    /// <summary>Crops to a rectangle, sharing nothing: the result owns its bytes.</summary>
    public Surface Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x + width > Width || y + height > Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Crop rectangle is outside the surface.");
        var result = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            int source = (y + row) * Stride + x * 4;
            new Span<byte>(Pixels, source, width * 4).CopyTo(result.Pixels.AsSpan(row * result.Stride));
        }
        return result;
    }

    /// <summary>A fully transparent surface of the same size.</summary>
    public Surface CloneEmpty() => new(Width, Height);

    public Surface Clone() => new(Width, Height, (byte[])Pixels.Clone(), Stride);

    internal Surface CloneForEditing()
    {
        var result = Clone();
        result.copiedFrom = new(this);
        result.copiedRevision = Revision;
        return result;
    }
}

/// <summary>An 8-bit grayscale coverage map: 255 reveals, 0 hides, with no alpha channel.</summary>
public sealed class MaskSurface
{
    public MaskSurface(int width, int height)
    {
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide)
            throw new ArgumentOutOfRangeException(nameof(width), "Mask dimensions exceed the supported limits.");
        if ((long)width * height > Limits.MaxSurfacePixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Mask exceeds the pixel budget.");
        Width = width;
        Height = height;
        Pixels = new byte[(long)width * height];
    }

    public MaskSurface(int width, int height, byte[] pixels)
    {
        if (pixels.Length < (long)width * height) throw new ArgumentException("Pixel buffer is too small.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>One byte per pixel, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>Bumped when the mask is painted in place; see Surface.Revision.</summary>
    public int Revision { get; private set; }

    private RectI[]? changes;
    private int knownChanges;
    private WeakReference<MaskSurface>? copiedFrom;
    private int copiedRevision;

    public void MarkModified() { Revision++; knownChanges = 0; }

    /// <summary>Known mask-pixel edits, bounded like Surface's revision journal.</summary>
    public void MarkModified(RectI region)
    {
        Revision++;
        changes ??= new RectI[64];
        changes[Revision & 63] = RectI.Normalize(region.X, region.Y, region.X + region.Width,
            region.Y + region.Height, Width, Height);
        knownChanges = Math.Min(64, knownChanges + 1);
    }

    public bool TryGetChangesSince(int revision, out RectI dirty)
    {
        dirty = default;
        long count = (long)Revision - revision;
        if (revision < 0 || count < 0 || count > knownChanges) return false;
        for (int back = 0; back < count; back++)
        {
            var rect = changes![(Revision - back) & 63];
            if (rect.Width == 0 || rect.Height == 0) continue;
            dirty = dirty.Width == 0 || dirty.Height == 0 ? rect : RectI.Union(dirty, rect);
        }
        return true;
    }

    /// <summary>Proves edits through immutable mask working copies without retaining history.</summary>
    internal bool TryGetChangesFrom(MaskSurface? basis, int revision, out RectI dirty)
    {
        dirty = default;
        if (basis is null || revision < 0) return false;
        var current = this;
        int captured = Revision;
        RectI accumulated = default;
        for (int depth = 0; depth < 64; depth++)
        {
            if (ReferenceEquals(current, basis))
            {
                if (captured != current.Revision || !current.TryGetChangesSince(revision, out var edits)) return false;
                dirty = UnionEdits(accumulated, edits);
                return true;
            }
            if (current.Revision != captured || !current.TryGetChangesSince(0, out var editsSinceCopy)
                || current.copiedFrom is null || !current.copiedFrom.TryGetTarget(out var parent)) return false;
            accumulated = UnionEdits(accumulated, editsSinceCopy);
            if (ReferenceEquals(parent, basis) && current.copiedRevision == revision)
            {
                dirty = accumulated;
                return true;
            }
            captured = current.copiedRevision;
            current = parent;
        }
        return false;

        static RectI UnionEdits(RectI left, RectI right) => left.Width == 0 || left.Height == 0 ? right
            : right.Width == 0 || right.Height == 0 ? left : RectI.Union(left, right);
    }

    public long PixelCount => (long)Width * Height;

    /// <summary>A 1×1 mask, the cheapest way to reveal or hide a whole layer.</summary>
    public static MaskSurface Solid(bool revealing) => new(1, 1, [(byte)(revealing ? 255 : 0)]);

    public MaskSurface Clone() => new(Width, Height, (byte[])Pixels.Clone());

    internal MaskSurface CloneForEditing()
    {
        var result = Clone();
        result.copiedFrom = new(this);
        result.copiedRevision = Revision;
        return result;
    }
}
