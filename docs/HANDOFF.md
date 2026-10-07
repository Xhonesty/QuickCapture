# 本轮追加交接 · 2026-10-07 · 本地 v0.11.0

以下新状态优先于后面的旧交接：音频设备/真实电平/试录、多段删除及恢复导出、截图版本项目/继续编辑/另存为已实现。本地程序集升级为 **0.11.0**，当前可运行程序是 `E:\TRY_IT\QuickCapture\dist\QuickCapture.exe`；需要整个 dist 文件夹。新独立便携包 `artifacts/QuickCapture-win-x64-v0.11.0-local.zip`；旧 `QuickCapture-win-x64.zip` 保留为发布的 v0.10.0。未提交、同步或发布 GitHub。

本轮 26/26 项分阶段与必要回归检查通过，包含有针对性的修复后复测；没有重跑全应用历史套件。当前说明以 [editable-media.md](editable-media.md)、[verification.md](verification.md) 和 `artifacts/editable-media-verification.json` 为准。最终构建日志 `artifacts/editable-final-build.log`。真实暂停音画差约 21ms；实体拔插、实际默认设备切换、真实语音听感与长录制仍需人工补查。

新增入口为录屏卡片“声音设备…”、录屏编辑器“删除中间片段”、最近图片“继续编辑”和主界面“编辑图片”。重点文件：`AudioMonitor.cs` / `AudioDeviceWindow.cs`、`VideoCuts.cs` / `RecordingEditWindow.cs` / `VideoExportService.cs`、`ScreenshotProjects.cs` / `ScreenshotEditor.cs` / `ScreenshotSaveWindow.cs`。音频增加本项目内的 NAudio.Wasapi/Core 2.2.1；离线 feed 和缓存保存在 .tools，许可证随构建复制。

母版 `Data/Recordings/master-*.mp4.json` 升为可选 Version=2/Edit，剪辑配置与原片同目录；未知版本禁止覆盖。外部录屏配置在 Data/RecordingEdits。截图 ZIP 项目及绝对导出路径关联在 Data/ScreenshotProjects；试录在 Data/AudioTest。界面均有明确清理入口，图片项目清理保留导出图片，母版删除说明会清理原片与配置。不要删除用户 dist/Data、Captures；自测只修改 Diagnostics。`docs/research/` 继续保留，未纳入交付。

自测参数：`--audio-devices-only`、`--video-cuts-only`、`--screenshot-projects-only`、`--screenshot-restart-only`；重启检查应在项目检查后使用同一构建目录。既有 `--recording-only`、`--recording-edit-only`、`--context-toolbar-only` 用于对应回归。Start-Process 的 --recheck 名称含空格时需在 ArgumentList 中保留整项引号，避免跳过计划复测的项。每次改动仍遵守只复测受影响路径的原则。

`tools/package.ps1` 新增 `-OutputFile`，限制 ZIP 输出在本项目 artifacts 内，可保留旧包另存新版本；构建/打包都不包含 Data、Captures、Diagnostics。新包没有发布到远端，继续保留未提交源码改动；不要把下面旧状态中的 v0.10.0 当作最新本地版本。

---

# QuickCapture 开发交接

交接日期：2026-10-07（Asia/Taipei）。本文件依据当前源码、Git 状态、已保存构建日志和验收结果整理；撰写本文件时未重新构建、运行测试或联网复核发布状态。

## 1. 接手时先了解的状态

**最新功能已实现并构建到本地 `dist`，但尚未提交、同步 GitHub、重新打包或发布。** 当前程序集版本仍为 0.10.0；仅凭版本号无法区分本地新构建和已发布的旧构建。

| 项目 | 当前状态 |
| --- | --- |
| 项目目录 | `E:\TRY_IT\QuickCapture` |
| 技术栈 | C# / WPF，.NET 10，Windows x64，自包含构建 |
| 分支 | `main` |
| HEAD | `ac47f2a492d0a6c44a281c93c6dca591aa4f8f8f`，即 v0.10.0 基线 |
| origin | `https://github.com/Xhonesty/QuickCapture.git` |
| 本地 origin/main 缓存 | 与 HEAD 相同；本轮未 fetch |
| 最新可运行程序 | `E:\TRY_IT\QuickCapture\dist\QuickCapture.exe`，需保留整个 dist 文件夹 |
| 最近成功构建 | 2026-10-06 22:50，本地 Release，无编译警告或错误 |
| 最近定向验收 | 10 项通过，包含定向修复后的复测；未重跑全应用套件 |
| 当前交付范围 | 最新功能的本地实现、构建和验收；本轮只新增交接文档 |

