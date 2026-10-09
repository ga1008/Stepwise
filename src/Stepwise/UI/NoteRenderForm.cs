using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Stepwise.Core;

namespace Stepwise.UI;

/// <summary>Off-screen, nonactivating renderer. Each PNG is bounded; no full-document bitmap is allocated.</summary>
internal sealed class NoteRenderForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x08000080;return p; } }
    private NoteRenderForm()
    {
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;
        AutoScaleMode=AutoScaleMode.None;SetBounds(-30000,-30000,1440,900);Controls.Add(web);
    }

    internal static async Task RenderAsync(ExportSnapshot snapshot,string work,string output,string label,bool pdf,
        IProgress<ExportProgress>? progress,CancellationToken cancellationToken)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("文档渲染需要界面线程。");
        await File.WriteAllTextAsync(Path.Combine(work,"render.html"),StaticNoteDocument.Render(snapshot),cancellationToken);
        using var form=new NoteRenderForm();
        form.Show();
        // Chromium locks its profile; reuse the project profile outside the disposable staging directory.
        var projectRoot=Directory.GetParent(Directory.GetParent(work)!.FullName)!.Parent!.FullName;
        var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(projectRoot,".cache","webview"))
            .WaitAsync(TimeSpan.FromSeconds(45),cancellationToken);
        await form.web.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(45),cancellationToken);
        var core=form.web.CoreWebView2;
        core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
        core.Settings.AreBrowserAcceleratorKeysEnabled=false;core.Settings.IsStatusBarEnabled=false;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.NewWindowRequested+=(_,e)=>e.Handled=true;
        core.DownloadStarting+=(_,e)=>e.Cancel=true;
        const string url="https://export.stepwise.local/render.html";
        core.SetVirtualHostNameToFolderMapping("export.stepwise.local",work,CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting+=(_,e)=>{if(e.Uri!=url)e.Cancel=true;};
        var navigation=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted+=(_,e)=>{if(e.IsSuccess)navigation.TrySetResult();else navigation.TrySetException(new IOException("导出页面载入失败："+e.WebErrorStatus));};
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride","{\"width\":1440,\"height\":900,\"deviceScaleFactor\":1,\"mobile\":false}");
        core.Navigate(url);
        await navigation.Task.WaitAsync(TimeSpan.FromSeconds(45),cancellationToken);
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.exportReady?'ready':(window.exportError||'loading')"));
            if(result=="ready")break;
            if(result!="loading")throw new IOException(result);
            if(timer.Elapsed>TimeSpan.FromSeconds(60))throw new TimeoutException("导出图片载入超时，请检查素材后重试。");
            await Task.Delay(80,cancellationToken);
        }
        if(pdf)
        {
            progress?.Report(new ExportProgress(30,"排版 PDF…"));
            var settings=environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds=true;settings.ShouldPrintHeaderAndFooter=false;
            settings.PageWidth=8.5;settings.PageHeight=11;
            settings.MarginTop=settings.MarginBottom=.75;settings.MarginLeft=settings.MarginRight=.71;
            if(!await core.PrintToPdfAsync(Path.Combine(output,label+".pdf"),settings).WaitAsync(cancellationToken))
                throw new IOException("PDF 未完成，请重试。");
        }
        else
        {
            const int width=1440,maxHeight=12000;
            var height=JsonSerializer.Deserialize<int>(await core.ExecuteScriptAsync("Math.ceil(document.documentElement.scrollHeight)"));
            var top=0;var index=0;
            while(top<height)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end=JsonSerializer.Deserialize<int>(await core.ExecuteScriptAsync($"window.exportCut({top},{maxHeight})"));
                end=Math.Clamp(end,top+1,Math.Min(height,top+maxHeight));
                progress?.Report(new ExportProgress((int)(top*100d/height),$"绘制长图 {index+1}…"));
                var parameters=JsonSerializer.Serialize(new{format="png",captureBeyondViewport=true,fromSurface=true,clip=new{x=0,y=top,width,height=end-top,scale=1}});
                var response=await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",parameters).WaitAsync(cancellationToken);
                using var json=JsonDocument.Parse(response);
                var bytes=Convert.FromBase64String(json.RootElement.GetProperty("data").GetString()!);
                var name=height<=maxHeight?label+".png":$"{label}-{++index:D3}.png";
                await File.WriteAllBytesAsync(Path.Combine(output,name),bytes,cancellationToken);
                top=end;
            }
        }
        progress?.Report(new ExportProgress(100,"排版完成"));
        form.Close();
    }
    protected override void Dispose(bool disposing){if(disposing)web.Dispose();base.Dispose(disposing);}
}
