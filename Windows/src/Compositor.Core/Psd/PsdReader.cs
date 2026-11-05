namespace Compositor.Core.Psd;

/// <summary>
/// The PSD reader, ported from the macOS PSDReader.swift — RGB 8-bit files, per-layer
/// pixels with names, offsets and opacity, raw and PackBits channel compression, user masks
/// (the −2 channel over its rect and default), and the group tree from the lsct/lsdk
/// section dividers. Depth 16/32, non-RGB modes, text, vector and smart-object layers are
/// rejected or ignored — the same gate the Mac's reader applies.
/// </summary>
public static partial class PsdReader
{
    private const int MaxSide = Limits.MaxSide;
    private const long MaxSurfacePixels = Limits.MaxSurfacePixels;

    public static PsdDocument Read(byte[] data, bool parallel = true, long? remainingPixels = null, CancellationToken cancellationToken = default)
        => Read(new PsdDataView(new PsdByteInput(data)), parallel, remainingPixels, cancellationToken);

    public static PsdDocument ReadFile(string path, bool parallel = true, long? remainingPixels = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new PsdMappedInput(path);
        return Read(new PsdDataView(input), parallel, remainingPixels, cancellationToken);
    }

    public static Task<PsdDocument> ReadFileAsync(string path, bool parallel = true, long? remainingPixels = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ReadFile(path, parallel, remainingPixels, cancellationToken), cancellationToken);

