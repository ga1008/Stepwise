using System.Net;
using System.Text;

namespace Stepwise.Core;

internal static class StaticNoteDocument
{
    internal static string Render(ExportSnapshot snapshot)
    {
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'\"><title>");
        html.Append(E(snapshot.Project.Title)).Append("</title><style>").Append(Css).Append("</style></head><body><main><header class=\"document-header\"><div class=\"eyebrow\">操作笔记 · STEPWISE</div><h1>")
            .Append(E(snapshot.Project.Title)).Append("</h1><p class=\"meta\">").Append(snapshot.Steps.Count).Append(" 个步骤 · ")
            .Append(snapshot.Project.UpdatedAt.ToLocalTime().ToString("yyyy 年 M 月 d 日")).Append("</p></header>");
        if (snapshot.Steps.Count == 0) html.Append("<p class=\"empty\">这份笔记还没有步骤。</p>");
        foreach (var item in snapshot.Steps)
        {
            var step = item.Step;
            var compact=step.Description.Length<=500 && step.Description.Count(c=>c=='\n')<=5;
            html.Append(compact?"<article class=\"compact\"><h2><span>":"<article><h2><span>").Append(step.Number.ToString("D2")).Append("</span> ")
                .Append(E(string.IsNullOrWhiteSpace(step.Title) ? $"步骤 {step.Number}" : step.Title)).Append("</h2>");
            if (!string.IsNullOrEmpty(step.Description))
                foreach (var paragraph in step.Description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    html.Append("<p class=\"description\">").Append(string.IsNullOrEmpty(paragraph) ? "&#160;" : E(paragraph)).Append("</p>");
            html.Append("<figure><img width=\"").Append(item.PosterWidth).Append("\" height=\"").Append(item.PosterHeight)
                .Append("\" src=\"").Append(E(Uri.EscapeDataString(Path.GetFileName(item.PosterPath)))).Append("\" alt=\"").Append(E(step.Title))
                .Append("\"><figcaption>").Append(step.Kind switch { "video" => "视频首帧", "gif" => "动图首帧", _ => "屏幕截图" });
            if (step.Kind is "video" or "gif") html.Append(" · 静态文档仅保留第一帧");
            if (step.IsDraft) html.Append(" · 待补充");
            html.Append("</figcaption></figure></article>");
        }
        html.Append("<footer>由 步录 Stepwise 整理</footer></main><style>@media print{article.compact{break-inside:avoid}}</style><script>").Append(Script).Append("</script></body></html>");
        return html.ToString();
    }

    private const string Css = """
        *{box-sizing:border-box}html{background:#eef2ed}body{margin:0;color:#263d35;font:22px/1.8 'Microsoft YaHei','Segoe UI',sans-serif}main{width:1440px;padding:86px 106px 62px;background:#fffefa;overflow:hidden}.eyebrow{color:#347965;letter-spacing:3px;font-size:17px}h1{font-size:49px;line-height:1.35;font-weight:650;margin:20px 0 18px;overflow-wrap:anywhere}.meta{color:#77887e;font-size:18px;margin:0}.document-header{border-bottom:1px solid #dfe7dc;padding-bottom:40px;margin-bottom:44px}article{margin-bottom:56px}h2{font-size:30px;line-height:1.5;margin:0 0 23px;font-weight:600;overflow-wrap:anywhere}h2 span{font-size:24px;font-weight:400;color:#237862;margin-right:16px}.description{white-space:pre-wrap;overflow-wrap:anywhere;margin:0 0 9px;color:#4e645a;line-height:1.8}figure{margin:25px 0 0;border:1px solid #dfe6dc;border-radius:14px;background:#eef2e9;overflow:hidden}img{display:block;max-width:100%;width:auto;height:auto;max-height:2100px;object-fit:contain;margin:auto}figcaption{padding:15px 22px;font-size:16px;color:#7b8d80;background:#f7f9f1}footer{border-top:1px solid #e1e8de;padding-top:22px;color:#88998c;font-size:16px}.empty{padding:35px 0;color:#73877a}@media print{@page{size:letter;margin:19mm 18mm}html,body{background:white;color:#000}body{font-size:11pt;line-height:1.6}main{width:auto;padding:0;overflow:visible;background:white}h1{font-size:26pt;color:#000;line-height:1.35;break-after:avoid}h2{font-size:15pt;color:#000;line-height:1.5;break-after:avoid;margin:0 0 11pt}h2 span{font-size:13pt;color:#000}.eyebrow{font-size:8pt;color:#555}.meta{font-size:9pt}.document-header{padding-bottom:16pt;margin-bottom:20pt;break-inside:avoid}article{margin-bottom:23pt}.description{font-size:11pt;line-height:1.7;orphans:3;widows:3;color:#222;margin-bottom:5pt}figure{break-inside:avoid;margin-top:12pt;border-radius:5pt}img{max-width:100%;max-height:165mm}figcaption{font-size:8pt;padding:7pt 9pt}footer{font-size:8pt;margin-top:14pt}}
        """;

    private const string Script = """
        window.exportReady=false;(async()=>{try{await document.fonts.ready;await Promise.all([...document.images].map(img=>img.decode()));window.exportReady=true;}catch(e){window.exportError='图片无法完整载入：'+e.message;}})();
        window.exportCut=(start,limit)=>{const height=Math.ceil(document.documentElement.scrollHeight),end=Math.min(start+limit,height);if(end>=height)return height;const ends=[...document.querySelectorAll('article')].map(x=>x.getBoundingClientRect().bottom+scrollY+28).filter(y=>y>start+limit*.45&&y<=end);if(ends.length)return Math.floor(ends.at(-1));for(const x of document.querySelectorAll('h1,h2,p,figure,header,footer')){const r=x.getBoundingClientRect(),top=r.top+scrollY,bottom=r.bottom+scrollY;if(top<end&&bottom>end){if(x.matches('p')&&top<=end-60){const line=parseFloat(getComputedStyle(x).lineHeight);return Math.max(start+1,Math.floor(top+Math.floor((end-top)/line)*line));}if(top>start+200)return Math.floor(top);}}return end;};
        """;
}
