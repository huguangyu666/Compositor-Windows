using System.Text.Json;
using Compositor.Core.Psd;
using System.Runtime.CompilerServices;

namespace Compositor.Core;

/// <summary>
/// An open document being edited: the project snapshot plus undoable layer operations. Undo
/// works on mementos — the manifest serialized to bytes, plus the image and mask dictionaries
/// by reference (surfaces are immutable, so replacing a layer's pixels is a new entry) — which
/// makes every operation revertible without hand-written inverses.
/// </summary>
public sealed partial class EditorDocument
{
    private static readonly JsonSerializerOptions MementoOptions = ManifestJson.CompactOptions;

    private readonly Stack<MementoState> undo = new();
    private readonly Stack<MementoState> redo = new();
    private object? coalesceKey;
    private bool suppressUndoHistory; // Detached automation drafts publish only the outer history step.

    public EditorDocument(ProjectSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    /// <summary>Creates an untitled document with one white background layer.</summary>
    public static EditorDocument Create(int width = 1280, int height = 800)
    {
        var id = Guid.NewGuid();
        var manifest = new ProjectManifest
        {
            DocumentId = Guid.NewGuid(),
            Width = width,
            Height = height,
            ActiveLayerId = id,
            Resolution = 72,
            Layers =
            [
                new ProjectLayerRecord
                {
                    Id = id,
                    Name = "Background",
                    IsVisible = true,
                    ImageFile = ProjectValidator.Uuid(id) + ".png",
                    Opacity = 1,
                    BlendMode = LayerBlendMode.Normal,
                    IsGroup = false,
                    Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(width, height) },
                },
            ],
        };
        var images = new Dictionary<Guid, Surface> { [id] = Solid(width, height, 255, 255, 255) };
        return new EditorDocument(new ProjectSnapshot
        {
            Manifest = manifest,
            Images = images,
            Masks = new Dictionary<Guid, MaskSurface>(),
        });
    }

    public ProjectSnapshot Snapshot { get; private set; }

    /// <summary>Raised after any change that should re-render the canvas or layer list.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when only pixels moved: brush dabs, drags, slider ticks. The canvas re-renders
    /// its dirty region; the layer list is untouched, which is what keeps painting smooth.
    /// </summary>
    public event Action? PixelsChanged;

    /// <summary>Session selection changed; the document composite and layer rows are unchanged.</summary>
    public event Action? SelectionChanged;

    public bool IsDirty { get; private set; }

    /// <summary>The text the on-canvas editor is still working on, if any. macOS keeps history closed while a
    /// draft is open (EditorSession.swift:607): the words have not become pixels yet, so there is no step to
    /// undo over — undoing past them would drop the very text being typed.</summary>
    public TextDraft? ActiveTextDraft { get; private set; }

    public bool CanUndo => undo.Count > 0 && ActiveTextDraft is null;

    public bool CanRedo => redo.Count > 0 && ActiveTextDraft is null;

    public int LayerCount => Snapshot.Manifest.Layers.Count;

