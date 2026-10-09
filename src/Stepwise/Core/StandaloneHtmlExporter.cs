using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Stepwise.Core;

/// <summary>A genuinely single-file document: bounded-memory base64 writing, deferred browser decoding, no network dependencies.</summary>
public static class StandaloneHtmlExporter
{
    // A multiple of three keeps every chunk independently decodable without mid-stream padding.
    private const int ChunkBytes = 192 * 1024;
    private sealed record Asset(string Id, string Path, string Mime, string Name, long Size, int Parts);

    public static async Task ExportAsync(ExportSnapshot snapshot, string destination,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        var assets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        Asset Register(string path)
        {
            var full = Path.GetFullPath(path);
            if (assets.TryGetValue(full, out var existing)) return existing;
            var info = new FileInfo(full);
            if (!info.Exists || info.Length == 0) throw new InvalidDataException("导出素材不存在或为空：" + info.Name);
            var count = checked((int)((info.Length + ChunkBytes - 1) / ChunkBytes));
            var asset = new Asset("a" + (assets.Count + 1), full, Mime(full), info.Name, info.Length, count);
            assets.Add(full, asset);
            return asset;
        }
        var items = snapshot.Steps.Select(item => (Item: item, Media: Register(item.MediaPath), Poster: Register(item.PosterPath))).ToArray();
        var total = assets.Values.Sum(asset => asset.Size);
        long completed = 0;
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(file, new UTF8Encoding(false), 65536);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script)));
        await writer.WriteAsync($"<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src blob: data:; media-src blob:; style-src 'unsafe-inline'; script-src 'sha256-{hash}'; base-uri 'none'; form-action 'none'; object-src 'none'\"><title>{E(snapshot.Project.Title)} · 步录</title><style>{Styles}</style></head><body>");
        await writer.WriteAsync("<aside class=\"sidebar\"><a class=\"brand\" href=\"#top\"><span>S</span>步录<small>STEPWISE</small></a><details class=\"contents\" open><summary>步骤目录</summary><nav aria-label=\"步骤目录\">");
        for (var index = 0; index < items.Length; index++)
            await writer.WriteAsync($"<a href=\"#step-{index + 1}\"><span>{index + 1:D2}</span>{E(Title(items[index].Item.Step))}</a>");
        await writer.WriteAsync($"</nav></details><div class=\"sidebar-footer\">记录每一个关键步骤。<br>全部素材内嵌 · 离线可用</div></aside><main id=\"top\"><header class=\"document-header\"><div class=\"eyebrow\">操作记录 / PROCESS NOTES</div><h1>{E(snapshot.Project.Title)}</h1><div class=\"meta\"><span>{items.Length} 个步骤</span><span>·</span><time>{snapshot.Project.CreatedAt.ToLocalTime():yyyy 年 M 月 d 日}</time><button id=\"print\" type=\"button\" title=\"动态图像和视频使用首帧\">打印 / PDF</button></div></header><noscript><p class=\"notice\">请允许此文档运行 JavaScript，以显示内嵌的图片和视频。文档完全离线，不会连接网络。</p></noscript>");
        if (items.Length == 0)
            await writer.WriteAsync("<section class=\"empty\"><span>＋</span><h2>暂无步骤</h2><p>这份笔记尚未添加操作记录。</p></section>");
        for (var index = 0; index < items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (item, media, poster) = items[index];
            var step = item.Step;
            var video = step.Kind == "video";
            var gif = step.Kind == "gif";
            var width = Math.Max(1, item.PosterWidth);
            var height = Math.Max(1, item.PosterHeight);
            await writer.WriteAsync($"<article class=\"step\" id=\"step-{index + 1}\"><div class=\"step-heading\"><span class=\"step-number\">{index + 1:D2}</span><div><div class=\"step-label\">STEP {index + 1:D2}{(step.IsDraft ? "<span class=\"draft\">待补充</span>" : "")}</div><h2>{E(Title(step))}</h2></div></div>");
            if (!string.IsNullOrWhiteSpace(step.Description))
                await writer.WriteAsync($"<p class=\"description\">{E(step.Description)}</p>");
            await writer.WriteAsync($"<figure data-kind=\"{(video ? "video" : gif ? "gif" : "image")}\" data-media=\"{media.Id}\" data-poster=\"{poster.Id}\"><div class=\"visual\" style=\"aspect-ratio:{width}/{height}\"><img class=\"poster\" width=\"{width}\" height=\"{height}\" alt=\"{E(Title(step))}\" decoding=\"async\"><div class=\"media-status\" role=\"status\">正在准备预览…</div>");
            if (video)
                await writer.WriteAsync($"<video controls preload=\"none\" playsinline{(step.Muted ? " muted" : "")} aria-label=\"{E(Title(step))}\" hidden></video><button class=\"play\" type=\"button\" aria-label=\"播放视频\"><span aria-hidden=\"true\">▶</span><span class=\"play-label\">播放视频</span></button>");
            else
                await writer.WriteAsync($"<button class=\"zoom\" type=\"button\" aria-label=\"放大：{E(Title(step))}\" title=\"查看原图\"></button>");
            await writer.WriteAsync($"</div><figcaption><span class=\"media-kind\">{(video ? "屏幕录制" : gif ? "GIF 动图" : "屏幕截图")}</span>{(step.DurationSeconds > 0 ? "<span>" + Duration(step.DurationSeconds) + "</span>" : "")}{(video && step.Muted ? "<span>无声</span>" : "")}<span class=\"file-size\">{Size(media.Size)}</span><button class=\"download\" type=\"button\">保存素材 ↓</button></figcaption></figure></article>");
        }
        await writer.WriteAsync($"<footer class=\"document-footer\"><span>由 步录 Stepwise 整理</span><span>更新于 {snapshot.Project.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}</span></footer></main><dialog id=\"lightbox\" aria-label=\"查看大图\"><button id=\"lightbox-close\" type=\"button\" aria-label=\"关闭大图\">×</button><img id=\"lightbox-image\" alt=\"步骤原图\"></dialog><div id=\"toast\" role=\"status\" hidden></div>");
        var metadata = assets.Values.Select(asset => new { id = asset.Id, mime = asset.Mime, name = asset.Name, size = asset.Size, parts = asset.Parts });
        await writer.WriteAsync("<script id=\"asset-map\" type=\"application/json\">" + JsonSerializer.Serialize(metadata) + "</script>");
        var buffer = new byte[ChunkBytes];
        var encoded = new char[ChunkBytes / 3 * 4];
        foreach (var asset in assets.Values)
        {
            ct.ThrowIfCancellationRequested();
            await using var source = new FileStream(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != asset.Size) throw new IOException("导出期间素材发生变化，请重试。");
            for (var part = 0; part < asset.Parts; part++)
            {
                ct.ThrowIfCancellationRequested();
                var count = await source.ReadAtLeastAsync(buffer.AsMemory(), ChunkBytes, false, ct);
                if (count == 0) throw new EndOfStreamException("素材读取不完整，请重试。");
                var characters = Convert.ToBase64CharArray(buffer, 0, count, encoded, 0);
                await writer.WriteAsync($"<script id=\"{asset.Id}-{part}\" type=\"application/octet-stream\">");
                await writer.WriteAsync(encoded.AsMemory(0, characters), ct);
                await writer.WriteAsync("</script>");
                completed += count;
                progress?.Report(new ExportProgress(total == 0 ? 95 : 5 + (int)(90 * completed / total), "正在嵌入素材"));
            }
        }
        await writer.WriteAsync("<script>" + Script + "</script></body></html>");
        await writer.FlushAsync(ct);
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ExportProgress(100, "单文件 HTML 已生成"));
    }

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Title(StepRecord step) => string.IsNullOrWhiteSpace(step.Title) ? $"步骤 {step.Number}" : step.Title;
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
        ".webp" => "image/webp", ".bmp" => "image/bmp", ".mp4" => "video/mp4",
        _ => throw new InvalidDataException("不支持的素材格式：" + Path.GetExtension(path))
    };
    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "";
        var value = TimeSpan.FromSeconds(Math.Min(seconds, 365 * 24 * 3600));
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}" : $"{(int)value.TotalMinutes}:{value.Seconds:D2}";
    }
    private static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024d):0.#} KB";

    private const string Styles = """
        :root{color-scheme:light;--ink:#243733;--muted:#76847d;--accent:#227a69;--line:#e0e7df;--paper:#fffefa;--canvas:#f4f5ef}*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:28px}body{margin:0;color:var(--ink);background:var(--canvas);font:15px/1.75 "Microsoft YaHei UI","Microsoft YaHei","Segoe UI",sans-serif}button,a{-webkit-tap-highlight-color:transparent}button{font:inherit;cursor:pointer}button:disabled{cursor:wait;opacity:.65}a{color:inherit}button:focus-visible,a:focus-visible{outline:3px solid #83bca9;outline-offset:4px}[hidden]{display:none!important}.sidebar{position:fixed;inset:0 auto 0 0;width:254px;padding:34px 25px;background:#fafbf6;border-right:1px solid var(--line);display:flex;flex-direction:column}.brand{display:flex;align-items:center;gap:8px;text-decoration:none;font-weight:700;font-size:18px}.brand>span{width:29px;height:29px;display:grid;place-items:center;background:var(--accent);color:white;border-radius:9px}.brand small{font-size:9px;letter-spacing:1.5px;color:var(--muted);margin-left:3px}.contents{margin-top:45px;overflow:auto;flex:1}.contents summary{font-size:11px;letter-spacing:2px;color:var(--muted);padding:0 10px 15px;cursor:pointer}.contents nav a{display:flex;align-items:baseline;gap:11px;margin:4px 0;padding:8px 10px;border-radius:9px;text-decoration:none;font-size:12px;overflow-wrap:anywhere}.contents nav a:hover{background:#e9eee6;color:var(--accent)}.contents nav a span{font:11px/1.7 "Segoe UI",sans-serif;color:#8ea396}.sidebar-footer{padding:24px 10px 0;font-size:10px;color:#90a095;line-height:2}main{max-width:1210px;margin-left:254px;padding:64px 6vw 28px}.document-header{padding-bottom:32px;border-bottom:1px solid var(--line);margin-bottom:42px}.eyebrow{font-size:10px;letter-spacing:2px;color:var(--accent);font-weight:700}h1{font-size:34px;font-weight:600;line-height:1.4;letter-spacing:-.7px;margin:15px 0 22px;overflow-wrap:anywhere}.meta{display:flex;align-items:center;gap:12px;font-size:11px;color:var(--muted)}.meta button{margin-left:auto;border:1px solid #d4dfd4;border-radius:9px;padding:7px 13px;color:var(--accent);background:transparent;font-size:11px;white-space:nowrap}.meta button:hover{background:#e8eee5}.step{margin-bottom:49px;scroll-margin-top:28px}.step-heading{display:flex;align-items:center;gap:18px;margin-bottom:18px}.step-number{display:grid;place-items:center;min-width:50px;height:50px;padding:0 8px;border-radius:13px;background:#e3ede3;color:var(--accent);font:400 24px/1 "Segoe UI",sans-serif}.step-label{font:10px/1.5 "Segoe UI",sans-serif;color:#8b9e91;letter-spacing:1.4px;margin-bottom:5px}.draft{display:inline-block;font:10px/1.5 "Microsoft YaHei UI",sans-serif;letter-spacing:0;border:1px solid #e4d9b6;color:#96752f;background:#fbf5e4;padding:1px 5px;border-radius:4px;margin-left:10px}h2{font-size:21px;line-height:1.5;font-weight:600;margin:0;overflow-wrap:anywhere}.description{white-space:pre-wrap;overflow-wrap:anywhere;margin:0 0 22px 68px;line-height:1.95;color:#53635a;font-size:14px}figure{margin:0;border:1px solid #dfe6db;border-radius:15px;overflow:hidden;background:var(--paper);box-shadow:0 6px 22px #24373305}.visual{position:relative;max-height:740px;min-height:70px;background:#e9ede5;overflow:hidden;display:grid;place-items:center}.visual>img,.visual>video{display:block;width:100%;height:100%;max-height:740px;object-fit:contain;grid-area:1/1;min-width:0;min-height:0}.visual>img.original{position:absolute;inset:0}.visual>video{z-index:2}.media-status{position:absolute;left:12px;bottom:12px;background:#fafff6eb;border-radius:7px;padding:5px 9px;font-size:11px;color:#5d7366;z-index:3;max-width:calc(100% - 24px);overflow-wrap:anywhere}.media-status.error{color:#a24536}.play{position:absolute;inset:0;background:linear-gradient(transparent,#18342821);border:0;color:#fff;display:flex;align-items:center;justify-content:center;flex-direction:column;gap:10px;z-index:4}.play>span:first-child{display:grid;place-items:center;width:62px;height:62px;border-radius:50%;background:#1d6c5be8;box-shadow:0 4px 22px #173c4330;padding-left:4px;font-size:23px}.play-label{font-size:11px;background:#244538be;padding:4px 10px;border-radius:6px}.play:hover>span:first-child{background:#185c4d;transform:scale(1.04)}.zoom{position:absolute;inset:0;background:transparent;border:0;cursor:zoom-in;z-index:2}.visual.playing .poster{visibility:hidden}figcaption{display:flex;align-items:center;gap:12px;padding:10px 16px;font-size:10px;color:#7b8a7f;background:var(--paper)}.download{margin-left:auto;padding:4px 0;border:0;background:none;color:#467665;font-size:11px}.download:hover{text-decoration:underline}.file-size{color:#a1ada3}.document-footer{display:flex;justify-content:space-between;gap:15px;padding:23px 0;border-top:1px solid var(--line);color:#90a094;font-size:10px}.empty{padding:45px 10px 80px;text-align:center;color:#889789}.empty>span{display:inline-grid;place-items:center;width:60px;height:60px;background:#e5ece1;border-radius:18px;font-size:30px;color:var(--accent);margin-bottom:20px}.empty h2{font-size:19px;color:#536c5b}.empty p{font-size:13px}.notice{background:#fff0d8;border:1px solid #ead8b5;padding:14px 18px;border-radius:12px;font-size:13px}dialog{border:0;padding:30px;background:#13251bf5;border-radius:16px;max-width:96vw;max-height:96vh;overflow:auto}dialog::backdrop{background:#152318bb}dialog img{display:block;max-width:88vw;max-height:86vh;object-fit:contain}#lightbox-close{position:fixed;right:19px;top:10px;background:transparent;color:#fff;border:0;font-size:30px;width:40px;height:40px}#toast{position:fixed;bottom:22px;left:50%;transform:translateX(-50%);max-width:min(600px,90vw);padding:12px 18px;border-radius:11px;background:#243d35f5;color:#fff;font-size:12px;box-shadow:0 4px 24px #16332322;z-index:50}script[type="application/octet-stream"],script[type="application/json"]{display:none}@media(max-width:950px){.sidebar{width:205px;padding:30px 17px}.brand small{display:none}main{margin-left:205px;padding:44px 4vw}}@media(max-width:650px){.sidebar{position:static;width:auto;padding:20px 22px;border-right:0;border-bottom:1px solid var(--line)}.brand small{display:inline}.sidebar-footer{display:none}.contents{margin-top:20px;max-height:190px}.contents summary{padding:0 0 8px}.contents nav a{padding:6px 0}.contents nav{columns:2;column-gap:20px}.contents nav a{break-inside:avoid}main{margin:0;padding:32px 20px}h1{font-size:27px}.document-header{margin-bottom:31px}.description{margin-left:0}.step-heading{gap:13px}.step-number{min-width:44px;height:44px;font-size:22px}h2{font-size:18px}.document-footer{flex-wrap:wrap}figcaption{gap:9px}.file-size{display:none}.meta{gap:8px}.meta button{padding:6px 9px}.visual{min-height:50px}}@media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.play:hover>span:first-child{transform:none}}@media print{@page{size:A4;margin:18mm}body{background:white;color:#16241b;font-size:10pt}.sidebar,.meta button,dialog,.download,.play,.zoom,.media-status,#toast,video,.original{display:none!important}main{margin:0;padding:0;max-width:none}.document-header{padding-bottom:16px;margin-bottom:25px}.eyebrow{font-size:8pt}h1{font-size:24pt}.step{margin-bottom:27px}.step-heading{break-after:avoid}.step-number,.draft{print-color-adjust:exact;-webkit-print-color-adjust:exact}.description{margin-left:0;line-height:1.7;font-size:10pt;break-after:avoid}figure{box-shadow:none;border-radius:6px;break-inside:avoid}.visual{max-height:220mm;min-height:0}.visual>img.poster{display:block!important;visibility:visible!important;max-height:220mm;object-fit:contain}figcaption{font-size:8pt}.document-footer{font-size:8pt}a{text-decoration:none;color:inherit}.empty{padding:30px 0}}
        """;

    private const string Script = """
        (() => {
          'use strict';
          const manifest = new Map(JSON.parse(document.getElementById('asset-map').textContent).map(item => [item.id, item]));
          const pending = new Map(), urls = new Map(), figures = [...document.querySelectorAll('figure[data-media]')];
          const pause = () => new Promise(resolve => setTimeout(resolve, 0));
          let decodeQueue = Promise.resolve(), toastTimer;
          function toast(message) { const node=document.getElementById('toast'); node.textContent=message; node.hidden=false; clearTimeout(toastTimer); toastTimer=setTimeout(()=>node.hidden=true,5500); }
          function load(id, progress) {
            if (urls.has(id)) return Promise.resolve(urls.get(id));
            if (pending.has(id)) return pending.get(id);
            const entry=manifest.get(id);
            if (!entry) return Promise.reject(new Error('缺少内嵌素材'));
            // Only one decoder runs at once. Each atob is <=256 KiB; the browser can paint/respond between chunks.
            const task=decodeQueue.then(async()=>{
              const parts=[];
              for(let index=0;index<entry.parts;index++) {
                const node=document.getElementById(id+'-'+index);
                if(!node) throw new Error('内嵌素材不完整');
                const binary=atob(node.textContent), bytes=new Uint8Array(binary.length);
                for(let offset=0;offset<binary.length;offset++) bytes[offset]=binary.charCodeAt(offset);
                parts.push(new Blob([bytes]));
                progress?.(Math.round((index+1)*100/entry.parts));
                await pause();
              }
              const blob=new Blob(parts,{type:entry.mime});
              if(blob.size!==entry.size) throw new Error('素材长度校验失败');
              const url=URL.createObjectURL(blob); urls.set(id,url); return url;
            });
            pending.set(id,task); decodeQueue=task.catch(()=>{});
            task.catch(()=>pending.delete(id));
            return task;
          }
          function status(figure,message,error=false) {const node=figure.querySelector('.media-status');node.textContent=message;node.hidden=!message;node.classList.toggle('error',error);}
          async function showPoster(figure) {
            const img=figure.querySelector('.poster');
            if(!img.src) img.src=await load(figure.dataset.poster);
            if(img.decode) await img.decode();
            return img;
          }
          async function activate(figure) {
            if(figure.dataset.loading) return;
            figure.dataset.loading='true';
            try {
              await showPoster(figure);
              status(figure,'');
              if(figure.dataset.kind==='video'||figure.dataset.media===figure.dataset.poster) return;
              const img=document.createElement('img');img.className='original';img.alt=figure.querySelector('.poster').alt;img.decoding='async';
              img.src=await load(figure.dataset.media);
              if(img.decode) await img.decode();
              figure.querySelector('.visual').append(img);
            } catch(error) {delete figure.dataset.loading;status(figure,'预览加载失败，可尝试保存素材。',true);}
          }
          const observer='IntersectionObserver' in window ? new IntersectionObserver(entries=>{
            for(const entry of entries) if(entry.isIntersecting) {observer.unobserve(entry.target);activate(entry.target);}
          },{rootMargin:'350px 0px'}) : null;
          for(const figure of figures) {
            if(observer) observer.observe(figure);else activate(figure);
            const play=figure.querySelector('.play');
            if(play) play.addEventListener('click',async()=>{
              play.disabled=true;
              try {
                status(figure,'正在加载视频…');
                const video=figure.querySelector('video');
                video.src=await load(figure.dataset.media,percent=>status(figure,'正在加载视频 '+percent+'%'));
                video.hidden=false; play.hidden=true;figure.querySelector('.visual').classList.add('playing');status(figure,'');
                try {await video.play();} catch(error) {if(error.name==='NotAllowedError')toast('视频已准备好，请点击播放。');else throw error;}
              } catch(error) {play.hidden=false;figure.querySelector('video').hidden=true;figure.querySelector('.visual').classList.remove('playing');status(figure,'无法播放，请保存素材后使用视频播放器打开。',true);}
              finally {play.disabled=false;}
            });
            const zoom=figure.querySelector('.zoom');
            if(zoom) zoom.addEventListener('click',async()=>{
              zoom.disabled=true;
              try {
                const image=document.getElementById('lightbox-image');image.src=await load(figure.dataset.media);image.alt=figure.querySelector('.poster').alt;
                document.getElementById('lightbox').showModal();
              } catch(error) {toast('原图加载失败，请尝试保存素材。');}
              finally {zoom.disabled=false;}
            });
            const download=figure.querySelector('.download');
            download.addEventListener('click',async()=>{
              download.disabled=true;download.textContent='准备中…';
              try {
                const entry=manifest.get(figure.dataset.media), url=await load(entry.id);
                const anchor=document.createElement('a');anchor.href=url;anchor.download=entry.name;document.body.append(anchor);anchor.click();anchor.remove();
              } catch(error) {toast('素材读取失败，请重新导出文档。');}
              finally {download.disabled=false;download.textContent='保存素材 ↓';}
            });
          }
          const dialog=document.getElementById('lightbox');
          document.getElementById('lightbox-close').addEventListener('click',()=>dialog.close());
          dialog.addEventListener('click',event=>{if(event.target===dialog)dialog.close();});
          const print=document.getElementById('print');
          print.addEventListener('click',async()=>{
            print.disabled=true;print.textContent='准备打印…';
            try {for(const figure of figures) await showPoster(figure);await pause();window.print();}
            catch(error) {toast('部分预览未能加载，请重试后打印。');}
            finally {print.disabled=false;print.textContent='打印 / PDF';}
          });
          document.addEventListener('keydown',event=>{
            if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='p') {event.preventDefault();if(!print.disabled)print.click();}
          });
          // Ctrl+P also asks for first-frame previews without decoding video/GIF payloads.
          window.addEventListener('beforeprint',()=>{for(const figure of figures)showPoster(figure).catch(()=>{});});
          window.addEventListener('pagehide',event=>{if(!event.persisted){for(const url of urls.values())URL.revokeObjectURL(url);urls.clear();}});
          if(window.matchMedia?.('(max-width:650px)').matches)document.querySelector('.contents').open=false;
        })();
        """;
}
