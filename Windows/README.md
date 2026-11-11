# Compositor for Windows

See the [project homepage](../README.md) or [中文说明](../README.zh-CN.md) for downloads, features,
build commands and credits.

- [User guide](docs/user-guide.md)
- [MCP and companion skill](docs/mcp.md)
- [AI integration](docs/ai-integration.md)
- [Editable MCP sample](samples/README.md)
- [Documentation index](docs/README.md)
- [Build and verification](docs/testing.md)
- [Single-file publish profiles](docs/portable-build.md)

The implementation is under `src/`; component tests are under `tests/`. The solution is
`Compositor.Windows.slnx`. Build output belongs in ignored `artifacts/`, `bin/` and `obj/` folders.

This version adds [GPU composition with CPU fallback](docs/gpu-rendering.md), a
[layer animation timeline](docs/timeline.md) and consistent automatic Chinese/English localization.

- Generate with the agent's own image tools and return PNGs through MCP, with precise placement, selection-protected previews and one Undo; no editor API key is required.
- Import model-authored SVG as transparent raster layers.
- Use optional local SAM 2.1 point/box object selection, DirectML/CPU inference and a hollow-circle cursor.
- Scrub a smoother timeline with immediate subframe pointer movement and continuous playback indication.

[External-agent generation](docs/agent-image-mcp.md) · [SVG](docs/svg-mcp.md) · [Object Selection](docs/object-selection-2026-11-06.md)