    private static byte[] MementoOf(ProjectSnapshot snapshot)
    {
        // A memento is the manifest plus the surface dictionaries by reference; surfaces never
        // mutate, so a shallow dictionary copy is all the image state an undo needs.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            JsonSerializer.Serialize(writer, snapshot.Manifest, MementoOptions);
        }
        return stream.ToArray();
    }

    private sealed class MementoState
    {
        public Guid HistoryId { get; init; }
        public string HistoryOperation { get; init; } = "Initial";
        public required byte[] Manifest { get; init; }
        public required Dictionary<Guid, Surface> Images { get; init; }
        public required Dictionary<Guid, MaskSurface> Masks { get; init; }
        public required bool Dirty { get; init; }
        public Selection? Selection { get; init; }
        public bool SelectionOnly { get; init; }
        public Guid? ActiveLayerId { get; init; }
    }

    private List<MementoState>? pendingRedoRestore; // what BeginChange cleared, for AbortChange

    private MementoState CaptureState() => new MementoState
    {
        HistoryId = CurrentHistoryId, HistoryOperation = historyOperation,
        Manifest = MementoOf(Snapshot),
        Images = new Dictionary<Guid, Surface>(Snapshot.Images),
        Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks),
        Dirty = IsDirty,
        Selection = ActiveSelection,
    };

    private void BeginChange(MementoState? captured = null, [CallerMemberName] string operation = "")
    {
        protectionManifest = null;
        if (suppressUndoHistory) return;
        pendingRedoRestore = null;
        if (redo.Count > 0)
        {
            pendingRedoRestore = new List<MementoState>(redo);
            redo.Clear();
        }
        undo.Push(captured ?? CaptureState());
        StartHistoryState(operation);
    }

    /// <summary>
    /// Rolls back a <see cref="BeginChange"/> whose operation was refused before it touched
    /// anything: the pushed memento (a step Undo would have consumed as a no-op) is popped,
    /// and the redo stack an aborted change never invalidated is put back.
    /// </summary>
    private void AbortChange()
    {
        if (suppressUndoHistory) return;
        if (undo.Count > 0) RestoreHistoryIdentity(undo.Pop());
        if (pendingRedoRestore is { } restore)
        {
            for (int i = restore.Count - 1; i >= 0; i--) redo.Push(restore[i]);
            pendingRedoRestore = null;
        }
        NotifyHistoryChanged();
    }

    private void EndChange()
    {
        pendingRedoRestore = null;
        IsDirty = true;
        if (suppressUndoHistory) return;
        TrimUndoHistory();
        NotifyHistoryChanged();
        Changed?.Invoke();
    }

    public bool Undo() => ActiveTextDraft is null && Revert(undo, redo);

    public bool Redo() => ActiveTextDraft is null && Revert(redo, undo);

    /// <summary>
    /// The pixel bytes the undo history may pin before the oldest steps are dropped. Editing
    /// replaces whole surfaces (Undo restores them by reference), so without a ceiling every
    /// step keeps a layer's previous bytes alive and a long session grows without bound —
    /// the document pixel budget counts only the live snapshot. Default 2 GB.
    /// </summary>
    public long UndoByteBudget { get; set; } = 2L * 1024 * 1024 * 1024;

    private bool Revert(Stack<MementoState> from, Stack<MementoState> to)
    {
        if (from.Count == 0) return false;
        var state = from.Pop();
        if (state.SelectionOnly)
        {
            to.Push(CaptureSelectionState());
            ActiveSelection = state.Selection;
            Snapshot.Manifest.ActiveLayerId = state.ActiveLayerId;
            IsDirty = state.Dirty; coalesceKey = null;
            RestoreHistoryIdentity(state);
            if (!navigatingHistory) { TrimUndoHistory(); NotifyHistoryChanged(); SelectionChanged?.Invoke(); }
            return true;
        }
        var manifest = ManifestJson.Deserialize(System.Text.Encoding.UTF8.GetString(state.Manifest));
        to.Push(new MementoState
        {
            HistoryId = CurrentHistoryId, HistoryOperation = historyOperation,
            Manifest = MementoOf(Snapshot),
            Images = new Dictionary<Guid, Surface>(Snapshot.Images),
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks),
            Dirty = IsDirty,
            Selection = ActiveSelection,
        });
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = state.Images,
            Masks = state.Masks,
        };
        IsDirty = state.Dirty;
        ActiveSelection = state.Selection;
        coalesceKey = null;
        RestoreHistoryIdentity(state);
        if (!navigatingHistory) { TrimUndoHistory(); NotifyHistoryChanged(); Changed?.Invoke(); }
        return true;
    }

    /// <summary>Drops the oldest history steps until the surfaces they pin fit the budget.
    /// A step's cost is the surfaces it references that nothing newer — a later step or the
    /// live snapshot — also references: those are the bytes the step alone keeps alive.</summary>
    private void TrimUndoHistory()
    {
        TrimStack(undo);
        TrimStack(redo);
    }

    private void TrimStack(Stack<MementoState> stack)
    {
        if (stack.Count <= 1) return;
        var keepAlive = new HashSet<object>();
        foreach (var (_, surface) in Snapshot.Images) keepAlive.Add(surface);
        foreach (var (_, mask) in Snapshot.Masks) keepAlive.Add(mask);
        if (ActiveSelection is { } currentSelection) foreach (var coverage in currentSelection.RetainedCoverage) keepAlive.Add(coverage);
        int keep = 0;
        long pinned = 0;
        foreach (var step in stack) // Stack enumerates newest first
        {
            pinned += step.Manifest.LongLength;
            if (step.Selection is { } selection) foreach (var coverage in selection.RetainedCoverage)
                if (keepAlive.Add(coverage)) pinned += coverage.Pixels.LongLength;
            foreach (var (_, surface) in step.Images)
            {
                if (keepAlive.Add(surface)) pinned += (long)surface.Stride * surface.Height;
            }
            foreach (var (_, mask) in step.Masks)
            {
                if (keepAlive.Add(mask)) pinned += mask.Pixels.LongLength;
            }
            if (pinned > UndoByteBudget) break;
            keep++;
        }
        keep = Math.Max(1, keep); // the newest step survives even over budget
        if (keep >= stack.Count) return;
        var kept = new List<MementoState>(keep);
        foreach (var step in stack)
        {
            kept.Add(step);
            if (kept.Count == keep) break;
        }
        stack.Clear();
        for (int i = kept.Count - 1; i >= 0; i--) stack.Push(kept[i]);
    }

    // --- Incremental saves: which assets on disk still match their surfaces ---

    private readonly Dictionary<Guid, (Surface Surface, int Revision, string File, long? Bytes, DateTime? Written)> savedImages = new();

    private byte[]? savedDiskDigest;

    /// <summary>
    /// The digest of the saved package as of the last <see cref="MarkSaved"/> (or reload that went
    /// through it), or null while the document has never met its folder. A save hands this to
    /// ProjectStore, which refuses to overwrite anything else's work when the disk has moved since.
    /// </summary>
    public byte[]? SavedDiskDigest => savedDiskDigest;

    private static byte[]? DigestOf(string? projectDirectory)
    {
        if (projectDirectory is null) return null;
        try
        {
            return Directory.Exists(projectDirectory) ? ProjectStore.ComputeDigest(projectDirectory) : null;
        }
        catch (IOException) { return null; } // nothing to compare against yet; the save simply goes through
        catch (UnauthorizedAccessException) { return null; }
    }
    private readonly Dictionary<Guid, (MaskSurface Surface, int Revision, string File, long? Bytes, DateTime? Written)> savedMasks = new();

    /// <summary>
    /// The asset file names whose bytes on disk still match their current surfaces — the
    /// files a save may skip re-encoding. A surface is current when it is the very object
    /// that was written, unmutated since (same Revision), under the same file name.
    /// With a `projectDirectory` (the one the save writes to) an asset is only current when
    /// its length and write time are still what `MarkSaved` recorded there, so an outside
    /// program that replaced a file — even one of the same size — gets re-encoded rather than
    /// left under our manifest.
    /// </summary>
    public IReadOnlySet<string> UnchangedAssetNames(string? projectDirectory = null)
    {
        var names = new HashSet<string>();
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile
                && savedImages.TryGetValue(layer.Id, out var savedImage)
                && savedImage.File == imageFile
                && Snapshot.Images.TryGetValue(layer.Id, out var image)
                && ReferenceEquals(image, savedImage.Surface)
                && image.Revision == savedImage.Revision
                && AssetStillOnDisk(projectDirectory, imageFile, savedImage.Bytes, savedImage.Written))
            {
                names.Add(imageFile);
            }
            if (layer.MaskFile is { } maskFile
                && savedMasks.TryGetValue(layer.Id, out var savedMask)
                && savedMask.File == maskFile
                && Snapshot.Masks.TryGetValue(layer.Id, out var mask)
                && ReferenceEquals(mask, savedMask.Surface)
                && mask.Revision == savedMask.Revision
                && AssetStillOnDisk(projectDirectory, maskFile, savedMask.Bytes, savedMask.Written))
            {
                names.Add(maskFile);
            }
        }
        return names;
    }

    private static bool AssetStillOnDisk(string? projectDirectory, string fileName, long? bytes, DateTime? written)
    {
        if (projectDirectory is null) return true;
        if (bytes is null || written is null) return false;
        var info = new FileInfo(Path.Combine(projectDirectory, ProjectStore.ImagesFolderName, fileName));
        return info.Exists && info.Length == bytes && info.LastWriteTimeUtc == written;
    }

    public void MarkSaved(string? projectDirectory = null)
    {
        savedImages.Clear();
        savedMasks.Clear();
        foreach (var layer in Snapshot.Manifest.Layers)
        {
            if (layer.ImageFile is { } imageFile && Snapshot.Images.TryGetValue(layer.Id, out var image))
            {
                var (bytes, written) = AssetStamp(projectDirectory, imageFile);
                savedImages[layer.Id] = (image, image.Revision, imageFile, bytes, written);
            }
            if (layer.MaskFile is { } maskFile && Snapshot.Masks.TryGetValue(layer.Id, out var mask))
            {
                var (bytes, written) = AssetStamp(projectDirectory, maskFile);
                savedMasks[layer.Id] = (mask, mask.Revision, maskFile, bytes, written);
            }
        }
        IsDirty = false;
        // Record the package's shape at the moment we stopped disagreeing with it, so the next save
        // can tell whether somebody else wrote in between (ProjectStore's expectedDiskDigest).
        savedDiskDigest = DigestOf(projectDirectory);
        Changed?.Invoke();
    }

    /// <summary>
    /// Takes the package on disk as the baseline the next save may overwrite, without claiming the
    /// document is saved. This is the "Keep Mine" answer to an external change: the outside write was
    /// seen and chosen against, so <see cref="IsDirty"/> and the undo history stay exactly as they are.
    /// A further write by anyone else is refused again, because the digest is re-compared from here.
    /// </summary>
    public void AdoptDiskState(string? projectDirectory)
    {
        savedDiskDigest = DigestOf(projectDirectory);
    }

    private static (long? Bytes, DateTime? Written) AssetStamp(string? projectDirectory, string fileName)
    {
        if (projectDirectory is null) return (null, null);
        var info = new FileInfo(Path.Combine(projectDirectory, ProjectStore.ImagesFolderName, fileName));
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (null, null);
    }

    // --- Session state: the active selection (never saved with the project) ---

    public Selection? ActiveSelection { get; private set; }

    public void SetSelection(Selection? selection)
    {
        ActiveSelection = selection;
        SelectionChanged?.Invoke();
    }

    // --- Selection clipboard (session state; the system pasteboard is App territory) ---

    /// <summary>The copied pixels, the document offset they were taken from, and the id
    /// that ties them to what the App put on the system clipboard.</summary>
    public sealed record ClipboardPixels(Surface Pixels, PointD Origin, Guid Id);

    private ClipboardPixels? pixelClipboard;

    /// <summary>The copy region: the selection's bounds (floor/ceil with tolerance, as on
    /// the Mac), or the whole canvas without a selection. Null when degenerate.</summary>
    public RectI? SelectionCopyRegion()
    {
        var manifest = Snapshot.Manifest;
        var bounds = ActiveSelection?.Bounds ?? new RectI(0, 0, manifest.Width, manifest.Height);
        const double tolerance = 0.001;
        int minX = (int)Math.Floor(bounds.X + tolerance);
        int minY = (int)Math.Floor(bounds.Y + tolerance);
        int width = (int)Math.Ceiling(bounds.X + bounds.Width - tolerance) - minX;
        int height = (int)Math.Ceiling(bounds.Y + bounds.Height - tolerance) - minY;
        int x0 = Math.Clamp(minX, 0, manifest.Width);
        int y0 = Math.Clamp(minY, 0, manifest.Height);
        int x1 = Math.Clamp(minX + width, 0, manifest.Width);
        int y1 = Math.Clamp(minY + height, 0, manifest.Height);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        return new RectI(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// The active layer's pixels exactly as they sit on the canvas, clipped to the selection
    /// (soft edges kept), or the whole canvas without one. Stored for Paste; not undoable.
    /// </summary>
    public ClipboardPixels? CopySelection(Guid layerId)
    {
        if (SelectionCopyRegion() is not { } region) return null;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return null;
        if (!Snapshot.Images.TryGetValue(layerId, out var source)) return null;
        var pixels = RenderRegionPixels(layer, source, region);
        var result = new ClipboardPixels(pixels, new PointD(region.X, region.Y), Guid.NewGuid());
        pixelClipboard = result;
        return result;
    }

    /// <summary>Renders the layer's pixels over the region and multiplies the selection's
    /// coverage through (premultiplied channels shrinking with alpha) — the shared body of
    /// Copy and Layer via Copy.</summary>
    private Surface RenderRegionPixels(ProjectLayerRecord layer, Surface source, RectI region)
    {
        var pixels = new Surface(region.Width, region.Height);
        var placed = new LayerTransform
        {
            Origin = new PointD(layer.Transform.Origin.X - region.X, layer.Transform.Origin.Y - region.Y),
            Size = layer.Transform.Size,
            Rotation = layer.Transform.Rotation,
            FlipX = layer.Transform.FlipX,
            FlipY = layer.Transform.FlipY,
            Sampling = layer.Transform.Sampling,
        };
        Resampler.Draw(source, placed, pixels, 0, 0, region.Width, region.Height);

        if (ActiveSelection is { } selection)
        {
            for (int y = 0; y < region.Height; y++)
            {
                int docY = region.Y + y;
                int row = y * region.Width;
                for (int x = 0; x < region.Width; x++)
                {
                    // CoverageAt, so a feathered selection copies its soft edge through.
                    byte coverage = selection.CoverageAt(region.X + x, docY);
                    if (coverage == 255) continue;
                    int index = row + x;
                    double factor = coverage / 255.0;
                    pixels.Pixels[index + 3] = (byte)(pixels.Pixels[index + 3] * factor);
                }
            }
            // Premultiplied channels must shrink with the alpha after the coverage multiply.
            for (int i = 0; i < pixels.Pixels.Length; i += 4)
            {
                byte alpha = pixels.Pixels[i + 3];
                pixels.Pixels[i] = (byte)(pixels.Pixels[i] * alpha / 255);
                pixels.Pixels[i + 1] = (byte)(pixels.Pixels[i + 1] * alpha / 255);
                pixels.Pixels[i + 2] = (byte)(pixels.Pixels[i + 2] * alpha / 255);
            }
        }
        return pixels;
    }

    /// <summary>
    /// Copy, then clear the selected pixels on the source layer — one undoable step for the
    /// clear (the copy itself is not undoable, as on the Mac). Null when there is nothing
    /// to copy.
    /// </summary>
    public ClipboardPixels? CutSelection(Guid layerId)
    {
        if (ActiveSelection is null) return null;
        if (CopySelection(layerId) is not { } copied) return null;
        if (SelectionCopyRegion() is not { } region) return copied;
        var layer = Find(layerId);
        if (layer is null || !Snapshot.Images.TryGetValue(layerId, out var source)) return copied;

        BeginChange();
        var transform = layer.Transform;
        // Surfaces never mutate: the cut clears a fresh copy that replaces the layer's entry,
        // so the memento's reference to the untouched surface is what Undo restores.
        var cut = new Surface(source.Width, source.Height, (byte[])source.Pixels.Clone(), source.Stride);
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                // Doc pixel → unit within the layer transform → source pixel. Axis-aligned
                // layers only; a rotated layer's clear is approximate, as the inverse map
                // of Point(unit) is not linear in the rotation.
                var unit = new PointD((region.X + x + 0.5 - transform.Origin.X) / transform.Size.Width,
                                      (region.Y + y + 0.5 - transform.Origin.Y) / transform.Size.Height);
                if (unit.X is < 0 or >= 1 || unit.Y is < 0 or >= 1) continue;
                int sourceX = (int)(unit.X * cut.Width);
                int sourceY = (int)(unit.Y * cut.Height);
                int index = sourceY * cut.Stride + sourceX * 4;
                cut.Pixels[index] = 0;
                cut.Pixels[index + 1] = 0;
                cut.Pixels[index + 2] = 0;
                cut.Pixels[index + 3] = 0;
            }
        }
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [layerId] = cut },
            Masks = Snapshot.Masks,
        };
        layer.Text = null;
        EndChange();
        return copied;
    }

    /// <summary>
    /// Copy Merged (Shift-Cmd-C on the Mac): every visible layer composited exactly as the
    /// canvas shows them, clipped to the selection — or the whole canvas without one. Stored
    /// for Paste; not undoable.
    /// </summary>
    public ClipboardPixels? CopyMergedSelection()
    {
        if (SelectionCopyRegion() is not { } region) return null;
        var composited = new CanvasCompositor(Snapshot).Render();
        var pixels = new Surface(region.Width, region.Height);
        for (int y = 0; y < region.Height; y++)
        {
            int sourceRow = (region.Y + y) * composited.Stride + region.X * 4;
            int targetRow = y * pixels.Stride;
            System.Array.Copy(composited.Pixels, sourceRow, pixels.Pixels, targetRow, region.Width * 4);
        }

        if (ActiveSelection is { } selection)
        {
            for (int y = 0; y < region.Height; y++)
            {
                int docY = region.Y + y;
                int row = y * region.Width;
                for (int x = 0; x < region.Width; x++)
                {
                    // CoverageAt, so a feathered selection copies its soft edge through.
                    byte coverage = selection.CoverageAt(region.X + x, docY);
                    if (coverage == 255) continue;
                    int index = row + x;
                    double factor = coverage / 255.0;
                    pixels.Pixels[index + 3] = (byte)(pixels.Pixels[index + 3] * factor);
                }
            }
            // Premultiplied channels must shrink with the alpha after the coverage multiply.
            for (int i = 0; i < pixels.Pixels.Length; i += 4)
            {
                byte alpha = pixels.Pixels[i + 3];
                pixels.Pixels[i] = (byte)(pixels.Pixels[i] * alpha / 255);
                pixels.Pixels[i + 1] = (byte)(pixels.Pixels[i + 1] * alpha / 255);
                pixels.Pixels[i + 2] = (byte)(pixels.Pixels[i + 2] * alpha / 255);
            }
        }
        var result = new ClipboardPixels(pixels, new PointD(region.X, region.Y), Guid.NewGuid());
        pixelClipboard = result;
        return result;
    }

    /// <summary>
    /// Pastes the clipboard as a new layer above the active one, at the copied origin —
    /// pixels copied here go back exactly where they came from, as on the Mac.
    /// </summary>
    public ProjectLayerRecord? PasteClipboard()
    {
        if (pixelClipboard is not { } clip) return null;
        var record = ImportImage(clip.Pixels, "Pasted");
        record.Transform.Origin = clip.Origin;
        return record;
    }

    /// <summary>
    /// Pastes pixels copied in another app as a new layer, centered on the canvas, as on
    /// the Mac. Null when the image exceeds the surface limit.
    /// </summary>
    public ProjectLayerRecord? PasteExternal(Surface pixels)
    {
        if ((long)pixels.Width * pixels.Height > Limits.MaxSurfacePixels) return null;
        var manifest = Snapshot.Manifest;
        var record = ImportImage(pixels, "Pasted");
        record.Transform.Origin = new PointD(Math.Floor((manifest.Width - pixels.Width) / 2.0),
                                             Math.Floor((manifest.Height - pixels.Height) / 2.0));
        return record;
    }

    /// <summary>
    /// Layer via Copy (Ctrl-J): the selection's pixels become a new layer where they sit,
    /// one undoable step. Without a selection the whole layer is duplicated, as on the
    /// Mac, folders included. With a selection groups have no pixels of their own. Does not touch the
    /// pasteboard — the Mac's renderSelectedPixels doesn't either.
    /// </summary>
    public ProjectLayerRecord? LayerViaCopy(Guid id)
    {
        var layer = Find(id);
        if (layer is null) return null;
        if (ActiveSelection is null)
        {
            var duplicates = DuplicateLayers([id]);
            return duplicates.FirstOrDefault(l => l.IsGroupOrFalse == layer.IsGroupOrFalse && l.Id == Snapshot.Manifest.ActiveLayerId);
        }
        if (layer.IsGroupOrFalse) return null;
        if (SelectionCopyRegion() is not { } region) return null;
        if (!Snapshot.Images.TryGetValue(id, out var source)) return null;
        var record = ImportImage(RenderRegionPixels(layer, source, region), "Layer");
        record.Transform.Origin = new PointD(region.X, region.Y);
        return record;
    }

    /// <summary>
    /// Magic Wand: selects the pixels matching the color sampled at the canvas point — the
    /// visible composite when sampling all layers, else the active layer as it sits. Null
    /// when the point is outside the canvas or nothing matched; a replace-mode caller
    /// clears the selection then, as on the Mac.
    /// </summary>
    public Selection? MagicWandSelect(PointD point, WandSettings settings)
    {
        var manifest = Snapshot.Manifest;
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= manifest.Width || y >= manifest.Height) return null;
        var source = WandSample(settings);
        var mask = new MaskSurface(manifest.Width, manifest.Height);
        long count = WandPixels.WandMask(source, x, y, settings.SampleRadius, Math.Clamp(settings.Tolerance, 0, 255), settings.Contiguous, mask);
        return count > 0 ? Selection.FromCoverage(mask) : null;
    }

    /// <summary>The wand's sample: the composite, or the active layer drawn as it sits.</summary>
    private Surface WandSample(WandSettings settings)    {
        var manifest = Snapshot.Manifest;
        if (settings.SampleAllLayers) return new CanvasCompositor(Snapshot).Render();
        var canvas = new Surface(manifest.Width, manifest.Height);
        if (manifest.ActiveLayerId is { } active
            && Find(active) is { } layer
            && !layer.IsGroupOrFalse
            && Snapshot.Images.TryGetValue(layer.Id, out var image))
        {
            var placed = new LayerTransform
            {
                Origin = layer.Transform.Origin,
                Size = layer.Transform.Size,
                Rotation = layer.Transform.Rotation,
                FlipX = layer.Transform.FlipX,
                FlipY = layer.Transform.FlipY,
                Sampling = layer.Transform.Sampling,
            };
            Resampler.Draw(image, placed, canvas, 0, 0, manifest.Width, manifest.Height);
        }
        return canvas;
    }

    /// <summary>
    /// Select > Subject (AI): builds a selection from the registered AI segmenter's subject
    /// mask. Null when no segmenter is wired or no subject was found — callers report the
    /// difference.
    /// </summary>
    public Selection? SelectSubject()
    {
        if (AiSegmentation.Registered is not { } segmenter) return null;
        var mask = segmenter.SegmentSubject(new CanvasCompositor(Snapshot).Render());
        return mask is null ? null : Selection.FromCoverage(mask);
    }

    /// <summary>
    /// Select > Object (AI): the instance masks under the clicked point, most confident
    /// first. Empty when no segmenter is wired or nothing was found.
    /// </summary>
    public IReadOnlyList<Selection> SelectObject(PointD point)
    {
        if (AiSegmentation.Registered is not { } segmenter) return Array.Empty<Selection>();
        var masks = segmenter.SegmentObjects(new CanvasCompositor(Snapshot).Render(), point);
        return masks.Select(coverage => Selection.FromCoverage(coverage)).ToList();
    }

    /// <summary>
    /// Image &gt; Invert (PixelInvert.swift's matrix): inverts the active layer's colors
    /// while keeping alpha — in premultiplied terms each color becomes alpha − color —
    /// limited to the selection when there is one. One undoable step; false for groups
    /// or layers without pixels.
    /// </summary>
    public bool InvertPixels(Guid layerId) => InvertPixels(layerId, invertMask: false);

    /// <summary>
    /// The active selection's coverage resampled onto a layer's own pixel grid: each mask
    /// pixel samples the document point its center lands on through the layer transform.
    /// The selection lives in document space, so pixel edits that loop a layer's own pixels
    /// must go through this map — indexing the document coverage by layer coordinates filled
    /// the wrong region (or ran past it) on any layer smaller or larger than the canvas.
    /// </summary>
    private MaskSurface LayerCoverage(Selection selection, ProjectLayerRecord layer, int width, int height, LayerTransform? placement = null,
        CancellationToken cancellation = default)
    {
        var coverage = new MaskSurface(width, height);
        var transform = placement ?? layer.Transform;
        double cos = Math.Cos(transform.Radians), sin = Math.Sin(transform.Radians);
        var center = transform.Center;
        for (int py = 0; py < height; py++)
        {
            cancellation.ThrowIfCancellationRequested();
            double unitY = (py + .5) / height;
            double y = ((transform.FlipY ? 1 - unitY : unitY) - .5) * transform.Size.Height;
            for (int px = 0; px < width; px++)
            {
                double unitX = (px + .5) / width;
                double x = ((transform.FlipX ? 1 - unitX : unitX) - .5) * transform.Size.Width;
                int docX = (int)Math.Floor(center.X + x * cos - y * sin);
                int docY = (int)Math.Floor(center.Y + x * sin + y * cos);
                coverage.Pixels[py * width + px] = docX >= 0 && docY >= 0
                    && docX < Snapshot.Manifest.Width && docY < Snapshot.Manifest.Height
                    ? selection.CoverageAt(docX, docY)
                    : (byte)0;
            }
        }
        return coverage;
    }

    /// <summary>
    /// The active selection's coverage resampled onto a layer's own pixel grid: each mask
    /// pixel samples the document point its center lands on through the layer transform.
    /// The selection lives in document space, so pixel edits that loop a layer's own pixels
    /// must go through this map — indexing the document coverage by layer coordinates filled
    /// the wrong region (or ran past it) on any layer smaller or larger than the canvas.
    /// </summary>
    private MaskSurface LayerCoverage(ProjectLayerRecord layer, int width, int height) =>
        ActiveSelection is { } selection ? LayerCoverage(selection, layer, width, height) : new MaskSurface(width, height);

    /// <summary>
    /// The active selection mapped onto a layer's own pixel grid, or null when nothing is selected.
    /// A whole-layer worker (the dither) needs this to stay inside the selection, and it runs off the
    /// UI thread, so the mapping is handed over as a plain surface instead of the selection object.
    /// </summary>
    public MaskSurface? SelectionCoverageForLayer(Guid layerId, int width, int height) =>
        ActiveSelection is { } selection && Find(layerId) is { } layer ? LayerCoverage(selection, layer, width, height) : null;

    /// <summary>The same coverage sampled through an EXPLICIT placement instead of the layer's own one:
    /// a spreading filter works on a grid larger than the layer (GrownLayerGrid), and its clip has to
    /// say what the selection covers on THAT grid — the grown placement describes where this very grid
    /// sits on the document, so its units are the grid's. The Mac builds exactly this mapping for the
    /// grown image (prepare(from: grownImage, placed: expanded), Filters.swift:418) and blends the run
    /// through it (Filters.swift:268-269, :183-186).</summary>
    public MaskSurface? SelectionCoverageOnGrid(Guid layerId, LayerTransform placement, int width, int height) =>
        ActiveSelection is { } selection && Find(layerId) is { } layer
            ? LayerCoverage(selection, layer, width, height, placement) : null;

    /// <summary>
    /// Inverts the layer's pixels or mask — same body, different plane. The selection
    /// (when one exists) is mapped through the layer transform first.
    /// </summary>
    public bool InvertPixels(Guid layerId, bool invertMask)
    {
        if (!CanEditPixels(layerId, invertMask)) return false;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (invertMask)
        {
            if (layer.MaskFile is null || !Snapshot.Masks.TryGetValue(layerId, out var stored)) return false;
            // A uniform 1×1 mask travels by identity through a warp (DistortWarp.WarpMask returns it
            // unchanged, TransformEdit.Distort assigns it straight), so two layer ids can hold the
            // same object. Inverting writes a copy instead of flipping the other layer's mask too.
            var mask = stored.CloneForEditing();
            BeginChange();
            for (int i = 0; i < mask.Pixels.Length; i++) mask.Pixels[i] = (byte)(255 - mask.Pixels[i]);
            mask.MarkModified();
            Snapshot = new ProjectSnapshot
            {
                Manifest = Snapshot.Manifest,
                Images = Snapshot.Images,
                Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [layerId] = mask },
            };
            EndChange();
            return true;
        }
        if (!Snapshot.Images.TryGetValue(layerId, out var image)) return false;
        var inverted = new Surface(image.Width, image.Height, (byte[])image.Pixels.Clone(), image.Stride);
        MaskSurface? coverage = ActiveSelection is null ? null : LayerCoverage(layer, image.Width, image.Height);
        for (int y = 0; y < inverted.Height; y++)
        {
            int row = y * inverted.Stride;
            int coverageRow = y * (coverage?.Width ?? 0);
            for (int x = 0; x < inverted.Width; x++)
            {
                int index = row + x * 4;
                if (coverage is not null
                    && (x >= coverage.Width || y >= coverage.Height || coverage.Pixels[coverageRow + x] == 0)) continue;
                byte alpha = inverted.Pixels[index + 3];
                for (int channel = 0; channel < 3; channel++)
                {
                    inverted.Pixels[index + channel] = (byte)(alpha - inverted.Pixels[index + channel]);
                }
            }
        }
        return ReplaceLayerImage(layerId, inverted, null, nameof(InvertPixels));
    }

    /// <summary>
    /// Edit &gt; Fill with the foreground or background color (SelectionEdits.swift's
    /// `fillSelection`): the color comes in over the layer's own pixels with the selection's
    /// coverage as its opacity, so a feathered edge stays feathered and no selection fills the
    /// whole layer. One undoable step; a blank layer acquires its first raster here.
    /// </summary>
    public bool FillPixels(Guid layerId, byte red, byte green, byte blue)
    {
        if (!CanEditPixels(layerId)) return false;
        var layer = Find(layerId);
        if (layer is null || ActiveSelection is { IsEmpty: true } || RasterForPainting(layer) is not { } image) return false;
        var filled = new Surface(image.Width, image.Height, (byte[])image.Pixels.Clone(), image.Stride);
        var coverage = ActiveSelection is { } selection ? LayerCoverage(selection, layer, image.Width, image.Height) : null;
        for (int y = 0; y < filled.Height; y++)
        {
            int row = y * filled.Stride;
            for (int x = 0; x < filled.Width; x++)
            {
                // Premultiplied source-over: the fill arrives already carrying `strength` as its
                // alpha, so its color scales by the coverage but its alpha adds in full; whatever
                // the fill does not cover stays.
                byte strength = CoverageAt(coverage, x, y);
                if (strength == 0) continue;
                double over = strength / 255.0;
                double keep = 1 - over;
                int index = row + x * 4;
                filled.Pixels[index] = (byte)RoundedMix(red, filled.Pixels[index], over, keep);
                filled.Pixels[index + 1] = (byte)RoundedMix(green, filled.Pixels[index + 1], over, keep);
                filled.Pixels[index + 2] = (byte)RoundedMix(blue, filled.Pixels[index + 2], over, keep);
                filled.Pixels[index + 3] = (byte)Math.Round(strength + filled.Pixels[index + 3] * keep, MidpointRounding.AwayFromZero);
            }
        }
        return ReplaceLayerImage(layerId, filled, null, nameof(FillPixels));
    }

    /// <summary>
    /// Fill on the mask plane: the mask is gray-scale, so the palette's red channel is the level
    /// (Mac's `CGColor(gray: value.red)`). A uniform 1×1 mask cannot express a partial selection,
    /// so a selected edit first expands it onto the layer's pixel grid. Coverage uses the mask placement.
    /// The stored mask is edited through a copy — a solid mask is shared by identity with other layers.
    /// </summary>
    public bool FillMask(Guid layerId, byte level) => FillMask(layerId, level, default);

    public bool FillMask(Guid layerId, byte level, CancellationToken cancellation)
    {
        if (!CanEditPixels(layerId, onMask: true)) return false;
        cancellation.ThrowIfCancellationRequested();
        var layer = Find(layerId);
        if (layer is null || layer.MaskFile is null) return false;
        if (!Snapshot.Masks.TryGetValue(layerId, out var stored)) return false;
        if (ActiveSelection is { IsEmpty: true }) return false;
        MaskSurface editable;
        if (stored.Width == 1 && stored.Height == 1 && ActiveSelection is not null)
        {
            var image = Snapshot.Images.GetValueOrDefault(layerId);
            editable = new MaskSurface(image?.Width ?? Math.Max(1, (int)Math.Round(layer.Transform.Size.Width)),
                image?.Height ?? Math.Max(1, (int)Math.Round(layer.Transform.Size.Height)));
            Array.Fill(editable.Pixels, stored.Pixels[0]);
        }
        else editable = stored.CloneForEditing();
        var coverage = ActiveSelection is { } selection
            ? LayerCoverage(selection, layer, editable.Width, editable.Height, layer.MaskPlacement ?? layer.Transform, cancellation)
            : null;
        bool changed = false;
        for (int i = 0; i < editable.Pixels.Length; i++)
        {
            int x = i % editable.Width;
            if (x == 0) cancellation.ThrowIfCancellationRequested();
            byte strength = CoverageAt(coverage, x, i / editable.Width);
            if (strength == 0) continue;
            double over = strength / 255.0;
            byte value = (byte)RoundedMix(level, editable.Pixels[i], over, 1 - over);
            changed |= value != editable.Pixels[i];
            editable.Pixels[i] = value;
        }
        cancellation.ThrowIfCancellationRequested();
        if (!changed) return false;
        BeginChange();
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [layerId] = editable },
        };
        editable.MarkModified();
        EndChange();
        return true;
    }

    /// <summary>
    /// The selection's coverage for one layer pixel, zero outside the mapped coverage grid; a null
    /// coverage means "no selection", which fills everywhere.
    /// </summary>
    private static byte CoverageAt(MaskSurface? coverage, int x, int y) =>
        coverage is null ? (byte)255
        : x < coverage.Width && y < coverage.Height ? coverage.Pixels[y * coverage.Width + x]
        : (byte)0;

    private static int RoundedMix(byte source, byte destination, double over, double keep) =>
        (int)Math.Round(source * over + destination * keep, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Flip the named layers across one axis (macOS LayerFlip.swift:24): one layer mirrors about its
    /// own middle, several about the middle of the box around them, and a linked mask travels with
    /// its layer while an unlinked one stays where it is. One undoable step.
    /// </summary>
    public bool FlipLayers(IReadOnlyList<Guid> layerIds, bool horizontally)
    {
        if (layerIds.Any(id => !CanEditLayer(id, LayerLocks.Position, branch: true))) return false;
        var targets = layerIds.Distinct().Select(Find).OfType<ProjectLayerRecord>().ToList();
        if (targets.Count == 0) return false;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var layer in targets)
        {
            foreach (var corner in new[] { new PointD(0, 0), new(1, 0), new(1, 1), new(0, 1) })
            {
                var point = layer.Transform.Point(corner);
                left = Math.Min(left, point.X); right = Math.Max(right, point.X);
                top = Math.Min(top, point.Y); bottom = Math.Max(bottom, point.Y);
            }
        }
        double axis = horizontally ? (left + right) / 2 : (top + bottom) / 2;
        var flippedMap = targets.ToDictionary(layer => layer, layer => layer.Transform.Mirrored(horizontally, axis));
        if (flippedMap.Values.Any(transform => !transform.IsValid)) return false;

        BeginChange();
        foreach (var (layer, flipped) in flippedMap)
        {
            var previous = layer.Transform;
            if (layer.MaskLinked != false && layer.MaskPlacement is { } placement)
                layer.MaskPlacement = placement.Following(previous, flipped);
            layer.Transform = flipped;
        }
        EndChange();
        return true;
    }

    /// <summary>
    /// Flip the whole canvas across its own middle (macOS LayerFlip.swift:55): every layer and every
    /// placed mask mirrors — linked or not, since the canvas moves under them together — and the
    /// selection and guides mirror with them, as one undoable step.
    /// </summary>
    public bool FlipCanvas(bool horizontally)
    {
        if (layerLocks.Count != 0 && Snapshot.Manifest.Layers.Any(l => GetLayerLocks(l.Id) != LayerLocks.None)) return false;
        var manifest = Snapshot.Manifest;
        double axis = horizontally ? manifest.Width / 2.0 : manifest.Height / 2.0;
        var flippedMap = manifest.Layers.ToDictionary(layer => layer, layer => layer.Transform.Mirrored(horizontally, axis));
        // Nothing is written until every placement — the layers' and the masks' — still fits the
        // limits, so a rejected flip leaves no half-mirrored document behind.
        if (flippedMap.Values.Any(transform => !transform.IsValid)) return false;
        if (manifest.Layers.Any(layer => layer.MaskPlacement is { } placement
            && !placement.Mirrored(horizontally, axis).IsValid)) return false;

        BeginChange();
        foreach (var (layer, flipped) in flippedMap)
        {
            if (layer.MaskPlacement is { } placement) layer.MaskPlacement = placement.Mirrored(horizontally, axis);
            layer.Transform = flipped;
        }
        if (manifest.Guides is { Count: > 0 } guides)
        {
            manifest.Guides = guides.Select(guide =>
            {
                bool vertical = guide.Axis == GuideAxis.Vertical;
                bool mirrored = vertical == horizontally;
                return new CanvasGuide
                {
                    Id = guide.Id,
                    Axis = guide.Axis,
                    Position = mirrored ? (vertical ? manifest.Width : manifest.Height) - guide.Position : guide.Position,
                };
            }).ToList();
        }
        if (ActiveSelection is { } selection) SetSelection(selection.Mirrored(horizontally));
        EndChange();
        return true;
    }

    /// <summary>
    /// Color Range: selects every pixel in the composite near the include colors and away
    /// from the exclude ones (flat RGB triplets, 0–200 fuzziness, invert to take the
    /// complement). Null without include colors or when nothing matched.
    /// </summary>
    public Selection? ColorRangeSelect(byte[] include, byte[] exclude, int fuzziness, bool invert)
    {
        if (include.Length < 3) return null;
        var manifest = Snapshot.Manifest;
        var sample = new CanvasCompositor(Snapshot).Render();
        var mask = new MaskSurface(manifest.Width, manifest.Height);
        long count = WandPixels.ColorRangeMask(sample, include, exclude, Math.Clamp(fuzziness, 0, 200), invert, mask);
        return count > 0 ? Selection.FromCoverage(mask) : null;
    }

    /// <summary>
    /// Clears the pixels under `mask` on the layer (Remove Subject's write side): a fresh
    /// surface with the masked area zeroed, one undoable step. False for groups or layers
    /// without pixels.
    /// </summary>
    public bool CutSelectionMasked(Guid layerId, Selection mask) => CutSelectionMasked(layerId, mask, default);

    public bool CutSelectionMasked(Guid layerId, Selection mask, CancellationToken cancellation)
    {
        if (!CanEditPixels(layerId) || KeepsAlpha(layerId)) return false;
        cancellation.ThrowIfCancellationRequested();
        if (mask.IsEmpty) return false;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (!Snapshot.Images.TryGetValue(layerId, out var image)) return false;
        var cleared = new Surface(image.Width, image.Height, (byte[])image.Pixels.Clone(), image.Stride);
        // The selection arrives in document space; map it onto the layer's own pixel grid.
        var coverage = LayerCoverage(mask, layer, cleared.Width, cleared.Height, cancellation: cancellation);
        bool changed = false;
        for (int y = 0; y < cleared.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            int row = y * cleared.Stride;
            int coverageRow = y * coverage.Width;
            for (int x = 0; x < cleared.Width; x++)
            {
                if (x >= coverage.Width || y >= coverage.Height) continue;
                byte alpha = coverage.Pixels[coverageRow + x];
                if (alpha == 0) continue;
                int index = row + x * 4;
                changed |= (cleared.Pixels[index] | cleared.Pixels[index + 1] | cleared.Pixels[index + 2] | cleared.Pixels[index + 3]) != 0;
                if (alpha == 255)
                {
                    cleared.Pixels[index] = 0;
                    cleared.Pixels[index + 1] = 0;
                    cleared.Pixels[index + 2] = 0;
                    cleared.Pixels[index + 3] = 0;
                }
                else
                {
                    // Partial coverage removes proportionally, like an eraser at that strength.
                    double keep = 1 - alpha / 255.0;
                    for (int channel = 0; channel < 4; channel++) cleared.Pixels[index + channel] = (byte)(cleared.Pixels[index + channel] * keep);
                }
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return changed && ReplaceLayerImage(layerId, cleared);
    }

    /// <summary>
    /// Edit > Content-Aware Fill: fills the current selection on the layer from the
    /// unselected opaque pixels around it (ContentFill.c's coherent patch synthesis),
    /// one undoable step. False when nothing can donate — a smaller selection with some
    /// surrounding image is needed.
    /// </summary>
    public bool ContentFillSelection(Guid layerId)
    {
        if (!CanEditPixels(layerId)) return false;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (!Snapshot.Images.TryGetValue(layerId, out var image)) return false;
        // The selection is document space; ContentFill loops the layer's own pixels, so the
        // coverage goes through the layer transform (a canvas-sized mask indexed by layer
        // coordinates misfilled layers smaller than the canvas and overflowed larger ones).
        MaskSurface coverage;
        if (ActiveSelection is { } selection)
        {
            coverage = LayerCoverage(selection, layer, image.Width, image.Height);
        }
        else
        {
            coverage = new MaskSurface(image.Width, image.Height);
            Array.Fill(coverage.Pixels, (byte)255);
        }
        var filled = new Surface(image.Width, image.Height, (byte[])image.Pixels.Clone(), image.Stride);
        if (!ContentFill.Fill(filled, coverage)) return false;
        return ReplaceLayerImage(layerId, filled, null, nameof(FillPixels));
    }

    /// <summary>The composite as the color-range panel's sampling image, taken once at open.</summary>
    public Surface ColorRangeSample() => new CanvasCompositor(Snapshot).Render();

    /// <summary>
    /// Clone Stamp: one continuous-tip dab from `source` to `target`, in document pixels,
    /// from the original layer or the visible composite. Source-over blending follows the
    /// brush's coverage and opacity; pixels outside the sample are left alone.
    /// </summary>
    public bool CloneStampApply(Guid layerId, PointD source, PointD target, double radius, double hardness, bool sampleAllLayers, object? coalesceKey = null, double opacity = 1)
    {
        if (!CanEditPixels(layerId)) return false;
        if (!BeginCloneStroke(layerId, source, target, radius, hardness, sampleAllLayers, coalesceKey ?? new object(), opacity)) return false;
        EndCloneStroke();
        return true;
    }

    // --- Clone Stamp session: a working copy stamped in place, swapped in at stroke start ---
    // A whole stroke mutates one buffer instead of cloning the layer's full surface for every
    // stamp (and recompositing the canvas for every all-layers stamp), the WarpStroke model.

    private Surface? cloneWorking;
    private Surface? cloneSample, cloneBase;
    private ImageStrokeGrid? cloneGrid;
    private RectI cloneImageBounds;
    private PointD? cloneLastTarget;
    private Guid? cloneLayerId;
    private TileStroke? cloneCoverage;
    private PointD cloneStrokeOffset;
    private bool cloneCanvasSample;

    /// <summary>
    /// Clone Stamp stroke start: opens the stroke's undo step and swaps in a working copy of
    /// the layer. The stamps that follow mutate that one buffer. Sampling all layers reads a
    /// composite frozen at stroke start; sampling the layer reads its original pixels. The
    /// continuous tip shares the brush's opacity cap, selection clip and placement mapping.
    /// </summary>
    public bool BeginCloneStroke(Guid layerId, PointD source, PointD target, double radius, double hardness, bool sampleAllLayers, object strokeKey, double opacity = 1)
    {
        if (!CanEditPixels(layerId)) return false;
        var layer = Find(layerId);
        if (WhyPaintRefused(layerId, onMask: false) is not null || layer is null || !layer.Transform.IsValid
            || !double.IsFinite(source.X) || !double.IsFinite(source.Y) || !double.IsFinite(target.X) || !double.IsFinite(target.Y)
            || !double.IsFinite(radius) || radius <= 0 || !double.IsFinite(hardness) || !double.IsFinite(opacity) || opacity <= 0) return false;
        if (!sampleAllLayers && !Snapshot.Images.ContainsKey(layerId)) return false;
        if (RasterForPainting(layer) is not { } destination) return false;
        var grid = ImageStrokeGrid.Create(Snapshot, layer, destination, new(radius, hardness, opacity, 0, 0, 0));
        var coverage = grid is null
            ? new TileStroke(new(radius, hardness, opacity, 0, 0, 0), layer.Transform, destination.Width, destination.Height)
            : new TileStroke(new(radius, hardness, opacity, 0, 0, 0), grid.Placement, grid.Width, grid.Height);
        var touched = coverage.SweepSegment(target);
        if (touched.Width <= 0 || touched.Height <= 0) return false;
        if (grid is null && KeepsAlpha(layerId)) return false;
        if (grid is not null && !grid.CanMaterialize(grid.CandidateBounds(grid.SourceBounds, target, target))) return false;
        cloneSample = sampleAllLayers ? new CanvasCompositor(Snapshot).Render() : destination;
        cloneCanvasSample = sampleAllLayers;
        cloneStrokeOffset = new(source.X - target.X, source.Y - target.Y);
        Surface working; RectI bounds;
        if (grid is null)
        {
            cloneBase = destination; cloneGrid = null; cloneCoverage = coverage;
            working = destination.CloneForEditing(); bounds = new RectI(0, 0, working.Width, working.Height);
            ReplaceLayerImage(layerId, working, strokeKey); // opens the undo step over the original
        }
        else
        {
            cloneGrid = grid; cloneBase = destination; cloneCoverage = coverage;
            bounds = KeepsAlpha(layerId) || !CanEditLayer(layerId, LayerLocks.Position) ? grid.SourceBounds : RectI.Union(grid.SourceBounds, touched); // the tip's reach, not a snapped tile
            working = grid.Materialize(bounds);
            cloneImageBounds = bounds; cloneWorking = working;
            OpenGrowingPlane(layer, grid, bounds, working, strokeKey);
        }
        cloneLayerId = layerId;
        if (grid is null) coverage.PaintCloneRegion(working, destination, cloneSample, touched, ActiveSelection, cloneStrokeOffset, cloneCanvasSample);
        else { coverage.PaintCloneGrid(working, grid, cloneSample, bounds, touched, ActiveSelection, cloneStrokeOffset, cloneCanvasSample); ProtectGridAlpha(layerId, working, grid, bounds, touched); }
        PixelsChanged?.Invoke();
        return true;
    }

    /// <summary>Continues the clone sweep. Source offset and tip settings stay fixed at
    /// stroke start; the source/radius/hardness arguments remain for existing callers.</summary>
    public bool CloneStampAppend(Guid layerId, PointD source, PointD target, double radius, double hardness)
    {
        if (cloneWorking is not { } stamped || cloneLayerId != layerId || cloneCoverage is not { } coverage
            || cloneSample is not { } sample || cloneBase is not { } original
            || !double.IsFinite(target.X) || !double.IsFinite(target.Y)) return false;
        var growing = cloneGrid;
        if (growing is not null && !growing.CanMaterialize(growing.CandidateBounds(cloneImageBounds, cloneLastTarget ?? target, target))) return false;
        coverage.RetireTail(); var touched = coverage.SweepSegment(target);
        if (touched.Width <= 0 || touched.Height <= 0) return false;
        if (growing is null) coverage.PaintCloneRegion(stamped, original, sample, touched, ActiveSelection, cloneStrokeOffset, cloneCanvasSample);
        else
        {
            cloneLastTarget = target;
            var bounds = KeepsAlpha(layerId) || !CanEditLayer(layerId, LayerLocks.Position) ? cloneImageBounds : RectI.Union(cloneImageBounds, touched);
            bool grows = bounds != cloneImageBounds;
            if (grows) stamped = growing.Materialize(bounds, stamped, cloneImageBounds);
            cloneWorking = stamped; cloneImageBounds = bounds;
            coverage.PaintCloneGrid(stamped, growing, sample, bounds, touched, ActiveSelection, cloneStrokeOffset, cloneCanvasSample);
            ProtectGridAlpha(layerId, stamped, growing, bounds, touched);
            if (grows)
            {
                if (Find(layerId) is { } moved) moved.Transform = growing.TransformFor(bounds);
                PublishGrowingImage(layerId, stamped); Changed?.Invoke();
            }
        }
        PixelsChanged?.Invoke();
        return true;
    }

    /// <summary>Clone Stamp stroke end: closes the stroke's undo step and drops the session.</summary>
    public void EndCloneStroke()
    {
        if (cloneLayerId is { } grown && cloneGrid is { } grownPlane && cloneWorking is { } stampedPlane) CommitGrowingPlane(grown, grownPlane, cloneImageBounds, stampedPlane);
        cloneGrid = null; cloneLastTarget = null;
        cloneWorking = null;
        cloneSample = null;
        cloneBase = null; cloneCoverage = null;
        cloneLayerId = null;
        strokeImage = null; strokeLayerId = null;
        coalesceKey = null;
    }

    /// <summary>The RGBA color of the sample at the canvas point; null outside it.</summary>
    public static (byte R, byte G, byte B, byte A)? ColorAt(Surface sample, PointD point)
    {
        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= sample.Width || y >= sample.Height) return null;
        int index = y * sample.Stride + x * 4;
        return (sample.Pixels[index], sample.Pixels[index + 1], sample.Pixels[index + 2], sample.Pixels[index + 3]);
    }


    // --- Operations ---

    /// <summary>What the trim measures against: the transparent pixels' bounds, or the
    /// canvas corner pixel's color (ImageTrim.swift's TrimBasedOn).</summary>
    public enum TrimBasedOn
    {
        TransparentPixels,
        TopLeftColor,
        BottomRightColor,
    }

    /// <summary>Image &gt; Trim options: which edges to cut and how close a pixel may be to
    /// the reference color and still count as trimmable.</summary>
    public sealed record TrimOptions
    {
        public TrimBasedOn BasedOn { get; init; } = TrimBasedOn.TransparentPixels;
        public bool Top { get; init; } = true;
        public bool Bottom { get; init; } = true;
        public bool Left { get; init; } = true;
        public bool Right { get; init; } = true;
        public int Tolerance { get; init; }
    }

    /// <summary>
    /// Image &gt; Trim: renders the composite, works out the trim rectangle (transparent
    /// bounds or the corner pixel's color within tolerance, per the enabled edges) and
    /// moves the canvas bounds to it — one undoable step. False when there is nothing to
    /// trim (everything matched, or the kept edges leave an empty rectangle).
    /// </summary>
    public bool TrimCanvas(TrimOptions options)
    {
        var composite = new CanvasCompositor(Snapshot).Render();
        if (CalculateTrimRect(composite, options) is not { } rect) return false;
        return SetCanvasBounds(rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>The trim rectangle over the composite — ImageTrim.swift's calculateTrimRect.</summary>
    private static RectI? CalculateTrimRect(Surface image, TrimOptions options)
    {
        int width = image.Width, height = image.Height;
        int left, top, right, bottom;
        if (options.BasedOn == TrimBasedOn.TransparentPixels)
        {
            left = width; top = height; right = 0; bottom = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * image.Stride;
                for (int x = 0; x < width; x++)
                {
                    if (image.Pixels[row + x * 4 + 3] == 0) continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
            if (right == 0) return null; // fully transparent
        }
        else
        {
            int sampleX = options.BasedOn == TrimBasedOn.TopLeftColor ? 0 : width - 1;
            int sampleY = options.BasedOn == TrimBasedOn.TopLeftColor ? 0 : height - 1;
            int sampleOffset = sampleY * image.Stride + sampleX * 4;
            int targetR = image.Pixels[sampleOffset];
            int targetG = image.Pixels[sampleOffset + 1];
            int targetB = image.Pixels[sampleOffset + 2];
            int targetA = image.Pixels[sampleOffset + 3];
            int tolerance = options.Tolerance;

            left = width; top = height; right = 0; bottom = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * image.Stride;
                int first = 0;
                while (first < width && MatchesColor(image.Pixels, row + first * 4, targetR, targetG, targetB, targetA, tolerance)) first++;
                if (first == width) continue; // the whole row matches
                int last = width;
                while (last > first && MatchesColor(image.Pixels, row + (last - 1) * 4, targetR, targetG, targetB, targetA, tolerance)) last--;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y + 1);
            }
            if (right == 0) return null; // every pixel matched the sample color
        }

        int minX = options.Left ? left : 0;
        int minY = options.Top ? top : 0;
        int maxX = options.Right ? right : width;
        int maxY = options.Bottom ? bottom : height;
        if (maxX <= minX || maxY <= minY) return null;
        return new RectI(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool MatchesColor(byte[] pixels, int index, int targetR, int targetG, int targetB, int targetA, int tolerance)
    {
        return Math.Abs(pixels[index] - targetR) <= tolerance
            && Math.Abs(pixels[index + 1] - targetG) <= tolerance
            && Math.Abs(pixels[index + 2] - targetB) <= tolerance
            && Math.Abs(pixels[index + 3] - targetA) <= tolerance;
    }

    /// <summary>
    /// Moves the canvas bounds: the document becomes width×height with its top-left at
    /// (x, y) in the old coordinates. Every layer keeps its pixels and shifts with the
    /// canvas, guides follow, and smaller bounds crop — one undoable step for both
    /// Canvas Size and Crop.
    /// </summary>
    public bool SetCanvasBounds(int x, int y, int width, int height)
    {
        if (layerLocks.Count != 0 && Snapshot.Manifest.Layers.Any(l => !CanEditLayer(l.Id, LayerLocks.Position))) return false;
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide) return false;
        BeginChange();
        var manifest = Snapshot.Manifest;
        foreach (var layer in manifest.Layers)
        {
            layer.Transform.Origin = new PointD(layer.Transform.Origin.X - x, layer.Transform.Origin.Y - y);
            if (layer.MaskPlacement is { } placement)
            {
                placement.Origin = new PointD(placement.Origin.X - x, placement.Origin.Y - y);
            }
        }
        if (manifest.Guides is { } guides)
        {
            foreach (var guide in guides)
            {
                guide.Position -= guide.Axis == GuideAxis.Horizontal ? y : x;
            }
        }
        manifest.Width = width;
        manifest.Height = height;
        EndChange();
        return true;
    }



    /// <summary>
    /// Adds a text layer above the active one: the style rasterizes to the layer's pixels, the
    /// style itself rides in the manifest so a later slice can re-edit it, and `origin` places the
    /// layer's top-left (the Mac's applyText → addPixelLayer). Null when the style is invalid or
    /// its rasterization does not fit — no undo step is left behind. One undoable step.
    /// <paramref name="placement"/> is the box a handle drag made of a brand-new draft: the original keeps the
    /// draft's transform for boxed text (TypeTool.swift:288-297), so a box dragged before the first commit
    /// lands where it was dragged to instead of at the click with whatever size the words measure.
    /// </summary>
    public ProjectLayerRecord? AddTextLayer(LayerTextStyle style, PointD origin, LayerTransform? placement = null)
    {
        if (style is not { IsValid: true }) return null;
        Surface image;
        try
        {
            image = TextRasterizer.Rasterize(style);
        }
        catch (ProjectFormatException)
        {
            return null;
        }
        var record = ImportImage(image, TextLayerName(style.Content), style);
        if (placement is not null && placement.IsValid
            && placement.Size.Width is > 0 and < 300_000 && placement.Size.Height is > 0 and < 300_000)
            record.Transform = new LayerTransform
            {
                Origin = placement.Origin, Size = placement.Size, Rotation = placement.Rotation,
                FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling,
            };
        else record.Transform.Origin = origin;
        return record;
    }

    /// <summary>The uppermost text layer under the pointer, ignoring hidden folder branches,
    /// as the Mac's beginText(at:) does.</summary>
    public ProjectLayerRecord? TextLayerAt(PointD point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return null;
        var order = DrawOrder.Resolve(Snapshot.Manifest.Layers);
        var byId = Snapshot.Manifest.Layers.ToDictionary(layer => layer.Id);
        foreach (var id in order.Drawn.Reverse())
        {
            var layer = byId[id];
            if (layer.Text is not null && Snapshot.Images.ContainsKey(id) && layer.Transform.Contains(point)) return layer;
        }
        return null;
    }

    /// <summary>Re-rasterizes live text in one undo step, keeping its transformed upper-left
    /// corner and pixel scale, as the Mac's applyText editing path does.</summary>
    public bool UpdateTextLayer(Guid id, LayerTextStyle style) => UpdateTextLayer(id, style, null);

    /// <summary>
    /// As <see cref="UpdateTextLayer(Guid, LayerTextStyle)"/>, with the placement the editor is showing.
    /// Passing it is the macOS inline path, where the draft already carries the layer's transform, so a fixed
    /// box keeps that box and only the lines re-layout inside it (TypeTool.swift:288-297 grows the placement
    /// only when `transform == nil || boxSize == nil`). Null means "no editor is asserting a placement" —
    /// the dialog's long-standing behaviour, where a bigger box grows the placement to keep the pixel scale.
    /// </summary>
    public bool UpdateTextLayer(Guid id, LayerTextStyle style, LayerTransform? placedAs)
        => UpdateTextLayer(id, style, placedAs, history: true, out _);

    /// <summary>
    /// `history: false` is the live preview a draft paints with: the pixels change, the canvas redraws, and no
    /// step exists yet — the draft's commit is one step, and its Escape leaves nothing behind (the macOS canvas
    /// draws the draft the same way, `EditorCanvas.swift:31, 577`).
    /// </summary>
    private bool UpdateTextLayer(Guid id, LayerTextStyle style, LayerTransform? placedAs, bool history, out bool changed)
    {
        changed = false;
        if (!CanEditPixels(id) || KeepsAlpha(id)) return false;
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse || layer.Text is null || style is not { IsValid: true }
            || !Snapshot.Images.TryGetValue(id, out var previous)) return false;
        if (layer.Text.Equals(style)) return true;

        Surface image;
        try
        {
            image = TextRasterizer.Rasterize(style);
        }
        catch (ProjectFormatException)
        {
            return false;
        }
        var old = layer.Transform;
        // The macOS `applyText` re-grows the placement only for point text (TypeTool.swift:288-297): a fixed
        // box keeps the size the user dragged and the lines re-layout inside it, so editing the words never
        // moves the box. Point text is as wide as what it now says, and growing it about the centre would
        // swing the rotated corner away, so that corner is put back where it was.
        LayerTransform transform;
        if (style.BoxSize is null || placedAs is null)
        {
            // Growing is measured from the placement the caller is editing in, which for the dialog is the
            // layer's own (`placedAs` null → same object), so the box/point rule lands the same as before there.
            var basePlacement = placedAs ?? old;
            transform = new LayerTransform
            {
                Origin = basePlacement.Origin,
                Size = new SizeD(image.Width * basePlacement.Size.Width / previous.Width, image.Height * basePlacement.Size.Height / previous.Height),
                Rotation = basePlacement.Rotation,
                FlipX = basePlacement.FlipX,
                FlipY = basePlacement.FlipY,
                Sampling = basePlacement.Sampling,
            };
            var anchor = basePlacement.Point(PointD.Zero);
            var moved = transform.Point(PointD.Zero);
            transform.Origin = new PointD(transform.Origin.X + anchor.X - moved.X, transform.Origin.Y + anchor.Y - moved.Y);
        }
        else transform = new LayerTransform { Origin = placedAs.Origin, Size = placedAs.Size, Rotation = placedAs.Rotation,
            FlipX = placedAs.FlipX, FlipY = placedAs.FlipY, Sampling = placedAs.Sampling };
            // A copy, not the caller's instance: the change memento below would otherwise hold the same object
            // the editor keeps mutating, and a later drag would rewrite the history step it came from.
        if (!transform.IsValid || !CanEditLayer(id, LayerLocks.Position) && !transform.Equals(old)) return false;

        if (history) BeginChange();
        if (layer.MaskFile is not null && layer.MaskPlacement is null) layer.MaskPlacement = old;
        layer.Text = style;
        layer.Transform = transform;
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = image },
            Masks = Snapshot.Masks,
        };
        coalesceKey = null;
        if (history) EndChange();
        else { IsDirty = true; TrimUndoHistory(); Changed?.Invoke(); }
        changed = true;
        return true;
    }

    private MementoState? draftCapture;
    private LayerTextStyle? draftOpeningStyle;
    private bool draftPreviewed;

    /// <summary>
    /// Opens a draft over an existing text layer or over a fresh point of the canvas, as the macOS
    /// <c>beginText(at:)</c> does. One draft at a time: a second call is refused because the first holds words
    /// that are not committed yet — the caller settles it first, exactly as pressing down with the Text tool
    /// does there (InlineTextEditor.swift:517). The document as it stands now is kept so the draft can either
    /// be booked as one step or wiped without a trace.
    /// </summary>
    public bool BeginTextDraft(Guid? layerId, PointD origin, LayerTransform? transform, LayerTextStyle style)
    {
        if (layerId is { } protectedId && !CanEditPixels(protectedId)) return false;
        if (ActiveTextDraft is not null || style is not { IsValid: true }) return false;
        if (layerId is { } id)
        {
            var layer = Find(id);
            if (layer is null || layer.IsGroupOrFalse || layer.Text is null) return false;
            transform ??= layer.Transform;
            draftCapture = CaptureState();
            draftOpeningStyle = layer.Text;
            draftPreviewed = false;
        }
        else { draftCapture = null; draftOpeningStyle = null; }
        textResize = null; // a handle taken by the draft before this one is not this draft's
        ActiveTextDraft = new TextDraft { LayerId = layerId, Origin = origin, Transform = transform, Style = style };
        return true;
    }

    /// <summary>
    /// What the editor mutates on every keystroke. Over an existing layer this also writes the preview: the
    /// macOS canvas draws the draft's own style while editing (`EditorCanvas.swift:31, 577`), so what the user
    /// reads on canvas is the pixels the commit will leave — same rasterizer, same mask, same placement rules.
    /// A preview that the rasterizer refuses (over budget, say) fails and changes nothing.
    /// </summary>
    public bool UpdateTextDraft(LayerTextStyle? style = null, PointD? origin = null, LayerTransform? transform = null,
        (int Location, int Length)? selection = null)
    {
        if (ActiveTextDraft is not { } draft) return false;
        if (style is { IsValid: false }) return false;
        if (style is not null && draft.LayerId is { } layerId)
        {
            if (!UpdateTextLayer(layerId, style, draft.Transform, history: false, out var previewed)) return false;
            draftPreviewed |= previewed;
        }
        if (style is not null) draft.Style = style;
        if (origin is { } placed) draft.Origin = placed;
        if (transform is not null) draft.Transform = transform;
        if (selection is { } caret) draft.Selection = caret;
        return true;
    }

    /// <summary>
    /// The macOS <c>applyText(_ :)</c> (TypeTool.swift:271-322): the draft becomes the document as exactly one
    /// history step, and a commit that cannot happen hands the draft back rather than eating the words. Two
    /// rules that are easy to lose in a port: new text that is only whitespace commits nothing at all, and an
    /// unchanged style books no step — here it also puts the previewed pixels back.
    /// </summary>
    public bool ApplyTextDraft()
    {
        if (ActiveTextDraft is not { } draft) return false;
        if (draft.Style is not { IsValid: true }) return false;
        if (draft.IsBlankNewText)
        {
            ActiveTextDraft = null;
            return true;
        }
        var capture = draftCapture;
        var opening = draftOpeningStyle;
        var hadPreview = draftPreviewed; // read before the fields below are cleared: the commit checks it
        ActiveTextDraft = null; draftCapture = null; draftOpeningStyle = null; draftPreviewed = false;
        if (draft.LayerId is not { } id) return AddTextLayer(draft.Style, draft.Origin, draft.Transform) is not null
            || RestoreDraft(draft, capture, opening) is null;
        if (capture is null || opening is null) return UpdateTextLayer(id, draft.Style, draft.Transform);
        if (!UpdateTextLayer(id, draft.Style, draft.Transform, history: false, out _))
        {
            RestoreDraft(draft, capture, opening);
            return false;
        }
        if (draft.Style.Equals(opening))
        {
            // Typed and typed back: the preview left pixels behind, so put the captured state back and book
            // nothing. A draft never previewed anything has not touched the document, so it must not announce
            // a redraw either.
            if (hadPreview) RestoreCaptured(capture);
            return true;
        }
        PushCaptured(capture);
        return true;
    }

    /// <summary>
    /// Types into the open draft. The style is cloned through its own JSON shape rather than field by field so
    /// a property added later cannot be silently dropped by this path, and the runs of per-letter colour and
    /// face are moved with the letters rather than thrown away: the edit is found by what the two strings
    /// disagree about, so an inserted letter takes the face of the one before it and a deleted one takes its
    /// run with it (the macOS replaceCharacters, TypeTool.swift:123-139).
    /// </summary>
    public bool UpdateTextDraftContent(string content)
    {
        if (ActiveTextDraft is not { } draft) return false;
        var style = ManifestJson.Clone(draft.Style);
        if (style.ColorRuns is not null || style.FontRuns is not null)
        {
            var edit = LayerTextStyle.MinimalEdit(draft.Style.Content, content);
            style.ReplaceCharacters(edit.Location, edit.Length, edit.WithLength);
        }
        style.Content = content;
        return UpdateTextDraft(style);
    }

    private (int Handle, LayerTransform From, SizeD Logical, PointD Start)? textResize;

    /// <summary>
    /// Takes a handle for a drag. The press is where the original converts point text into a fixed box of the
    /// size it has at that moment (<c>InlineTextEditor.swift:443-457</c>: the glyphs are never scaled, only the
    /// box and the placement), and where it snapshots the box and the placement that every later frame is
    /// measured against — measuring each frame against the draft as it then stands would add the same movement
    /// once per frame.
    /// </summary>
    /// <param name="handle">Index into <see cref="LayerTransform.Handles"/>.</param>
    /// <param name="start">Where the press landed, in document pixels.</param>
    public bool BeginTextResize(int handle, PointD start)
    {
        if (ActiveTextDraft is not { } draft || handle < 0 || handle >= LayerTransform.Handles.Count) return false;
        if (DraftGeometry(draft) is not { } geometry) return false;
        if (draft.Style.BoxSize is null)
        {
            var boxed = ManifestJson.Clone(draft.Style);
            boxed.BoxSize = new TextBoxSize(geometry.Logical.Width, geometry.Logical.Height);
            if (!UpdateTextDraft(boxed, transform: geometry.From)) return false;
        }
        textResize = (handle, geometry.From, geometry.Logical, start);
        return true;
    }

    /// <summary>
    /// One pointer move of the taken drag: the two edges the handle owns follow the pointer by however far it
    /// has come since it went down, everything else stays (<c>InlineTextEditor.swift:460-489</c>, see
    /// <see cref="LayerTransform.ResizeBox"/>). A frame whose box or placement would be invalid leaves the draft
    /// as it stands — that code's <c>guard … else return</c> is "nothing changed this frame", not an error.
    /// </summary>
    public bool ResizeTextDraft(PointD moved)
    {
        if (textResize is not { } drag || ActiveTextDraft is null) return false;
        if (drag.From.ResizeBox(drag.Handle, drag.Logical, drag.Start, moved) is not { } resized) return false;
        var style = ManifestJson.Clone(ActiveTextDraft.Style);
        style.BoxSize = new TextBoxSize(resized.Box.Width, resized.Box.Height);
        // The placement first, then the style: UpdateTextDraft previews a style against the placement the draft
        // is already showing, so writing a new box over the placement from before the drag sizes the pixels by
        // the drag and the layer by the frame before it.
        if (!UpdateTextDraft(transform: resized.Placement)) return false;
        return UpdateTextDraft(style);
    }

    /// <summary>The pointer went up. Whatever the drag made stays in the draft, which still owes one step.</summary>
    public void EndTextResize() => textResize = null;

    /// <summary>
    /// Where the draft is being shown: the placement it carries, the layer's while the editor has not taken one
    /// of its own, and for new text that was never placed, the box where it was clicked — together with the box's
    /// size in its own units, which is its fixed box or the text as it measures (the Mac's <c>logicalSize</c>).
    /// </summary>
    private (LayerTransform From, SizeD Logical)? DraftGeometry(TextDraft draft)
    {
        SizeD logical;
        try
        {
            logical = TextRasterizer.TextBoxSize(draft.Style);
        }
        catch (ProjectFormatException)
        {
            return null;
        }
        var from = draft.Transform
            ?? (draft.LayerId is { } id && Find(id) is { } layer ? layer.Transform : null)
            ?? new LayerTransform { Origin = draft.Origin, Size = logical };
        return from.Size.Width <= 0 || from.Size.Height <= 0 ? null : (from, logical);
    }

    /// <summary>Commit and close: Cmd+Return, the Done button, switching tools or layers in the original.</summary>
    public bool FinishTextDraft() => ApplyTextDraft();

    /// <summary>Escape. The words and every previewed pixel go back to how the document was before the draft
    /// opened, and no history step is written — so the step Undo takes afterwards is the one from before.</summary>
    public void CancelTextDraft()
    {
        var capture = draftCapture;
        var hadPreview = draftPreviewed;
        ActiveTextDraft = null; draftCapture = null; draftOpeningStyle = null; draftPreviewed = false;
        if (capture is not null && hadPreview) RestoreCaptured(capture);
    }

    private TextDraft? RestoreDraft(TextDraft draft, MementoState? capture, LayerTextStyle? opening)
    {
        ActiveTextDraft = draft; draftCapture = capture; draftOpeningStyle = opening;
        return null;
    }

    /// <summary>Books the step for a finished draft: the state captured when it opened becomes the thing
    /// Undo returns to, which is why the preview writing above had to leave history alone.</summary>
    private void PushCaptured(MementoState prior)
    {
        pendingRedoRestore = null;
        if (redo.Count > 0)
        {
            pendingRedoRestore = new List<MementoState>(redo);
            redo.Clear();
        }
        undo.Push(prior);
        StartHistoryState("Text");
        TrimUndoHistory();
        IsDirty = true;
        coalesceKey = null;
        Changed?.Invoke();
    }

    private void RestoreCaptured(MementoState state)
    {
        RestoreHistoryIdentity(state);
        Snapshot = new ProjectSnapshot
        {
            Manifest = ManifestJson.Deserialize(System.Text.Encoding.UTF8.GetString(state.Manifest)),
            Images = state.Images,
            Masks = state.Masks,
        };
        IsDirty = state.Dirty;
        ActiveSelection = state.Selection;
        coalesceKey = null;
        pendingRedoRestore = null;
        TrimUndoHistory();
        Changed?.Invoke();
    }

    /// <summary>A text layer's name: its first words on one line, as the Mac's layerName(for:) —
    /// line breaks and runs of spaces become single spaces, capped at 40 characters.</summary>
    public static string TextLayerName(string content)
    {
        var flattened = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flattened.Length == 0 ? "Text" : flattened[..Math.Min(40, flattened.Length)];
    }

    /// <summary>Imports an image as a new layer above the active one, at its natural size.
    /// `text` attaches the editable-text metadata the layer's pixels were rasterized from.</summary>
    public ProjectLayerRecord ImportImage(Surface image, string name, LayerTextStyle? text = null)
    {
        BeginChange(operation: text is null ? nameof(ImportImage) : nameof(AddTextLayer));
        var id = Guid.NewGuid();
        var manifest = Snapshot.Manifest;
        var layer = new ProjectLayerRecord
        {
            Id = id,
            Name = UniqueName(name),
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            IsGroup = false,
            Text = text,
            Transform = new LayerTransform
            {
                Origin = new PointD(0, 0),
                Size = new SizeD(image.Width, image.Height),
            },
        };
        int insertion = manifest.ActiveLayerId is { } active
            ? manifest.Layers.FindIndex(l => l.Id == active) + 1
            : manifest.Layers.Count;
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), layer);
        manifest.ActiveLayerId = id;
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = image },
            Masks = Snapshot.Masks,
        };
        EndChange();
        return layer;
    }

    /// <summary>
    /// Adds a shape-tool layer above the active one, as on the Mac: the shape rasterizes to
    /// an ordinary pixel layer placed at its drag origin, and the style rides in the manifest
    /// so resizing later can redraw it. One undoable step; the name skips taken numbers.
    /// </summary>
    public ProjectLayerRecord AddShapeLayer(ShapeStyle style, int width, int height, PointD origin)
    {
        var image = ShapeRasterizer.Draw(style, width, height);
        BeginChange();
        var id = Guid.NewGuid();
        var manifest = Snapshot.Manifest;
        var layer = new ProjectLayerRecord
        {
            Id = id,
            Name = UniqueNumberedName($"{style.Kind}"),
            IsVisible = true,
            ImageFile = ProjectValidator.Uuid(id) + ".png",
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            IsGroup = false,
            Shape = style.ToElement(),
            Transform = new LayerTransform
            {
                Origin = origin,
                Size = new SizeD(image.Width, image.Height),
            },
        };
        int insertion = manifest.ActiveLayerId is { } active
            ? manifest.Layers.FindIndex(l => l.Id == active) + 1
            : manifest.Layers.Count;
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), layer);
        manifest.ActiveLayerId = id;
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = image },
            Masks = Snapshot.Masks,
        };
        EndChange();
        return layer;
    }

    /// <summary>
    /// Imports a parsed PSD as one undoable step: layers bottom to top with their offsets,
    /// opacity and visibility; group folders rebuilt from the section pairing; user masks
    /// attached canvas-sized and unlinked so they sit where the PSD put them. Names come in
    /// verbatim — Photoshop itself allows duplicates.
    /// </summary>
    /// <summary>
    /// Imports the document's layers in one undo step. The optional collector receives the Mac's
    /// PSDConversion lines in its order: the reader's skipped lines first, then per layer what its
    /// kind forced to pixels and what will not match Photoshop exactly.
    /// </summary>
    /// <summary>
    /// The builder's per-layer conversion, shared by both PSD import routes (ImportPsd's direct
    /// attach and the drop batch's AddPhotoshop): decoded pixels, then the adjustment/shape/text
    /// records the reader parsed, each replacing or dropping the asset the Mac's branch does, and
    /// the PSDConversion lines in the builder's order. <paramref name="dx"/>/<paramref name="dy"/>
    /// shift the placed transform for a drop centered on a point.
    /// </summary>
    internal static void AttachPsdExtras(PsdLayer layer, ProjectLayerRecord record, Dictionary<Guid, Surface> images,
        int canvasWidth, int canvasHeight, double dx, double dy, List<PsdConversion>? conversions)
    {
        // The Mac's builder renders the parsed style up front; the render outcome decides both the
        // branch that owns the asset and the text lines of the report. A failed render leaves the
        // decoded pixels the record already carries.
        Surface? renderedText = null;
        LayerTransform? textPlacement = null;
        if (layer.Text is { } style)
        {
            try
            {
                renderedText = TextRasterizer.Rasterize(style);
                var anchor = PsdText.ImageAnchorOf(style, renderedText.Width);
                textPlacement = PsdText.LayerTransformOf(renderedText.Width, renderedText.Height, anchor.X, anchor.Y,
                    layer.TextAnchor.X, layer.TextAnchor.Y, layer.TextRotation, layer.TextFlipY);
            }
            catch (ProjectFormatException)
            {
                // The box does not fit: the style is dropped, the raster stays, as there.
            }
        }
        if (conversions is not null)
        {
            if (layer.CroppedToCanvas) conversions.Add(new PsdConversion(layer.Name, PsdConversion.CroppedToCanvas));
            if (layer.Kind == PsdLayerKind.Text)
            {
                if (layer.Text is { } parsed && renderedText is not null)
                {
                    if (layer.TextNotes is { } textNotes)
                        foreach (var note in textNotes) conversions.Add(new PsdConversion(layer.Name, note));
                    if (PsdText.MissingFontNote(parsed.FontName) is { } fontNote)
                        conversions.Add(new PsdConversion(layer.Name, fontNote));
                }
                else conversions.Add(new PsdConversion(layer.Name, PsdText.RasterizedNote));
            }
            if (layer.Kind == PsdLayerKind.SmartObject)
                conversions.Add(new PsdConversion(layer.Name, PsdConversion.SmartObjectRasterized));
            if (layer.Kind == PsdLayerKind.Effects)
                conversions.Add(new PsdConversion(layer.Name, PsdConversion.EffectsDiscarded));
            if (layer.Kind == PsdLayerKind.Vector)
            {
                if (layer.ShapeNotes is { } shapeNotes)
                    foreach (var note in shapeNotes) conversions.Add(new PsdConversion(layer.Name, note));
                else if (layer.Shape is null) conversions.Add(new PsdConversion(layer.Name, PsdConversion.VectorRasterized));
            }
            if (layer.HasUnsupportedBlendMode)
                conversions.Add(new PsdConversion(layer.Name,
                    layer.IsGroup ? PsdConversion.FolderBlendUnsupported(layer.BlendKey) : PsdConversion.BlendUnsupported(layer.BlendKey)));
            if (layer.Kind == PsdLayerKind.Adjustment && layer.Adjustment is not null)
                conversions.Add(new PsdConversion(layer.Name, PsdConversion.AdjustmentApproximate));
        }
        // The Mac's branch precedence for the asset: adjustment (no asset) beats editable text
        // (asset = the render), which beats the record's own pixels, shape riding along.
        if (layer.Adjustment is { } adjustment)
        {
            record.Adjustment = adjustment;
            record.Transform = new LayerTransform { Origin = new PointD(0, 0), Size = new SizeD(canvasWidth, canvasHeight) };
        }
        else if (renderedText is not null && textPlacement is { } placement)
        {
            record.Transform = new LayerTransform
            {
                Origin = new PointD(placement.Origin.X + dx, placement.Origin.Y + dy),
                Size = placement.Size, Rotation = placement.Rotation, FlipY = placement.FlipY,
                Sampling = placement.Sampling, FlipX = placement.FlipX,
            };
            record.ImageFile = ProjectValidator.Uuid(record.Id) + ".png";
            images[record.Id] = renderedText;
            record.Text = layer.Text;
        }
        else
        {
            if (layer.Pixels is { } imported)
            {
                record.ImageFile = ProjectValidator.Uuid(record.Id) + ".png";
                images[record.Id] = imported;
            }
            if (layer.Shape is { } shape) record.Shape = shape.ToElement();
        }
    }

    /// <summary>
    /// The conversion lines this document's import WOULD report, collected without touching the
    /// document: the Mac reads a PSD fully and puts its confirm sheet up before anything is
    /// applied, so the app asks here first. The import replays the same walk, so the lists match
    /// line for line; the import is the one that throws when the file cannot fit the budget.
    /// </summary>
    public static List<PsdConversion> CollectPsdConversions(PsdDocument psd)
    {
        ArgumentNullException.ThrowIfNull(psd);
        var conversions = new List<PsdConversion>();
        if (psd.ReaderConversions is { } lines) conversions.AddRange(lines);
        var images = new Dictionary<Guid, Surface>();
        var scratch = new ProjectLayerRecord { Id = Guid.Empty, Name = "", IsVisible = true };
        foreach (var layer in psd.Layers)
        {
            scratch.Name = layer.Name;
            AttachPsdExtras(layer, scratch, images, psd.Width, psd.Height, 0, 0, conversions);
        }
        return conversions;
    }

    public List<ProjectLayerRecord> ImportPsd(PsdDocument psd, List<PsdConversion>? conversions = null)
    {
        ArgumentNullException.ThrowIfNull(psd);
        if (conversions is not null && psd.ReaderConversions is { } lines) conversions.AddRange(lines);
        long totalPixels = Snapshot.Images.Sum(pair => pair.Value.PixelCount) + Snapshot.Masks.Sum(pair => pair.Value.PixelCount)
            + psd.Layers.Sum(layer => (layer.Pixels?.PixelCount ?? 0) + (layer.Mask?.PixelCount ?? 0));
        if (totalPixels > Limits.DocumentPixelBudget) throw new ProjectFormatException(ProjectErrorKind.TooLarge);
        BeginChange();
        var manifest = Snapshot.Manifest;
        var images = new Dictionary<Guid, Surface>(Snapshot.Images);
        var masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks);
        var created = new List<ProjectLayerRecord>();
        foreach (var layer in psd.Layers)
        {
            var record = new ProjectLayerRecord
            {
                Id = layer.Id,
                Name = layer.Name,
                IsVisible = !layer.Hidden,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                IsGroup = layer.IsGroup,
                Transform = new LayerTransform
                {
                    Origin = layer.Offset,
                    Size = layer.Pixels is { } pixels
                        ? new SizeD(pixels.Width, pixels.Height)
                        : new SizeD(psd.Width, psd.Height),
                },
            };
            if (layer.ParentId is { } parentId)
            {
                record.ParentId = parentId; // PsdReader ids are already unique across the file
            }
            AttachPsdExtras(layer, record, images, psd.Width, psd.Height, 0, 0, conversions);
            if (layer.Mask is { } mask)
            {
                record.MaskFile = ProjectValidator.Uuid(record.Id) + ".mask.png";
                record.MaskEnabled = layer.MaskEnabled;
                record.MaskLinked = layer.MaskLinked;
                var placement = layer.MaskPlacement ?? new LayerTransform { Size = new(psd.Width, psd.Height) };
                record.MaskPlacement = new LayerTransform { Origin = placement.Origin, Size = placement.Size,
                    Rotation = placement.Rotation, Sampling = placement.Sampling, FlipX = placement.FlipX, FlipY = placement.FlipY };
                masks[record.Id] = mask;
            }
            manifest.Layers.Add(record);
            created.Add(record);
            manifest.ActiveLayerId = record.Id;
        }
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = images,
            Masks = masks,
        };
        EndChange();
        return created;
    }

    /// <summary>
    /// Merges the given layers — with the subtrees of any folders among them — exactly as
    /// the canvas shows them (blend, opacity, masks, clipping, adjustments baked) into one
    /// pixel layer trimmed to its content, placed where the topmost merged layer sat and
    /// named after it. One undoable step, gated by the hierarchy validator, as on the Mac.
    /// </summary>
    public ProjectLayerRecord? MergeLayers(IEnumerable<Guid> ids)
    {
        ids = ids.ToArray();
        if (ids.Any(id => !CanEditLayer(id, LayerLocks.Pixels, branch: true) || KeepsAlpha(id))) return null;
        var manifest = Snapshot.Manifest;
        var byId = manifest.Layers.ToDictionary(candidate => candidate.Id, candidate => candidate);
        var picked = new HashSet<Guid>(ids.Where(byId.ContainsKey));
        foreach (var id in picked.ToArray())
        {
            picked.UnionWith(Descendants(id));
        }
        if (picked.Count == 0) return null;

        // The topmost picked layer lends the result its name and slot.
        ProjectLayerRecord? anchor = null;
        foreach (var candidate in manifest.Layers)
        {
            if (picked.Contains(candidate.Id)) anchor = candidate;
        }
        if (anchor is null) return null;
        return MergePicked(picked, anchor.Name, anchor.ParentId, anchor.Id);
    }

    /// <summary>
    /// The Mac's merge-down mode: the active layer joins the nearest layer below it in the
    /// same folder, and the result takes that lower layer's name and slot. Null when there
    /// is nothing below to merge into.
    /// </summary>
    public ProjectLayerRecord? MergeDown(Guid id)
    {
        if (!CanEditLayer(id, LayerLocks.Pixels, branch: true) || KeepsAlpha(id)) return null;
        var manifest = Snapshot.Manifest;
        int index = manifest.Layers.FindIndex(candidate => candidate.Id == id);
        if (index < 0) return null;
        var active = manifest.Layers[index];
        if (active.IsGroupOrFalse) return null;
        ProjectLayerRecord? below = null;
        for (int i = index - 1; i >= 0; i--)
        {
            if (manifest.Layers[i].ParentId == active.ParentId && !manifest.Layers[i].IsGroupOrFalse)
            {
                below = manifest.Layers[i];
                break;
            }
        }
        if (below is null) return null;
        return MergePicked(new HashSet<Guid> { below.Id, id }, below.Name, below.ParentId, below.Id);
    }

    /// <summary>
    /// The Mac's merge-group mode: the folder's whole subtree composites into one pixel
    /// layer that takes the folder's name, place and parent, and the folder goes. Null for
    /// a folder with no pixel layers inside.
    /// </summary>
    public ProjectLayerRecord? MergeGroup(Guid id)
    {
        if (!CanEditLayer(id, LayerLocks.Pixels, branch: true) || KeepsAlpha(id)) return null;
        var manifest = Snapshot.Manifest;
        var folder = manifest.Layers.FirstOrDefault(candidate => candidate.Id == id);
        if (folder is null || !folder.IsGroupOrFalse) return null;
        var picked = Descendants(id);
        if (!manifest.Layers.Any(candidate => picked.Contains(candidate.Id) && !candidate.IsGroupOrFalse))
        {
            return null;
        }
        return MergePicked(picked, folder.Name, folder.ParentId, folder.Id);
    }

    /// <summary>
    /// Composites the picked layers as the canvas shows them into one pixel layer named
    /// <paramref name="name"/>, inserted at the branch point of <paramref name="slotId"/>,
    /// parented per <paramref name="parentId"/>. One undoable step, gated by the hierarchy
    /// validator with a manual pre-merge restore.
    /// </summary>
    private ProjectLayerRecord? MergePicked(HashSet<Guid> picked, string name, Guid? parentId, Guid slotId)
    {
        if (picked.Any(id => !CanEditLayer(id, LayerLocks.Pixels | LayerLocks.Position) || KeepsAlpha(id))) return null;
        var manifest = Snapshot.Manifest;

        // The subset composites in its own document, on deep copies: parents and clip
        // sources outside the merge are cut loose on the copies, exactly as the Mac's
        // mergePlan does, and the real records stay untouched until the merge commits.
        var subsetLayers = manifest.Layers.Where(candidate => picked.Contains(candidate.Id)).ToList();
        // The clone round-trips through the real manifest so every required property
        // carries its value (a fresh manifest would omit the defaulted ones and the
        // [JsonRequired] read would throw).
        var subsetManifest = System.Text.Json.JsonSerializer.Deserialize<ProjectManifest>(
            ManifestJson.Serialize(new ProjectManifest
            {
                Format = manifest.Format,
                Version = manifest.Version,
                ColorSpace = manifest.ColorSpace,
                Resolution = manifest.Resolution,
                DocumentId = manifest.DocumentId,
                Width = manifest.Width,
                Height = manifest.Height,
                Layers = subsetLayers,
            }), ManifestJson.CompactOptions);
        if (subsetManifest is null) return null;
        foreach (var candidate in subsetManifest.Layers)
        {
            if (candidate.ParentId is { } parent && !picked.Contains(parent)) candidate.ParentId = null;
            if (candidate.MaskSourceId is { } source && !picked.Contains(source)) candidate.MaskSourceId = null;
        }
        var subsetSnapshot = new ProjectSnapshot
        {
            Manifest = subsetManifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images.Where(pair => picked.Contains(pair.Key))),
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks.Where(pair => picked.Contains(pair.Key))),
        };
        var composited = new CanvasCompositor(subsetSnapshot).Render();
        if (Trimmed(composited) is not { } trimmedResult) return null;
        var (trimmed, origin) = (trimmedResult.Trimmed, trimmedResult.Origin);

        BeginChange();
        var savedList = manifest.Layers.ToList();
        var savedActive = manifest.ActiveLayerId;
        var merged = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsVisible = true,
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            IsGroup = false,
            ParentId = parentId,
            Transform = new LayerTransform
            {
                Origin = origin,
                Size = new SizeD(trimmed.Width, trimmed.Height),
            },
        };
        merged.ImageFile = ProjectValidator.Uuid(merged.Id) + ".png";
        var next = manifest.Layers.Where(candidate => !picked.Contains(candidate.Id)).ToList();
        // Layers that clipped to anything merged now clip to the result.
        var reclipped = new List<(ProjectLayerRecord Layer, Guid? Source)>();
        foreach (var candidate in next)
        {
            if (candidate.MaskSourceId is { } source && picked.Contains(source))
            {
                reclipped.Add((candidate, source));
                candidate.MaskSourceId = merged.Id;
            }
        }
        int slot = manifest.Layers.FindIndex(candidate => candidate.Id == slotId);
        int insertion = slot - manifest.Layers.Take(slot).Count(candidate => picked.Contains(candidate.Id));
        next.Insert(Math.Clamp(insertion, 0, next.Count), merged);
        var savedImages = Snapshot.Images;
        var savedMasks = Snapshot.Masks;
        manifest.Layers = next;
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = new Dictionary<Guid, Surface>(
                Snapshot.Images.Where(pair => !picked.Contains(pair.Key)))
            {
                [merged.Id] = trimmed,
            },
            Masks = new Dictionary<Guid, MaskSurface>(
                Snapshot.Masks.Where(pair => !picked.Contains(pair.Key))),
        };
        // The merged layers are gone, so the selection moves before the gate runs.
        manifest.ActiveLayerId = merged.Id;
        if (!HierarchyIsValid(manifest))
        {
            // The merge must not commit a bad tree: put the pre-merge state back and leave
            // the undo history exactly as it was.
            manifest.Layers = savedList;
            manifest.ActiveLayerId = savedActive;
            foreach (var (candidate, source) in reclipped)
            {
                candidate.MaskSourceId = source;
            }
            Snapshot = new ProjectSnapshot
            {
                Manifest = manifest,
                Images = savedImages,
                Masks = savedMasks,
            };
            AbortChange();
            return null;
        }
        EndChange();
        return merged;
    }

    /// <summary>
    /// The Image Size command: every layer's transform maps through the canvas scale
    /// (sx, sy) and its pixels resample into the scaled bounding box, guides scale per
    /// axis, and the canvas becomes width × height — one undoable step, budget-gated,
    /// as the macOS ImageResizer does.
    /// </summary>
    public bool ResizeImage(int width, int height)
    {
        if (layerLocks.Count != 0 && Snapshot.Manifest.Layers.Any(l => GetLayerLocks(l.Id) != LayerLocks.None)) return false;
        var manifest = Snapshot.Manifest;
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide) return false;
        if ((long)width * height > Limits.MaxSurfacePixels) return false;
        if (manifest.Width == width && manifest.Height == height) return true;
        double sx = (double)width / manifest.Width;
        double sy = (double)height / manifest.Height;

        var savedList = manifest.Layers.ToList();
        var savedGuides = manifest.Guides?.ToList();
        int savedWidth = manifest.Width;
        int savedHeight = manifest.Height;
        var savedImages = Snapshot.Images;
        var savedMasks = Snapshot.Masks;

        BeginChange();

        var newImages = new Dictionary<Guid, Surface>();
        long usedPixels = 0;
        foreach (var layer in manifest.Layers)
        {
            var corners = new[]
            {
                layer.Transform.Point(new PointD(0, 0)),
                layer.Transform.Point(new PointD(1, 0)),
                layer.Transform.Point(new PointD(1, 1)),
                layer.Transform.Point(new PointD(0, 1)),
            }.Select(point => new PointD(point.X * sx, point.Y * sy)).ToList();
            double left = Math.Floor(corners.Min(point => point.X));
            double top = Math.Floor(corners.Min(point => point.Y));
            int boxWidth = (int)Math.Ceiling(corners.Max(point => point.X) - left);
            int boxHeight = (int)Math.Ceiling(corners.Max(point => point.Y) - top);
            var transform = new LayerTransform
            {
                Origin = new PointD(left, top),
                Size = new SizeD(boxWidth, boxHeight),
                Rotation = layer.Transform.Rotation,
                FlipX = layer.Transform.FlipX,
                FlipY = layer.Transform.FlipY,
                Sampling = layer.Transform.Sampling,
            };
            if (!transform.IsValid) { RollbackImageSize(savedList, savedGuides, savedWidth, savedHeight, savedImages, savedMasks); return false; }
            if (layer.ImageFile is not null && Snapshot.Images.TryGetValue(layer.Id, out var source))
            {
                if (boxWidth < 1 || boxHeight < 1 || (long)boxWidth * boxHeight > Limits.MaxSurfacePixels - usedPixels)
                {
                    RollbackImageSize(savedList, savedGuides, savedWidth, savedHeight, savedImages, savedMasks);
                    return false;
                }
                usedPixels += (long)boxWidth * boxHeight;
                var resampled = new Surface(boxWidth, boxHeight);
                var placed = new LayerTransform
                {
                    // The bbox-sized surface is the coordinate space: origin zero, the
                    // document offset (left, top) only names where the box came from.
                    Origin = new PointD(0, 0),
                    Size = new SizeD(boxWidth, boxHeight),
                    Rotation = layer.Transform.Rotation,
                    FlipX = layer.Transform.FlipX,
                    FlipY = layer.Transform.FlipY,
                    Sampling = LayerSampling.HighQuality,
                };
                Resampler.Draw(source, placed, resampled, 0, 0, boxWidth, boxHeight, LayerSampling.HighQuality);
                newImages[layer.Id] = resampled;
            }
            layer.Transform = transform;
            // Every independently placed mask scales with the canvas, including linked ones.
            if (layer.MaskPlacement is { } placement)
            {
                placement.Origin = new PointD(placement.Origin.X * sx, placement.Origin.Y * sy);
                placement.Size = new SizeD(placement.Size.Width * sx, placement.Size.Height * sy);
            }
        }
        manifest.Layers = new List<ProjectLayerRecord>(manifest.Layers);
        manifest.Width = width;
        manifest.Height = height;
        if (manifest.Guides is { } guides)
        {
            foreach (var guide in guides)
            {
                guide.Position *= guide.Axis == GuideAxis.Vertical ? sx : sy;
            }
        }
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = newImages,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks),
        };
        if (!HierarchyIsValid(manifest))
        {
            RollbackImageSize(savedList, savedGuides, savedWidth, savedHeight, savedImages, savedMasks);
            return false;
        }
        EndChange();
        return true;
    }

    /// <summary>Puts the pre-resize state back after a rejected Image Size, undoing the
    /// change step BeginChange opened for it.</summary>
    private void RollbackImageSize(List<ProjectLayerRecord> savedList, List<CanvasGuide>? savedGuides,
        int savedWidth, int savedHeight, IReadOnlyDictionary<Guid, Surface> savedImages, IReadOnlyDictionary<Guid, MaskSurface> savedMasks)
    {
        var manifest = Snapshot.Manifest;
        manifest.Layers = savedList;
        manifest.Guides = savedGuides;
        manifest.Width = savedWidth;
        manifest.Height = savedHeight;
        Snapshot = new ProjectSnapshot
        {
            Manifest = manifest,
            Images = savedImages,
            Masks = savedMasks,
        };
        AbortChange();
    }

    /// <summary>
    /// Image &gt; Canvas Size (IO/CanvasResizer.swift:7-72): the canvas becomes width × height and
    /// every layer, placed mask and guide slides by the anchor's offset. Nothing is resampled — this
    /// is the one that only moves the window on the artwork, unlike <see cref="ResizeImage"/>.
    /// An extension fill adds a bottom layer covering the newly gained area only: the old canvas
    /// rectangle stays transparent inside it, holes in the artwork included, as the macOS resizer
    /// clears it. One undoable step.
    /// </summary>
    public bool SetCanvasSize(int width, int height, int anchor = 4, (byte R, byte G, byte B)? extensionFill = null)
    {
        if (layerLocks.Count != 0 && Snapshot.Manifest.Layers.Any(l => !CanEditLayer(l.Id, LayerLocks.Position))) return false;
        var manifest = Snapshot.Manifest;
        if (width is < 1 or > Limits.MaxSide || height is < 1 or > Limits.MaxSide) return false;
        if ((long)width * height > Limits.MaxSurfacePixels) return false;
        if (anchor is < 0 or > 8) return false;
        int oldWidth = manifest.Width, oldHeight = manifest.Height;
        // Floor puts the extra pixel on the right/bottom when growing and takes it from the
        // left/top when shrinking, which is what the macOS CanvasSizeOptions.offset does.
        double offsetX = Math.Floor((width - oldWidth) * (anchor % 3) / 2.0);
        double offsetY = Math.Floor((height - oldHeight) * (anchor / 3) / 2.0);
        if (Math.Abs(offsetX) > 1_000_000 || Math.Abs(offsetY) > 1_000_000) return false;
        bool grows = width > oldWidth || height > oldHeight;
        if (width == oldWidth && height == oldHeight && offsetX == 0 && offsetY == 0) return true;

        // Validate every shifted placement before anything is written, so a rejected size change
        // cannot leave a document half-moved.
        foreach (var layer in manifest.Layers)
        {
            if (!Shifted(layer.Transform, offsetX, offsetY).IsValid) return false;
            if (layer.MaskPlacement is { } placement && !Shifted(placement, offsetX, offsetY).IsValid) return false;
        }
        var images = new Dictionary<Guid, Surface>(Snapshot.Images);
        if (extensionFill is not null && grows)
        {
            long used = Snapshot.Images.Values.Sum(surface => surface.PixelCount);
            if (used + (long)width * height > Limits.DocumentPixelBudget) return false;
            if (manifest.Layers.Count >= Limits.MaxLayers) return false;
        }

        BeginChange();
        foreach (var layer in manifest.Layers)
        {
            layer.Transform = Shifted(layer.Transform, offsetX, offsetY);
            if (layer.MaskPlacement is { } placement) layer.MaskPlacement = Shifted(placement, offsetX, offsetY);
        }
        if (manifest.Guides is { Count: > 0 } guides)
        {
            manifest.Guides = guides.Select(guide => new CanvasGuide
            {
                Id = guide.Id,
                Axis = guide.Axis,
                Position = guide.Axis == GuideAxis.Vertical ? guide.Position + offsetX : guide.Position + offsetY,
            }).ToList();
        }
        manifest.Width = width;
        manifest.Height = height;
        if (extensionFill is { } fill && grows)
        {
            var extension = new Surface(width, height);
            for (int y = 0; y < height; y++)
            {
                int row = y * extension.Stride;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x * 4;
                    // The old canvas rectangle is cleared out, so the artwork keeps showing through
                    // — including wherever it is itself transparent.
                    bool insideOld = x >= offsetX && y >= offsetY && x < offsetX + oldWidth && y < offsetY + oldHeight;
                    if (insideOld) continue;
                    extension.Pixels[index] = fill.R;
                    extension.Pixels[index + 1] = fill.G;
                    extension.Pixels[index + 2] = fill.B;
                    extension.Pixels[index + 3] = 255;
                }
            }
            var extensionLayer = new ProjectLayerRecord
            {
                Id = Guid.NewGuid(),
                Name = "Canvas Extension",
                IsVisible = true,
                Opacity = 1,
                BlendMode = LayerBlendMode.Normal,
                IsGroup = false,
                Transform = new LayerTransform { Size = new SizeD(width, height) },
            };
            extensionLayer.ImageFile = ProjectValidator.Uuid(extensionLayer.Id) + ".png";
            images[extensionLayer.Id] = extension;
            manifest.Layers.Insert(0, extensionLayer);
        }
        Snapshot = new ProjectSnapshot { Manifest = manifest, Images = images, Masks = Snapshot.Masks };
        EndChange();
        return true;
    }

    /// <summary>The same placement, slid by a document-space offset.</summary>
    private static LayerTransform Shifted(LayerTransform transform, double offsetX, double offsetY) => new()
    {
        Origin = new PointD(transform.Origin.X + offsetX, transform.Origin.Y + offsetY),
        Size = transform.Size,
        Rotation = transform.Rotation,
        FlipX = transform.FlipX,
        FlipY = transform.FlipY,
        Sampling = transform.Sampling,
    };

    /// <summary>
    /// Duplicates the given layers — with the subtrees of any folders among them — as the
    /// source's panel order: multiple roots stack above the highest original. Root names
    /// gain " copy", descendants keep their names, and immutable assets are shared. One undoable step.
    /// </summary>
    public List<ProjectLayerRecord> DuplicateLayers(IEnumerable<Guid> ids) => DuplicateLayers(ids, out _);

    public List<ProjectLayerRecord> DuplicateLayers(IEnumerable<Guid> ids, out IReadOnlyDictionary<Guid, Guid> mapping)
    {
        var duplicate = PrepareDuplication(Snapshot, ids);
        mapping = duplicate?.Mapping ?? new Dictionary<Guid, Guid>();
        if (duplicate is null) return [];
        BeginChange(); coalesceKey = null; Snapshot = duplicate.Snapshot; EndChange();
        var copiedIds = duplicate.Mapping.Values.ToHashSet();
        return Snapshot.Manifest.Layers.Where(l => copiedIds.Contains(l.Id)).ToList();
    }

    /// <summary>
    /// Lifts a layer out of its folder: the ParentId clears and the layer joins its former
    /// folder's siblings. One undoable step; false when the layer is not in a folder.
    /// </summary>
    public bool MoveOutOfFolder(Guid id)
    {
        if (!CanEditLayer(id, LayerLocks.None, branch: true)) return false;
        var layer = Find(id);
        if (layer is null || layer.ParentId is null) return false;
        BeginChange();
        var savedParent = layer.ParentId;
        layer.ParentId = null;
        var manifest = Snapshot.Manifest;
        if (!HierarchyIsValid(manifest))
        {
            layer.ParentId = savedParent;
            AbortChange();
            return false;
        }
        manifest.ActiveLayerId = layer.Id;
        EndChange();
        return true;
    }

    /// <summary>Crops a surface to its alpha bounding box; null when it is fully transparent.</summary>
    private static (Surface Trimmed, PointD Origin)? Trimmed(Surface surface)
    {
        int minX = surface.Width, minY = surface.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < surface.Height; y++)
        {
            int row = y * surface.Stride;
            for (int x = 0; x < surface.Width; x++)
            {
                if (surface.Pixels[row + x * 4 + 3] > 0)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        if (maxX < minX || maxY < minY) return null;
        int width = maxX - minX + 1, height = maxY - minY + 1;
        var trimmed = new Surface(width, height);
        for (int y = 0; y < height; y++)
        {
            System.Array.Copy(
                surface.Pixels, (minY + y) * surface.Stride + minX * 4,
                trimmed.Pixels, y * trimmed.Stride, width * 4);
        }
        return (trimmed, new PointD(minX, minY));
    }

    public bool DeleteLayer(Guid id) => DeleteLayers([id]);

    /// <summary>Moves a layer one step toward the top (up) or bottom (down) among its siblings.</summary>
    public bool MoveLayerWithinSiblings(Guid id, bool towardTop)
    {
        if (!CanEditLayer(id, LayerLocks.None, branch: true)) return false;
        var layers = Snapshot.Manifest.Layers;
        int index = layers.FindIndex(layer => layer.Id == id);
        if (index < 0) return false;
        Guid? parent = layers[index].ParentId;
        int swapWith = towardTop ? index + 1 : index - 1;
        if (swapWith < 0 || swapWith >= layers.Count) return false;
        if (layers[swapWith].ParentId != parent) return false;
        BeginChange();
        (layers[index], layers[swapWith]) = (layers[swapWith], layers[index]);
        EndChange();
        return true;
    }

    /// <summary>
    /// Whether a drop may put this layer under <paramref name="parent"/>: the top level (null) takes
    /// anything, and a folder takes it only when it is neither the layer itself nor one of its own
    /// descendants — a folder cannot be moved into what it contains. The Mac's canPlaceLayer.
    /// </summary>
    public bool CanPlaceLayer(Guid id, Guid? parent)
    {
        if (Find(id) is null) return false;
        if (parent is not { } folder) return true;
        return !Descendants(id).Contains(folder) && Find(folder)?.IsGroupOrFalse == true;
    }

    /// <summary>
    /// Drops a layer at a row of the panel as one undo step. See <see cref="PlaceLayers"/> for where the row
    /// lands and what a refusal leaves behind; this is the Mac's single-layer placeLayer.
    /// </summary>
    public bool PlaceLayer(Guid id, Guid? parent, Guid? above = null, bool atBottom = false)
        => PlaceLayers([id], parent, above, atBottom);

    /// <summary>
    /// Drops the given rows at one row of the panel as a single undo step: each lands under
    /// <paramref name="parent"/> (null for the top level) above the sibling <paramref name="above"/>, or at
    /// the very bottom of that parent when <paramref name="atBottom"/> — the manifest array is bottom-to-top,
    /// so "above" is the index after. A layer dropped into the middle of a clipping group joins it and one
    /// carried out of a stack stops clipping. The caller hands the ids in the order they should be placed,
    /// which is why a drop into a folder is fed bottom-up while a drop above a row is fed top-down (the
    /// Mac's place(_:at:intoFolder:copying:), LayerGroups.swift:285 with NativeLayerList.swift:440). A row
    /// that cannot go where the others go is skipped, and a result that would not be a legal tree is taken
    /// back whole: a refusal leaves neither a reordered list nor an undo entry.
    /// </summary>
    public bool PlaceLayers(IReadOnlyList<Guid> ids, Guid? parent, Guid? above = null, bool atBottom = false)
    {
        if (ids.Any(id => !CanEditLayer(id, LayerLocks.None, branch: true)) || parent is { } p && !CanEditLayer(p, LayerLocks.None)) return false;
        if (ids.Count == 0) return false;
        var layers = Snapshot.Manifest.Layers;
        var arrangement = layers.Select(layer => (layer.Id, layer.ParentId, layer.MaskSourceId)).ToList();
        Guid? placed = null;
        BeginChange();
        foreach (var id in ids)
            if (TryPlace(layers, id, parent, above, atBottom)) placed = id;
        if (placed is null || !HierarchyIsValid(Snapshot.Manifest))
        {
            Restore(layers, arrangement);
            AbortChange();
            return false;
        }
        Snapshot.Manifest.ActiveLayerId = placed;
        EndChange();
        return true;
    }

    /// <summary>
    /// Option-drag in the layer panel: a copy of each row dropped where the pointer landed, as one undo
    /// step. It is the Mac's duplicateLayer(in:above:atBottom:) (SelectionClipboard.swift:238), which does
    /// the same two things in the same order — duplicate, then move the copy to the row.
    /// <paramref name="copies"/> is empty unless at least one copy reached the row; on success it names the
    /// copies of the rows that were dragged, and those are what the panel selects, not the originals.
    /// </summary>
    public bool DuplicateLayersAt(IReadOnlyList<Guid> ids, Guid? parent, Guid? above, bool atBottom, out IReadOnlyList<Guid> copies)
    {
        // The guard is on the row that was dragged, not on the copy it produces, which is how the Mac writes
        // it: a folder Option-dropped into itself is refused even though its copy would sit legally inside
        // the original (SelectionClipboard.swift:238-240).
        var draggable = ids.Where(id => CanPlaceLayer(id, parent)).ToArray();
        copies = Array.Empty<Guid>();
        if (draggable.Length == 0) return false;
        var duplicate = PrepareDuplication(Snapshot, draggable);
        if (duplicate is null) return false;
        var before = Snapshot;
        BeginChange();
        Snapshot = duplicate.Snapshot;
        Guid? placed = null;
        foreach (var id in draggable)
            if (duplicate.Mapping.TryGetValue(id, out var copy)
                && TryPlace(Snapshot.Manifest.Layers, copy, parent, above, atBottom)) placed = copy;
        if (placed is null || !HierarchyIsValid(Snapshot.Manifest))
        {
            Snapshot = before;
            AbortChange();
            return false;
        }
        Snapshot.Manifest.ActiveLayerId = placed;
        // Every copy, the ones inside a copied folder too: that is the set the Mac selects after a duplicate,
        // so the panel can highlight what the drop actually made.
        copies = duplicate.Mapping.Values.ToArray();
        EndChange();
        return true;
    }

    /// <summary>One row's move without the change tracking: false when the row is not a place this layer can
    /// go, in which case the layer is back where it was found.</summary>
    private bool TryPlace(List<ProjectLayerRecord> layers, Guid id, Guid? parent, Guid? above, bool atBottom)
    {
        if (!CanPlaceLayer(id, parent) || above == id) return false;
        int index = layers.FindIndex(layer => layer.Id == id);
        if (index < 0) return false;
        var layer = layers[index];
        layers.RemoveAt(index);
        var savedParent = layer.ParentId;
        layer.ParentId = parent;
        int insertion = atBottom ? 0 : layers.Count;
        if (above is { } targetId)
        {
            // The anchor has to be a sibling of the parent being dropped into — the Mac writes the guard the
            // same way, and it is what stops a row of another branch being read as this row's neighbour.
            int target = layers.FindIndex(candidate => candidate.Id == targetId && candidate.ParentId == parent);
            if (target < 0)
            {
                layer.ParentId = savedParent;
                layers.Insert(index, layer);
                return false;
            }
            insertion = target + 1;
        }
        layers.Insert(insertion, layer);
        AdoptClipping(layer, layers);
        ReleaseDetachedClipping(layers);
        return true;
    }

    /// <summary>Puts an arrangement taken before a drop back: the list order, each layer's folder, and every
    /// clipping link, since adopting and releasing clipping touch layers other than the ones that moved.</summary>
    private static void Restore(List<ProjectLayerRecord> layers, List<(Guid Id, Guid? Parent, Guid? Source)> arrangement)
    {
        var byId = layers.ToDictionary(layer => layer.Id, layer => layer);
        layers.Clear();
        layers.AddRange(arrangement.Select(entry => byId[entry.Id]));
        foreach (var entry in arrangement)
        {
            var layer = byId[entry.Id];
            layer.ParentId = entry.Parent;
            layer.MaskSourceId = entry.Source;
        }
    }

    /// <summary>
    /// A layer dropped between a base and something clipped to that base clips to the same base, as in
    /// Photoshop. It runs while the list is being rearranged and before
    /// <see cref="ReleaseDetachedClipping"/>, which would otherwise read the unlinked layer in the middle
    /// of the stack as the group breaking apart. The Mac's adoptClipping (LiveLayerMask.swift:42).
    /// </summary>
    private static void AdoptClipping(ProjectLayerRecord layer, List<ProjectLayerRecord> layers)
    {
        if (layer.IsGroupOrFalse) return;
        var siblings = layers.Where(candidate => candidate.ParentId == layer.ParentId).ToList();
        int index = siblings.FindIndex(candidate => candidate.Id == layer.Id);
        if (index <= 0 || index + 1 >= siblings.Count) return;
        if (siblings[index + 1].MaskSourceId is not { } source || source == layer.Id) return;
        var below = siblings[index - 1];
        if (below.Id != source && below.MaskSourceId != source) return;
        layer.MaskSourceId = source;
    }

    public bool SetVisible(Guid id, bool visible) => SetVisible(id, visible, null);

    /// <summary>One eye swipe can set several rows under the same undo step.</summary>
    public bool SetVisible(Guid id, bool visible, object? coalesceKey)
    {
        var layer = Find(id);
        if (layer is null || layer.IsVisible == visible) return false;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious) BeginChange();
        layer.IsVisible = visible;
        if (joinsPrevious) PixelsChanged?.Invoke();
        else EndChange();
        return true;
    }

    public bool SetOpacity(Guid id, double opacity) => SetOpacity(id, opacity, null);

    /// <summary>
    /// Sets opacity, optionally coalescing consecutive calls under one undo step — the slider
    /// fires dozens of times during a drag and a drag should undo as one.
    /// </summary>
    public bool SetOpacity(Guid id, double opacity, object? coalesceKey)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        opacity = Math.Clamp(opacity, 0, 1);
        if (layer is null || layer.Opacity == opacity) return false;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        layer.Opacity = opacity;
        if (joinsPrevious)
        {
            PixelsChanged?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    public bool SetBlendMode(Guid id, LayerBlendMode mode)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if ((layer.BlendMode ?? LayerBlendMode.Normal) == mode) return false;
        BeginChange();
        layer.BlendMode = mode;
        EndChange();
        return true;
    }

    public bool RenameLayer(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var layer = Find(id);
        if (layer is null || layer.Name == name) return false;
        BeginChange();
        layer.Name = name;
        EndChange();
        return true;
    }

    /// <summary>
    /// Replaces a layer's effects (stroke, shadows, glows) as one manifest edit, serialized
    /// back into the verbatim Effects JSON the renderer reads. Null (or an empty set) clears
    /// them. False — leaving no undo step — when the JSON matches what the layer already
    /// carries.
    /// </summary>
    public bool SetLayerEffects(Guid layerId, LayerEffects? effects) => SetLayerEffects(layerId, effects, null);

    /// <summary>As <see cref="SetLayerEffects(Guid, LayerEffects?)"/> with the usual
    /// coalescing key, so a live-previewed editing session undoes as one step.</summary>
    public bool SetLayerEffects(Guid layerId, LayerEffects? effects, object? coalesceKey)
    {
        if (!CanEditLayer(layerId, LayerLocks.None)) return false;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (effects is { } candidate && !candidate.IsValid) return false;
        JsonElement? serialized = effects is { IsEmpty: false } set ? set.Serialize() : null;
        if (layer.Effects.HasValue == serialized.HasValue
            && (!serialized.HasValue || JsonElement.DeepEquals(layer.Effects.GetValueOrDefault(), serialized.GetValueOrDefault())))
        {
            return false;
        }
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        layer.Effects = serialized;
        if (joinsPrevious)
        {
            PixelsChanged?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    /// <summary>
    /// Whether one effect can be copied onto a layer: the source has to actually carry it, and the target
    /// has to be a pixel layer that is not the source itself — a folder has nothing to draw and an
    /// adjustment carries no pixels of its own (LayerEffects.swift:322's canCopyEffect).
    /// </summary>
    public bool CanCopyEffect(LayerEffectKind kind, Guid source, Guid target)
    {
        if (source == target) return false;
        if (Find(source) is not { } from || Find(target) is not { } to) return false;
        if (to.IsGroupOrFalse || to.ImageFile is null) return false;
        return LayerEffects.Parse(from.Effects)?.Contains(kind) == true;
    }

    /// <summary>
    /// Copies one effect from a layer onto another as its own undo step, leaving the source untouched and
    /// keeping whatever else the target already carried (LayerEffects.swift:330's copyEffect). The value is
    /// read out of the source's JSON and written into the target's, so the two layers never share one
    /// effect — editing the shadow afterwards moves one layer, not both. False with no step when the copy
    /// would change nothing.
    /// </summary>
    public bool CopyEffect(LayerEffectKind kind, Guid source, Guid target)
    {
        if (!CanCopyEffect(kind, source, target)) return false;
        var from = LayerEffects.Parse(Find(source)!.Effects) ?? new LayerEffects();
        var to = LayerEffects.Parse(Find(target)!.Effects) ?? new LayerEffects();
        switch (kind)
        {
            case LayerEffectKind.Stroke: to.Stroke = from.Stroke; break;
            case LayerEffectKind.Shadow: to.Shadow = from.Shadow; break;
            case LayerEffectKind.ColorOverlay: to.ColorOverlay = from.ColorOverlay; break;
            case LayerEffectKind.InnerShadow: to.InnerShadow = from.InnerShadow; break;
            case LayerEffectKind.OuterGlow: to.OuterGlow = from.OuterGlow; break;
            case LayerEffectKind.InnerGlow: to.InnerGlow = from.InnerGlow; break;
        }
        return SetLayerEffects(target, to);
    }

    public bool SetActiveLayer(Guid? id)
    {
        var manifest = Snapshot.Manifest;
        if (id is { } value && !manifest.Layers.Any(layer => layer.Id == value)) return false;
        if (manifest.ActiveLayerId == id) return false;
        manifest.ActiveLayerId = id;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a layer by whole document pixels, as a drag leaves behind.</summary>
    public bool MoveLayerBy(Guid id, double deltaX, double deltaY) => MoveLayerBy(id, deltaX, deltaY, null);

    /// <summary>Moves with an optional coalescing key, so a drag undoes as one step.</summary>
    public bool MoveLayerBy(Guid id, double deltaX, double deltaY, object? coalesceKey)
    {
        if (!CanEditLayer(id, LayerLocks.Position, branch: true)) return false;
        var layer = Find(id);
        if (layer is null || (deltaX == 0 && deltaY == 0)) return false;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        layer.Transform.Origin = new PointD(layer.Transform.Origin.X + deltaX, layer.Transform.Origin.Y + deltaY);
        if (layer.MaskLinked != false && layer.MaskPlacement is { } maskPlacement)
            maskPlacement.Origin = new(maskPlacement.Origin.X + deltaX, maskPlacement.Origin.Y + deltaY);
        if (joinsPrevious)
        {
            PixelsChanged?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    /// <summary>Scales a layer to a percentage of its pixel size, keeping its center.</summary>
    public bool ScaleLayer(Guid id, double percent) => ScaleLayer(id, percent, null);

    /// <summary>Scales with an optional coalescing key, so a drag undoes as one step.</summary>
    public bool ScaleLayer(Guid id, double percent, object? coalesceKey)
    {
        if (!CanEditLayer(id, LayerLocks.Position, branch: true)) return false;
        var layer = Find(id);
        if (layer is null || percent <= 0) return false;
        var image = Snapshot.Images.GetValueOrDefault(id);
        double pixelWidth = image?.Width ?? layer.Transform.Size.Width;
        double pixelHeight = image?.Height ?? layer.Transform.Size.Height;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        var transform = layer.Transform;
        var oldTransform = new LayerTransform { Origin = transform.Origin, Size = transform.Size, Rotation = transform.Rotation,
            FlipX = transform.FlipX, FlipY = transform.FlipY, Sampling = transform.Sampling };
        double width = Math.Max(1, pixelWidth * percent / 100);
        double height = Math.Max(1, pixelHeight * percent / 100);
        var center = transform.Center;
        transform.Size = new SizeD(width, height);
        transform.Origin = new PointD(center.X - width / 2, center.Y - height / 2);
        if (layer.MaskLinked != false && layer.MaskPlacement is { } maskPlacement)
            layer.MaskPlacement = maskPlacement.Following(oldTransform, transform);
        if (joinsPrevious)
        {
            PixelsChanged?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    /// <summary>Adds an adjustment layer above the active one, covering the canvas.</summary>
    public ProjectLayerRecord AddAdjustment(AdjustmentKind kind)
    {
        BeginChange();
        var manifest = Snapshot.Manifest;
        var layer = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = UniqueName(RawEnumText.Resolve<AdjustmentKind>(Enum.GetName(kind)!)),
            IsVisible = true,
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            IsGroup = false,
            Adjustment = new LayerAdjustment
            {
                Kind = kind,
                // Grain and Add Noise get a stable random pattern of their own, as on the Mac.
                GrainSettings = kind == AdjustmentKind.Grain ? new GrainSettings { Seed = RandomSeed() } : null,
                NoiseSeed = kind == AdjustmentKind.AddNoise ? RandomSeed() : null,
            },
            Transform = new LayerTransform
            {
                Origin = new PointD(0, 0),
                Size = new SizeD(manifest.Width, manifest.Height),
            },
        };
        layer.ParentId = manifest.ActiveLayerId is { } active
            ? (Find(active) is { IsGroupOrFalse: true } folder ? folder.Id : Find(active)?.ParentId)
            : null;
        int insertion = manifest.ActiveLayerId is { } activeId
            ? manifest.Layers.FindIndex(candidate => candidate.Id == activeId) + 1
            : manifest.Layers.Count;
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), layer);
        manifest.ActiveLayerId = layer.Id;
        EndChange();
        return layer;
    }

    private static uint RandomSeed() => (uint)Random.Shared.Next();

    public bool AddFolder()
    {
        BeginChange();
        var manifest = Snapshot.Manifest;
        var active = manifest.ActiveLayerId is { } id ? Find(id) : null;
        var folder = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = UniqueNumberedName("Folder"),
            IsVisible = true,
            IsGroup = true,
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            Transform = new LayerTransform
            {
                Origin = new PointD(0, 0),
                Size = new SizeD(manifest.Width, manifest.Height),
            },
        };
        folder.ParentId = active is { IsGroupOrFalse: true } ? active.Id : active?.ParentId;
        int insertion = manifest.ActiveLayerId is { } activeId
            ? manifest.Layers.FindIndex(l => l.Id == activeId) + 1
            : manifest.Layers.Count;
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), folder);
        manifest.ActiveLayerId = folder.Id;
        EndChange();
        return true;
    }

    public ProjectLayerRecord? GroupLayers(Guid id) => GroupLayers(new[] { id });

    /// <summary>
    /// Wraps the selected branches in a new folder, porting the macOS groupSelectedLayers:
    /// layers whose ancestor is also selected stay with that ancestor (only roots move), the
    /// folder joins the roots' common parent and takes the topmost selected branch's spot,
    /// and the roots enter it in draw order. One undoable step.
    /// </summary>
    public ProjectLayerRecord? GroupLayers(IEnumerable<Guid> layerIds)
    {
        var manifest = Snapshot.Manifest;
        var byId = manifest.Layers.ToDictionary(candidate => candidate.Id, candidate => candidate);
        var selected = layerIds.Where(byId.ContainsKey).ToHashSet();
        if (selected.Any(id => !CanEditLayer(id, LayerLocks.None, branch: true))) return null;
        if (selected.Count == 0 || manifest.Layers.Count >= 10_000) return null;

        // A selected folder carries its subtree: descendants of a selected layer are not roots.
        Guid? SelectedAncestorOf(Guid id)
        {
            var parent = byId[id].ParentId;
            while (parent is { } current)
            {
                if (selected.Contains(current)) return current;
                parent = byId[current].ParentId;
            }
            return null;
        }
        var rootIds = selected.Where(id => SelectedAncestorOf(id) is null).ToHashSet();
        var roots = DrawOrder.Resolve(manifest.Layers).Order.Where(rootIds.Contains).ToList();

        // The nearest ancestor of the first root that every root shares (no parent counts).
        Guid? CommonParent()
        {
            bool HasAncestor(Guid id, Guid? candidate)
            {
                if (candidate is null) return true;
                var parent = byId[id].ParentId;
                while (parent is { } current)
                {
                    if (current == candidate) return true;
                    parent = byId[current].ParentId;
                }
                return false;
            }
            var parent = byId[roots[0]].ParentId;
            while (parent is { } current)
            {
                if (roots.All(id => HasAncestor(id, current))) return current;
                parent = byId[current].ParentId;
            }
            return null;
        }

        BeginChange();
        var original = manifest.Layers.ToList();
        var originalParents = roots.ToDictionary(id => id, id => byId[id].ParentId);
        var folder = new ProjectLayerRecord
        {
            Id = Guid.NewGuid(),
            Name = UniqueNumberedName("Folder"),
            IsVisible = true,
            IsGroup = true,
            Opacity = 1,
            BlendMode = LayerBlendMode.Normal,
            Transform = new LayerTransform
            {
                Origin = new PointD(0, 0),
                Size = new SizeD(manifest.Width, manifest.Height),
            },
        };
        folder.ParentId = CommonParent();
        // The wrapper takes the topmost selected branch's spot, counted over the layers staying out.
        var branches = roots.Select(id =>
        {
            var branch = id;
            while (byId[branch].ParentId != folder.ParentId) branch = byId[branch].ParentId!.Value;
            return branch;
        }).ToHashSet();
        int highest = manifest.Layers.FindLastIndex(candidate => branches.Contains(candidate.Id));
        int insertion = manifest.Layers.Take(highest + 1).Count(candidate => !rootIds.Contains(candidate.Id));
        manifest.Layers.RemoveAll(candidate => rootIds.Contains(candidate.Id));
        manifest.Layers.Insert(Math.Min(insertion, manifest.Layers.Count), folder);
        foreach (var root in roots)
        {
            byId[root].ParentId = folder.Id;
            manifest.Layers.Add(byId[root]);
        }
        if (!HierarchyIsValid(manifest))
        {
            manifest.Layers.Clear();
            manifest.Layers.AddRange(original);
            foreach (var root in roots)
            {
                byId[root].ParentId = originalParents[root];
            }
            AbortChange();
            return null;
        }
        manifest.ActiveLayerId = folder.Id;
        EndChange();
        return folder;
    }

    /// <summary>
    /// Reverses GroupLayers as on the Mac: the folder's direct children take its place among
    /// its own siblings, in the order they had inside it, and the folder goes — its own
    /// opacity, blend mode and mask are discarded with it. One undoable step.
    /// </summary>
    public bool Ungroup(Guid id)
    {
        if (!CanEditLayer(id, LayerLocks.None, branch: true)) return false;
        var group = Find(id);
        if (group is null || !group.IsGroupOrFalse) return false;
        BeginChange();
        var manifest = Snapshot.Manifest;
        var original = manifest.Layers.ToList();
        var originalActive = manifest.ActiveLayerId;
        var children = manifest.Layers.Where(candidate => candidate.ParentId == id).ToList();
        var childIds = children.Select(candidate => candidate.Id).ToHashSet();
        var originalParents = children.ToDictionary(candidate => candidate.Id, candidate => candidate.ParentId);
        foreach (var child in children)
        {
            child.ParentId = group.ParentId;
        }
        var rebuilt = new List<ProjectLayerRecord>();
        foreach (var candidate in manifest.Layers)
        {
            if (candidate.Id == id) rebuilt.AddRange(children);
            else if (!childIds.Contains(candidate.Id)) rebuilt.Add(candidate);
        }
        ReleaseDetachedClipping(rebuilt);
        manifest.Layers.Clear();
        manifest.Layers.AddRange(rebuilt);
        // The folder is gone, so the selection moves before the gate runs.
        manifest.ActiveLayerId = children.FirstOrDefault()?.Id;
        if (!HierarchyIsValid(manifest))
        {
            manifest.Layers.Clear();
            manifest.Layers.AddRange(original);
            foreach (var child in children)
            {
                child.ParentId = originalParents[child.Id];
            }
            manifest.ActiveLayerId = originalActive;
            AbortChange();
            return false;
        }
        EndChange();
        return true;
    }

    /// <summary>A clipping layer that lost its contiguous base stops clipping, as on the Mac.</summary>
    private static void ReleaseDetachedClipping(List<ProjectLayerRecord> layers)
    {
        var release = new HashSet<Guid>();
        foreach (var stack in layers.GroupBy(candidate => candidate.ParentId))
        {
            Guid? source = null;
            foreach (var layer in stack)
            {
                if (layer.MaskSourceId is { } maskSource)
                {
                    if (maskSource != source)
                    {
                        release.Add(layer.Id);
                        source = layer.Id;
                    }
                }
                else
                {
                    source = layer.IsGroupOrFalse ? null : layer.Id;
                }
            }
        }
        foreach (var layer in layers)
        {
            if (release.Contains(layer.Id)) layer.MaskSourceId = null;
        }
    }

    /// <summary>The pre-save hierarchy gate, run so a bad tree never enters the undo history.</summary>
    private bool HierarchyIsValid(ProjectManifest manifest)
    {
        try
        {
            ProjectValidator.Validate(manifest);
            return true;
        }
        catch (ProjectFormatException)
        {
            return false;
        }
    }
    public bool AddRasterMask(Guid id, MaskSurface mask) => AddRasterMask(id, mask, clearSelection: false);

    private bool AddRasterMask(Guid id, MaskSurface mask, bool clearSelection)
    {
        if (!CanEditPixels(id, onMask: true)) return false;
        var layer = Find(id);
        // A layer that already has a mask keeps it: the Mac disables the affordance
        // (LayerMaskMenu.swift:16) and its model refuses (LayerMask.swift:243) rather than replacing a
        // mask somebody painted. Returning before BeginChange also means a refused add costs no undo step
        // and no dirty mark.
        if (layer is null || layer.MaskFile is not null) return false;
        BeginChange();
        layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
        layer.MaskEnabled = true;
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [id] = mask },
        };
        if (clearSelection) SetSelection(null);
        EndChange();
        return true;
    }

    /// <summary>Adds a plain all-black mask: the layer starts fully hidden, as on the Mac.</summary>
    public bool AddHideAllMask(Guid id)
    {
        if (!CanEditPixels(id, onMask: true)) return false;
        var layer = Find(id);
        if (layer is null || layer.MaskFile is not null) return false;
        var image = Snapshot.Images.GetValueOrDefault(id);
        int width = Math.Max(1, image?.Width ?? (int)layer.Transform.Size.Width);
        int height = Math.Max(1, image?.Height ?? (int)layer.Transform.Size.Height);
        var mask = new MaskSurface(width, height); // zeros hide everything
        return AddRasterMask(id, mask);
    }

    /// <summary>
    /// Adds a mask generated from the selection on the layer's own pixel grid: with
    /// revealing, the selected area is white and the rest black; hiding is the inverse.
    /// One undoable step; false when the layer already has a mask or nothing is selected.
    /// </summary>
    public bool AddSelectionMask(Guid id, bool revealing) => AddSelectionMask(id, revealing, default);

    public bool AddSelectionMask(Guid id, bool revealing, CancellationToken cancellation)
    {
        if (!CanEditPixels(id, onMask: true)) return false;
        cancellation.ThrowIfCancellationRequested();
        var layer = Find(id);
        if (layer is null || layer.MaskFile is not null || ActiveSelection is not { } selection) return false;
        var image = Snapshot.Images.GetValueOrDefault(id);
        int width = Math.Max(1, image?.Width ?? (int)layer.Transform.Size.Width);
        int height = Math.Max(1, image?.Height ?? (int)layer.Transform.Size.Height);
        if ((long)width * height > Limits.MaxSurfacePixels) return false;
        var mask = LayerCoverage(selection, layer, width, height, cancellation: cancellation);
        if (!revealing)
        {
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                int end = (y + 1) * width;
                for (int i = y * width; i < end; i++) mask.Pixels[i] = (byte)(255 - mask.Pixels[i]);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return AddRasterMask(id, mask, clearSelection: true);
    }

    public bool RemoveMask(Guid id)
    {
        if (!CanEditPixels(id, onMask: true)) return false;
        var layer = Find(id);
        if (layer is null || layer.MaskFile is null) return false;
        BeginChange();
        layer.MaskFile = null;
        layer.MaskEnabled = null;
        layer.MaskLinked = null;
        layer.MaskPlacement = null;
        var masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks);
        masks.Remove(id);
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = Snapshot.Images,
            Masks = masks,
        };
        EndChange();
        return true;
    }

    /// <summary>
    /// Links or unlinks the layer's mask: linked, the mask follows the layer's transform;
    /// unlinked, it keeps its own placement. One undoable step; false when there is no mask.
    /// </summary>
    public bool SetMaskLinked(Guid id, bool linked)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        if (layer is null || layer.MaskFile is null) return false;
        BeginChange();
        layer.MaskLinked = linked;
        EndChange();
        return true;
    }

    /// <summary>
    /// Enables or disables the layer's mask: disabled, the mask is kept but stops hiding
    /// pixels. One undoable step; false when there is no mask.
    /// </summary>
    public bool SetMaskEnabled(Guid id, bool enabled)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        if (layer is null || layer.MaskFile is null) return false;
        BeginChange();
        layer.MaskEnabled = enabled;
        EndChange();
        return true;
    }

    /// <summary>
    /// Whether Create/Release Clipping Mask applies: a non-group layer with an existing link
    /// to release, or a lower non-group sibling whose base it may clip to without breaking
    /// the live-mask graph — the Mac's canToggleClippingMask + canLinkMask.
    /// </summary>
    public bool CanToggleClippingMask(Guid id)
    {
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (layer.MaskSourceId is not null) return true;
        if (!TryLowerSibling(id, out var below)) return false;
        var source = below.MaskSourceId ?? below.Id;
        var sourceLayer = Find(source);
        if (source == id || sourceLayer is null || sourceLayer.IsGroupOrFalse || sourceLayer.Adjustment is not null) return false;
        // Simulate the link in place: the graph must stay cycle-free.
        Guid? saved = layer.MaskSourceId;
        layer.MaskSourceId = source;
        bool valid = ProjectValidator.LiveMaskGraphIsValid(Snapshot.Manifest.Layers);
        layer.MaskSourceId = saved;
        return valid;
    }

    /// <summary>
    /// Create/Release Clipping Mask: link to the nearest lower sibling's base (which follows
    /// that sibling's own link), or release — a release takes the contiguous clipped siblings
    /// above it that share the source, one undoable step, as on the Mac.
    /// </summary>
    public bool ToggleClippingMask(Guid id)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (layer.MaskSourceId is { } source)
        {
            ReleaseLiveMask(id, source);
            return true;
        }
        if (!CanToggleClippingMask(id) || !TryLowerSibling(id, out var below)) return false;
        BeginChange();
        layer.MaskSourceId = below.MaskSourceId ?? below.Id;
        EndChange();
        return true;
    }

    /// <summary>The nearest sibling below in draw order, within the same folder.</summary>
    private bool TryLowerSibling(Guid id, out ProjectLayerRecord below)
    {
        below = null!;
        var layer = Find(id) ?? throw new InvalidOperationException("layer vanished");
        var order = DrawOrder.Resolve(Snapshot.Manifest.Layers).Order;
        var siblings = order.Select(gid => Find(gid)!).Where(candidate => candidate.ParentId == layer.ParentId).ToList();
        int index = siblings.FindIndex(candidate => candidate.Id == id);
        if (index <= 0) return false;
        below = siblings[index - 1];
        return true;
    }

    /// <summary>Nil-out the target and the contiguous siblings above it that clip to the
    /// same source — releasing a base releases its clipped children, as on the Mac.</summary>
    private void ReleaseLiveMask(Guid targetId, Guid source)
    {
        var target = Find(targetId)!;
        var order = DrawOrder.Resolve(Snapshot.Manifest.Layers).Order;
        var siblings = order.Select(gid => Find(gid)!).Where(candidate => candidate.ParentId == target.ParentId).ToList();
        var releases = new List<Guid>();
        foreach (var candidate in siblings.Skip(siblings.FindIndex(s => s.Id == targetId)))
        {
            if (candidate.Id != targetId && candidate.MaskSourceId != source) break;
            releases.Add(candidate.Id);
        }
        BeginChange();
        foreach (var releaseId in releases)
        {
            Find(releaseId)!.MaskSourceId = null;
        }
        EndChange();
    }

    /// <summary>Replaces a layer's pixels (brush strokes, pastes, external updates).</summary>
    public bool ReplaceLayerImage(Guid id, Surface image) => ReplaceLayerImage(id, image, null);

    /// <summary>Replaces a layer's pixels, optionally coalescing under one undo step (brush strokes).</summary>
    public bool ReplaceLayerImage(Guid id, Surface image, object? coalesceKey, [CallerMemberName] string operation = "")
    {
        if (!CanEditPixels(id) || ProtectAlpha(id, image) is not { } protectedImage) return false;
        image = protectedImage;
        var layer = Find(id);
        if (layer is null) return false;
        bool joins = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joins) BeginChange(operation: operation);
        // Painting or filtering retires live text; Undo restores both the pixels and its style.
        // (The stroke plane path drops the shape record too, as commitPaintSnapshot does; a
        // filter's cancel restores the captured pixels inside its own step, so the shared choke
        // point stays at the pre-existing text-only rule rather than touching filter metadata.)
        if (!ReferenceEquals(image, Snapshot.Images.GetValueOrDefault(id))) layer.Text = null;
        layer.ImageFile ??= ProjectValidator.Uuid(id) + ".png";
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = image },
            Masks = Snapshot.Masks,
        };
        // Joined updates only move pixels (live previews, stroke segments): the structure
        // and the undo step are already in place, so skip the full structural refresh.
        if (joins) PixelsChanged?.Invoke();
        else EndChange();
        return true;
    }

    public bool ReplaceLayerMask(Guid id, MaskSurface mask) => ReplaceLayerMask(id, mask, null);

    /// <summary>Replaces a layer's mask, creating the mask reference when absent.</summary>
    public bool ReplaceLayerMask(Guid id, MaskSurface mask, object? coalesceKey)
    {
        if (!CanEditPixels(id, onMask: true)) return false;
        var layer = Find(id);
        if (layer is null) return false;
        bool joins = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joins) BeginChange();
        if (layer.MaskFile is null)
        {
            layer.MaskFile = ProjectValidator.Uuid(id) + ".mask.png";
            layer.MaskEnabled = true;
        }
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [id] = mask },
        };
        if (joins) Changed?.Invoke();
        else EndChange();
        return true;
    }

    /// <summary>
    /// Replaces a layer's pixels AND its placement as one step, for the filters that grow the layer's
    /// grid while they work (Filters.swift:393-418): a blurred layer really gets bigger — alpha spread
    /// into the padding — and OK trims it back to what has coverage with the placement that puts the
    /// piece on the same document rectangle (Filters.swift:616-627). ReplaceLayerImage moves pixels
    /// inside the placement it already has; this is the one place the grid and the placement must land
    /// together, on one undo step, or a half-committed layer would draw its old-sized box around new
    /// pixels. The budget check runs before anything is touched (the Mac's grow throws too large,
    /// Filters.swift:405), and the layer's own old pixels are not counted against it.
    /// </summary>
    public bool ReplaceLayerGrid(Guid id, Surface image, LayerTransform placement, object? coalesceKey = null)
    {
        if (!CanEditPixels(id) || (KeepsAlpha(id) || !CanEditLayer(id, LayerLocks.Position)) && Find(id)?.Transform.Equals(placement) != true
            || ProtectAlpha(id, image) is not { } protectedImage) return false;
        image = protectedImage;
        var layer = Find(id);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (!placement.IsValid) return false;
        long other = Snapshot.Images.Where(pair => pair.Key != id).Sum(pair => pair.Value.PixelCount)
            + Snapshot.Masks.Where(pair => pair.Key != id).Sum(pair => pair.Value.PixelCount);
        if (other + image.PixelCount > Limits.DocumentPixelBudget) return false;
        bool joins = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joins) BeginChange();
        // Filtering retires live text the way painting does; Undo restores both pixels and style.
        if (!ReferenceEquals(image, Snapshot.Images.GetValueOrDefault(id))) layer.Text = null;
        layer.ImageFile ??= ProjectValidator.Uuid(id) + ".png";
        layer.Transform = new LayerTransform
        {
            Origin = placement.Origin, Size = placement.Size, Rotation = placement.Rotation,
            FlipX = placement.FlipX, FlipY = placement.FlipY, Sampling = placement.Sampling,
        };
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = new Dictionary<Guid, Surface>(Snapshot.Images) { [id] = image },
            Masks = Snapshot.Masks,
        };
        if (joins) PixelsChanged?.Invoke();
        else EndChange();
        return true;
    }

    /// <summary>
    /// Carries a layer's attached mask (one that covers the layer's own grid) onto the layer's new grid,
    /// edge tone filling the room the grid grew by — the Mac's commitFilter carry (Filters.swift:642-649):
    /// the mask is composited as if fully enabled, its placement mapped through the old-onto-the-new grid,
    /// so a reveal that hugged the old edge keeps hugging the same document edge after the grow. A
    /// separately placed mask (MaskPlacement not null) already lives on the document and needs nothing,
    /// and a 1x1 uniform mask scales to any grid as it stands — both travel untouched, as the Mac's
    /// guards leave them (Filters.swift:643-644). A disabled mask rides along too: the Mac composites it
    /// AS IF awake — its bytes are still what was painted — through enabled.clipImage, but writes the
    /// result back via owned.replacing, which keeps isEnabled (LayerMask.swift:20-22), so it stays
    /// asleep. The carry preserves what was painted; it does not wake a mask the user switched off.
    /// </summary>
    public bool CarryAttachedMask(Guid id, LayerTransform oldPlacement, int oldWidth, int oldHeight,
        LayerTransform newPlacement, int newWidth, int newHeight, object? coalesceKey = null)
    {
        var layer = Find(id);
        if (layer is null || layer.MaskFile is null || layer.MaskPlacement is not null) return false;
        if (!Snapshot.Masks.TryGetValue(id, out var mask) || mask.Width != oldWidth || mask.Height != oldHeight) return false;
        byte edge = CarriedMaskEdgeTone(mask);
        var carried = new MaskSurface(newWidth, newHeight);
        Array.Fill(carried.Pixels, edge);
        for (int y = 0; y < newHeight; y++)
            for (int x = 0; x < newWidth; x++)
            {
                // The grown grid's pixel center mapped to the document, then back through the OLD placement:
                // exactly the Mac's concatenation of the two grids' document mappings, done per pixel.
                var document = BrushEngine.PixelToDocument(newPlacement, newWidth, newHeight, new PointD(x + .5, y + .5));
                var source = BrushEngine.DocumentToPixel(oldPlacement, oldWidth, oldHeight, document);
                if (source.X < 0 || source.Y < 0 || source.X >= oldWidth || source.Y >= oldHeight) continue;
                carried.Pixels[y * newWidth + x] = mask.Pixels[(int)source.Y * oldWidth + (int)source.X];
            }
        bool joins = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joins) BeginChange();
        Snapshot = new ProjectSnapshot
        {
            Manifest = Snapshot.Manifest,
            Images = Snapshot.Images,
            Masks = new Dictionary<Guid, MaskSurface>(Snapshot.Masks) { [id] = carried },
        };
        if (joins) PixelsChanged?.Invoke();
        else EndChange();
        return true;
    }

    /// <summary>The tone a carried mask fills its grid's border with: the majority of the old mask's own
    /// edge pixels — outside wins on a tie, the Mac's "total * 2 >= count * 255 ? 1 : 0" vote against the
    /// thumbnail border (LayerMask.swift:63-77) — because the padding a blur spreads into must keep the
    /// edge's own visibility rather than an invented one.</summary>
    private static byte CarriedMaskEdgeTone(MaskSurface mask)
    {
        int outside = 0, count = 0;
        void Sample(int index) { if (mask.Pixels[index] < 128) outside++; count++; }
        for (int x = 0; x < mask.Width; x++) { Sample(x); Sample((mask.Height - 1) * mask.Width + x); }
        for (int y = 0; y < mask.Height; y++) { Sample(y * mask.Width); Sample(y * mask.Width + mask.Width - 1); }
        return outside * 2 >= count ? (byte)0 : (byte)255;
    }

    /// <summary>
    /// Edits an adjustment layer's settings in place, with the usual coalescing key so a
    /// slider drag undoes as one step.
    /// </summary>
    public bool MutateAdjustment(Guid id, Action<LayerAdjustment> mutate, object? coalesceKey = null)
    {
        if (!CanEditLayer(id, LayerLocks.None)) return false;
        var layer = Find(id);
        if (layer?.Adjustment is not { } adjustment) return false;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        mutate(adjustment);
        adjustment.MarkModified();
        if (joinsPrevious)
        {
            PixelsChanged?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    // --- Blur tool: a working copy stamped in place from the blurred sample ---

    private GaussianBlurSample? blurSample;
    private Surface? blurWorking, blurBase;
    private ImageStrokeGrid? blurGrid;
    private RectI blurImageBounds;
    private Guid? blurLayerId;
    private LayerTransform? blurTransform;
    private TileStroke? blurCoverage;
    private double blurOpacity;
    private PointD? blurLastTarget;
    private object? blurKey;
    private WarpStroke? warpStroke;

    /// <summary>
    /// Blur stroke start: freezes the layer's source for on-demand gaussian sample windows
    /// (sigma = canvas radius / placement scale) and swaps in a working copy the
    /// stamps mutate — one buffer per stroke, not one clone per stamp — under one coalesced
    /// undo step. False for groups or layers without pixels.
    /// </summary>
    public bool BeginBlurStroke(Guid layerId, double blurRadius, object strokeKey, double opacity = 1)
    {
        if (!CanEditPixels(layerId)) return false;
        var layer = Find(layerId);
        if (WhyPaintRefused(layerId, onMask: false) is not null || layer is null || !layer.Transform.IsValid
            || !double.IsFinite(blurRadius) || !double.IsFinite(opacity) || opacity <= 0) return false;
        if (!Snapshot.Images.TryGetValue(layerId, out var image)) return false;
        var placed = layer.Transform;
        double perPixel = Math.Max(1e-6, Math.Sqrt(Math.Abs(placed.Size.Width * placed.Size.Height / ((double)image.Width * image.Height))));
        double sigma = Math.Min(Math.Clamp(blurRadius, .5, 50) / perPixel, Math.Max(image.Width, image.Height) / 2d);
        blurSample = new GaussianBlurSample(image, sigma, transparentEdges: true);
        blurTransform = new() { Origin = placed.Origin, Size = placed.Size, Rotation = placed.Rotation,
            FlipX = placed.FlipX, FlipY = placed.FlipY, Sampling = placed.Sampling };
        blurBase = image; blurCoverage = null; blurOpacity = opacity; blurKey = strokeKey;
        // A 1:1 layer grows with the brush's grid: the stamps past its pixels feather the
        // transparent edge there, and the radius stays tied to the source grid. A scaled or
        // turned layer keeps its own plane: grid growth would move its sampling density.
        blurGrid = placed.Rotation % 360 == 0 && !placed.FlipX && !placed.FlipY
            && Math.Abs(placed.Size.Width - image.Width) < 1e-9 && Math.Abs(placed.Size.Height - image.Height) < 1e-9
            ? ImageStrokeGrid.Create(Snapshot, layer, image, new(Math.Clamp(blurRadius, .5, 50), 1, opacity, 0, 0, 0))
            : null;
        var working = image.CloneForEditing();
        blurImageBounds = blurGrid?.SourceBounds ?? new RectI(0, 0, working.Width, working.Height);
        ReplaceLayerImage(layerId, working, strokeKey); // opens the stroke's undo step
        blurWorking = working;
        blurLayerId = layerId;
        return true;
    }

    /// <summary>
    /// Continues a canvas-space sweep. Coverage and opacity blend the frozen blurred sample
    /// source-over the original, without re-blurring the current stroke's working pixels.
    /// </summary>
    public bool BlurStampApply(Guid layerId, PointD target, double radius, double hardness)
    {
        if (blurSample is not { } frozen || blurWorking is not { } stamped || blurLayerId != layerId
            || blurBase is not { } original || blurTransform is not { } transform || !Equals(blurKey, coalesceKey)
            || !double.IsFinite(target.X) || !double.IsFinite(target.Y) || !double.IsFinite(radius) || radius <= 0 || !double.IsFinite(hardness)) return false;
        var grid = blurGrid;
        if (grid is not null && !grid.CanMaterialize(grid.CandidateBounds(blurImageBounds, blurLastTarget ?? target, target))) return false;
        blurCoverage ??= grid is not null
            ? new(new(radius, hardness, blurOpacity, 0, 0, 0), grid.Placement, grid.Width, grid.Height)
            : new(new(radius, hardness, blurOpacity, 0, 0, 0), transform, stamped.Width, stamped.Height);
        blurCoverage.RetireTail(); var touched = blurCoverage.SweepSegment(target);
        if (touched.Width <= 0 || touched.Height <= 0) return false;
        blurLastTarget = target;
        if (grid is null)
        {
            blurCoverage.PaintBlurRegion(stamped, original, frozen.Render(touched), touched, ActiveSelection);
            PixelsChanged?.Invoke();
            return true;
        }
        var bounds = KeepsAlpha(layerId) || !CanEditLayer(layerId, LayerLocks.Position) ? blurImageBounds : RectI.Union(blurImageBounds, touched); // grows only when the tip leaves the plane
        bool grows = bounds != blurImageBounds;
        if (grows) stamped = grid.Materialize(bounds, stamped, blurImageBounds);
        blurImageBounds = bounds; blurWorking = stamped;
        // The frozen sample is the source's own grid: the stamp maps back by the source
        // rectangle and keeps the transparent-edge radius the stroke started with. The
        // window spans a radius margin so a pixel's whole kernel comes from one sample.
        var stampRect = new RectI(touched.X + grid.SourceBounds.X, touched.Y + grid.SourceBounds.Y, touched.Width, touched.Height);
        var windowRect = GaussianBlurSample.KernelSpan(stampRect, frozen.Radius);
        var sample = frozen.Render(windowRect);
        blurCoverage.PaintBlurGrid(stamped, grid, sample, windowRect, bounds, touched, ActiveSelection);
        ProtectGridAlpha(layerId, stamped, grid, bounds, touched);
        if (grows)
        {
            if (Find(layerId) is { } moved) moved.Transform = grid.TransformFor(bounds);
            PublishGrowingImage(layerId, stamped); Changed?.Invoke();
        }
        PixelsChanged?.Invoke();
        return true;
    }

    /// <summary>Blur stroke end: closes the stroke's undo step and drops the cached sample.</summary>
    public void EndBlurStroke()
    {
        if (blurGrid is { } grown && blurWorking is { } plane && blurLayerId is { } stampLayer)
            CommitGrowingPlane(stampLayer, grown, blurImageBounds, plane);
        blurSample = null;
        blurWorking = null;
        blurBase = null; blurTransform = null; blurCoverage = null;
        blurGrid = null; blurImageBounds = default; blurLastTarget = null;
        blurLayerId = null;
        if (Equals(blurKey, coalesceKey)) coalesceKey = null;
        blurKey = null;
    }


    /// <summary>
    /// Smudge/Liquify stroke start: caches a canvas-aligned working copy of the active
    /// layer. False for groups or layers without pixels.
    /// </summary>
    public bool BeginWarpStroke(Guid layerId, WarpMode mode, double diameter, double hardness, double strength)
    {
        if (!CanEditPixels(layerId) || !CanEditLayer(layerId, LayerLocks.Position) || KeepsAlpha(layerId)) return false;
        var layer = Find(layerId);
        if (layer is null || layer.IsGroupOrFalse) return false;
        if (!Snapshot.Images.TryGetValue(layerId, out var image)) return false;
        var manifest = Snapshot.Manifest;
        warpStroke = new WarpStroke(image, layer.Transform, manifest.Width, manifest.Height, mode, diameter, hardness, strength);
        coalesceKey = null;
        return true;
    }

    /// <summary>Smudge/Liquify stroke dab.</summary>
    public void WarpStrokeAppend(PointD point)
    {
        warpStroke?.Append(point);
    }

    /// <summary>
    /// Smudge/Liquify stroke end: writes the working copy back into the layer's pixels
    /// along the stroke path — one undoable step — and drops the working copy.
    /// </summary>
    public bool EndWarpStroke(Guid layerId)
    {
        if (warpStroke?.FinishIntoLayer() is not { } result)
        {
            warpStroke = null;
            return false;
        }
        warpStroke = null;
        coalesceKey = null;
        return ReplaceLayerImage(layerId, result);
    }

    /// <summary>
    /// Free distortion: warps the layer's pixels (and its mask) so its corners land on
    /// `corners` — document coordinates — leaving an ordinary axis-aligned layer over the
    /// shape's bounds. One undoable step; false when the shape is unusable.
    /// </summary>
    public bool DistortLayer(Guid layerId, PointD[] corners)
    {
        if (BeginTransformDraft([layerId]) is not { } edit || edit.PrepareDistortCommit(edit.Value, corners) is not { } preview) return false;
        return CommitTransform(preview);
    }

    /// <summary>Edits a Levels adjustment's settings in place, with the usual coalescing key.</summary>
    public bool MutateLevels(Guid id, Action<LevelsSettings> mutate, object? coalesceKey = null) =>
        MutateAdjustment(id, adjustment => mutate(adjustment.Levels), coalesceKey);

    /// <summary>
    /// The Levels panel's auto buttons: rebuilds the adjustment's settings from the
    /// composite histogram — shared endpoints (Contrast), per-channel (Color) or with a
    /// neutral-gray gamma. One undoable step; false for non-Levels adjustments.
    /// </summary>
    public bool AutoLevels(Guid adjustmentLayerId, LevelsAutoMode mode)
    {
        var layer = Find(adjustmentLayerId);
        if (layer?.Adjustment is not { } adjustment || adjustment.Kind != AdjustmentKind.Levels) return false;
        return AutoLevels(adjustmentLayerId, mode, LevelsHistogramInput.Capture(Snapshot, adjustmentLayerId).Compute());
    }

    /// <summary>Uses a worker result only while its adjustment input still matches. A stale
    /// result leaves history untouched; the panel requests a fresh histogram instead.</summary>
    public bool AutoLevels(Guid adjustmentLayerId, LevelsAutoMode mode, LevelsHistogramResult histogram)
    {
        if (!histogram.Matches(Snapshot, adjustmentLayerId)) return false;
        var settings = LevelsAutomatic.Settings(histogram.CopyBins(), mode);
        return MutateLevels(adjustmentLayerId, levels =>
        {
            for (int i = 0; i < 4; i++) levels.Ranges[i] = settings.Ranges[i];
        });
    }

    /// <summary>Edits a Curves adjustment's settings in place, with the usual coalescing key.</summary>
    public bool MutateCurves(Guid id, Action<CurvesSettings> mutate, object? coalesceKey = null) =>
        MutateAdjustment(id, adjustment => mutate(adjustment.Curves), coalesceKey);

    // --- Guides ---

    public CanvasGuide AddGuide(GuideAxis axis, double position)
    {
        BeginChange();
        var manifest = Snapshot.Manifest;
        var guide = new CanvasGuide { Id = Guid.NewGuid(), Axis = axis, Position = position };
        manifest.Guides ??= new List<CanvasGuide>();
        manifest.Guides.Add(guide);
        EndChange();
        return guide;
    }

    /// <summary>Moves one guide, optionally coalescing consecutive calls under one undo step.</summary>
    public bool MoveGuide(Guid guideId, double position, object? coalesceKey = null)
    {
        var manifest = Snapshot.Manifest;
        var guide = manifest.Guides?.FirstOrDefault(candidate => candidate.Id == guideId);
        if (guide is null || guide.Position == position) return false;
        bool joinsPrevious = coalesceKey is not null && Equals(coalesceKey, this.coalesceKey);
        this.coalesceKey = coalesceKey;
        if (!joinsPrevious)
        {
            BeginChange();
        }
        guide.Position = position;
        if (joinsPrevious)
        {
            Changed?.Invoke();
        }
        else
        {
            EndChange();
        }
        return true;
    }

    public bool RemoveGuide(Guid guideId)
    {
        var manifest = Snapshot.Manifest;
        if (manifest.Guides is null || !manifest.Guides.Any(candidate => candidate.Id == guideId)) return false;
        // The memento is taken by BeginChange, so the removal must happen after it — taking
        // it first recorded the already-emptied list and Undo could never bring the guide back.
        BeginChange();
        manifest.Guides.RemoveAll(candidate => candidate.Id == guideId);
        EndChange();
        return true;
    }

    public bool ClearGuides()
    {
        var manifest = Snapshot.Manifest;
        if (manifest.Guides is not { Count: > 0 }) return false;
        BeginChange();
        manifest.Guides.Clear();
        EndChange();
        return true;
    }

    // --- Transform drafts ---

    /// <summary>
    /// Begins a transform drag: one undo step covering every update until it ends, as on
    /// the Mac (a drag is one edit regardless of pointer-event count).
    /// </summary>
    public bool BeginTransform(object draftKey, Guid layerId)
    {
        if (!CanEditLayer(layerId, LayerLocks.Position, branch: true)) return false;
        var layer = Find(layerId);
        if (layer is null) return false;
        BeginChange();
        coalesceKey = draftKey;
        return true;
    }

    /// <summary>Applies a draft transform while the drag is live; every call joins the drag's undo step.</summary>
    public bool UpdateTransform(object draftKey, Guid layerId, LayerTransform draft)
    {
        if (!CanEditLayer(layerId, LayerLocks.Position, branch: true)) return false;
        var layer = Find(layerId);
        if (layer is null || !layer.Transform.IsValid || !draft.IsValid) return false;
        bool joins = Equals(draftKey, coalesceKey);
        if (!joins)
        {
            BeginChange();
            coalesceKey = draftKey;
        }
        if (layer.MaskLinked != false && layer.MaskPlacement is { } maskPlacement)
            layer.MaskPlacement = maskPlacement.Following(layer.Transform, draft);
        layer.Transform = new LayerTransform
        {
            Origin = draft.Origin,
            Size = draft.Size,
            Rotation = draft.Rotation,
            FlipX = draft.FlipX,
            FlipY = draft.FlipY,
            Sampling = draft.Sampling,
        };
        PixelsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Ends (or aborts) a transform drag. Abort via Undo, as on the Mac.
    ///
    /// A session that ends on the state it started with — a Camera Raw grade reset, a slider dragged back
    /// to the value the panel opened on, a filter preview cancelled — leaves no undo step behind: the step
    /// it pushed would undo to the picture that is already on screen, and a Ctrl+Z that does nothing is a
    /// step the user cannot see but has to sit through. The saved-state flag goes back with it, because
    /// nothing is waiting to be written that was not waiting before the panel opened. Redo is not part of
    /// the bargain: opening a panel and cancelling it clears the redo stack the same way any change does,
    /// and <see cref="EndChange"/> has already dropped the stash by the time a session closes.
    /// The live selection is not compared either — no keyed session moves it (SetSelection is its own step).
    /// </summary>
    public void EndTransform(object draftKey)
    {
        if (!Equals(draftKey, coalesceKey)) return;
        coalesceKey = null;
        if (undo.Count == 0 || !SameStateAs(undo.Peek())) return;
        var opened = undo.Pop();
        IsDirty = opened.Dirty;
        RestoreHistoryIdentity(opened); NotifyHistoryChanged();
    }

    /// <summary>Whether the document now holds the manifest, pixels and masks a memento was taken at.
    /// Surfaces are compared by reference first, since a revert usually hands back the very surface it
    /// captured and only a re-rendered copy needs its bytes read.</summary>
    private bool SameStateAs(MementoState state)
    {
        if (!state.Manifest.SequenceEqual(MementoOf(Snapshot))) return false;
        if (state.Images.Count != Snapshot.Images.Count || state.Masks.Count != Snapshot.Masks.Count) return false;
        foreach (var (id, surface) in state.Images)
        {
            if (!Snapshot.Images.TryGetValue(id, out var current)) return false;
            if (!ReferenceEquals(current, surface) && !current.Pixels.AsSpan().SequenceEqual(surface.Pixels)) return false;
        }
        foreach (var (id, mask) in state.Masks)
        {
            if (!Snapshot.Masks.TryGetValue(id, out var current)) return false;
            if (!ReferenceEquals(current, mask) && !current.Pixels.AsSpan().SequenceEqual(mask.Pixels)) return false;
        }
        return true;
    }

    // --- Brush strokes ---

    private Surface? strokeImage;
    private MaskSurface? strokeMask;
    private Guid? strokeLayerId;
#if DEBUG
    internal double DebugStrokeCloneMs, DebugStrokeReplaceMs, DebugStrokeDepositMs, DebugStrokePaintMs, DebugStrokeNotifyMs;
#endif

    /// <summary>
    /// Why a stroke cannot start on this target, the way the macOS session answers before the brush
    /// refuses (EditorSession+Brush.swift:5-33). Null means the target is paintable. The reason is a
    /// value, not a string, because the message belongs to the UI's bilingual resources.
    /// </summary>
    public enum PaintRefusal
    {
        Locked,
        EmptySelection,
        MissingLayer,
        FolderHasNoPixels,
        AdjustmentHasNoPixels,
        MaskTurnedOff,
    }

    /// <summary>
    /// A folder and an adjustment layer have no image of their own, but the macOS rule is explicit:
    /// "A folder has no pixels of its own, so only its mask can be painted." Both take mask paint.
    /// A mask that exists but is switched off takes nothing, and an explicitly empty selection leaves
    /// nowhere to paint. Whether several layers are selected, and whether the layer is visible, are
    /// session facts this class cannot see, so the app still owns those two checks.
    /// </summary>
    public PaintRefusal? WhyPaintRefused(Guid layerId, bool onMask)
    {
        if (ActiveSelection is { IsEmpty: true }) return PaintRefusal.EmptySelection;
        if (!CanEditPixels(layerId, onMask)) return PaintRefusal.Locked;
        var layer = Find(layerId);
        if (layer is null) return PaintRefusal.MissingLayer;
        if (onMask)
        {
            if (layer.MaskFile is not null && layer.MaskEnabled == false) return PaintRefusal.MaskTurnedOff;
            return null;
        }
        if (layer.IsGroupOrFalse) return PaintRefusal.FolderHasNoPixels;
        if (layer.Adjustment is not null) return PaintRefusal.AdjustmentHasNoPixels;
        return null;
    }

    /// <summary>
    /// Begins painting on a layer's pixels (or its mask with onMask), ported from the macOS
    /// brush handoff: density accumulates in 256×256 tiles, the working copy of the surface
    /// is rebuilt from the untouched base plus the accumulated coverage after every segment,
    /// and the whole stroke reverts as one undo step recorded when the stroke ends.
    /// </summary>
    public bool BeginStroke(object strokeKey, Guid layerId, BrushSettings brush, PointD documentPoint, bool onMask = false, bool erase = false)
        => BeginStrokeCore(strokeKey, layerId, brush, documentPoint, onMask, erase, null);

    private bool BeginStrokeCore(object strokeKey, Guid layerId, BrushSettings brush, PointD documentPoint, bool onMask, bool erase, GaussianMaskBlurSample? blur)
    {
        if (WhyPaintRefused(layerId, onMask) is not null || !onMask && erase && KeepsAlpha(layerId)) return false;
        maskBlurSample = blur;
        if (blur is null) return onMask ? BeginGrowingMaskStroke(strokeKey, layerId, brush, documentPoint, erase)
            : BeginGrowingImageStroke(strokeKey, layerId, brush, documentPoint, erase);
        maskStrokeGrid = null;
        imageStrokeGrid = null; // the blur samples the published plane, not a growing stroke
        var layer = Find(layerId)!;
#if DEBUG
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLayerId = layerId;
        strokeErase = erase;
        {
            if (Snapshot.Masks.GetValueOrDefault(layerId) is { } existing)
            {
                strokeBaseMask = existing;
                strokeMask = existing.CloneForEditing();
            }
            else
            {
                // A fresh mask at the image's size, revealing everything until painted.
                var image = Snapshot.Images.GetValueOrDefault(layerId);
                int width = Math.Max(1, (int)Math.Round(image?.Width ?? layer.Transform.Size.Width));
                int height = Math.Max(1, (int)Math.Round(image?.Height ?? layer.Transform.Size.Height));
                strokeBaseMask = new MaskSurface(width, height);
                Array.Fill(strokeBaseMask.Pixels, (byte)255);
                strokeMask = (MaskSurface)strokeBaseMask.Clone();
            }
#if DEBUG
            DebugStrokeCloneMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            ReplaceLayerMask(layerId, strokeMask, strokeKey);
#if DEBUG
            DebugStrokeReplaceMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
#endif
            strokeImage = null;
            strokeBaseImage = null;
            stroke = new TileStroke(brush, layer.MaskPlacement ?? layer.Transform, strokeMask.Width, strokeMask.Height);
        }
        return ContinueStroke(strokeKey, documentPoint);
    }

    public bool ContinueStroke(object strokeKey, PointD documentPoint)
    {
        if (strokeLayerId is not { } id || !Equals(strokeKey, coalesceKey) || stroke is null) return false;
        var layer = Find(id);
        if (layer is null) return false;
        if (maskStrokeGrid is not null) return ContinueGrowingMaskStroke(id, documentPoint);
        if (imageStrokeGrid is not null) return ContinueGrowingImageStroke(id, documentPoint);
        // The previous tail joins the permanent coverage, then the new segment lays a fresh
        // tail — replacing the tail can never double-count its pixels.
#if DEBUG
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        stroke.RetireTail();
        var touched = stroke.SweepSegment(documentPoint);
#if DEBUG
        DebugStrokeDepositMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        strokeLastPoint = documentPoint;
        int x0 = touched.X, y0 = touched.Y;
        int x1 = touched.X + touched.Width, y1 = touched.Y + touched.Height;
        if (x1 > x0 && y1 > y0)
        {
            if (strokeMask is { } mask && strokeBaseMask is { } baseMask)
            {
                if (maskBlurSample is { } frozen) stroke.PaintMaskSample(mask, baseMask, frozen.Render(touched), touched, ActiveSelection);
                else stroke.PaintMaskRegion(mask, baseMask, x0, y0, x1, y1, ActiveSelection, strokeErase);
            }
        }
#if DEBUG
        DebugStrokePaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        start = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        PixelsChanged?.Invoke();
#if DEBUG
        DebugStrokeNotifyMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
#endif
        return true;
    }

    public bool EndStroke(object strokeKey)
    {
        if (!Equals(strokeKey, coalesceKey)) return false;
        if (imageStrokeGrid is not null && strokeLayerId is { } grown) CommitGrowingImageStroke(grown);
        if (maskBlurSample is not null) EndTransform(strokeKey);
        else coalesceKey = null;
        stroke = null;
        strokeImage = null;
        strokeBaseImage = null;
        strokeMask = null;
        strokeBaseMask = null;
        maskBlurSample = null;
        maskStrokeGrid = null;
        imageStrokeGrid = null;
        strokeLayerId = null;
        strokeLastPoint = null;
        return true;
    }

    private TileStroke? stroke;
    private Surface? strokeBaseImage;
    private MaskSurface? strokeBaseMask;
    private bool strokeErase;
    private PointD? strokeLastPoint;

    private ProjectLayerRecord? Find(Guid id) =>
        Snapshot.Manifest.Layers.FirstOrDefault(layer => layer.Id == id);

    private HashSet<Guid> Descendants(Guid id)
    {
        var result = new HashSet<Guid>();
        bool changed = true;
        result.Add(id);
        while (changed)
        {
            changed = false;
            foreach (var layer in Snapshot.Manifest.Layers)
            {
                if (layer.ParentId is { } parent && result.Contains(parent) && result.Add(layer.Id)) changed = true;
            }
        }
        return result;
    }

    private string UniqueName(string baseName)
    {
        var names = Snapshot.Manifest.Layers.Select(layer => layer.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (int number = 2; ; number++)
        {
            if (!names.Contains($"{baseName} {number}")) return $"{baseName} {number}";
        }
    }

    /// <summary>"Rectangle 1" / "Folder 1", "… 2", … skipping names already in the document, as on the Mac.</summary>
    private string UniqueNumberedName(string baseName)
    {
        var names = Snapshot.Manifest.Layers.Select(layer => layer.Name).ToHashSet();
        for (int number = 1; ; number++)
        {
            string name = $"{baseName} {number}";
            if (!names.Contains(name)) return name;
        }
    }

    private static Surface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new Surface(width, height);
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                int index = row * surface.Stride + column * 4;
                surface.Pixels[index] = r;
                surface.Pixels[index + 1] = g;
                surface.Pixels[index + 2] = b;
                surface.Pixels[index + 3] = 255;
            }
        }
        return surface;
    }
}