    /// <summary>Both Photoshop containers this reader opens: version 1 documents and version 2
    /// large documents, which the header check in `Read` distinguishes.</summary>
    public static bool IsPhotoshopFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".psd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase);
    }

    internal static PsdDocument Read(PsdDataView data, bool parallel, long? remainingPixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long budget = Math.Clamp(remainingPixels ?? Limits.DocumentPixelBudget, 0, Limits.DocumentPixelBudget);
        var reader = new PsdBinaryReader(data, cancellationToken);
        if (reader.Remaining < 26 || reader.ReadString4() != "8BPS")
        {
            throw new PsdNotSupportedException("Not a Photoshop document.");
        }
        int version = reader.ReadU16();
        bool isPsb = version == 2;
        if (version is not (1 or 2))
        {
            throw new PsdNotSupportedException($"Unsupported PSD version {version}.");
        }
        reader.Skip(6);
        int channelCount = reader.ReadU16();
        int height = checked((int)reader.ReadU32());
        int width = checked((int)reader.ReadU32());
        int depth = reader.ReadU16();
        int mode = reader.ReadU16();
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide || (long)width * height > MaxSurfacePixels)
        {
            throw new PsdNotSupportedException("Canvas exceeds the supported limits.");
        }
        if (depth != 8)
        {
            throw new PsdNotSupportedException("Only 8-bit PSD files are supported.");
        }
        if (mode != 3)
        {
            throw new PsdNotSupportedException("Only RGB PSD files are supported.");
        }
        if (channelCount is < 3 or > 56) throw new PsdNotSupportedException("Unsupported RGB channel count.");

        // Color mode data.
        reader.Skip(reader.ReadLength());

        // Image resources — first scope skips everything (resolution defaults to 72).
        reader.Skip(reader.ReadLength());

        // Layer and mask information.
        var section = reader.ReadSection(reader.ReadLength(isPsb));
        if (section.Remaining == 0)
        {
            // No layer records: present the merged composite instead.
            return MergedComposite(reader, width, height, channelCount, isPsb, budget);
        }
        var info = section.ReadSection(section.ReadLength(isPsb));
        int signedCount = info.Remaining == 0 ? 0 : info.ReadI16();
        int rawCount = Math.Abs(signedCount);
        if (rawCount > 10_000)
        {
            throw new PsdNotSupportedException("Too many layers.");
        }

        var raw = new List<RawLayer>(rawCount);
        for (int i = 0; i < rawCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            raw.Add(ReadRecord(info, isPsb));
        }
        if (!FitsBudget(raw, budget))
        {
            foreach (var layer in raw) { cancellationToken.ThrowIfCancellationRequested(); CropToCanvas(layer, width, height); }
            if (!FitsBudget(raw, budget)) throw new PsdNotSupportedException("PSD exceeds the remaining pixel budget.");
        }
        // Channel sections are sliced serially (one shared cursor), then decoded per layer
        // in parallel — UnpackRle and the RGBA composition are pure per-layer work, so the
        // output is bit-identical either way.
        foreach (var layer in raw)
        {
            SliceChannels(info, layer, isPsb);
        }
        // The outer reader already points beyond the bounded section, at the merged composite.
        if (parallel)
        {
            try
            {
                Parallel.For(0, raw.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
                    i => DecodeChannels(raw[i], cancellationToken));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Preserve the reader's own exception types (PsdNotSupportedException, truncation).
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
            }
        }
        else
        {
            foreach (var layer in raw)
            {
                DecodeChannels(layer, cancellationToken);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (layers, skipped) = Assemble(raw, width, height, budget, cancellationToken);
        if (!layers.Any(layer => !layer.IsGroup))
        {
            // No pixel layers survived: present the merged composite instead. The Mac's builder
            // still reports the layers it skipped on the way, so the lines ride the fallback too.
            var composite = MergedComposite(reader, width, height, channelCount, isPsb, budget, signedCount < 0);
            if (skipped.Count > 0) composite.ReaderConversions = skipped;
            return composite;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PsdDocument { Width = width, Height = height, Layers = layers, ReaderConversions = skipped.Count > 0 ? skipped : null };
    }

    private sealed class RawLayer
    {
        public string Name = "";
        public int Top, Left, Bottom, Right;
        public int SourceTop, SourceLeft, SourceBottom, SourceRight;
        public List<(int Id, long Length)> Channels = new();
        public List<PendingChannel> Pending = new();
        public bool IsPsb;
        public Dictionary<int, byte[]> Planes = new();
        public bool Hidden;
        public int? Section;
        public Surface? Pixels;
        public byte Opacity = 255;
        public string BlendKey = "norm";
        public byte Fill = 255;
        public bool HasEffects;
        public bool HasMask;
        public int MaskTop, MaskLeft, MaskBottom, MaskRight;
        public int SourceMaskTop, SourceMaskLeft, SourceMaskBottom, SourceMaskRight;
        public PsdCrop? ImageCrop, MaskCrop;
        public bool Cropped;
        public bool MaskLinked = true;
        public byte MaskDefault = 255;
        public bool MaskDisabled;
        public bool MaskFromRender;
        public byte[]? MaskPlane;
        public MaskSurface? Mask;
        // Small SoCo/vstk/vogk/vmsk/vsms payloads, kept in full for PsdVector.
        public Dictionary<string, byte[]>? VectorExtras;
        // The levl/curv/hue2/hue payloads, kept for PsdAdjustment.
        public Dictionary<string, byte[]>? AdjustmentExtras;
        // The type-layer descriptor, kept for PsdText (bounded like the Mac's 8 MB guard).
        public byte[]? TextRecord;
        // Kind-presence, from the key names alone like the Mac's kind(): true even when a
        // payload was too big to keep, because the label does not depend on parsing it.
        public bool HasTextKey;
        public bool HasVectorKey;
        public bool HasAdjustmentKey;
        // Smart-object keys: the Mac's kind() gate that keeps the adjustment drop from firing.
        public bool HasSmartObject;
    }

    /// <summary>A channel's compressed bytes, sliced serially and decoded later.</summary>
    private sealed class PendingChannel
    {
        public int Id;
        public bool IsMask;
        public long PlaneWidth, PlaneHeight;
        public int Compression;
        public PsdCrop? Crop;
        public required PsdDataView Payload;
    }

    private static RawLayer ReadRecord(PsdBinaryReader reader, bool isPsb)
    {
        var layer = new RawLayer();
        int top = reader.ReadI32();
        int left = reader.ReadI32();
        int bottom = reader.ReadI32();
        int right = reader.ReadI32();
        layer.Top = layer.SourceTop = top;
        layer.Left = layer.SourceLeft = left;
        layer.Bottom = layer.SourceBottom = bottom;
        layer.Right = layer.SourceRight = right;
        int channelCount = reader.ReadU16();
        if (channelCount > 56) throw new PsdNotSupportedException("Too many channels.");
        for (int i = 0; i < channelCount; i++)
        {
            int id = reader.ReadI16();
            layer.Channels.Add((id, reader.ReadLength(isPsb)));
        }
        string signature = reader.ReadString4();
        ushort channelProbe = reader.PeekU16();
        if (signature != "8BIM")
        {
            throw new PsdNotSupportedException(
                $"Corrupt layer record at offset {reader.Offset - 4}: signature '{signature}', next channel count {channelProbe}.");
        }
        layer.BlendKey = reader.ReadString4();
        layer.Opacity = reader.ReadU8();
        reader.Skip(1); // clipping
        int flags = reader.ReadU8();
        layer.Hidden = (flags & 2) != 0;
        reader.Skip(1);
        var extra = reader.ReadSection(reader.ReadLength());
        var mask = extra.ReadSection(extra.ReadLength());
        if (mask.Remaining >= 20)
        {
            layer.HasMask = true;
            layer.MaskTop = layer.SourceMaskTop = mask.ReadI32();
            layer.MaskLeft = layer.SourceMaskLeft = mask.ReadI32();
            layer.MaskBottom = layer.SourceMaskBottom = mask.ReadI32();
            layer.MaskRight = layer.SourceMaskRight = mask.ReadI32();
            layer.MaskDefault = mask.ReadU8();
            int maskFlags = mask.ReadU8();
            layer.MaskDisabled = (maskFlags & 2) != 0;
            layer.MaskLinked = (maskFlags & 1) == 0;
            layer.MaskFromRender = (maskFlags & 8) != 0;
        }
        extra.Skip(extra.ReadLength());
        int nameCount = extra.ReadU8();
        var nameBytes = extra.ReadBytes(nameCount);
        layer.Name = System.Text.Encoding.Latin1.GetString(nameBytes);
        int namePad = (4 - ((nameCount + 1) % 4)) % 4;
        extra.Skip(namePad);
        while (extra.Remaining >= 12)
        {
            string extraSignature = extra.ReadString4();
            if (extraSignature is not ("8BIM" or "8B64")) break;
            string key = extra.ReadString4();
            bool largeLength = extraSignature == "8B64" || isPsb && key is
                "LMsk" or "Lr16" or "Lr32" or "Layr" or "Mt16" or "Mt32" or "Mtrn" or "Alph" or "FMsk" or "lnk2" or "FEid" or "FXid" or "PxSD";
            long length = extra.ReadLength(largeLength);
            var payload = extra.ReadSection(length);
            if (key == "luni" && length >= 4)
            {
                // Photoshop's Unicode layer name wins over the pascal one.
                uint units = payload.ReadU32();
                if (units > payload.Remaining / 2) throw new EndOfStreamException("PSD data truncated.");
                if (units > 0)
                {
                    var name = payload.ReadView((long)units * 2);
                    long trimmed = units;
                    // Trim padding without allocating a potentially enormous metadata block.
                    while (trimmed > 0)
                    {
                        reader.Cancellation.ThrowIfCancellationRequested();
                        int take = (int)Math.Min(512, trimmed);
                        var tail = name.GetSpan((trimmed - take) * 2, take * 2);
                        int kept = take;
                        while (kept > 0 && tail[(kept - 1) * 2] == 0 && tail[(kept - 1) * 2 + 1] == 32) kept--;
                        trimmed -= take - kept;
                        if (kept > 0) break;
                    }
                    // The saved project contract limits layer names to 16KiB UTF-8.
                    if (trimmed > 16_384) throw new PsdNotSupportedException("Layer name exceeds the supported limits.");
                    layer.Name = System.Text.Encoding.BigEndianUnicode.GetString(name.GetSpan(0, (int)trimmed * 2));
                    if (System.Text.Encoding.UTF8.GetByteCount(layer.Name) > 16_384)
                        throw new PsdNotSupportedException("Layer name exceeds the supported limits.");
                }
            }
            else if ((key == "lsct" || key == "lsdk") && length >= 4)
            {
                layer.Section = payload.ReadI32();
            }
            else if (key == "iOpa" && length >= 1)
            {
                layer.Fill = payload.ReadU8();
            }
            else if (PsdText.TracksKey(key) && length is >= 8 and <= 8_000_000)
            {
                layer.TextRecord = payload.ReadBytes((int)length);
            }
            if (PsdAdjustment.AdjustmentKeys.Contains(key)) layer.HasAdjustmentKey = true;
            if (key is "vmsk" or "vsms" or "vogk") layer.HasVectorKey = true;
            if (PsdText.TracksKey(key) || key is "txt2") layer.HasTextKey = true;
            else if (PsdAdjustment.TracksKey(key) && length is >= 4 and <= PsdVector.MaxDescriptorBytes)
            {
                (layer.AdjustmentExtras ??= new Dictionary<string, byte[]>())[key] = payload.ReadBytes((int)length);
            }
            else if (PsdVector.TracksKey(key) && length is >= 4 and <= PsdVector.MaxDescriptorBytes)
            {
                // The vector path needs the whole descriptor; a giant blob is not a shape.
                (layer.VectorExtras ??= new Dictionary<string, byte[]>())[key] = payload.ReadBytes((int)length);
            }
            if (key is "lfx2" or "lrFX" or "lmfx")
            {
                layer.HasEffects = true;
            }
            if (key is "SoLd" or "SoLE")
            {
                layer.HasSmartObject = true;
            }
            if (length % 2 == 1) extra.Skip(1);
        }
        return layer;
    }

    /// <summary>Walks one layer's channel sections on the shared cursor, recording each
    /// compressed payload for a later (possibly parallel) decode.</summary>
    private static void SliceChannels(PsdBinaryReader reader, RawLayer layer, bool isPsb)
    {
        layer.IsPsb = isPsb;
        foreach (var (id, length) in layer.Channels)
        {
            var channel = reader.ReadSection(length);
            bool isImagePlane = id is 0 or 1 or 2 or -1;
            bool isMaskPlane = id == -2 && layer.HasMask && !layer.MaskFromRender;
            if (length < 2 || !(isImagePlane || isMaskPlane))
            {
                continue;
            }
            long planeWidth = isMaskPlane ? Extent(layer.SourceMaskRight, layer.SourceMaskLeft) : Extent(layer.SourceRight, layer.SourceLeft);
            long planeHeight = isMaskPlane ? Extent(layer.SourceMaskBottom, layer.SourceMaskTop) : Extent(layer.SourceBottom, layer.SourceTop);
            long targetWidth = isMaskPlane ? Extent(layer.MaskRight, layer.MaskLeft) : Extent(layer.Right, layer.Left);
            long targetHeight = isMaskPlane ? Extent(layer.MaskBottom, layer.MaskTop) : Extent(layer.Bottom, layer.Top);
            int compression = channel.ReadU16();
            var payload = channel.ReadView(channel.Remaining);
            if (targetWidth > 0 && targetHeight > 0)
            {
                layer.Pending.Add(new PendingChannel
                {
                    Id = id,
                    IsMask = isMaskPlane,
                    PlaneWidth = planeWidth,
                    PlaneHeight = planeHeight,
                    Compression = compression,
                    Crop = isMaskPlane ? layer.MaskCrop : layer.ImageCrop,
                    Payload = payload,
                });
            }
        }
    }

    /// <summary>Decodes one layer's sliced channel payloads and composes its premultiplied
    /// RGBA — pure per-layer work, safe to run in parallel.</summary>
    private static void DecodeChannels(RawLayer layer, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        long w = Extent(layer.Right, layer.Left), h = Extent(layer.Bottom, layer.Top);
        if (w == 0 || h == 0) { layer.Pending.Clear(); return; }
        int width = checked((int)w), height = checked((int)h), size = checked(width * height);
        foreach (var channel in layer.Pending)
        {
            var plane = PsdChannelCoder.Decode(channel.Payload, channel.Compression, channel.PlaneWidth, channel.PlaneHeight,
                layer.IsPsb, channel.Crop, cancellation);
            if (channel.IsMask) layer.MaskPlane = plane;
            else layer.Planes[channel.Id] = plane;
        }

        var red = layer.Planes.GetValueOrDefault(0);
        var green = layer.Planes.GetValueOrDefault(1);
        var blue = layer.Planes.GetValueOrDefault(2);
        var alpha = layer.Planes.GetValueOrDefault(-1);
        if (red is not null && red.Length < size || green is not null && green.Length < size
            || blue is not null && blue.Length < size || alpha is not null && alpha.Length < size)
        {
            throw new PsdNotSupportedException("Corrupt channel data.");
        }
        // Premultiplied RGBA, the macOS PSDChannelCoder.rgbaImage formula.
        cancellation.ThrowIfCancellationRequested();
        var pixels = new byte[checked(size * 4)];
        for (int i = 0; i < size; i++)
        {
            if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
            int a = alpha?[i] ?? 255;
            pixels[i * 4] = (byte)(((red?[i] ?? 0) * a + 127) / 255);
            pixels[i * 4 + 1] = (byte)(((green?[i] ?? 0) * a + 127) / 255);
            pixels[i * 4 + 2] = (byte)(((blue?[i] ?? 0) * a + 127) / 255);
            pixels[i * 4 + 3] = (byte)a;
        }
        layer.Pixels = new Surface(width, height, pixels, width * 4);
        layer.Mask = MaskSurfaceOf(layer, width, height, cancellation);
        layer.MaskPlane = null; layer.Planes.Clear(); layer.Pending.Clear();
    }

    /// <summary>
    /// Photoshop stores groups bottom-to-top: the type 3 divider comes first, then the
    /// children, then the folder record (type 1/2). The divider mints the group's id, the
    /// folder record takes it, and everything in between points at it — the macOS
    /// assemble()'s pairing stack.
    /// </summary>
    private static (List<PsdLayer> Layers, List<PsdConversion> Skipped) Assemble(List<RawLayer> raw, int canvasWidth, int canvasHeight, long remainingPixels, CancellationToken cancellation)
    {
        var layers = new List<PsdLayer>();
        var skipped = new List<PsdConversion>();
        var openGroups = new Stack<Guid>();
        // The decoded rasters already stand on this budget; the live shapes add their own.
        long remaining = Math.Max(0, remainingPixels
            - raw.Sum(layer => (layer.Pixels?.PixelCount ?? 0) + (layer.Mask?.PixelCount ?? 0)));
        foreach (var layer in raw)
        {
            cancellation.ThrowIfCancellationRequested();
            if (layer.Section == 3)
            {
                openGroups.Push(Guid.NewGuid());
                continue;
            }
            bool isGroup = layer.Section is 1 or 2;
            // The Mac reads adjustment and text independently: every non-group layer's records
            // parse, and the kind (decided by key presence alone) chooses which parse is even
            // attempted and whether the vector branch runs at all — a failed text parse still
            // falls through to it, like the reader's else-if.
            var adjustment = !isGroup && layer.AdjustmentExtras is { Count: > 0 }
                ? PsdAdjustment.ReadLive(layer.AdjustmentExtras) : null;
            bool textKeys = layer.HasTextKey;
            // The Mac's kind(): the first key family a layer carries labels it, regardless of
            // whether a payload was small enough to keep or parses here at all.
            var kind = isGroup ? PsdLayerKind.Group
                : textKeys ? PsdLayerKind.Text
                : layer.HasVectorKey ? PsdLayerKind.Vector
                : layer.HasSmartObject ? PsdLayerKind.SmartObject
                : layer.HasEffects ? PsdLayerKind.Effects
                : layer.HasAdjustmentKey ? PsdLayerKind.Adjustment
                : PsdLayerKind.Raster;
            if (!isGroup && kind == PsdLayerKind.Adjustment && adjustment is null)
            {
                // The Mac's builder notes the skip before dropping the layer, so the report keeps
                // the line even though nothing else of the layer survives.
                skipped.Add(new PsdConversion(layer.Name, PsdConversion.AdjustmentSkipped));
                continue;
            }
            PsdText.Parse? text = isGroup || kind != PsdLayerKind.Text
                ? null : PsdText.ReadLive(layer.TextRecord);
            PsdVector.Live? live = isGroup || (kind == PsdLayerKind.Text && text is not null)
                ? null : PsdVector.ReadLive(layer.VectorExtras, canvasWidth, canvasHeight, ref remaining);
            // After the chain, the Mac relabels the record by what actually parsed: adjustment
            // overrides everything, then a placed shape, then parsed text. The conversion report
            // reads this final label, so a vertical type record over a fill reads as vector.
            kind = adjustment is not null ? PsdLayerKind.Adjustment
                : text is not null ? PsdLayerKind.Text
                : live is not null ? PsdLayerKind.Vector
                : kind;
            if (!isGroup && layer.Pixels is null && live is null && text is null && adjustment is null) continue;
            Guid id = isGroup && openGroups.TryPop(out var paired) ? paired : Guid.NewGuid();
            Guid? parent = openGroups.Count > 0 ? openGroups.Peek() : null;
            // With live effects the fill factor does not apply, as on the Mac.
            double opacity = layer.Opacity / 255.0;
            if (!layer.HasEffects) opacity *= layer.Fill / 255.0;
            layers.Add(new PsdLayer
            {
                Id = id,
                ParentId = parent,
                IsGroup = isGroup,
                Name = string.IsNullOrWhiteSpace(layer.Name) ? "Layer" : layer.Name,
                // The adjustment branch carries no asset: Photoshop's own pixels are dropped.
                Kind = kind,
                Pixels = adjustment is not null ? null : live?.Image ?? layer.Pixels,
                Shape = live?.Style,
                ShapeNotes = live?.Notes,
                Adjustment = adjustment,
                Text = text?.Style,
                // The style is editable, so no rasterize caveat: only the parse's own notes ride.
                TextNotes = text is { } parsed && parsed.Notes.Count > 0 ? parsed.Notes : null,
                TextAnchor = text is { } anchor ? new PointD(anchor.AnchorX, anchor.AnchorY) : default,
                TextRotation = text?.Rotation ?? 0,
                TextFlipY = text?.FlipY ?? false,
                Offset = live is { } shape ? new PointD(shape.X, shape.Y) : new PointD(layer.Left, layer.Top),
                Hidden = layer.Hidden,
                Opacity = opacity,
                BlendKey = layer.BlendKey,
                Mask = !isGroup ? layer.Mask : null,
                MaskPlacement = layer.Mask is not null ? new LayerTransform { Origin = new(layer.Left, layer.Top),
                    // The mask grid follows the layer box, which its raster fills whenever one exists.
                    Size = new(layer.Pixels?.Width ?? (int)Extent(layer.Right, layer.Left),
                        layer.Pixels?.Height ?? (int)Extent(layer.Bottom, layer.Top)) } : null,
                MaskLinked = layer.MaskLinked,
                MaskEnabled = !layer.MaskDisabled,
                CroppedToCanvas = layer.Cropped,
            });
        }
        if (openGroups.Count > 0)
        {
            throw new PsdNotSupportedException("Unbalanced layer groups.");
        }
        return (layers, skipped);
    }

    /// <summary>
    /// PSDDocumentBuilder.maskOnLayerGrid: the patch occupies its document rect, with
    /// Photoshop's default everywhere else on the layer's own grid.
    /// </summary>
    private static MaskSurface? MaskSurfaceOf(RawLayer layer, int width, int height, CancellationToken cancellation)
    {
        if (!layer.HasMask || layer.MaskFromRender) return null;
        long w = Extent(layer.MaskRight, layer.MaskLeft), h = Extent(layer.MaskBottom, layer.MaskTop);
        if (w == 0 || h == 0) return null;
        int maskWidth = checked((int)w), maskHeight = checked((int)h);
        var plane = layer.MaskPlane;
        if (maskWidth <= 0 || maskHeight <= 0 || plane is null || plane.Length < maskWidth * maskHeight)
        {
            return null;
        }
        cancellation.ThrowIfCancellationRequested();
        var mask = new MaskSurface(width, height);
        for (int row = 0; row < height; row++)
        { cancellation.ThrowIfCancellationRequested(); mask.Pixels.AsSpan(row * width, width).Fill(layer.MaskDefault); }
        int x0 = Math.Max(layer.MaskLeft, layer.Left);
        int y0 = Math.Max(layer.MaskTop, layer.Top);
        int x1 = Math.Min(layer.MaskRight, layer.Right);
        int y1 = Math.Min(layer.MaskBottom, layer.Bottom);
        if (x1 <= x0 || y1 <= y0) return mask;
        for (int y = y0; y < y1; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int sourceRow = (y - layer.MaskTop) * maskWidth + (x0 - layer.MaskLeft);
            int targetRow = (y - layer.Top) * width + (x0 - layer.Left);
            plane.AsSpan(sourceRow, x1 - x0).CopyTo(mask.Pixels.AsSpan(targetRow, x1 - x0));
        }
        return mask;
    }

    /// <summary>
    /// The merged-composite fallback for files without usable layer records: the tail of
    /// the file holds one compression marker, all row counts, then planar channel data.
    /// </summary>
    private static PsdDocument MergedComposite(PsdBinaryReader reader, int width, int height, int channelCount, bool isPsb,
        long remainingPixels, bool transparency = false)
    {
        var cancellation = reader.Cancellation;
        cancellation.ThrowIfCancellationRequested();
        if ((long)width * height > remainingPixels) throw new PsdNotSupportedException("PSD exceeds the remaining pixel budget.");
        if (reader.Remaining < 2) throw new EndOfStreamException("PSD merged image missing.");
        if (transparency && channelCount < 4) throw new PsdNotSupportedException("Merged transparency channel missing.");
        int compression = reader.ReadU16();
        int planeSize = width * height;
        var planes = new byte[transparency ? 4 : 3][];
        long[][] counts = new long[channelCount][];
        if (compression == 1)
        {
            if ((long)channelCount * height * (isPsb ? 4 : 2) > reader.Remaining) throw new EndOfStreamException("PSD data truncated.");
            for (int channel = 0; channel < channelCount; channel++)
            {
                counts[channel] = new long[height];
                for (int row = 0; row < height; row++)
                {
                    uint length = isPsb ? reader.ReadU32() : (uint)reader.ReadU16();
                    counts[channel][row] = length;
                }
            }
        }
        else if (compression != 0) throw new PsdNotSupportedException("Unsupported channel compression.");
        for (int channel = 0; channel < channelCount; channel++)
        {
            if (channel >= planes.Length)
            {
                long length = compression == 0 ? planeSize : counts[channel].Sum(value => (long)value);
                if (length > reader.Remaining) throw new EndOfStreamException("PSD data truncated.");
                reader.Skip(length); continue;
            }
            planes[channel] = compression == 0 ? RawPlane(reader, planeSize) : reader.ReadRlePlane(counts[channel], width, height);
        }
        cancellation.ThrowIfCancellationRequested();
        var pixels = new byte[planeSize * 4];
        for (int i = 0; i < planeSize; i++)
        {
            if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
            int alpha = transparency ? planes[3][i] : 255;
            pixels[i * 4] = (byte)((planes[0][i] * alpha + 127) / 255);
            pixels[i * 4 + 1] = (byte)((planes[1][i] * alpha + 127) / 255);
            pixels[i * 4 + 2] = (byte)((planes[2][i] * alpha + 127) / 255);
            pixels[i * 4 + 3] = (byte)alpha;
        }
        cancellation.ThrowIfCancellationRequested();
        var surface = new Surface(width, height, pixels, width * 4);
        return new PsdDocument
        {
            Width = width,
            Height = height,
            IsCompositeFallback = true,
            Layers = [new PsdLayer { Name = "Merged", Pixels = surface, Offset = new PointD(0, 0), Id = Guid.NewGuid() }],
        };
    }

    private static byte[] RawPlane(PsdBinaryReader reader, int planeSize)
    {
        if (reader.Remaining < planeSize) throw new PsdNotSupportedException("Corrupt merged image.");
        return reader.ReadBytes(planeSize);
    }
}
