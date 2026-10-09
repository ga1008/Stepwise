# 项目与文档契约

`ProjectStore.Create(parentDirectory, title)` 在所选目录下创建唯一项目文件夹；名称包含标题、时间与随机标识。`Open(root)` 用于明确打开已有项目。项目文件夹可以整体复制或归档。

```text
项目目录/
  project.json             权威数据：项目、有效步骤、已移除步骤
  project.json.bak         上一次成功提交的清单
  document.html            自动生成的离线文档，浏览器直接打开、打印
  document.html.bak        上一份生成文档
  media/images/            PNG 等截图
  media/videos/            导出的 MP4
  media/gifs/              导出的 GIF
  recordings/<id>/        原始录制、音轨、录制会话与转换过程
  exports/<name-time-format-id>/  独立分享成品：DOCX、HTML、PNG 或 PDF
  exports/.pending-<id>/  本次导出的临时输出与首帧，完成或取消后清理
  drafts/                  预留的草稿过程文件
  logs/                    运行日志与损坏清单恢复副本
  .cache/                  可重建的浏览器等缓存
```

`ProjectData` 版本为 `schemaVersion: 1`，包含 `id`、`title`、`createdAt`、`updatedAt`、`steps` 与 `deletedSteps`。时间为含时区偏移的 ISO 8601 值。`StepRecord` 包含 `id`、`number`、`title`、`description`、`kind`、`mediaPath`、`originalPath`、`createdAt`、`muted`、`isDraft`、`durationSeconds`、`width`、`height`、`deletedAt`。

- `kind` 为 `image`、`video` 或 `gif`；`mediaPath` 是当前展示的项目内相对文件路径。`originalPath` 可指向原截图，或保存原始录制的相对目录。
- `annotationPath` 为可选的项目内相对 JSON 路径；新标注截图同时保留原图、合成图及矢量笔迹。旧项目无需迁移，缺省为 null。
- `isDraft` 默认 `true`。截图确认标注后、录制停止后入库，步骤编辑窗口关闭也保留素材；明确保存步骤后由宿主置为 `false`。标注取消发生在入库之前，不创建步骤。
- `number` 由有效步骤的排列顺序重新计算，从 1 连续编号。`id` 是稳定标识，重排不改变 ID。
- 删除操作将记录移入 `deletedSteps` 并填写 `deletedAt`，不删除素材。再次转换也由宿主保留原始录制。
- 路径拒绝绝对路径、父级跳转、URI、目录链接和文件链接。素材地址逐段进行 URL 编码；所有用户文本使用 HTML 编码或 DOM `textContent`，说明按纯文本保留换行。

写入由单个异步锁串行化。每个文件先写临时文件并刷新到磁盘，再原子替换且保留 `.bak`。先更新派生 HTML，再提交权威 JSON；失败时从已提交 JSON 恢复 HTML。`Open` 优先读取清单，损坏时读 `.bak` 并保留损坏副本，之后重新生成 HTML。宿主应为一个已打开项目持有排他的项目锁，防止多个进程同时写入。

## 内置页面桥

应用偏好独立于项目，保存于当前用户 `%LOCALAPPDATA%/Stepwise/settings.json`，默认由 `PreferencesStore.Default` 访问。诊断环境可通过 `STEPWISE_SETTINGS_FILE` 指定隔离文件，发行快捷方式不设置该变量。读取不改文件；每次更新在跨进程锁内重读最新数据并只修改调用方字段，使用原子替换与备份，损坏字段回退默认值。`Remember` 在同一锁内判断 `RememberChoices` 后才写入操作选择；`ApplyConfiguration` 用于用户明确保存设置，始终写入且保留独立管理的操作栏位置和 `NoteExportFormat`。设置草稿不管理文档格式，保存时不得将草稿中的缺省值覆盖到最新的文档导出偏好。配置含默认延时；单次菜单延时、选区、笔迹和步骤内容不在偏好结构中。

设置使用独立 `settings.html` / `SettingsForm`，只映射应用资源、不映射项目素材。快捷键检测和保存均由本机真实 `RegisterHotKey` 完成；注册候选组合后再提交设置，持久化失败会释放候选注册并保留当前动作。设置窗口打开时全局快捷键只保留占用、不执行动作，以便准确检测自己的组合并安全录入。默认延时仅用于按钮/托盘，快捷键传递固定 0 秒。

标注初始化额外带 `preferences`，明确的用户选择发送 `annotationPreferences` 和局部 `preferences` 字段；原生关闭读取 `takePendingPreferences()`，确保调色/缩放合并等待中的选择不会遗失。录制编辑首次进入新录制时可带 `recordingDefaults:{format,muted}`，旧步骤按实际素材显示；明确选择发送 `recordingPreferences`，其中 `muted` 表示 MP4 静音偏好，不能用 GIF 固有无声覆盖。`preferencesError` 仅提示，不修改当前保存忙碌状态。

截图标注使用独立的 `annotation.html`。宿主通过内存图片响应 `https://capture.stepwise.local/image.png`，不提前写图片文件。页面 `ready` 后接收 `window.stepwiseAnnotation.receive({type:"init",imageUrl,width,height})`；`confirm` 带 `pngBase64` 与 `layers` 对象，`cancel` 直接取消。`ScreenshotWriter` 提交三份媒体后再新增步骤，清单提交失败回收本次新文件。原生宿主收到确认后等待实际持久化才关闭标注窗口，失败发回 `error`，保留未提交笔迹供重试。

宿主将 `Web` 映射到 `https://app.stepwise.local/`，将项目映射到 `https://project.stepwise.local/`。页面仅依赖同目录的 `app.css` 与 `app.js`，不使用网络字体、CDN 或框架。

