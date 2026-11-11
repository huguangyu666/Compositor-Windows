# Compositor Windows 版

下载、功能介绍、构建命令和致谢见 [中文项目首页](../README.zh-CN.md)。

- [用户指南](docs/user-guide.md)
- [MCP 与配套 Skill](docs/mcp.md)
- [AI 集成](docs/ai-integration.md)
- [可编辑 MCP 海报样品](samples/README.md)
- [文档索引](docs/README.md)
- [构建与验证](docs/testing.md)
- [单文件 EXE 发布配置](docs/portable-build.md)

源码位于 `src/`，测试位于 `tests/`，解决方案为 `Compositor.Windows.slnx`。

本版新增 [GPU 加速与 CPU 回退](docs/gpu-rendering.md)、[图层动画时间轴](docs/timeline.md)，并统一中英文自动适配。

- Agent 可用自己的生图工具，再经 MCP 精确放入图层或回传选区修改预览，接受后一步撤销；软件无需填写 API Key。
- 支持模型编写 SVG 并导入透明图层。
- 可选本地 SAM 2.1 点选/框选对象，支持 DirectML/CPU 与空心圆光标。
- 优化时间轴拖动与播放指针，帧内移动及时跟手。

[External-agent generation](docs/agent-image-mcp.md) · [SVG](docs/svg-mcp.md) · [Object Selection](docs/object-selection-2026-11-06.md)
