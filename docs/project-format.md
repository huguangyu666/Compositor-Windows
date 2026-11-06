# Compositor project format, versions 1–12

A `.comp` file is a macOS document package containing `manifest.json` and an `images/` directory of `<layer UUID>.png` assets.

The manifest identifies `com.compositor.project` and the sRGB working space. New static projects use version `11`; creating a Windows animation upgrades the project to `12`. Windows reads versions `1`–`12`. The current macOS implementation reads `1`–`11` and rejects version `12` before replacing the live document. It stores document UUID, pixel dimensions, active layer UUID, and layers in bottom-to-top order. Each layer stores its UUID, name, visibility, transform (origin, size, clockwise rotation, flips, sampling), and optional image filename. Blank layers have no image asset.

Embedded PNGs preserve source pixels and transparency; transforms remain separate. Projects survive moving or deleting imported source photos. Saving uses a coordinated atomic package replacement. Unsupported versions, invalid metadata, missing assets, unsafe paths, and oversized data are rejected before replacing the live document.

## Version 12: layer animation (Windows)

An optional top-level `timeline` object stores required integer `durationFrames` (1–216000),
required integer `framesPerSecond` (1–120), optional `loop` (default true), and `tracks` (default
empty). Each track has required `layerID`, inclusive `inFrame` and exclusive `outFrame` inside
the duration. A layer without a track is visible for the whole duration. A track never overrides
the layer's own hidden flag or inherited visibility.

Tracks have optional arrays `positionX`, `positionY`, `scaleX`, `scaleY`, `rotation` and `opacity`.
Each array contains strictly increasing, unique keys with required integer `frame` and finite
numeric `value`. Frames must be inside the duration. `interpolation` defaults to `linear`; `hold`
and smoothstep `smooth` are also supported. The left key controls its outgoing segment. Values
are held outside the first/last key; an empty channel uses base layer metadata.

Positions are absolute centers in document pixels (±1000000), scales are base-size multipliers
(.001–1000), rotations are absolute clockwise degrees (±1000000, multiple turns allowed), and
opacity is 0–1. Evaluated dimensions are clamped to 1–300000. Folders and adjustments support
opacity and clips only. Linked placed masks follow the evaluated layer; independent masks retain
their placement. Source PNGs and base transforms remain unchanged by playback.

At most one track may reference each existing layer, with at most 40000 keys across the timeline.
Null channels, invalid references, duplicate tracks, unordered keys and invalid values are
rejected. The existing manifest byte limit still applies. Animation metadata participates in Undo
and captured project saves; current frame, playback, panel visibility and zoom are session-only.
Clearing animation from a v12 project does not downgrade its declared version. macOS support for
v12 is not implemented; retaining its v11 reader limit prevents silently discarding animation.

## Limits and earlier versions

Limits: 30,000 pixels per canvas/image/mask side, 200 million pixels per surface, 10,000 layers, 4 MiB manifest, 512 MiB per encoded asset. Images and masks share one document pixel budget: `min(800_000_000, max(200_000_000, physicalMemoryBytes / 16))`. See `DocumentLimits.swift` and `ProjectStore.swift` for validation.

Undo history and viewport are session-only. Opening fits the canvas, restores selection, and starts with clean history. Future editable features must extend the schema and round-trip tests. PNG export is a flattened derivative and does not mark project edits saved.

Image Size adds optional `resolution` (pixels/inch, 1–9600). Older manifests without it default to 72. This additive field retains version 1 compatibility. Both PNG and JPEG exports include document resolution metadata. Resampling stores the new layer pixels and bounds; undo retains the prior sources only during the current session.

Version 2 adds optional `parentID` and `isGroup` on layer records. A group has no image file. Root nodes have no parent; children refer to an existing group. Array order defines bottom-to-top sibling order; renderers traverse each group as a contiguous subtree. Visibility is inherited without changing child flags. Cycles, missing/non-group parents, image-bearing groups, and nesting beyond 64 ancestor levels are rejected. Group ancestors permit room for leaf nodes at the deepest level. Group metadata survives image/canvas resizing and cropping. Older app builds reject version 2 rather than misrender grouped documents. Collapse state is not serialized.

