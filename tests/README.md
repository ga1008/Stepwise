# 开发测试

应用本身无需 Node.js。以下命令用于维护内置页面与标注引擎，在仓库根目录运行。

```powershell
npm install
npm test
npm run test:annotation
```

原生存储、偏好与文档导出检查可在 Windows 构建后运行：

```powershell
.\dist\Stepwise-1.3.0-Win11-x64\Stepwise.exe --self-test verification/storage
.\dist\Stepwise-1.3.0-Win11-x64\Stepwise.exe --preferences-test verification/preferences
.\dist\Stepwise-1.3.0-Win11-x64\Stepwise.exe --word-export-test verification/word
.\dist\Stepwise-1.3.0-Win11-x64\Stepwise.exe --note-export-test verification/note-export
```

导出检查会合成 PNG、GIF 和 MP4，实际调用 FFmpeg 与 WebView2 并生成结果；需先完成媒体工具准备。报告写入所指定的隔离目录，结果以 JSON 中的 passed 为准。测试素材、缓存与本机报告均不提交仓库。

快捷键测试会临时申请系统组合键；媒体采集测试会录制屏幕和音频，应在适合采集的桌面环境中主动运行。自动检查不等同于在所有显示器、缩放、音频硬件和办公软件上的实机验证。
