using System.Net;
using System.Text;

namespace Stepwise.Core;

/// <summary>Builds a portable, network-free document. User text is always HTML-encoded.</summary>
public static class DocumentExporter
{
    public static string Render(ProjectData project)
    {
        var html = new StringBuilder(Styles.Length + project.Steps.Count * 1000);
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self' data: file:; media-src 'self' file:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">");
        html.Append("<title>").Append(E(project.Title)).Append(" · 步录</title><style>").Append(Styles).Append("</style></head><body>");
        html.Append("<aside class=\"sidebar\"><a class=\"brand\" href=\"#top\"><span class=\"brand-icon\">S</span> 步录<span class=\"brand-en\">STEPWISE</span></a><p class=\"sidebar-label\">步骤目录</p><nav aria-label=\"步骤目录\">");
        foreach (var step in project.Steps)
            html.Append("<a href=\"#step-").Append(step.Number).Append("\"><span>").Append(step.Number.ToString("D2")).Append("</span>").Append(E(Title(step))).Append("</a>");
        html.Append("</nav><div class=\"sidebar-footer\">记录每一个关键步骤。<br>本地文档 · 可离线查看</div></aside><main id=\"top\"><header class=\"document-header\"><div class=\"eyebrow\">操作记录 / PROCESS NOTES</div><h1>").Append(E(project.Title)).Append("</h1>");
        html.Append("<div class=\"meta\"><span>").Append(project.Steps.Count).Append(" 个步骤</span><span class=\"dot\">·</span><span>").Append(project.CreatedAt.ToLocalTime().ToString("yyyy 年 M 月 d 日")).Append("</span><button id=\"print\" type=\"button\" title=\"打印或另存为 PDF\">打印 / PDF</button></div></header>");
        if (project.Steps.Count == 0) html.Append("<section class=\"empty\"><span>＋</span><h2>从第一个步骤开始</h2><p>通过悬浮操作栏截图或录制，记录会自动汇集于此。</p></section>");
        foreach (var step in project.Steps)
        {
            var url = MediaUrl(step.MediaPath);
            html.Append("<article class=\"step\" id=\"step-").Append(step.Number).Append("\"><div class=\"step-heading\"><span class=\"step-number\">").Append(step.Number.ToString("D2")).Append("</span><div><div class=\"step-label\">STEP ").Append(step.Number.ToString("D2"));
            if (step.IsDraft) html.Append("<span class=\"draft\">待补充</span>");
            html.Append("</div><h2>").Append(E(Title(step))).Append("</h2></div></div>");
            if (!string.IsNullOrWhiteSpace(step.Description)) html.Append("<p class=\"description\">").Append(E(step.Description)).Append("</p>");
            html.Append("<figure>");
            if (step.Kind == "video")
            {
                html.Append("<video controls preload=\"metadata\" playsinline");
                if (step.Muted) html.Append(" muted");
                html.Append(" src=\"").Append(E(url)).Append("\">此浏览器无法播放视频，请打开同目录中的 MP4 文件。</video><div class=\"print-video\">视频片段 · ").Append(E(Path.GetFileName(step.MediaPath))).Append("</div>");
            }
            else html.Append("<img src=\"").Append(E(url)).Append("\" alt=\"").Append(E(Title(step))).Append("\" loading=\"lazy\" class=\"zoomable\" tabindex=\"0\" role=\"button\" title=\"点击放大\">");
            html.Append("<figcaption><span>").Append(step.Kind switch { "video" => "屏幕录制", "gif" => "GIF 动图", _ => "屏幕截图" }).Append("</span>");
            if (step.DurationSeconds > 0) html.Append("<span>").Append(FormatDuration(step.DurationSeconds)).Append("</span>");
            if (step.Kind == "video" && step.Muted) html.Append("<span>无声</span>");
            html.Append("<a href=\"").Append(E(url)).Append("\" download>原始素材 ↗</a></figcaption></figure></article>");
        }
        html.Append("<footer class=\"document-footer\"><span>由 步录 Stepwise 整理</span><span>更新于 ").Append(project.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append("</span></footer></main><dialog id=\"lightbox\" aria-label=\"查看大图\"><button id=\"lightbox-close\" aria-label=\"关闭大图\">×</button><img id=\"lightbox-image\" alt=\"放大的步骤截图\"></dialog><script>").Append(Script).Append("</script></body></html>");
        return html.ToString();
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Title(StepRecord step) => string.IsNullOrWhiteSpace(step.Title) ? $"步骤 {step.Number}" : step.Title;
    private static string MediaUrl(string path) => string.Join("/", path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    private static string FormatDuration(double duration)
    {
        var value = TimeSpan.FromSeconds(duration);
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}" : $"{(int)value.TotalMinutes}:{value.Seconds:D2}";
    }

    private const string Styles = """
        :root{color-scheme:light;--ink:#243733;--muted:#76847d;--accent:#227a69;--line:#e4e8e1;--paper:#fffefa;--canvas:#f4f5ef}*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:30px}body{margin:0;color:var(--ink);background:var(--canvas);font:15px/1.7 "Microsoft YaHei UI","Microsoft YaHei","Segoe UI",sans-serif}button,a,img{ -webkit-tap-highlight-color:transparent}button{font:inherit;cursor:pointer}a{color:inherit}.sidebar{position:fixed;inset:0 auto 0 0;width:254px;background:#fafbf6;border-right:1px solid var(--line);padding:34px 25px;display:flex;flex-direction:column}.brand{display:flex;align-items:center;gap:8px;text-decoration:none;font-weight:700;font-size:18px;letter-spacing:.5px}.brand-icon{width:28px;height:28px;display:grid;place-items:center;background:var(--accent);color:white;border-radius:9px;font-size:18px}.brand-en{font-size:10px;letter-spacing:1.4px;color:var(--muted);margin-left:3px}.sidebar-label{font-size:11px;color:var(--muted);letter-spacing:2px;margin:50px 10px 14px}.sidebar nav{overflow:auto;flex:1}.sidebar nav a{display:flex;gap:12px;align-items:baseline;margin:5px 0;padding:9px 10px;border-radius:9px;text-decoration:none;font-size:13px;overflow-wrap:anywhere}.sidebar nav a:hover{background:#e9eee6;color:var(--accent)}.sidebar nav a span{font:11px/1.7 "Segoe UI",sans-serif;color:#92a49a}.sidebar-footer{font-size:11px;line-height:2;color:#90a095;padding:24px 10px 0}main{max-width:1180px;margin-left:254px;padding:68px 6vw 30px}.document-header{padding-bottom:36px;border-bottom:1px solid var(--line);margin-bottom:45px}.eyebrow{color:var(--accent);font-size:10px;font-weight:700;letter-spacing:2px}h1{font-weight:600;font-size:34px;line-height:1.4;letter-spacing:-.7px;margin:15px 0 20px;overflow-wrap:anywhere}.meta{display:flex;align-items:center;gap:13px;font-size:12px;color:var(--muted)}.meta button{margin-left:auto;border:1px solid #d8e0d7;border-radius:8px;padding:7px 14px;color:var(--accent);background:transparent;font-size:12px}.meta button:hover{background:#e8eee5}.dot{color:#b5c0b4}.step{margin-bottom:52px;scroll-margin-top:30px}.step-heading{display:flex;align-items:center;gap:18px;margin-bottom:19px}.step-number{font:400 25px/1 "Segoe UI",sans-serif;color:var(--accent);background:#e3ede3;border-radius:13px;min-width:52px;height:52px;display:grid;place-items:center}.step-label{font:10px/1.5 "Segoe UI",sans-serif;color:#91a096;letter-spacing:1.3px;margin:0 0 5px}.draft{display:inline-block;font:10px/1.5 "Microsoft YaHei UI",sans-serif;letter-spacing:0;border:1px solid #e4d9b6;color:#96752f;background:#fbf5e4;padding:1px 5px;border-radius:4px;margin-left:10px}h2{font-size:21px;line-height:1.5;font-weight:600;margin:0;overflow-wrap:anywhere}.description{white-space:pre-wrap;overflow-wrap:anywhere;margin:0 0 22px 70px;line-height:1.95;color:#53635a;font-size:14px}figure{margin:0;border:1px solid #e1e6dc;border-radius:15px;overflow:hidden;background:var(--paper);box-shadow:0 6px 22px #24373305}figure img,figure video{display:block;width:100%;max-height:730px;object-fit:contain;background:#e9ede5}figure img{width:auto;max-width:100%;margin:auto}figure img.zoomable{cursor:zoom-in}figure{background:#e9ede5}figcaption{background:var(--paper)}figcaption{display:flex;align-items:center;gap:15px;padding:11px 17px;font-size:10px;color:#7e8c82}figcaption a{margin-left:auto;color:#567969;text-decoration:none}figcaption a:hover{text-decoration:underline}.document-footer{display:flex;justify-content:space-between;padding:24px 0;border-top:1px solid var(--line);color:#90a094;font-size:10px}.empty{padding:60px 20px 90px;text-align:center;color:#889789}.empty>span{display:inline-grid;place-items:center;width:60px;height:60px;background:#e5ece1;border-radius:18px;font-size:30px;font-weight:200;margin-bottom:20px;color:var(--accent)}.empty h2{font-size:19px;color:#536c5b}.empty p{font-size:13px}.print-video{display:none}dialog{border:0;padding:35px;background:#13251bee;border-radius:16px;max-width:96vw;max-height:96vh;overflow:auto}dialog::backdrop{background:#152318bb}dialog img{display:block;max-width:88vw;max-height:86vh;object-fit:contain}#lightbox-close{position:fixed;right:20px;top:12px;background:transparent;color:#fff;border:0;font-size:30px;width:40px;height:40px}a:focus-visible,button:focus-visible,img:focus-visible{outline:3px solid #80bba4;outline-offset:4px}@media(max-width:900px){.sidebar{width:205px;padding:30px 17px}.brand-en{display:none}main{margin-left:205px;padding:45px 4vw}}@media(max-width:650px){.sidebar{position:static;width:auto;padding:20px 22px;border-right:0;border-bottom:1px solid var(--line)}.sidebar-label,.sidebar nav,.sidebar-footer{display:none}.brand-en{display:inline}main{margin:0;padding:32px 20px}h1{font-size:27px}.document-header{margin-bottom:32px}.description{margin-left:0}.step-heading{gap:13px}.step-number{min-width:44px;height:44px;font-size:22px}h2{font-size:18px}.document-footer{gap:15px;flex-wrap:wrap}figcaption{gap:10px}}@media print{@page{size:A4;margin:18mm}body{background:white;color:#16241b;font-size:10pt}.sidebar,.meta button,dialog,figcaption a{display:none!important}main{padding:0;margin:0;max-width:none}.document-header{padding-bottom:16px;margin-bottom:25px}.eyebrow{font-size:8pt}h1{font-size:24pt}.step{margin-bottom:28px}.step-heading{break-after:avoid}.step-number{print-color-adjust:exact;-webkit-print-color-adjust:exact}.description{margin-left:0;line-height:1.65;font-size:10pt;break-after:avoid}figure{box-shadow:none;border-radius:6px;break-inside:avoid}figure img{max-height:220mm;object-fit:contain}video{display:none!important}.print-video{display:block;padding:32px;background:#f1f3ed;color:#536759;text-align:center}figcaption{font-size:8pt}.document-footer{font-size:8pt}.draft{print-color-adjust:exact;-webkit-print-color-adjust:exact}.empty{padding:40px 0}a{color:inherit;text-decoration:none}}
        """;
    private const string Script = """
        (()=>{const dialog=document.getElementById('lightbox'),large=document.getElementById('lightbox-image');function show(img){large.src=img.src;large.alt=img.alt;if(typeof dialog.showModal==='function')dialog.showModal();else window.open(img.src,'_blank','noopener');}document.querySelectorAll('.zoomable').forEach(img=>{img.addEventListener('click',()=>show(img));img.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();show(img);}});});document.getElementById('lightbox-close').addEventListener('click',()=>dialog.close());dialog.addEventListener('click',e=>{if(e.target===dialog)dialog.close();});document.getElementById('print').addEventListener('click',()=>window.print());})();
        """;
}