Version 3 adds optional per-layer `opacity` (finite 0–1) and `blendMode` (Normal, Multiply, Screen, Overlay, Darken, Lighten, Difference, Color Dodge, Color Burn). Missing fields default to full opacity and Normal. Group records required those defaults until version 8, which lets a folder carry its own opacity; a folder's opacity multiplies into every layer inside it, while its blend mode stays Normal because folders are pass-through. Effects are applied during compositing and retained as metadata when resizing sources. Files declaring older versions cannot contain non-default appearance values.

Version 4 adds optional `maskFile` and `maskEnabled` fields to individual layers. Mask filenames must be `<layer UUID>.mask.png` under `images/`; enabled defaults to true when a mask exists. Records without masks omit both fields. Groups cannot carry masks in this version. Files declaring versions 1–3 cannot contain mask metadata.

Masks store 8-bit grayscale coverage without alpha (white reveals, black hides). Their normalized extent matches the image’s local rectangle, so the same layer transform applies to both. A uniform 1×1 mask is valid and avoids allocating full-resolution pixels before painting. Nonuniform mask pixels and a thumbnail are immutable assets shared by history. Image Size resamples them with the image transform; Canvas Size and Crop preserve their pixels. Mask pixels count toward the same document budget as image pixels; masks also obey the per-surface, per-side and per-file limits above. Disabled masks remain embedded and editable but do not affect compositing. Image-versus-mask target selection is session-only and reopens on image pixels.

Version 5 adds optional `maskSourceID`: the UUID of a non-group layer supplying live alpha in document coordinates. It multiplies the target’s alpha alongside its enabled raster mask. Source pixels, transform, opacity, raster mask and upstream live masks contribute coverage; visibility and RGB color do not. Sources remain independent layers. Missing references, self-links, cycles, group endpoints and chains over 256 nodes are rejected. Deletion can bake the live coverage into dependent image pixels (retaining their raster masks) or remove the links, as one undoable operation. Links survive image/canvas resize and crop. Older versions default to no live mask; older app builds reject v5.

UI terminology: these alpha links are clipping masks. Option-click assigns the lower sibling’s base or releases the connection. Multiple clipped layers share one base, show indented above it, and release when moved outside the contiguous stack. The underlying `maskSourceID` representation is unchanged.

Version 6 allows `maskFile` and `maskEnabled` on group records. A folder has no image, so its mask covers the folder's own transform rectangle (the canvas size when the folder was created); Image Size resamples it through that transform, and Canvas Size and Crop preserve its pixels, exactly as for layer masks. Groups are pass-through, so an enabled folder mask multiplies the coverage of every descendant layer, together with that layer's own mask and any enclosing folders' masks; clipping-mask coverage is unaffected. Files declaring versions 1–5 cannot give a group a mask, and older app builds reject v6.

Version 7 adds adjustment layers: a layer record with an optional `adjustment` object and no `imageFile`. Adjustment layers cannot be groups and cannot carry `text`; like any layer they take a transform, opacity, blend mode, raster mask and clipping link, and they affect everything composited below them within their folder. `kind` is one of `Hue/Saturation`, `Levels`, `Curves`, `Exposure`, `Gradient Map`, `Grain`, `Invert`, `Black & White` and `Color Balance` (version 9 adds three more). The record carries the settings of every kind, each optional and defaulting to an identity adjustment: `hue` (±360), `saturation` and `lightness` (±100), `colorize` and `hsvSettings` for Hue/Saturation; `levels` (four channel ranges, RGB then red, green, blue); `curves` (four channel point lists); `exposureSettings`; `gradientMapSettings` (`shadows`, `highlights`, `reversed`); `grainSettings`; `blackWhiteSettings`; `colorBalanceSettings`. Out-of-range or non-finite values are rejected. Files declaring versions 1–6 cannot contain adjustment records, and older app builds reject v7. See `LayerAdjustment.swift` for the exact ranges.

