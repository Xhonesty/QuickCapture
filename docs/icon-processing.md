# 0.4.0 图标透明边缘处理

使用内置 image_gen 工具的编辑模式，将提供图标的外围白边改为透明背景。保留黑色圆角底板和绿色图案，输出为 1254 × 1254 RGBA PNG。四角 alpha 为 0。

项目文件：`src/Assets/app.png`；通过 ICO 格式转换生成 `src/Assets/app.ico`，包含 16 / 24 / 32 / 48 / 64 / 128 / 256 像素版本。原图保留在 Git 提交 `2d74757` 的同一路径中。

工具参数：`transparent_background: true`，引用本项目原有 `src/Assets/app.png`。未使用 CLI 模式。

最终提示词：

```text
Use case: background-extraction. Asset type: Windows application icon. Input image: the provided local image is the exact existing QuickCapture app icon to edit, not merely a style reference. Primary request: Remove only the white exterior margin and white exterior background around the rounded black square, replacing all exterior area with actual alpha transparency. Preserve the rounded black square, its size and curvature, every neon green/cyan/yellow gradient capture corner, speed streak, Q ring and scissors, their proportions and locations, and the black interior holes. Do not redraw, reinterpret, add elements, recolor, add shadows, or change the logo. Preserve the existing canvas framing, with the rounded black square nearly filling the square canvas. The outside corners must be fully transparent with clean antialiased edges and absolutely no white outline or halo. This is a precise background removal edit.
```
