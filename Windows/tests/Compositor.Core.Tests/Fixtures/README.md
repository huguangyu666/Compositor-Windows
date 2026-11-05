# Spot healing reference fixtures

`spot-healing-reference.json` records SHA-256 hashes from the unmodified macOS
`Compositor/Rendering/HealPixels.c`, compiled with MSVC x64 `/O2 /fp:precise`.
`HealingTestImages.cs` creates seven inputs for all three modes: a central spot,
an edge, partial alpha/coverage, multiscale texture fallback, multiscale patch
search, fully covered/no-ring input, and padded rows. The fixed seed is
`0x13579bd`, and opacity is the C float `.65f`.

To regenerate with Visual Studio C++ build tools and .NET 9 installed, run from
the repository root:

    pwsh -NoProfile -File Windows/tools/generate-healing-reference.ps1

The optional generator builds the original C code in ignored `Windows/artifacts`.
The app, MCP server, and regular tests use the managed implementation and do not
need a native DLL or C++ toolchain. These fixtures verify kernel pixels; they do
not verify Core Graphics selection rasterization or Windows mouse workflows.

# P78 gradient frames

`gradient-p78-reference.json` contains 24 frame hashes captured before optimization
from the exact `GradientEdit.cs` in commit `e5ff3ab`. `GradientScenario.cs` defines
the inputs: pixel/mask targets, linear/radial, transparency, reverse, selection
feather, rotations/flips, grid expansion, attached/placed masks and padded rows.
The fixed endpoints are `(3.12, 2.43)` and `(37.62, 24.14)`.

The local capture used `git show e5ff3ab:Windows/src/Compositor.Core/Editing/GradientEdit.cs`
as a separate reference class in ignored `Windows/artifacts/gradient-reference`,
then hashed the output asset bytes. These hashes lock the old Windows pixels;
they do not claim Core Graphics equivalence or UI performance.
