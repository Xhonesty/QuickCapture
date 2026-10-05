# 界面设计规范 · 0.7.0

浅色 / 深色主题颜色集中位于 `src/ThemeService.cs`。截图与录屏主面板、设置页采用 2026-10-05 定稿的实色双主题；其最新尺寸、状态、对比度处理和验证见 [界面优化说明](panel-optimization.md)，公共控件模板位于 `src/PanelStyles.xaml`。以下表格主要记录编辑器等既有页面的通用尺寸；通用资源位于 `src/DesignTokens.xaml`、`src/App.xaml` 和 `src/UiDesign.cs`。

数值单位均为 Windows DIP（系统缩放后显示），截图标注及裁剪坐标始终使用原始物理像素。

| 项目 | 规范 | 资源 / 应用 |
| --- | --- | --- |
| 按钮、输入框、下拉框、面板、弹出菜单圆角 | 8 DIP | `RadiusControl` |
| 小型裁剪手柄圆角 | 2 DIP | `RadiusHandle`，10 DIP 手柄专用 |
| 间距阶梯 | 4 / 6 / 8 / 10 / 16 / 24 DIP | `SpaceXS` 至 `SpaceXXL` |
| 页面留白 | 16 DIP；主界面 10 DIP | `PagePadding` |
| 分组面板内边距 | 10 DIP；主界面 8 DIP | `PanelPadding` |
| 悬停面板 / tooltip 内边距 | 8 DIP | `PopupPadding` |
| 浮动图标工具栏内边距 | 8 DIP | 避免遮挡原位截图 |
| 应用名称 | 28 DIP，Semibold | `FontAppTitle` |
| 页面标题 | 18 DIP，Semibold；主界面标题 16 DIP | `FontPageTitle` |
| 区块标题 | 14 DIP，Semibold | `FontSection` |
| 正文 / 控件文字 | 12 DIP，Regular | `FontBody` |
| 提示 / 辅助信息 | 11 DIP，Regular | `FontHelp` |
| 字体 | Segoe UI；中文回退 Microsoft YaHei UI | 应用窗口样式 |
| 普通按钮 / 单行下拉框高度 | 32 DIP | `ControlHeight` |
| 单行输入框 | 最小高度 32 DIP；左右内边距 8 DIP | `ControlHeight` / `ControlPadding` |
| 原位文字输入框 | 透明背景，内边距 0；按内容调整范围，编辑时显示 1 DIP 细边框 | `InPlaceTextEditor`；编辑装饰不导出 |
| 文字标注样式 | 默认 24 原始像素；12–144 原始像素预设字号，普通 / 粗体，左 / 中 / 右对齐 | `Annotation` / `AnnotationGeometry`；随画布缩放显示 |
| 最近文件行 | 固定 44 DIP；默认完整显示约 3–5 行，仅列表内部滚动 | `RecentFileRow`；按可用高度取完整行 |
| 工具图标 | 18 × 18 DIP，24 × 24 设计网格，2 单位线宽 | `IconSize`，Lucide SVG |
| 图标按钮 | 32 × 32 DIP；相邻间距 4 DIP | 与文字按钮同高度 |
| 裁剪手柄 | 10 × 10 DIP；随预览缩放反向补偿 | `HandleSize` |
| 时间轴手柄 | 16 × 36 DIP | 横向拖拽，方向键按帧微调 |
| 普通面板边框 | 1 DIP；不加阴影 | `PanelBorder` |
| 浮动工具栏 / 悬停菜单阴影 | 模糊 16 DIP、偏移 3 DIP、不透明度 0.25 | `ShadowBlur` / `ShadowDepth` / `ShadowOpacity` |
| 按钮禁用态 | 不透明度 0.4 | 公共按钮模板 |
| 其他控件禁用态 | 不透明度 0.45 | 公共输入 / 下拉模板 |

所有页面使用同一主题颜色源。主面板与设置页使用独立共享控件模板、14 DIP 圆角自定义标题栏、10 DIP 圆角卡片，表面不透明，不使用阴影。编辑器的既有浮动工具栏保留原几何资源。文件夹选择器及消息框继续使用 Windows 样式。

主窗口默认 480 × 680 DIP，设置窗口打开时取主窗口当前宽高，尺寸完全一致；左上角仅显示“设置”，不显示图标或应用名。设置内容可内部滚动，保存和取消固定在底部。录制帧率按 MP4 / WebM / GIF 分别显示和保存，与主界面同步，详见 [帧率同步说明](recording-preferences.md)。主界面截图、录屏选项直接显示，最近文件采用同样的圆角卡片、边框、背景、内边距和间距，标题与保存目录按钮位于卡片内同一行。最近列表高度按当前 DPI 取约 3–5 个完整的 48 DIP 行，多记录只在列表内部滚动，空状态使用紧凑高度；状态行固定在底部，默认主界面没有整体纵向滚动。截图保存、录屏导出页的操作按钮也固定在底部；较短屏幕通过滚动查看内容。图标工具栏的 tooltip 展示功能名及快捷键，画笔粗细和马赛克模式继续只在悬停后展开。

文字工具直接在截图画布上透明输入，字体、字号、颜色、粗体及对齐实时预览。编辑框使用零内边距，与原始像素文字绘制对齐；画布缩放和滚动共同作用于文字及编辑范围。完成或取消按钮提供明确操作，光标、边框和占位提示仅在编辑时显示。
