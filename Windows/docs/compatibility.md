# Compatibility and known limitations

## Windows

The downloadable Beta targets x64 and bundles .NET and Windows App SDK runtimes. It was tested on
a Windows 11 development computer. The source declares Windows 10 version 1809 as its minimum,
but that minimum and clean-machine installation have not been separately verified. ARM64 is not
included in this release. There is no signature, installer or automatic update service.

## Files

- Native `.comp` folders retain layers, masks, text and adjustment metadata. Versions 1–11 are
  read; new projects use version 11. Saving does not automatically upgrade an old manifest.
- PSD/PSB import supports a subset of 8-bit RGB documents. Arbitrary Photoshop features and
  vector/advanced content are not guaranteed to reproduce exactly. PSD/PSB export is unavailable.
- PNG/JPEG/TIFF and other supported Windows image formats can be imported as raster layers.
  PNG/JPEG export creates a flattened image.
- RAW development, optional HEIC/SVG decoding and full color-managed print workflows are not
  established capabilities of this Beta.

## Editing and automation

The renderer is CPU based. Large selections, many layers and complex effects can still have
noticeable latency. Some editable shape and transform interactions remain narrower than Photoshop.
Fonts and antialiasing may differ across systems; preserve native projects and inspect exports.

Undo history is session-only. Layer locks are local preferences, not portable `.comp` metadata.
Live MCP has no new-document command; open an existing project or create a blank document in the UI.
MCP returns canvas images, not screenshots of the full app window.

## AI and interchange

Custom API compatibility depends on the provider. Account OAuth is experimental, and reference
images or preservation prompts do not guarantee subject identity. Local selection protection
does not limit uploads to that region: the complete composite is sent to the chosen provider.

The native format follows upstream Compositor. Cross-platform pixel equivalence and arbitrary
third-party document compatibility have not been fully verified. Test exchanges with copies.
