# 界面设计规范 · 0.6.0

现有浅色 / 深色主题的颜色值原样保留。本表仅列布局、尺寸和字体；统一资源位于 `src/DesignTokens.xaml`，公共控件模板位于 `src/App.xaml`，代码构建界面使用 `src/UiDesign.cs`。

数值单位均为 Windows DIP（系统缩放后显示），截图标注及裁剪坐标始终使用原始物理像素。

| 项目 | 规范 | 资源 / 应用 |
| --- | --- | --- |
| 按钮、输入框、下拉框、面板、弹出菜单圆角 | 8 DIP | `RadiusControl` |
| 小型裁剪手柄圆角 | 2 DIP | `RadiusHandle`，10 DIP 手柄专用 |
| 间距阶梯 | 4 / 8 / 12 / 16 / 24 / 32 DIP | `SpaceXS` 至 `SpaceXXL` |
| 页面留白 | 24 DIP | `PagePadding` |
| 分组面板内边距 | 16 DIP | `PanelPadding` |
| 悬停面板 / tooltip 内边距 | 12 DIP | `PopupPadding` |
| 浮动图标工具栏内边距 | 8 DIP | 避免遮挡原位截图 |
| 应用名称 | 30 DIP，Semibold | `FontAppTitle` |
| 页面标题 | 20 DIP，Semibold | `FontPageTitle` |
| 区块标题 | 16 DIP，Semibold | `FontSection` |
| 正文 / 控件文字 | 14 DIP，Regular | `FontBody` |
| 提示 / 辅助信息 | 12 DIP，Regular | `FontHelp` |
| 字体 | Segoe UI；中文回退 Microsoft YaHei UI | 应用窗口样式 |
| 普通按钮 / 单行下拉框高度 | 36 DIP | `ControlHeight` |
| 单行输入框 | 最小高度 36 DIP；左右内边距 10 DIP | `ControlHeight` / `ControlPadding` |
| 多行输入框 | 按内容增高；内边距 12 DIP | 文字标注弹窗 |
| 工具图标 | 20 × 20 DIP，24 × 24 设计网格，2 单位线宽 | `IconSize`，Lucide SVG |
| 图标按钮 | 36 × 36 DIP；相邻间距 4 DIP | 与文字按钮同高度 |
| 裁剪手柄 | 10 × 10 DIP；随预览缩放反向补偿 | `HandleSize` |
| 时间轴手柄 | 16 × 36 DIP | 横向拖拽，方向键按帧微调 |
| 普通面板边框 | 1 DIP；不加阴影 | `PanelBorder` |
| 浮动工具栏 / 悬停菜单阴影 | 模糊 16 DIP、偏移 3 DIP、不透明度 0.25 | `ShadowBlur` / `ShadowDepth` / `ShadowOpacity` |
| 按钮禁用态 | 不透明度 0.4 | 公共按钮模板 |
| 其他控件禁用态 | 不透明度 0.45 | 公共输入 / 下拉模板 |

主界面、编辑器、设置页、截图保存页、录屏编辑导出页使用同一套资源。普通面板使用边框；只有悬浮内容使用统一阴影。系统原生窗口标题栏、文件夹选择器及消息框继续使用 Windows 样式。

设置、截图保存、录屏导出页的操作按钮固定在底部；较短屏幕通过滚动查看内容。图标工具栏的 tooltip 展示功能名及快捷键，画笔粗细和马赛克模式继续只在悬停后展开。
