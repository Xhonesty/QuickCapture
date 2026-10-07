# 设置分类、主界面精简与马赛克图标 · 2026-10-07

设置采用顶部三个原生 WPF 标签，默认尺寸 480×680 DIP 下每页完整显示，无页面滚动。标签栏与底部取消、保存固定；设置窗口仍匹配打开时主窗口的大小。主界面将主题、设置及窗口控制整合在一行，移除图片编辑、录屏编辑按钮及其独立文件选择事件。

| 分类 | 内容 |
| --- | --- |
| 通用与媒体工具 | 保存目录、截图/录屏全局快捷键、主题即时预览、FFmpeg 目录和检测 |
| 截屏 | PNG/JPG/WebP/BMP、质量、窗口吸附、复制自动保存、OCR 语言/换行、步骤编号起点/大小、贴图透明度 |
| 录屏 | MP4/WebM/GIF、质量、各格式独立帧率、硬件编码、区域吸附、鼠标、系统声音/麦克风、输入/播放设备、刷新及声音检测/试录二级页 |

点击标签或聚焦标签后使用左右方向键、Home/End 切换。Ctrl+Tab / Ctrl+Shift+Tab 在任意页循环切换，Tab / Shift+Tab 导航当前页可见控件。标签保持原生选中状态和辅助功能名称。控件属于各页的逻辑树，切换不重建输入；保存提交所有分类，取消或关闭恢复原主题和帧率预览。声音二级页接收临时配置，在外层保存前不会写入设置。媒体工具检测读取当前填写路径，无需保存；悬停结果可查看实际工具路径。

继续沿用原有设置 JSON 和帧率迁移、快捷键录入/冲突回滚、路径写入校验、共享图片及录屏编辑器、截图项目和原片恢复。最近文件“更多 → 继续编辑 / 编辑录屏”和录屏停止后的编辑流程继续保留。

## 后续扩展

`SettingsWindow.Category` 创建分类内容，`SettingsStyles.xaml` 管理标签模板，`PanelStyles.xaml` 继续管理共用控件。新设置归入对应分类；若内容超过一屏，使用像声音检测这样的二级窗口或按需高级设置，不给分类页添加长滚动容器。二级窗口接受草稿配置；最终持久化仍由外层统一保存。

## 已确认的马赛克图标

先提供当前图标与三个 SVG 候选的同图预览，均展示 72 DIP 放大图、18 DIP 实际图标、45×32 DIP 含下拉箭头工具按钮，以及两种主题和普通/选中/禁用状态。用户要求优化方案 1 后，提供九格等大、交错深浅的优化稿，用户确认“采用优化版方案 1”后才替换正式资源。

正式资源为 `src/Assets/Icons/mosaic-pixels.svg`。两组 currentColor 实心路径分别包含五个主要块和四个 42% 透明度块；使用一致的 1.5 单位间隙和 3 单位外围留白。`IconSet` 保留 SVG 图元的 fill、stroke、opacity，绑定所属按钮的 Foreground，跟随现有工具栏主题、选中和禁用态，保持矢量和高 DPI 清晰度。原有线性图标继续使用无填充、2 单位描边。候选保存在 `src/Assets/Icons/MosaicCandidates/`，不进入正式资源通配列表。

预览与实际界面截图在 `artifacts/screenshots/`，验收报告在 `artifacts/`：

- `mosaic-candidates-comparison.png`：原始候选。
- `mosaic-01-refined-comparison.png`：优化方案 1 与当前/原稿/方案 2。
- `settings-tabs-light-0/1/2.png`、`settings-tabs-dark-0/1/2.png`：六张完整设置页。
- `main-simplified-light.png`、`main-simplified-dark.png`：精简主界面。
- `mosaic-toolbar-light/dark-normal/selected/disabled.png`：正式工具栏图标状态。

定向验证命令：

```powershell
.\tools\build.ps1 -Offline
.\dist\QuickCapture.exe --self-test --settings-tabs-only
.\dist\QuickCapture.exe --self-test --mosaic-icon-only
```

验证读取真实 WPF 窗口、布局边界、动态画刷及路由事件；最近文件菜单真实打开共享编辑器。视频流程使用生成的 MP4 原片，没有在本轮重新采集桌面/麦克风。当前环境为单屏 125% DPI，混合 DPI 多屏未专项验证。结果归档见 `artifacts/settings-tabs-verification.json` 与 `artifacts/mosaic-and-edit-entry-verification.json`。


GitHub 和便携包文档使用 `docs/images/` 中的精选图件副本；原始验收输出仍保存在 `artifacts/screenshots/`。
