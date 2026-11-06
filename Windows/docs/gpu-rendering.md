# GPU canvas rendering and CPU fallback

**View > GPU Acceleration (Automatic Fallback)** defaults to enabled and remembers your choice.
Compatible Direct3D 11 hardware can accelerate canvas composition, including integrated graphics.
Turning the switch off releases GPU resources and uses the complete CPU compositor.

The GPU path covers all 24 blend modes, 12 adjustment kinds, six layer effects, nearest/smooth/HQ
sampling, arbitrary affine rotation, opacity, raster/folder masks and live clipping chains. It is
used for ordinary frames, settled frames, layer-animation frames and draft composition. Cached
source textures and known pixel/mask dirty regions reduce repeat uploads while dragging.

Unavailable hardware/features, software adapters, device errors and memory limits fall back to
CPU. Texture memory uses a bounded LRU budget; large projects can cause cache churn. There is no
general tile cache. Heavy 4K effects may exceed that budget and remain noticeably slower on CPU.
The switch does not promise constant 60/120 fps on every document or device.

Saving/export, image decoding, source-pixel tools, perspective pixel preparation and solo-mask
display still use CPU processing. Gaussian noise prepares its deterministic random basis once on
the serial CPU worker and caches it for GPU composition. Existing CPU dirty-region and vector
optimizations remain useful, including when the GPU path is enabled.

Local November 6 development checks compared 365 whole GPU images with the CPU reference without
differing bytes, and exercised device reconstruction, software-device fallback, budget eviction,
dirty uploads and padded rows. These checks used an RTX 4070 Laptop GPU; integrated GPUs, a
physically GPU-less computer and a clean Windows installation were not separately verified.

The native host uses [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) and Win2D.
The [Vortice MIT notice](../third-party/Vortice-LICENSE.txt) is included. Shader sources and compiled
SM5 bytecode are in `src/Compositor.App/Shaders/`. To regenerate bytecode with Windows SDK fxc:

```powershell
pwsh -NoProfile -File Windows/tools/compile-preview-shaders.ps1
```

The default SDK version is 10.0.26100.0; use `-SdkVersion` for another installed version.
Normal application builds use the checked-in bytecode and do not need shader regeneration.
