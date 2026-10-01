# 轻截 · QuickCapture

Windows 个人截图与录屏工具。C# / WPF，截图及录屏使用 Windows.Graphics.Capture；通过 ScreenRecorderLib 使用 Media Foundation 实时编码和 WASAPI 音频采集。

## Git 仓库

GitHub：<https://github.com/Xhonesty/QuickCapture>（私有仓库），默认分支 `main`。

```powershell
git clone https://github.com/Xhonesty/QuickCapture.git
cd QuickCapture
```

仓库包含源码、文档和构建脚本；SDK、依赖缓存、构建产物、截图、录音及本地设置均由 `.gitignore` 排除。新电脑克隆后先安装 .NET 10 SDK，再运行 `tools/build.ps1` 生成 `dist`。便携版压缩包在本机 `artifacts` 目录中。

## 开始使用

1. 打开 `dist` 文件夹，双击 `QuickCapture.exe`。发布版自带 .NET，不需要安装开发 SDK。
2. `Ctrl+Alt+S`：按主界面当前模式截图，默认框选区域。松开鼠标后保留原位置选框，在选区旁的工具栏中标注、复制或保存。
3. 编辑器中 `Enter` 或 `Ctrl+C` 复制，`Ctrl+S` 保存，`Ctrl+Z` 撤销，`Ctrl+Y` 重做，`Esc` 取消。
4. `Ctrl+Alt+R`：按当前模式开始录屏；再次按下停止并保存。也可点录制计时条的“停止并保存”。
5. 关闭主窗口后驻留托盘。双击托盘图标重新打开，右键托盘菜单可退出。退出会先完成正在录制的视频。

系统声音和麦克风分别勾选，默认均关闭。默认 30 FPS、H.264 / MP4、显示鼠标。可选择 15 / 30 / 60 FPS。

主界面右上角可切换浅色 / 深色界面，也可在设置中选择。主题立即生效，并在下次启动时保留。

首次启动时，若默认快捷键被其他软件占用，会自动尝试 `Ctrl+Alt+F8 / F9` 等备用组合；实际快捷键显示在主界面。已有设置的快捷键冲突时会显示提示，可在设置中修改。修改失败时保留先前成功注册的快捷键。

## 已有功能

- 区域、窗口、整个桌面截图（包括多屏桌面）。
- 区域截图保留原位置选框并直接标注；浮动工具栏优先放在选区下方，靠近边缘时自动调整到上方或侧面。选满屏幕时放在屏幕内，可点击“重新框选”重选。窗口及整个桌面截图使用独立编辑器。
- 浅色 / 深色主题切换，覆盖主界面、标注工具栏、编辑器及设置窗口。
- 箭头、矩形、文字、马赛克；撤销和重做。PNG 导出保持原始像素，不受预览缩放影响。
- 复制截图、自动保存和置顶贴图。贴图左键拖动，右键或 Esc 关闭。
- 区域、窗口、鼠标所在显示器录屏；重复上次区域。
- 系统音频 / 麦克风混音，硬件编码开关。
- 托盘、可配置全局快捷键、最近文件列表，双击文件通过系统默认应用预览。

## 文件位置

- `Captures`：默认截图和视频目录，也可在设置中修改。
- `Data/settings.json`：设置和上次录屏区域，原子替换保存。
- `Data/errors.log`：应用错误。
- `Data/recorder.log`：最近一次录制日志，每次录制重置。
- `*.partial.mp4`：正在写入或未成功完成的视频。正常停止并完成编码后转为正式 `.mp4`，未完成文件不会出现在最近列表。异常断电或强制结束可能损坏未完成视频。

移动或分享程序时，请复制整个 `dist` 文件夹；DLL 和运行时文件不可只留 EXE。程序目录需可写。移动文件夹后如保存位置仍指向原目录，可在设置中重新选择。

## 系统要求与当前范围

- 面向 Windows 10 22H2 / Windows 11、x64；使用 Windows.Graphics.Capture 要求 Windows 10 1903 或更新版本。
- 需要 Microsoft Visual C++ 2015–2022 x64 运行库；本机已安装。缺失时从 [Microsoft 官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 获取。
- Windows N / KN 版本需要 Media Feature Pack。
- 区域选择限于同一显示器；整个桌面截图可包含所有显示器。物理像素坐标转换支持不同 DPI 及负坐标显示器。
- 窗口录制保持窗口未最小化；录制窗口可移动，输出尺寸在开始时固定。
- H.264 输出使用偶数尺寸；奇数宽高会调整至邻近的较小偶数尺寸。
- Windows 可能显示系统捕获边框。录制计时条通过 Windows 排除捕获接口避免入镜，Windows 10 2004 及以上支持该排除行为。
- 麦克风使用 Windows 默认输入设备；需在 Windows 隐私设置中允许桌面应用访问麦克风。音频设备变更或断开后请停止并重新开始录制。
- 当前未提供滚动截图、OCR、暂停、视频裁剪、云端分享和开机自启。

## 开发与构建

安装 .NET 10 SDK 后，在项目目录运行：

```powershell
.\tools\build.ps1
```

本次环境已在 `.tools/dotnet` 放置独立 SDK，并在 `.tools/feed` 缓存所需 NuGet 包，可离线构建：

```powershell
.\tools\build.ps1 -Offline
```

源代码在 `src`，无需 Visual Studio。常用入口：

| 文件 | 职责 |
| --- | --- |
| `MainWindow.xaml` / `.cs` | 主界面、托盘、截图录屏流程 |
| `SelectionWindow.cs` / `ToolbarPlacement.cs` | 各显示器原位选区、工具栏避让与 DPI 坐标转换 |
| `AnnotationSurface.cs` / `ScreenshotEditor.cs` / `EditorWindow.cs` | 共享标注操作、导出、复制、贴图 |
| `ThemeService.cs` / `App.xaml` | 浅色与深色资源、即时主题切换 |
| `CaptureService.cs` / `RecorderService.cs` | 原生采集、实时编码、音频与文件完成 |
| `HotkeyService.cs` / `Settings.cs` | 快捷键注册和本地设置 |

## 集成验证

```powershell
.\tools\build.ps1 -Offline -Test
# 加测默认麦克风与系统声音混音（会短暂采集麦克风）：
.\tools\build.ps1 -Offline -Test -Microphone
# 仅验证主题和原位截图界面，不采集录屏及音频：
.\dist\QuickCapture.exe --self-test --ui-only
```

完整测试会短暂显示动态验证窗口及选区，获取桌面图像并生成约 3 秒的视频；诊断文件保存在 `dist/Diagnostics`，界面预览保存在 `artifacts/screenshots`。仅界面检查使用生成的示例桌面，设置和保存文件也使用诊断目录。测试需要普通交互桌面中的系统采集权限，受限进程可能无法正确访问窗口、鼠标或快捷键。

`Diagnostics/results.json` 为结果，`display.json` 记录测试 DPI。覆盖快捷键冲突回滚、标注与撤销、PNG 原始尺寸和马赛克替换、负坐标、当前 DPI 下选区、原生截图、窗口/区域录屏、奇数尺寸硬件编码与系统声音；新增主题切换及持久化、工具栏边缘位置、原位编辑像素和重新框选检查。最终交付验证结果见发布目录中的 `verification.md`，源项目中位于 `docs/verification.md`。
