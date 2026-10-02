# 轻截 · QuickCapture

Windows 个人截图与录屏工具。C# / WPF，使用 Windows 原生采集接口；通过 ScreenRecorderLib 使用 Media Foundation 实时编码和 WASAPI 音频采集。当前版本 0.6.0。

## Git 仓库

GitHub：<https://github.com/Xhonesty/QuickCapture>（私有仓库），默认分支 `main`。

```powershell
git clone https://github.com/Xhonesty/QuickCapture.git
cd QuickCapture
```

仓库包含源码、文档和构建脚本；SDK、依赖缓存、媒体工具二进制、构建产物、截图、录音及本地设置均由 `.gitignore` 排除。新电脑克隆后先安装 .NET 10 SDK，运行 `tools/setup-media.ps1` 获取固定版本 FFmpeg，再运行 `tools/build.ps1` 生成 `dist`。便携版压缩包在本机 `artifacts` 目录中，已包含媒体工具。

## 开始使用

1. 打开 `dist` 文件夹，双击 `QuickCapture.exe`。发布版自带 .NET，不需要安装开发 SDK。
2. `Ctrl+Alt+S`：按主界面当前模式截图，默认框选区域。松开鼠标后保留原位置选框，在选区旁的工具栏中标注、复制或保存。
3. 编辑器中默认处于裁剪工具：拖动八个手柄调整大小，拖动内部移动选区；方向键移动 1 原始像素，Shift+方向键移动 10。标注在原图上保留，裁剪范围外只隐藏，可扩大范围或撤销恢复。`Enter` / `Ctrl+C` 复制，`Ctrl+S` 打开格式保存窗口，`Ctrl+Z / Y` 撤销 / 重做，`Esc` 取消。
4. `Ctrl+Alt+R`：按当前模式开始录屏；再次按下停止并打开编辑 / 导出窗口。拖动时间轴两端裁切长度，选择倍率、格式和质量再导出。也可点击计时条的“停止并保存”。
5. 关闭主窗口后驻留托盘。双击托盘图标重新打开，右键托盘菜单可退出。退出会先完成正在录制的视频。

系统声音和麦克风分别勾选，默认均关闭。默认 30 FPS、H.264 / MP4、显示鼠标。可选择 15 / 30 / 60 FPS。

主界面右上角可切换浅色 / 深色界面，也可在设置中选择。主题立即生效，并在下次启动时保留。

工具栏全部使用 Lucide 线性图标，悬停查看名称和快捷键：`C` 裁剪、`R` 矩形、`A` 箭头、`T` 文字、`B` 涂鸦、`M` 马赛克、`K` 颜色、`Z / Y` 撤销 / 重做、`P` 贴图、`N` 重新框选、`S` 保存、`V` 复制、`Esc` 取消。现有 Ctrl 快捷键保留，文本输入时不会切换工具。“重新框选”清除已有标注前会提示；普通二次裁剪保留标注。

截图格式 PNG / JPG / WebP / BMP；录屏格式 MP4 / WebM / GIF，两套默认格式和质量在设置中分组配置。保存窗口临时选择不会改变默认值，扩展名自动匹配。复制和自动保存仍使用默认截图格式，不弹出保存窗口；剪贴板同时提供标准位图兼容性。

录屏支持 P0 时间裁切、P1 0.5× / 1× / 1.5× / 2× 变速，以及静音和专门调色板流程导出 GIF。GIF 无声音，支持帧率与质量选择。取消导出后原片保留，双击最近文件的“待导出”项目继续；退出只完成录制，下次再导出。详见 [媒体工具与导出说明](docs/media-tools.md)。

截图卡片中的“自动吸附窗口”默认开启，也可在设置中关闭。鼠标悬停会预选可见窗口，单击进入原位标注；按住并拖动始终自由框选。窗口跨屏时截取当前显示器内的部分。

录屏卡片也提供独立的“自动吸附窗口”开关，默认开启。在录屏的“框选区域”模式中，悬停预选窗口、单击开始录制；拖动仍可自由框选。吸附确认后按该固定区域录制。

标注工具栏提供“涂鸦”，可选择颜色和画笔粗细。鼠标悬停在“涂鸦”按钮上时，粗细面板在按钮下方展开；悬停在“马赛克”上时，展开“框选马赛克 / 涂鸦马赛克”选择，涂鸦模式还显示画笔粗细。移入面板及下拉框可继续选择，移开后自动收起。选择粗细或模式会启用对应工具，单次笔迹可整体撤销。

