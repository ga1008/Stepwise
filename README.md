# 步录 · Stepwise

**把桌面操作变成可分享的步骤笔记。** 适用于 Windows 11 x64，截图、标注、录屏和文字说明在本机完成，按项目保存。

[下载便携版](https://github.com/ga1008/Stepwise/releases/latest) · [详细使用指南](docs/USER_GUIDE.md) · [作者](AUTHORS.md)

## 功能

- 置顶浮动操作栏，贴边自动隐藏；截图、录制支持立即或延时开始。
- 冻结桌面后框选或单击截取全桌面，支持画笔、箭头、形状和中文文字标注。
- 区域录屏，采集系统声音与麦克风；首次准备好转换器后，可保存 MP4、GIF 或静音视频。
- 自动编号步骤，填写标题与说明，内置预览支持编辑、排序和移除。
- 设置默认颜色、线宽、字号、录制规格与快捷键；支持偏好记忆及快捷键占用检查。
- 导出 Word、单文件 HTML、长图和 PDF，提供进度、取消和独立成品目录。

## 开始使用

下载 **Stepwise-1.3.0-Win11-x64-Setup.zip** 并解压。首次联网双击 `Prepare-Stepwise.cmd`，它会从原作者下载固定版本的 FFmpeg 8.1.2、校验 SHA-256，然后自动启动。以后双击 `Stepwise.exe` 即可离线使用，请保留整个文件夹一起移动。已有的本机完整包可直接运行，无需重新准备。

无需安装 .NET 或 Office；内置预览需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 通常已提供）。

1. 输入项目名、选择保存位置，创建新项目；启动页也可继续已有项目。
2. 点击相机截图并标注，或点击录制按钮选择区域，结束时点击停止。
3. 在步骤浮窗中填写标题和说明，点击“保存步骤”；录制可选择 MP4、GIF 或静音。
4. 点击文档图标查看整份笔记，调整内容与顺序，再点击“导出”选择分享格式。
5. 点击齿轮配置偏好；完成后关闭操作栏退出，项目和已保存的偏好会保留。

默认快捷键：`Ctrl+Alt+S` 立即截图，`Ctrl+Alt+R` 开始／停止录制，`Ctrl+Alt+P` 预览，`Ctrl+Alt+B` 显示操作栏，`Ctrl+Alt+O` 设置。可在设置中修改。

## 导出与分享

| 格式 | 适合用途 | 动图与视频 |
| --- | --- | --- |
| Word `.docx` | 可编辑正文，继续整理和交付 | 使用第一帧，标明静态预览 |
| 单文件 HTML `.html` | 离线阅读，保留完整操作过程 | 全部媒体 Base64 内嵌，保留播放 |
| 长图 `.png` | 聊天分享，快速浏览 | 使用第一帧；过长自动分成多张 |
| PDF `.pdf` | 固定版式，归档和打印 | 使用第一帧 |

导出文件不依赖原项目。长图分片需按编号一起分享；直接分享项目中的 `document.html` 则必须同时提供 `media` 文件夹。

HTML 分块写入、按需解码，视频点击后加载；Base64 约增加 33% 体积，长录像仍会增加文件大小与浏览器内存占用。Word、PDF 使用 Letter 分页；长图宽 1440 像素，每张最多高 12000 像素。

## 项目文件

```text
项目目录/
├── project.json       步骤清单与文字（附 .bak 备份）
├── document.html      实时文档，引用 media 素材
├── media/             截图、MP4、GIF
├── recordings/        原始录屏、音轨与转换日志
├── exports/           每次独立导出的分享文件
└── drafts/、logs/、.cache/
```

普通启动创建新项目；“继续已有项目”选择原目录的 `project.json`。移除步骤不会删除原素材。受保护的视频、安全桌面与锁屏受 Windows 捕获机制限制；不同 HDR、缩放和多屏设备需在实际硬件上检查。

## 从源码构建

在 Windows 11 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，进入仓库目录，在 PowerShell 中运行：

```powershell
.\tools\Get-FFmpeg.ps1
.\tools\Get-AnnotationFont.ps1
.\build.ps1
```

脚本校验下载依赖的 SHA-256，构建结果位于 `dist/`。源码入口为 `src/Stepwise`，测试说明见[使用指南](docs/USER_GUIDE.md#开发与复现)。

## 开源与作者

Stepwise 自有源代码采用 [MIT 许可证](LICENSE.txt)。第三方组件遵循各自许可证：FFmpeg 为 GPL v3，Noto Sans CJK 为 SIL OFL 1.1，NAudio 为 MIT；运行时及 WebView2 亦保留各自条款。公开包不含 FFmpeg 二进制，首次准备时从原作者下载。详见[第三方说明](tools/THIRD-PARTY.md)。

1. **guardianangel（[@ga1008](https://github.com/ga1008)）** — 第一作者、项目发起与产品设计。
2. **OpenAI Codex** — 第二作者，AI 协作开发、实现与验证支持；此署名说明工具参与，不代表真人或 OpenAI 官方背书。