页面加载后发送 `{type:"ready"}`；宿主使用 `window.stepwise.receive(message)` 传入 `{type:"init",mode:"editor"|"preview",project,step,rootUrl,warnings,isRecording}`。同一步骤重复初始化保留当前编辑文字；预览页可在项目变化后重新初始化。

文字修改 300 毫秒后发送 `draft`，保存发送 `save`。二者都带 `{step:{id,title,description},format:"mp4"|"gif",muted,revision}`；`revision` 随用户修改递增。GIF 的 `muted` 总为 `true`。原始格式转换由宿主完成，页面只表达目标格式。

`draftSaved` 应回传相同 `revision`；页面只在当前版本确认后显示“已暂存”。旧确认不会清除后续输入。`error` 清除忙碌并允许重试，`busy` 锁定输入，`notice` 显示完成提示。宿主关闭窗口前，应执行 `window.stepwise.getDraft()` 获取完整表单并 **等待** 宿主持久化；不要只依赖异步消息。`flushDraft()` 也可主动发送尚未确认的草稿。

其他页面消息为 `close`、`edit`、`delete`、`move`、`reveal`、`preview`、`rename`、`recover`、`print`、`export`。编辑与移除按 `id`，移动另带 `direction: -1|1`，重命名带 `title`。删除前页面弹出确认，说明素材会保留。`print` 由宿主调用 `CoreWebView2.ShowPrintUI()` 打开打印/PDF预览；预览页的 `export` 打开原生 `ExportForm`，通过统一服务生成可单独分享的成品。

`document.html` 不依赖编辑器或 WebView2。它使用相对素材路径，支持视频控制、截图放大、目录导航、窄屏布局与打印。打印视频显示素材文件名，动态视频和 GIF 的完整效果以电子文档为准。

## 分享导出契约（1.3.0）

`document.html` 是项目的实时派生视图，仍引用 `media`；它不能冒充自包含成品。`NoteExportService` 接收一个项目快照，返回 `ExportResult(Directory, Files, Summary)`，支持 `Word`、`Html`、`LongImage`、`Pdf` 四种 `ExportFormat`。所有成品发布到项目内 `exports/<标题-时间-格式-随机编号>/`，无需依赖原项目。长图超过单张上限时返回多个按顺序编号的 PNG。

导出入口先等待开放编辑窗口的 `FlushDraftAsync()`，再获取 `messageGate` 并调用 `ProjectStore.SnapshotAsync()`。`export` 分支必须在通常的消息锁之外调度：草稿落盘也使用同一消息锁，持锁等待 flush 会死锁。快照在存储锁内深复制有效步骤及文字，导出期间阻止冲突的采集和项目编辑，重排、删除及后续修改不会混入本次结果。

`ExportSnapshot(ProjectData Project, IReadOnlyList<ExportStep> Steps)` 是各格式共享的输入。`ExportStep` 持有步骤、原媒体绝对路径、首帧路径和首帧尺寸；绝对路径仅供本机工作流程使用，不能写入分享文档。服务通过项目路径边界检查解析素材，按原媒体路径复用首帧，以内置 FFmpeg 制作不超过 **1600 × 2400** 的预览图，保持比例且不放大原图。原媒体从不改写；GIF/视频用于静态格式时取第一帧并标明其性质。

每次作业仅操作新建的 `exports/.pending-<id>`：`work` 保存首帧与渲染中间文件，`output` 接收完整成品。完成所有文件并确认未取消后，将 `output` 在同一文件系统中移动到独立结果目录；不会替换既有结果。失败或取消不发布结果，在 `finally` 中仅清理已验证边界且属于本作业的临时目录。外部进程占用导致清理失败时记录诊断，不误报导出成功。`ExportForm` 负责取消、重试和完成后的打开文件/目录；关闭正在运行的窗口先请求取消，等待工作结束后关闭。

| 实现 | 线程、输出与约束 |
| --- | --- |
| `WordNoteExporter` | 工作线程生成标准 DOCX，图像嵌入包内，标题和正文可编辑；Letter 页面、页眉页脚和页码，不要求安装 Office。 |
| `StandaloneHtmlExporter` | 工作线程按 192 KiB 原始字节分块读写 Base64，避免将整个视频或 HTML 拼为 C# 字符串；同一路径只嵌入一次。CSP 哈希限定脚本，无外部依赖；文本 HTML 转义，清单 JSON 使用安全编码。 |
| `StaticNoteDocument` / `NoteRenderForm` | PDF 与长图共用静态排版。WebView2 必须在拥有窗口的 STA 界面线程创建和使用，不能整体放进 `Task.Run`。PDF 为 Letter、正文可搜索；PNG 视口宽 1440，每次截图最多高 12000，优先按步骤边界切分，避免分配整份长文档位图。 |

HTML 的页面脚本在可见区域按需解码图片和 GIF，视频点击播放后才加载；每块解码后让出主线程，同资源共享未完成 Promise 与 Blob URL，离页释放 URL。打印按钮和 Ctrl+P 会先等待首帧就绪，仅以首帧打印动态图像与视频。内嵌原媒体字节保持不变，可下载保存；静态首帧仅用于预览。Base64 约增加 33% 体积，另有首帧和页面内容；浏览器仍需读入整个 HTML，懒解码不能消除整文件解析和较大素材的内存成本。

`UserPreferences.NoteExportFormat` 取 `word|html|longimage|pdf`，旧配置缺字段时默认 `word`，无效值规范化为默认。格式卡片的主动选择调用 `Remember`，遵循“根据操作更新记忆”开关；初始化选择不写偏好，保存失败仅提示，不阻断本次导出。设置窗口的完整草稿提交保留最新 `NoteExportFormat`，避免用户修改无关设置时抹掉刚选择的格式。录屏的 `ExportFormat=mp4|gif` 与文档导出格式是独立字段。
