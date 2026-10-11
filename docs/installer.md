# Windows x64 安装包

安装 EXE 与便携 ZIP 使用同一份已校验的应用发布清单；两种形式均保留离线运行所需的 .NET、媒体工具和 OCR 模型。

## 安装与使用

面向 Windows 10 22H2 / Windows 11 x64。默认仅为当前用户安装，程序自身不请求管理员权限；安装目录默认为 `%LOCALAPPDATA%/Programs/QuickCapture`。中文向导提供安装目录、开始菜单入口、可选桌面快捷方式和可选完成后启动。运行中的轻截必须先从托盘退出，安装和卸载都检查既有应用互斥锁，不强制结束录制或编辑。

程序目录旁的 `installed-mode` 标记启用安装布局：

| 内容 | 安装版位置 | 便携版位置 |
| --- | --- | --- |
| 设置、日志、截图项目、录屏母版与剪辑配置 | `%LOCALAPPDATA%/QuickCapture/Data` | 程序目录的 `Data` |
| 默认截图和视频 | 系统“图片”目录下的 `QuickCapture` | 程序目录的 `Captures` |
| 自定义保存目录 | 沿用设置 | 沿用设置 |
| FFmpeg、OCR 模型和运行时 | 安装目录 | 程序目录 |

系统“图片”目录使用 Windows 已知文件夹位置，支持系统配置的重定向；不可用时退到 `%LOCALAPPDATA%/QuickCapture/Captures`。升级不写用户配置，卸载只移除安装器登记的程序文件、快捷方式和卸载入口，保留用户数据与捕获。没有递归删除用户目录的卸载脚本。

卸载入口统一为安装目录的 `uninsQuickCapture.exe`（无空格）；Windows“已安装的应用”和开始菜单“卸载轻截 QuickCapture”均指向它。该入口是原生 x64 启动器，不依赖 .NET；转交参数后立即退出，避免阻碍卸载自身。内部继续使用 Inno 的 `unins000.exe`（存在文件名占用时由 Inno 选择编号）及关联日志；`uninsQuickCapture.ini` 记录本次安装实际使用的内部文件名。这些文件一起由安装器管理，请勿单独改名或删除。保留内部命名与目录，确保已有安装的卸载记录在覆盖重装时继续合并。

捆绑 .NET x64 运行时、FFmpeg / ffprobe、简繁中与英文 OCR 模型及第三方许可。安装前检查 Microsoft Visual C++ x64 运行库注册状态；缺失时停止安装，交互向导可打开[微软官方下载页](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)。安装该运行库可能需要管理员权限。安装包不捆绑或重新分发 VC++ 安装器。安装包未进行代码签名。

## 从便携版迁移

先退出两种版本，备份原 `Data` 和捕获目录。将需要的 `Data` 内容复制到 `%LOCALAPPDATA%/QuickCapture/Data`，如已有安装版数据，先分别备份再选择保留版本，不要盲目覆盖。设置中的保存目录保持原值；图片、录屏母版及外部视频的关联使用绝对路径，应保留原导出文件位置。新安装器拒绝覆盖含 `Data` / `Captures` 且没有安装标记的便携目录。

安装器不会搜寻其他便携目录或自动移动用户文件。卸载后如需清理，先备份，再由用户自行删除数据目录。

## 构建

在 QuickCapture 目录执行：

```powershell
# 应用更新：同一次构建生成便携 ZIP 和安装 EXE，验证后清理旧备份
.\tools\build-release.ps1 -Offline
# 已核实指定发布目录是本次已验证构建时：
.\tools\build-release.ps1 -SkipBuild -SourceDirectory 'artifacts/builds/<本次发布目录>'
# 仅诊断 / 修复安装打包流程时的底层命令：
.\tools\build-installer.ps1 -SkipBuild -SourceDirectory 'artifacts/builds/<本次发布目录>'
```

统一更新脚本每次构建到新的 `artifacts/builds/release-<标识>` 目录（已有非空目录拒绝覆盖），便携包为 `artifacts/QuickCapture-win-x64.zip`，安装器为 `artifacts/QuickCapture-Setup-<版本>-x64.exe`，生成各自 SHA-256 与 `artifacts/release-manifest.json`。版本和应用 DLL 必须一致，隔离安装及升级会核对实际安装 DLL 与本次发布目录的 SHA-256。旧包按实际版本保存在 `artifacts/backups/v<版本>/`，按内容摘要去重。

两种新包都验证成功后，保留当前及之前三个不同版本（最多四个），清理更早的包和已确认版本的程序备份。预览用 `tools/prune-backups.ps1 -Preview`；结果保存在 `artifacts/backup-cleanup-results.json`，包含目标、版本依据、释放大小和保留数据摘要。旧程序副本中的 Data / Captures 保持原位置，版本不明的源码备份保留。长期要求见项目 `AGENTS.md`。

底层安装脚本单独构建时默认输出应用到新的 `artifacts/builds/installer-<标识>`。`payload-manifest.json` 固定运行文件路径、尺寸与校验值，ZIP 和安装包共用同一清单复制函数。打包暂存使用一次性子目录，排除 `Data` / `Captures` / `Diagnostics`、安装布局标记及旧卸载器。便携 ZIP 打包也排除安装标记和卸载器，确保继续采用便携布局。

另需 PATH 中的 MinGW-w64 x64 `gcc.exe` 和 `windres.exe`；`tools/build-uninstall-launcher.ps1` 使用现有编译器，将原生卸载入口、资源和中间文件放在 `artifacts/uninstall-launcher`，不安装全局工具。版本资源跟随应用项目版本。

`tools/setup-installer.ps1` 获取[官方 Inno Setup 7.1.0 x64](https://jrsoftware.org/isdl.php)，校验固定 SHA-256 与 Pyrsys B.V. 数字签名，以 `/PORTABLE=1` 放到项目 `.tools/inno`。缓存与临时文件都在本项目内。简体中文语言资源来自[固定上游标签](https://github.com/jrsoftware/issrc/blob/is-7_1_0/Files/Languages/ChineseSimplified.isl)，SHA-256 为 `e0b0b350e2245f3c5e65586dfe43d574f6e7f06f2261149aba284954b3fc9a8d`，保留原头部。

## 隔离验证模式

开发验收可使用 `/VALIDATION=1`：保留文件安装及卸载，跳过系统快捷方式和卸载注册。配合 `/DIR=<隔离测试目录>`、`/LOG=<日志>`、`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`，避免写入日常用户目录。VC++ 和运行实例检查仍生效；正常使用无需此参数。
