# Notes for contributors and agents

This repository contains Compositor for Windows, a C# / WinUI 3 image editor. Implementation,
tests, tools and documentation live under `Windows/`. Keep the existing upstream MIT license.

- Work in the current checkout; create another worktree only when requested.
- Follow the nearest `AGENTS.md`, surrounding code style and American spelling in code/UI.
- Build with .NET SDK 9 from `Windows/ci`, using the commands in `README.md`.
- Run `./Windows/ci/test-windows.ps1` for headless component tests.
- Read `Windows/docs/project-format.md` before changing serialization or project storage.
  Preserve asset-first, atomic-manifest-last writes and document format changes explicitly.
- Use the MCP tools and `Windows/skills/compositor-mcp/SKILL.md` when editing artwork in the
  live application. Read `docs/writing-comp-files.md` for explicitly offline package authoring.
- Keep credentials, personal material, build output and local runtime evidence out of Git.