首次启动时，若默认快捷键被其他软件占用，会自动尝试 `Ctrl+Alt+F8 / F9` 等备用组合；实际快捷键显示在主界面。已有设置的快捷键冲突时会显示提示，可在设置中修改。修改失败时保留先前成功注册的快捷键。

修改快捷键：打开“设置”，点击截图或录屏快捷键输入框，直接按下组合键，例如按住 `Ctrl`、`Alt` 再按 `S`，输入框会显示 `Ctrl+Alt+S`。需包含至少一个修饰键（Ctrl / Alt / Shift / Win）和一个普通按键，支持字母、数字及功能键。按 `Esc` 撤销当前输入框本次录入，`Tab / Shift+Tab` 切换输入框。点击“保存设置”后生效；“取消”保留原设置。设置窗口打开期间，应用已有快捷键用于录入，不会触发截图或录屏；原组合仍保留注册，保存时检查重复和占用。

## 已有功能

- 区域、窗口、整个桌面截图（包括多屏桌面）。
- 区域截图保留原位置选框并直接标注；浮动工具栏优先放在选区下方，靠近边缘时自动调整到上方或侧面。选满屏幕时放在屏幕内，可点击“重新框选”重选。窗口及整个桌面截图使用独立编辑器。
- 浅色 / 深色主题切换，覆盖主界面、标注工具栏、编辑器及设置窗口。
- 箭头、矩形、文字、涂鸦；框选或涂鸦式马赛克，可调画笔粗细；统一标注 / 裁剪撤销和重做。PNG / BMP 导出保持原始像素，不受预览缩放影响。
- 复制截图、自动保存和置顶贴图。贴图左键拖动，右键或 Esc 关闭。
- 区域、窗口、鼠标所在显示器录屏；重复上次区域。
- 区域录屏显示贴合选区的红色边框，鼠标可穿透边框操作原窗口；停止或录制失败后关闭边框。
- 系统音频 / 麦克风混音，硬件编码开关。
- 录屏停止后预览、时间轴裁切、变速、静音与多格式导出；缺媒体工具时仍可保存完整原生 MP4。
- 托盘、按键录入的全局快捷键设置、最近文件列表，双击文件通过系统默认应用预览。

## 文件位置

- `Captures`：默认截图和视频目录，也可在设置中修改。
- `Data/settings.json`：设置和上次录屏区域，原子替换保存。
- `Data/errors.log`：应用错误。
- `Data/recorder.log`：最近一次录制日志，每次录制重置。
- `Data/Recordings`：等待导出的录屏母版与恢复元数据；取消 / 失败保留，成功导出后清理对应母版。
- `Tools/media`：固定版本 FFmpeg / ffprobe 捆绑备用；检测时优先指定目录与系统 PATH。
- `*.partial.mp4`：正在写入或未成功完成的视频。正常停止并完成编码后转为正式 `.mp4`，未完成文件不会出现在最近列表。异常断电或强制结束可能损坏未完成视频。

移动或分享程序时，请复制整个 `dist` 文件夹；DLL 和运行时文件不可只留 EXE。程序目录需可写。移动文件夹后如保存位置仍指向原目录，可在设置中重新选择。

## 系统要求与当前范围

- 面向 Windows 10 22H2 / Windows 11、x64；使用 Windows.Graphics.Capture 要求 Windows 10 1903 或更新版本。
- 需要 Microsoft Visual C++ 2015–2022 x64 运行库；本机已安装。缺失时从 [Microsoft 官方页面](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) 获取。
- Windows N / KN 版本需要 Media Feature Pack。
- 区域选择限于同一显示器；整个桌面截图可包含所有显示器。物理像素坐标转换支持不同 DPI 及负坐标显示器。
- 窗口录制保持窗口未最小化；录制窗口可移动，输出尺寸在开始时固定。
- H.264 输出使用偶数尺寸；奇数宽高会调整至邻近的较小偶数尺寸。
- 区域录屏使用 Desktop Duplication 裁剪，避免 Windows.Graphics.Capture 围住整块显示器的系统边框。窗口及整屏录制仍可能显示对应目标的系统捕获边框。
- 区域录制边框绘制在选区外；边框和计时条还通过 Windows 排除捕获接口避免入镜，Windows 10 2004 及以上支持该排除行为。
- 麦克风使用 Windows 默认输入设备；需在 Windows 隐私设置中允许桌面应用访问麦克风。音频设备变更或断开后请停止并重新开始录制。
- 当前未提供滚动截图、OCR、暂停、多段拼接、提取音频、MKV / MOV、云端分享和开机自启。