Version 8 lets a folder carry its own `opacity`, which multiplies into every layer inside it; a folder's blend mode stays Normal because folders are pass-through (files declaring 1–7 require folders at full opacity). It also adds an optional top-level `guides` array of alignment guides, each with `id`, `axis` (`horizontal` or `vertical`) and `position` in document pixels (finite, at most 1,000,000 in magnitude). At most 1,000 guides are stored; files declaring 1–7 cannot contain guides. Guides survive Canvas Size and Crop by offsetting with the canvas.

Version 9 adds three adjustment kinds that sample neighboring pixels: `Gaussian Blur` (`blurRadius`, 0.1–250 document pixels), `Motion Blur` (`motionAngle`, −90 to 90 degrees, and `motionDistance`, 1–2000) and `Add Noise` (`noiseAmount`, 0.1–400, `noiseGaussian`, `noiseMonochromatic` and `noiseSeed`, so the pattern is stable between sessions). Files declaring 1–8 cannot contain these kinds; the earlier adjustment kinds remain valid at version 7 and up.

Version 10 lets a text layer color some of its letters differently: optional `colorRuns` in its `text` metadata (see Editable text). Files declaring 1–9 cannot contain it.

Version 11 lets those letters use different faces too: optional `fontRuns` in the same metadata. Files declaring 1–10 cannot contain it. `colorRuns` stays valid from version 10.

### Additive layer fields

Later fields are optional and not gated on the version, so older readers ignore them and keep the pixels or the linked mask as they were:

- `maskPlacement` and `maskLinked`: an unlinked mask (`maskLinked` false; missing means linked) keeps its own transform in `maskPlacement`, a document-space rectangle like the layer transform, and no longer follows the layer when it moves. Both require a `maskFile`.
- `shape`: a layer made with the Shape tool keeps its style (`kind`, `red`/`green`/`blue`, `cornerRadius` in document pixels, and for lines `lineWidth` plus `start` and `end` as fractions of the layer box) so it redraws cleanly when scaled. Its PNG is still an ordinary raster; once anything else changes those pixels the metadata is dropped.

### Editable text

Pixel layer records may include optional `text` metadata: content, PostScript font name, font size in pixels, RGB color, alignment, tracking, line spacing and optional `boxSize` paragraph bounds. Text wraps inside these bounds; changing them reflows the text without scaling the font. The PNG remains the display and export fallback. Older readers ignore this metadata. Transforms, duplication, masks and canvas-size changes preserve it; destructive pixel operations rasterize text and omit the metadata on the next save. Missing fonts use the system font when edited, while the saved PNG preserves the original appearance until then. From version 10, optional `colorRuns` lists letters painted in another color than the text's own `red`/`green`/`blue`: each run has `location` and `length` in UTF-16 units of the content, plus `red`, `green` and `blue` (0–1). From version 11, optional `fontRuns` lists letters set in another face than `fontName`: the same `location` and `length`, plus `fontName`. Runs of either kind are sorted, do not overlap, have a positive length and end within the content; letters outside every run use the text's color or face.

### Layer effects

An optional `effects` record contains independent `stroke`, `shadow`, `colorOverlay`, `innerShadow`, `outerGlow` and `innerGlow` records. Stroke carries a size (0–500 layer pixels), a color, an opacity and an `inside` flag choosing which side of the edge it sits on; drop shadow and inner shadow each carry an angle, a distance, a blur, a color and an opacity; color overlay carries a color and an opacity; outer glow and inner glow each carry a size (0–500 layer pixels), a color and an opacity. Each supports optional `enabled` visibility (missing means visible); hidden effects keep all parameters and remain listed under their layer. Effects, including their visibility, are saved and participate in document undo. Canvas previews run on a serial background worker with a shared pixel budget; exports render the full-resolution effects. A record omitting an effect means that layer does not have it, so older readers see the effects they understand and ignore the rest.
