# Windows .comp project format

Guide axes use the shared lowercase strings `horizontal` and `vertical` (v8–11). The Windows
reader also accepts the exact `Horizontal` and `Vertical` values written by earlier Windows
builds, then saves lowercase. This narrow recovery path does not loosen other enum validation
or introduce a format version. See the guide compatibility checks (local development reference).

This document describes what the Windows implementation reads, validates, renders, preserves, and
writes. The canonical cross-platform file contract is also described in the repository's
[project-format.md](../../docs/project-format.md) and
[writing-comp-files.md](../../docs/writing-comp-files.md).

## Package layout

A .comp project is a directory:

    Example.comp/
      manifest.json
      images/
        <LAYER-UUID>.png
        <LAYER-UUID>.mask.png
      QuickLook/                 optional preview, removed by a Windows save

The manifest format identifier is com.compositor.project. The working color space must be sRGB.
Layer records are ordered bottom to top; the last drawn layer is visually on top. A folder record
has no image asset and groups child records by parentID.

## Version behavior

The reader accepts manifest versions 1 through 12. A new static Windows document defaults to version 11.
The saver writes the version number held in the manifest; it does not migrate an opened older
manifest automatically. Thus, reading v1–v11 is not the same as upgrading every old project to v11.

The validator enforces the following introduction gates:

| Version | Fields or behavior accepted from this version |
| --- | --- |
| 1 | Flat layer stack, base metadata |
| 2 | parentID, isGroup folders |
| 3 | Non-default layer opacity and blendMode |
| 4 | Raster masks on ordinary layers |
| 5 | maskSourceID clipping links |
| 6 | Raster masks on folders |
| 7 | Adjustment layers except the three sampling kinds below |
| 8 | Folder opacity and canvas guides |
| 9 | Gaussian Blur, Motion Blur, and Add Noise adjustments |
| 10 | Text colorRuns |
| 11 | Text fontRuns |
| 12 | Optional layer-animation timeline; currently implemented on Windows |

Creating a timeline upgrades the manifest to v12 as part of the same Undo step. Static documents
stay v11 by default. Current macOS builds reject v12. See the canonical
[v12 animation schema](../../docs/project-format.md#version-12-layer-animation-windows) for track
names, ranges and interpolation. Playback evaluates a metadata copy while sharing source assets;
it does not serialize transient frames, change source pixels or add Undo steps. Windows project
saves retain the existing asset-first, atomic-manifest-last contract.

The Windows model can represent adjustment, mask, text, guide, folder, transform, shape, and
effects records. Shape and layer-effect JSON is stored as an opaque JSON value; Windows creates
basic shapes and renders/edits six layer effects. The typed effect model preserves the existing optional
per-effect `enabled` field (missing means visible), including when editing or copying another effect.
This implements the shared visibility contract without changing the format version. Text metadata is parsed and validated; the UI
composes NEW text in a dialog and re-edits EXISTING text inline on the canvas (matching the Mac's
InlineTextEditor door), regenerating its raster PNG either way. Pixel replacement
clears stale text metadata; gradients also clear shape metadata. Unknown future JSON
fields are not guaranteed to survive a Windows round trip.

Some operations on a legacy manifest can introduce fields that its declared version does not
permit. For example, a v3 manifest cannot be saved with a new raster mask. Since there is no
automatic version migration yet, such a save fails validation. Keep a backup before using the
Windows build to edit legacy projects that need newer features.

## JSON mapping

The Windows serializer matches the macOS JSON conventions used by the format:

- camel-case names with the ID suffix preserved in names such as documentID, activeLayerID, and
  maskSourceID;
- uppercase, hyphenated UUIDs on save;
- origin and size represented as two-number arrays;
- exact, case-sensitive enum strings, including High quality, Color Burn, and Linear Dodge (Add);
- optional null-valued properties omitted and two-space indentation.

Malformed JSON, wrong required fields, unknown enum strings, unsupported versions, and invalid
metadata are rejected. Image filenames must match the uppercase UUID form expected for the layer.
The loader rejects reparse-point assets and oversized files.

## Assets and pixels

Layer assets are 8-bit PNG files named exactly <LAYER-UUID>.png. Mask assets are 8-bit grayscale
PNG files named <LAYER-UUID>.mask.png. In a mask, 255 reveals and 0 hides. Windows decodes layer
images to premultiplied RGBA8 using WIC with sRGB color management; masks are grayscale coverage.
Project save re-encodes both asset types as PNG.

The common geometry fields are:

- origin: top-left of the unrotated layer bounds in document pixels;
- size: rendered width and height in document pixels;
- rotation: clockwise angle in degrees around the bounds' center;
- flipX and flipY: local horizontal and vertical flips;
- sampling: Nearest, Smooth, or High quality.

The layer transform is metadata. Moving, rotating, or scaling a layer does not inherently resample
its source pixels.

## Validation limits

The current Windows limits in Compositor.Core.Project.Limits are:

| Resource | Limit |
| --- | ---: |
| Canvas, source image, mask, or generated surface side | 30,000 px |
| One generated surface | 200,000,000 pixels |
| Layers | 10,000 |
| manifest.json | 4 MiB |
| One encoded image or mask asset | 512 MiB |
| Decoded pixels summed across ALL images and masks | Memory-derived, clamped to 200,000,000–800,000,000 px |
| Folder nesting | 64 levels |
| Clipping-mask chain | 256 nodes |
| Guides | 1,000 |

The document pixel budget is computed as physical memory divided by 16, clamped to the listed range.
Images and masks share this ONE pool: every check sums image and mask pixels against the single
DocumentPixelBudget (Limits.cs:31; EditorDocument.cs:1895, FileImport.cs:132,
FloatingSelectionEdit.cs:69). The operating system can still deny allocations before the format
limit is reached.

Validation also rejects duplicate IDs, invalid or cyclic folder parents, image-bearing folders,
non-folder parents, missing or invalid clipping-mask links, invalid transforms, empty or excessive
layer names, invalid opacity, incompatible folder blend modes, and missing/damaged assets.

## Saving protocol

ProjectStore.SaveAsync applies this order:

1. Validate the manifest and verify that every referenced surface is present.
2. Encode referenced images and masks.
3. Write each PNG beside its target and move it into place atomically.
4. Write a temporary manifest beside manifest.json and replace the manifest atomically.
5. Remove stale QuickLook previews and unreferenced PNG files.

External writers should use the same asset-first, manifest-last ordering. Never write a partial
manifest directly over the live file.

## Cross-platform scope

The Windows store is designed to read and write the shared package contract. That does not imply
feature parity with the macOS renderer or editor. On Windows, blend modes, adjustments, raster
masks, clipping masks, folders, transforms, and six layer effects participate in rendering. Text
and shapes display their embedded raster fallback and carry style metadata; text is re-editable
(new in a dialog, existing inline on the canvas), while existing-shape editing is incomplete. See
[Compatibility](compatibility.md) for the application boundaries.
