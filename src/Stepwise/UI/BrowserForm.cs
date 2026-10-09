using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Stepwise.UI;

public sealed class BrowserForm : Form
{
    private readonly WebView2 web=new() { Dock=DockStyle.Fill,DefaultBackgroundColor=Color.FromArgb(247,248,245) };
    private readonly string root;
    private readonly bool editorMode;
    private bool ready;
    private object? latest;
    private bool closingFlushed;
    public bool IsSaving {get;set;}
    public Func<JsonElement,Task>? MessageHandler {get;set;}
    public BrowserForm(string projectRoot,bool editor)
    {
        root=projectRoot;editorMode=editor;Icon=Program.AppIcon;TopMost=editor; Text=editor?"步录 · 编辑步骤":"步录 · 文档预览";
        ClientSize=editor?new Size(1080,740):new Size(1160,800); MinimumSize=new Size(760,560);
        StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(247,248,245);
        AutoScaleMode=AutoScaleMode.Dpi; Controls.Add(web);
        Load+=(_,_)=>
        {
            var scale=DeviceDpi/96d;
            var work=Screen.FromControl(this).WorkingArea;
            ClientSize=new Size(Math.Min((int)((editorMode?1080:1160)*scale),work.Width-48),Math.Min((int)((editorMode?740:800)*scale),work.Height-100));
            MinimumSize=new Size(Math.Min((int)(760*scale),work.Width-24),Math.Min((int)(560*scale),work.Height-48));
            CenterToScreen();
        };
        Shown+=async(_,_)=>await InitializeAsync();
        FormClosing+=async(_,e)=>
        {
            if(IsSaving){e.Cancel=true;return;}
            if(editor&&!closingFlushed&&ready)
            {
                e.Cancel=true;
                try {await FlushDraftAsync();closingFlushed=true;if(!IsDisposed)BeginInvoke((Action)Close);}
                catch(Exception ex){await SendAsync(new{type="error",message="保存未完成，请重试："+ex.Message});}
            }
        };
    }
    private async Task InitializeAsync()
    {
        try
        {
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,".cache","webview"));
            if(IsDisposed)return;
            await web.EnsureCoreWebView2Async(env);
            if(IsDisposed)return;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.stepwise.local",Path.Combine(AppContext.BaseDirectory,"Web"),CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("project.stepwise.local",root,CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            web.CoreWebView2.Settings.AreDevToolsEnabled=false;
            web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled=false;
            web.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            web.CoreWebView2.NewWindowRequested+=(_,e)=>e.Handled=true;
            web.CoreWebView2.DownloadStarting+=(_,e)=>e.Cancel=true;
            web.CoreWebView2.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith("https://app.stepwise.local/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};
            web.CoreWebView2.WebMessageReceived+=async(_,e)=>
            {
                if(!e.Source.StartsWith("https://app.stepwise.local/",StringComparison.OrdinalIgnoreCase))return;
                try
                {
                    using var doc=JsonDocument.Parse(e.WebMessageAsJson); var msg=doc.RootElement.Clone();
                    if(msg.GetProperty("type").GetString()=="ready"){ready=true;if(latest!=null)_=SendAsync(latest);}
                    else if(MessageHandler!=null)await MessageHandler(msg);
                }
                catch(Exception ex){_ = SendAsync(new{type="error",message=ex.Message});}
            };
            web.Source=new Uri("https://app.stepwise.local/app.html");
        }
        catch(Exception ex)
        {
            if(IsDisposed)return;
            Controls.Clear(); web.Dispose();
            var note=new Label{Dock=DockStyle.Fill,Padding=new Padding(32),Text="内置浏览器无法启动。\n\n"+ex.Message+"\n\n项目和素材已经保留。请安装 Microsoft Edge WebView2 Runtime 后重试。",AutoSize=false};Controls.Add(note);
            var open=new Button{Text="打开已保存文档",Dock=DockStyle.Bottom,Height=48};open.Click+=(_,_)=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(root,"document.html")){UseShellExecute=true});Controls.Add(open);
        }
    }
    public Task SetContentAsync(object content){latest=content;return SendAsync(content);}
    public void CloseSaved(){closingFlushed=true;Close();}
    public void PrintDocument(){web.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);}
    public async Task FlushDraftAsync()
    {
        if(!ready||IsDisposed)return;
        var result=await web.ExecuteScriptAsync("window.stepwise.getDraft?.() ?? null");
        if(result!="null"&&MessageHandler!=null)
        {
            using var data=JsonDocument.Parse(result);
            await MessageHandler(data.RootElement.Clone());
        }
    }
    public async Task SendAsync(object message)
    {
        if(!ready||IsDisposed||web.CoreWebView2==null)return;
        var json=JsonSerializer.Serialize(message,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        try{await web.ExecuteScriptAsync("window.stepwise.receive("+json+")");}catch(ObjectDisposedException){}
    }
    protected override void Dispose(bool disposing){if(disposing)web.Dispose();base.Dispose(disposing);}
}