工作区有其他项目 `geo-portfolio/`、`lin-personal-site/`。所有命令、源码修改、缓存和生成文件继续限定在 QuickCapture 内。截图放 `artifacts/screenshots/`，其他交付物放 `artifacts/`；未新增或重命名任务目录，无需修改工作区根 README。

## 2. 已完成的功能

### 已发布的 v0.10.0 基线

- 录屏编辑窗口完整适配浅色 / 深色并实时跟随主题；按钮背景、边框、普通 / 悬停 / 按下 / 选中 / 焦点 / 禁用状态使用统一资源。
- 标题栏左侧移除 Logo 和文字，保留内容标题、原生命中区域以及最小化 / 最大化 / 关闭。
- 截图第二层属性栏按当前工具 / 对象显示，无属性工具及复制 / 保存 / 取消时完全收起；按内容测量宽度，窄屏均衡换行并避让边缘。
- 修复旧 FFmpeg 裁切并改变帧率后补尾帧导致的音视频时长差异。

该基线已发布：[v0.10.0 Release](https://github.com/Xhonesty/QuickCapture/releases/tag/v0.10.0)。发布事实来自此前已验证的本地记录 `artifacts/release-v0.10.0-result.json`。

### 尚未发布的后续改动

1. **时间轴预览同步**：拖动起点 / 终点或用方向键调整手柄时，暂停播放并定位对应时间。启用 `MediaElement.ScrubbingEnabled`；终点在视频末尾时定位最后一帧内，避免越过有效画面。加载前的跳转通过 `_pendingSeek` 保留，`MediaOpened` 后应用；没有待处理跳转时定位选段起点。
2. **文件夹按钮位置**：“选择文件夹”移到目录输入框右侧，使用同一行的自适应 Grid。
3. **一屏录屏编辑**：默认 880×820 DIP，移除外层整页 ScrollViewer；预览区占剩余高度，导出选项与保存位置并排。已检查 680×600 DIP 的小窗口和展开裁剪状态。高度低于 700 DIP 时，格式说明收进导出卡片提示，输入和操作控件保留。
4. **已保存录屏再次编辑**：主界面新增“编辑录屏”文件选择入口；最近文件的“更多 → 编辑录屏”仅为视频显示。支持应用输出的 MP4 / WebM / GIF。双击普通文件仍交给系统播放器；待导出母版仍使用原来的继续编辑行为。
5. **另存与源文件保护**：默认保存到源目录，使用“原文件名-编辑”及递增序号。禁止覆盖正在编辑的源文件；取消不删源文件，不为外部录屏写入母版 JSON。损坏视频显示错误并禁用导出，导出副本加入最近文件，可再次打开。
6. **兼容预览**：WebM / GIF 及不兼容编码准备 H.264 / AAC 临时预览，宽度最多 1280 像素。裁剪坐标和导出始终使用原始视频。临时文件在运行目录的 `Data/RecordingPreviews/`，关闭或取消后清理；准备过程可关闭窗口取消。

## 3. 关键文件与实现入口

下表路径均相对本项目目录。

| 文件 | 本轮重点 |
| --- | --- |
| `src/RecordingEditWindow.cs` | 一屏 Grid、可伸缩预览、并排卡片、目录按钮；`PreviewRange` / `_pendingSeek`；`EditedName`；`PreparePreviewAsync` / `CleanupPreviewAsync`；源文件覆盖保护与损坏输入处理 |
| `src/TrimTimeline.cs` | `RangePreviewRequested` 在拖动开始、变化和方向键调整时发出当前手柄的有效时间；`RangeChanged` 负责更新选段说明 |
| `src/MainWindow.xaml` | 主界面 `EditRecordingButton` |
| `src/MainWindow.xaml.cs` | `EditRecording_Click`、`EditSavedRecording`、通用 `EditRecording`；最近菜单根据当前行更新编辑项，兼容虚拟化复用 |
| `src/RecordingRecovery.cs` | 原有母版所有权、JSON 和删除边界；只允许移除应用自己的中间母版，本轮未改 |
| `src/RecordingReeditTests.cs` | 新增 4 项布局、视频帧、重新编辑和源保护检查 |
| `src/RecentFilesTests.cs` | 图片菜单断言改为检查可见项目，保留原有 2 项回归 |
| `src/SelfTest.cs` | `--recording-edit-only` 运行新增 4 项及最近文件 2 项；`--editor-ui-only` 保留原有录屏 UI 4 项 |
| `tools/build.ps1` | 沿用离线自包含构建；将新的使用说明复制到 dist/docs |
| `README.md`、`docs/recording-reedit.md`、`docs/verification.md` | 使用入口、实现边界及本地验收记录 |

主题资源仍由 `ThemeService.cs` / `RecordingEditorStyles.xaml` 管理。视频导出仍走 `VideoExportService.cs`，不得将兼容预览副本误用作导出源。

## 4. 未提交文件与应保留的内容

撰写交接前，以下已跟踪文件有本轮修改：

```text
README.md
docs/verification.md
src/MainWindow.xaml
src/MainWindow.xaml.cs
src/RecentFilesTests.cs
src/RecordingEditWindow.cs
src/SelfTest.cs
src/TrimTimeline.cs
tools/build.ps1
```

本轮新增且尚未跟踪的文件：`docs/recording-reedit.md`、`src/RecordingReeditTests.cs`，以及本交接文件 `docs/HANDOFF.md`。

**`docs/research/` 是本轮之前就存在的未跟踪用户资料，已保留，未纳入此前提交或便携包。** 后续提交应明确选择相关文件，避免直接 `git add .` 混入这些资料。

`dist/`、`artifacts/`、`.tools/`、`src/bin/`、`src/obj/` 等由 Git 忽略。用户运行数据和捕获文件应保留；不要为恢复“干净状态”删除 `dist/Data/` 或 `dist/Captures/`。

## 5. 验收结果与证据

| 检查组 | 结果 | 证据 |
| --- | --- | --- |
| 新录屏编辑专项 + 最近文件回归 | 6/6 | `artifacts/recording-reedit-final-results.json` |
| 原有录屏主题 / 窗口 / 导出回归 | 4/4 | `artifacts/recording-reedit-editor-ui-results.json` |
| 最终 dist 构建 | 成功，无编译警告或错误 | `artifacts/recording-reedit-final-build.log` |

实际覆盖：

- 浅色 / 深色、880×820 与 680×600 DIP 下，无整页滚动，输入、时间轴、底部及裁剪操作不被截断。
- 时间轴 0.5 / 1.5 / 2.5 秒分别显示实际 WPF 视频渲染的红 / 绿 / 蓝帧，像素检查通过；末尾定位处于最后一帧内。
- 通过真实最近文件菜单打开三种格式，截取并裁剪，输出 640×360、约 1.6 秒的 MP4 副本；副本出现在最近文件，并可再次打开后取消。
- 三种源文件 SHA-256 不变、外部源文件没有新增母版 JSON、临时兼容预览关闭后清理。
- 源文件覆盖拒绝、损坏输入禁用导出、导出错误 / 取消、MP4 / WebM / GIF 导出，以及最近图片的菜单 / 完整行 / 内部滚动 / 新文件置顶。

可查看的图件位于 `artifacts/screenshots/`：

```text
recording-compact-light-820-ready.png
recording-compact-dark-600-crop.png
trim-preview-0.5.png
trim-preview-1.5.png
trim-preview-2.5.png
editor-ready-light.png
editor-ready-dark.png
```

10 项是完成修改和定向复测后的汇总，包含保留已通过结果的 `--failed-only`，不代表在最后一次构建后重新跑过每个正常路径。构建遇到 DLL 占用时曾使用 `artifacts/recording-reedit-build/` 隔离验收；用户从托盘退出程序后，最终构建已写入 `dist`。该隔离目录是中间验收输出，最新使用入口为 dist。

## 6. 验证边界与接手注意点

- 本机为 Windows 单屏 2560×1440、125% DPI；未实测混合 DPI 多屏、显示器拔插及其他 Windows 版本。
- 时间轴拖动用例通过 WPF 路由事件触发，比较实际视频视觉；原生物理鼠标拖动、裁剪手柄、完整键盘快捷键和系统输入法候选窗仍需交互桌面复测。按钮悬停 / 按下图件使用 WPF 状态键模拟。
- 此前执行环境的前台窗口保护会拒绝原生键鼠投递；没有降低保护或向其他窗口投递输入。桌面截屏也可能捕到覆盖窗口，单窗口 WGC 捕获曾超时；当前预览像素检查读取实际 WPF 视频视觉，不将这些环境失败算作通过。
- 未在本轮重新采集系统声音 / 麦克风；使用生成的短视频与音轨。长时间录屏、长视频兼容预览的耗时 / 磁盘占用 / 中途取消未做专项实测。
- WebM / GIF 需要准备兼容预览，长视频首次打开可能需要等待。后续若优化性能，应保留关闭取消和临时文件清理，保持导出读取原始源文件。
- 现有最近文件主要由保存目录扫描和当前列表重建；外部导入入口已可用，若要求外部路径历史跨重启长期保存，需另行设计持久化，本轮没有新增历史数据库。
- `Data` 位于实际运行目录；隔离构建与 dist 使用不同运行数据。自测设置 `Paths.TestRoot` 到其 Diagnostics 目录，不应把测试数据复制进用户 Data。

## 7. 构建与复测方法

### 每次改动的验证原则

**每次改动不要进行过多的重复测试。验证范围应与改动影响相匹配，相关检查通过后即停止，继续交付。**

- 优先运行受影响功能的定向检查及必要回归；不要默认重跑全应用套件。
- 已通过且未受后续改动影响的结果可以沿用，不要重复构建、重复运行同一测试或重复截图。
- 只有新增改动影响已验证路径、测试失败、仍有未解决问题，或用户明确要求时，才追加相应验证；失败修复后优先复测失败项及关联路径。
- 仅修改文档时检查内容和差异即可，不为文档改动重新构建或运行应用测试。
- 完成一次必要构建和相关检查后，不为增加验收次数而重复执行。保留证据并准确说明本次验证范围，不能把沿用结果描述为本轮重新测试。

以下命令供按需选用，不要求每次改动都执行全部检查组。

先从托盘退出 QuickCapture。只关闭主窗口会继续驻留，仍可能锁住 DLL；不得为更新程序强制结束正在录屏或有未完成编辑的进程。

在 PowerShell 中：

```powershell
Set-Location -LiteralPath 'E:\TRY_IT\QuickCapture'
.\tools\build.ps1 -Offline
```

脚本优先使用 `.tools/dotnet/dotnet.exe` 和本项目离线源，构建 Windows x64 自包含 Release，校验并复制固定 FFmpeg / ffprobe、OCR 模型、许可和说明文件。不要安装全局 SDK 或跨任务共用可变缓存来替代现有工作流。

若 dist 被运行中的应用占用，可先独立构建，继续验证：

```powershell
.\tools\build.ps1 -Offline -OutputDirectory artifacts\recording-reedit-build
```

执行专项时使用进程对象等待，不用 `Start-Process -Wait` 等待整个后代进程树：

```powershell
$testProcess = Start-Process -FilePath .\dist\QuickCapture.exe `
  -ArgumentList '--self-test','--recording-edit-only' -WindowStyle Hidden -PassThru
$testProcess.WaitForExit()
$testProcess.ExitCode
Get-Content -LiteralPath dist\Diagnostics\results.json
```

第二组将参数替换为 `--self-test`,`--editor-ui-only`。每次运行会更新同一份 `dist/Diagnostics/results.json`，需要分别归档后再运行下一组；`artifacts` 中的两份最终结果已经分别保存。`--failed-only` 依赖当前结果文件，仅用于定向复测，不能代替首次验收。

## 8. 发布状态与后续顺序

现有 `artifacts/QuickCapture-win-x64.zip` 是此前 v0.10.0 的发布包，**不包含本地新增的一屏布局、预览同步和已保存视频再次编辑功能**。不要直接上传这个 ZIP 作为新版本。旧包的证明文件为 `artifacts/package-v0.10.0-verification.json`，摘要如下，仅用于识别旧包：

```text
02afd29011afd4661c59ddb89320893b04ceb2819f94cc9ca5971b202e1691ea
```

用户近期要求先修改，随后要求构建；最新功能尚无新的提交和发布操作，新版本号也未确定。后续按用户的新请求推进，不能把本交接文档当作自动发布指令。

建议接手顺序：

1. 保留当前工作树，在交互桌面补查真实时间轴拖动、裁剪与文件选择入口；优先检查用户实际保存的视频。
2. 若继续修改，围绕受影响路径定向复测，不把历史版本的验收结果当作最新版本全量通过。
3. 若用户要求同步 / 发布，确认新版本号，整理相关源码、文档、验收说明及 release notes，保持 `docs/research/` 不混入。
4. 重新构建和运行 `tools/package.ps1` 打包当前 dist。该脚本会覆盖同名 ZIP；先保留需要的旧包，再校验新包中的版本、运行依赖、文档、SHA-256，并确认不含 Data / Captures / Diagnostics。
5. 使用新提交 / 标签发布，不覆盖既有 v0.10.0。`tools/publish-release.py` 支持版本参数，依赖干净 main、新版本及 `docs/releases/v<版本>.md`；此前 v0.10.0 的专用 artifacts 脚本含固定版本和证明，不应直接用于新版本。

此前大包上传通过代理时，curl / urllib 多次发送超时，直接 HTTPS 分块上传成功；通用发布脚本使用 `http.client.HTTPSConnection`。认证使用已有 Git 凭据，在进程内存中使用，不在输出、参数或文档中记录令牌。

继续阅读：[功能说明](recording-reedit.md)、[完整验收记录](verification.md)、[项目 README](../README.md)。


## 9. 2026-10-07 后续完成：设置分类与九格马赛克

设置现为“通用与媒体工具 / 截屏 / 录屏”三个标签，默认 480×680 DIP 完整可见，顶部/底部固定，统一保存与取消。全部现有默认选项归类，声音检测/试录是接受草稿的二级窗口。主界面已移除两个顶部编辑按钮，主题与设置并入标题行，最近文件及录屏结束编辑沿用。

用户先要求优化候选 1，随后明确确认“采用优化版方案 1”；正式资源为 `src/Assets/Icons/mosaic-pixels.svg`，九个等大的交错深浅像素块。`IconSet` 增加 currentColor fill/stroke 和 opacity 支持，保持现有线性图标；下拉按钮仍为 45×32 DIP，内部图标 18 DIP。

定向检查 16 项通过，结果在 `artifacts/settings-tabs-verification.json`（13 项）和 `artifacts/mosaic-and-edit-entry-verification.json`（3 项）；失败修复后使用定向复测并沿用未受影响结果。细节与验证边界见 `docs/settings-categories-and-mosaic.md` 及 `docs/verification.md` 最新小节。截图均在任务自身 `artifacts/screenshots/`。

运行入口仍为 `dist/QuickCapture.exe`，新增独立交付包 `artifacts/QuickCapture-settings-tabs-win-x64.zip`；此前 v0.10.0 包保持原状。当前仍为本地 0.11.0 开发版本，未提交或在线发布。后续不要恢复已删除的顶栏编辑按钮，也不要把设置改回长滚动页面。


## 10. GitHub v0.11.0 发布准备

用户已授权同步 GitHub 并发布 Release。本轮采用项目已有版本 v0.11.0，合并本地可编辑媒体、声音设备、剪辑和设置/图标优化。`docs/releases/v0.11.0.md` 是正式更新说明；README 下载链接指向该版本，公开展示图复制到 `docs/images/`，原始验收图仍在 `artifacts/screenshots/`。`docs/research/` 保留为本地材料，不加入发布提交。

发布资产使用重新构建的 `artifacts/QuickCapture-win-x64.zip` 及 SHA-256 文件。原 v0.10.0 本地 ZIP 已保留为 `artifacts/QuickCapture-v0.10.0-win-x64.zip`；旧 GitHub Release 不修改。发布工具验证认证账号、远端提交、标签与上传摘要后才公开草稿。最终在线状态和提交/资产信息见 GitHub Release 及 `artifacts/release-v0.11.0-result.json`。
