# 本地 OCR 方案选择 · 2026-10-06

目标是现有 WPF / x64 免安装便携包中的中英文文字提取。以下对照依据官方文档和已下载固定模型；没有声称对三种引擎做统一准确率跑分。最终 Tesseract 的真实样本输出、耗时和误差见专项验收。

| 方案 | 识别效果与语言 | 运行与部署要求 | 包体增量 | 许可证与结论 |
| --- | --- | --- | --- | --- |
| Windows.Media.Ocr | 系统印刷文字引擎，按设备已安装语言识别；本轮未统一测量准确率 | Windows 本地组件；微软当前正式支持文档将 OCR 列入需要包身份的 API，缺语言需补充系统组件 | 不需随包模型，但系统组件大小和 SDK 投影随环境变化 | Windows 系统 API，不能把它的运行时当作开源模型再分发；现有 ZIP 无包身份，第一版不采用 |
| Tesseract 5.2.0 + tessdata_fast | 清晰横向印刷文字；简体、繁体及英文组合；fast 是官方较小、较快、相对 best 准确率更低的 LSTM 模型 | 纯 CPU、.NET 包装与 x64 本地 DLL；已有 VC++ 运行库要求；无需 Windows OCR 语言包 | 模型合计 8,948,886 字节（8.53 MiB）；两项 x64 DLL 合计 6,956,544 字节（6.63 MiB），另有小型托管包装和依赖；压缩增量以发布实测为准 | 引擎、包装、模型 Apache-2.0；Leptonica BSD-2-Clause；第一版选用，固定模型 commit 与 SHA-256 |
| RapidOCR / PaddleOCR ONNX | 默认中英文，支持检测、方向分类、识别；更复杂图片可评估相应模型，本轮未实测优劣 | CPU ONNX Runtime 或其他推理后端；需检测/识别模型、字典、预后处理及 .NET 集成 | 模型版本和 mobile/server 差异大，不能给一个统一安装大小；至少需要模型集合及推理运行时，需为固定候选建立可重复测量 | RapidOCR 代码及其对应 PaddleOCR 衍生模型 Apache-2.0，必须保留模型来源、版权及转换信息；留作效果升级候选 |

选用 Tesseract 的判断是现有部署方式、明确的体积成本及集成边界，不是“准确率最好”的结论。先稳定完成文字提取流程；需要复杂版面时用同一验证语料对 RapidOCR 及 tessdata_best 做增量评测，再决定是否更换或作为可选组件。

原生识别放在私有工作进程内，避免 .NET 取消令牌无法打断同步 `Process`。工作进程不注册托盘或全局快捷键，不通过主程序的单实例限制。取消先杀死该工作进程并等待退出，再清理图像和结果；完成通过 JSON 返回明确状态。构建校验语言文件固定哈希，打包保留上游许可证和清单。

官方来源：

- [微软桌面 WinRT 支持与包身份限制](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)
- [OcrEngine 语言、图像尺寸及识别接口](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)
- [Tesseract 数据文件的速度与准确率取舍](https://tesseract-ocr.github.io/tessdoc/Data-Files.html)
- [Tesseract .NET 包装](https://github.com/charlesw/tesseract)
- [固定 tessdata_fast 模型来源](https://github.com/tesseract-ocr/tessdata_fast/tree/87416418657359cb625c412a48b6e1d6d41c29bd)
- [RapidOCR 语言及代码、模型许可](https://github.com/RapidAI/RapidOCR)

下载与哈希记录位于 `vendor/ocr/manifest.json`，安装脚本 `tools/setup-ocr.py`。语言文件不放入 Git，保持仓库轻量并可复现构建。