## 开发与构建

安装 .NET 10 SDK 后，在项目目录运行：

```powershell
.\tools\setup-media.ps1
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
| `HoverToolOptions.cs` | 按钮下方的悬停设置面板、下拉选择与自动收起 |
| `ThemeService.cs` / `App.xaml` | 浅色与深色资源、即时主题切换 |
| `WindowSnapper.cs` / `Native.cs` | 窗口层级、可见边界与悬停吸附 |
| `RecordingFrame.cs` | 精确选区录屏边框、鼠标穿透与捕获排除 |
| `Assets/app.png` / `app.ico` | 去白边的透明图标及 16–256 像素多尺寸 ICO；处理提示词见 `docs/icon-processing.md` |
| `CaptureService.cs` / `RecorderService.cs` | 原生采集、实时编码、音频与文件完成 |
| `HotkeyService.cs` / `Settings.cs` | 快捷键注册和本地设置 |
| `CropHandles.cs` / `ToolCatalog.cs` / `IconSet.cs` | 二次裁剪、配置驱动图标与工具快捷键 |
| `ImageExportService.cs` / `ScreenshotSaveWindow.cs` | 四种图片格式、质量及剪贴板兼容性 |
| `RecordingEditWindow.cs` / `TrimTimeline.cs` | 录屏预览、时间段与导出交互 |
| `MediaTools.cs` / `VideoExportService.cs` / `RecordingRecovery.cs` | 依赖检测、转换、取消及母版恢复 |
| `DesignTokens.xaml` / `UiDesign.cs` | 统一布局与控件规范，见 [设计规范表](docs/design-spec.md) |

## 集成验证

```powershell
.\tools\build.ps1 -Offline -Test
# 加测默认麦克风与系统声音混音（会短暂采集麦克风）：
.\tools\build.ps1 -Offline -Test -Microphone
# 仅验证主题和原位截图界面，不采集录屏及音频：
.\dist\QuickCapture.exe --self-test --ui-only
# 单独复现新增截图检查或录屏导出检查：
.\dist\QuickCapture.exe --self-test --upgrade-only
.\dist\QuickCapture.exe --self-test --media-only
```

完整测试会短暂显示动态验证窗口及选区，获取桌面图像并生成约 3 秒的视频；诊断文件保存在 `dist/Diagnostics`，界面预览保存在 `artifacts/screenshots`。仅界面检查使用生成的示例桌面，设置和保存文件也使用诊断目录。测试需要普通交互桌面中的系统采集权限，受限进程可能无法正确访问窗口、鼠标或快捷键。

`Diagnostics/results.json` 为结果，`display.json` 记录测试 DPI。覆盖快捷键冲突回滚、标注与撤销、PNG 原始尺寸和马赛克替换、负坐标、当前 DPI 下选区、原生截图、窗口/区域录屏、奇数尺寸硬件编码与系统声音；界面检查覆盖主题持久化、工具栏边缘位置、原位编辑像素和重新框选，以及图标、曲线涂鸦、笔迹式马赛克、窗口吸附的开关和自由框选。快捷键录入检查使用实际按键，验证 Alt、数字、Esc、Tab、已有组合拦截、保存和取消；仅在验证窗口处于前台时发送测试按键。完整检查还验证实际录屏边框位置、焦点及区域采集像素。最终交付验证结果见发布目录中的 `verification.md`，源项目中位于 `docs/verification.md`。

新增检查比较二次裁剪后的整幅像素、各种标注与马赛克锚定、八个手柄实际拖动、1 / 10 px 按键移动、50% 缩放裁剪、四种图片的文件头与质量、实际剪贴板类型、默认值联动，以及带 440 Hz 音轨的视频裁切 / 变速时长与音调、完整解码、GIF 无音轨、取消及缺依赖降级。
