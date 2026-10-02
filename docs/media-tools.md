# 图片与录屏导出

截图和录屏使用独立设置，截图默认 PNG、JPG / WebP 质量 90；录屏默认 MP4、中质量，GIF 默认 15 FPS。保存时临时切换格式或质量不会改写默认设置。

## 截图

PNG、JPG、BMP 使用 Windows WPF 编码器；WebP 使用固定版本 SkiaSharp 4.153.1，不依赖 FFmpeg。JPG / BMP 将透明区域合成到白色背景；PNG / WebP 可保留透明度。保存对话框自动使用所选格式的扩展名。

复制使用默认格式的编码字节（PNG / JFIF / WebP / BMP 和对应 MIME 类型），同时提供标准 Windows 位图与 DIB；这样只支持位图的聊天软件、画图和文档软件也可粘贴。位图从本次编码结果解码，JPG / WebP 质量同样生效。接收软件可以自行选择它支持的剪贴板类型，不能保证每个接收软件最终保存的文件格式。

## 录屏与编辑

采集保持 ScreenRecorderLib 7.0.1 / Windows Media Foundation，实时生成 H.264 / MP4 母版，支持系统声音、麦克风和混音。母版的低 / 中 / 高质量分别使用 4 / 8 / 16 Mbps。

停止后打开录屏预览与导出窗口。时间轴两端拖动选择起止；时间轴手柄方向键按一帧、Shift 按十帧微调。单击时间轴可定位预览，播放选段到终点后停止。导出支持：

| 格式 | 编码 / 特点 | 质量 |
| --- | --- | --- |
| MP4 | H.264 + AAC；常用视频格式 | 高 / 中 / 低，重新编码 CRF 18 / 23 / 28 |
| WebM | VP9 + Opus；播放依赖播放器编码支持 | 高 / 中 / 低，CRF 26 / 31 / 36 |
| GIF | 专门抽帧 + 调色板生成；循环动画，无声音 | 高保留尺寸 / 256 色；中宽度至多 720 / 128 色；低宽度至多 480 / 64 色 |

GIF 支持 5 / 10 / 15 / 20 / 30 FPS；界面明确提示丢弃音频及动画体积特点。速度为 0.5× / 1× / 1.5× / 2×；视频与音频分别同步裁切、重置时间戳并变速，音频用 `atempo` 保持音调。静音去除音轨。

完整 MP4、1×、保留声音且质量与母版一致时直接复制，无需重编码。其他操作导出时通过独立 FFmpeg 进程重编码；只在导出完成并验证文件后原子提交最终文件。取消会终止编码器并清理本次临时输出。

P0 时间裁切、P1 变速、GIF 和静音已实现；提取音频、MKV / MOV、暂停及多段拼接暂不包含。

## 依赖检测与安装

按“设置中指定的工具目录 → 系统 PATH → 程序附带的 `Tools/media`”检测 `ffmpeg.exe` 和 `ffprobe.exe`。检测实际编码器与滤镜；系统工具损坏或不完整时尝试捆绑工具。

本次便携版包含固定版本 **9.0.2-essentials_build-www.gyan.dev** 的 FFmpeg / ffprobe。新电脑开发构建时：

```powershell
.\tools\setup-media.ps1
.\tools\build.ps1
```

脚本从 [Gyan 固定版本构建](https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip) 下载，核对 ZIP 及两个 EXE 的 SHA-256。版本、哈希及来源在 `vendor/media/manifest.json`；二进制不进入 Git。也可使用已经下载的 ZIP：

```powershell
.\tools\setup-media.ps1 -ArchivePath 'C:\Downloads\ffmpeg-9.0.2-essentials_build.zip'
```

若使用自己的 FFmpeg，在设置中选其所在目录，目录需含 `ffmpeg.exe` 和 `ffprobe.exe`；或者把两者加入系统 PATH。所需视频编码器为 `libx264` / `aac` / `libvpx-vp9` / `libopus`，GIF 需 `gif`、`palettegen`、`paletteuse`，编辑需 `trim`、`atrim`、`setpts`、`asetpts`、`atempo`、`scale`、`fps`、`split`。

缺失工具或对应编码器时，界面提示并禁止不可用的导出操作；仍能原生录屏、预览及保存完整 MP4。不会把用户要求的裁切或变速静默丢弃。补齐工具后重新打开“待导出”项目即可继续编辑。

## 恢复与文件

原始录屏存于 `Data/Recordings/master-*.mp4`，旁边 JSON 保存录制质量、帧率、声音及预估时长。取消、失败或退出后保留，主界面最近文件中的“待导出”项目可双击继续。成功导出后，仅删除程序恢复目录内对应母版和 JSON；导入视频不会被清理。播放器暂时占用文件导致清理失败时仍保留，不影响已保存的视频。

退出时只完成母版录制，不打开导出窗口，下次启动继续。强制杀进程或断电可能使未完成的 `.partial.mp4` 无法播放；这种文件不进入最近列表。

上游技术依据：[FFmpeg 滤镜文档](https://ffmpeg.org/ffmpeg-filters.html)、[构建说明](https://www.gyan.dev/ffmpeg/builds/)、[对应 FFmpeg 源码](https://github.com/FFmpeg/FFmpeg/tree/946fcce07b)。捆绑工具独立进程调用，使用上游 GPLv3 构建；许可及构建配置随包提供于 `licenses`。
