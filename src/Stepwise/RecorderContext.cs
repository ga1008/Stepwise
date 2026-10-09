using System.Diagnostics;
using System.Text.Json;
using Stepwise.Core;
using Stepwise.Media;
using Stepwise.UI;

namespace Stepwise;

internal sealed class RecorderContext : ApplicationContext
{
    private readonly ProjectStore store;
    private readonly ToolbarForm toolbar;
    private BrowserForm? editor, preview;
    private SettingsForm? settings;
    private ExportForm? exportForm;
    private RecordingSession? recording;
    private bool busy, exiting, exitPending, shutdownInProgress;
    private readonly FileStream projectLock;
    private CancellationTokenSource? countdown;
    private readonly SemaphoreSlim messageGate=new(1,1);
    private readonly System.Windows.Forms.Timer timer=new(){Interval=500};

    public RecorderContext(ProjectStore project,FileStream heldProjectLock,bool showPreview,bool inspectToolbar=false)
    {
        store=project;
        projectLock=heldProjectLock??throw new ArgumentNullException(nameof(heldProjectLock));
        toolbar=new ToolbarForm(inspectToolbar); MainForm=toolbar;
        toolbar.SetTrayIcon(Program.AppIcon);toolbar.Icon=Program.AppIcon;
        toolbar.ScreenshotRequested+=async(_,delay)=>await CaptureAsync(false,delay);
        toolbar.RecordRequested+=async(_,delay)=>await CaptureAsync(true,delay);
        toolbar.StopRequested+=async(_,_)=>await StopAsync();
        toolbar.PreviewRequested+=(_,_)=>ShowPreview();
        toolbar.SettingsRequested+=(_,_)=>ShowSettings();
        toolbar.SuspendedHotkeyPressed+=async(_,chord)=>
        {
            if(settings is not {IsDisposed:false} target)return;
            try {await target.CaptureRegisteredHotkeyAsync(chord);}
            catch(Exception ex){Log("快捷键录入："+ex.Message);}
        };
        toolbar.ExitRequested+=async(_,_)=>await ExitAsync();
        toolbar.FormClosing+=(_,e)=>{if(!exiting){e.Cancel=true;_=ExitAsync();}};
        store.Changed+=(_,_)=>{if(!toolbar.IsDisposed&&toolbar.IsHandleCreated)toolbar.BeginInvoke((Action)(()=>{if(preview is {IsDisposed:false,IsSaving:false})_=preview.SetContentAsync(Content("preview",null));}));};
        timer.Tick+=async(_,_)=>
        {
            if(recording==null||busy)return;
            toolbar.SetStatus(recording.Duration.ToString(@"mm\:ss"));
            if(!recording.IsRecording)await StopAsync();
        };
        toolbar.Show();timer.Start();
        if(toolbar.HotkeyErrors.Count>0)Log("快捷键已被其他程序占用: "+string.Join(", ",toolbar.HotkeyErrors));
        toolbar.SetStatus(toolbar.HotkeyErrors.Count>0 ? "快捷键冲突 · 打开设置" : "就绪");
        if(showPreview)ShowPreview();
    }

    private object Content(string mode,StepRecord? step,IEnumerable<string>? warnings=null,bool useRecordingDefaults=false)
    {
        // A saved GIF is inherently silent, but that is not the user's MP4 mute choice.
        var preferences = useRecordingDefaults || step?.Kind == "gif" ? PreferencesStore.Default.Read() : null;
        return new { type="init", mode, project=store.Data, step, rootUrl="https://project.stepwise.local/", warnings=warnings??[], isRecording=recording!=null,
            recordingDefaults=preferences == null ? null : new { format=useRecordingDefaults ? preferences.ExportFormat : "gif", muted=preferences.ExportMuted } };
    }
    private void SetBusy(bool value){busy=value;toolbar.SetBusy(value);}

    private static ExportOptions ExportSettings(UserPreferences preferences,bool gif) => gif
        ? new ExportOptions(preferences.GifFramesPerSecond,preferences.GifMaxWidth)
        : new ExportOptions(preferences.Mp4FramesPerSecond,preferences.Mp4MaxWidth);

    private void ShowSettings()
    {
        if(settings is {IsDisposed:false}){settings.Activate();return;}
        if(busy||recording!=null||exiting)return;
        toolbar.CloseMenus();toolbar.SuspendAutoHide(true);toolbar.SuspendHotkeyActions=true;
        try
        {
            using var form=new SettingsForm(store.Root,toolbar.ValidateHotkeys,preferences=>
                toolbar.ApplySettings(preferences,()=>PreferencesStore.Default.ApplyConfiguration(preferences)));
            settings=form;
            form.ShowDialog(toolbar);
        }
        catch(Exception ex){Error(ex);}
        finally
        {
            settings=null;toolbar.SuspendHotkeyActions=false;
            if(!toolbar.IsDisposed){toolbar.SuspendAutoHide(false);toolbar.RestoreBar();}
        }
    }

    private async Task CaptureAsync(bool video,int delay)
    {
        if(busy||recording!=null)return;
        if(settings is {IsDisposed:false}){settings.Activate();return;}
        if(editor is {IsDisposed:false}){editor.Activate();return;}
        SetBusy(true);countdown=new CancellationTokenSource();
        try
        {
            toolbar.CloseMenus();toolbar.SuspendAutoHide(true);preview?.Hide();
            for(var remaining=delay;remaining>0;remaining--)
            {
                toolbar.SetStatus(remaining.ToString());
                await Task.Delay(1000,countdown.Token);
            }
            toolbar.SuspendAutoHide(true);toolbar.Hide();
            await Task.Delay(180,countdown.Token);
            using var selection=await CaptureOverlay.SelectAsync(countdown.Token);
            if(selection==null)return;
            var id=Guid.NewGuid().ToString("N");
            if(video)
            {
                var defaults=PreferencesStore.Default.Read();
                recording=await RecordingSession.StartAsync(selection.Bounds,Path.Combine(store.Root,"recordings",id),new RecordingOptions(toolbar.SystemAudio,toolbar.Microphone,Math.Max(defaults.Mp4FramesPerSecond,defaults.GifFramesPerSecond)));
                toolbar.SetRecording(true);
                toolbar.SetStatus("00:00");
                if(recording.Warnings.Count>0)Log("录音提示: "+string.Join("; ",recording.Warnings));
            }
            else
            {
                StepRecord? confirmed = null;
                var accepted = await AnnotationForm.EditAsync(selection.Image, store.Root, async result =>
                {
                    confirmed = await ScreenshotWriter.SaveAsync(store, selection.Image, result.Image, result.LayersJson, id);
                }, countdown.Token);
                if (accepted && confirmed != null && !exitPending) ShowEditor(confirmed);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Error(ex);}
        finally
        {
            countdown.Dispose();countdown=null;SetBusy(false);
            toolbar.SuspendAutoHide(false);
            if(!exiting){if(recording==null||toolbar.CaptureExclusionEnabled||toolbar.InspectionMode){toolbar.Show();toolbar.RestoreBar();}if(recording==null)toolbar.SetStatus("就绪");}
            if(exitPending){exitPending=false;await ExitAsync();}
        }
    }

    private async Task StopAsync(bool openEditor=true)
    {
        if(recording==null||busy)return;
        SetBusy(true); toolbar.SetStatus("保存中…");
        var session=recording; recording=null;toolbar.SetRecording(false);
        try
        {
            await session.StopAsync();
            var id=Path.GetFileName(session.WorkingDirectory);
            var preferences = PreferencesStore.Default.Read();
            var gif = preferences.ExportFormat == "gif";
            var path=$"media/{(gif ? "gifs" : "videos")}/{id}.{(gif ? "gif" : "mp4")}";
            await session.ExportAsync(store.ResolvePath(path),gif,preferences.ExportMuted,options:ExportSettings(preferences,gif));
            var outputSize=await RecordingSession.GetExportSizeAsync(store.ResolvePath(path));
            var step=new StepRecord{Id=id,Title=$"步骤 {store.Data.Steps.Count+1}",Kind=gif?"gif":"video",MediaPath=path,OriginalPath=$"recordings/{id}",DurationSeconds=session.Duration.TotalSeconds,Width=outputSize.Width,Height=outputSize.Height,Muted=gif||preferences.ExportMuted||(!toolbar.SystemAudio&&!toolbar.Microphone),IsDraft=true};
            await store.AddStepAsync(step);
            if(openEditor)ShowEditor(store.Data.Steps.First(s=>s.Id==id),session.Warnings,true);
        }
        catch(Exception ex)
        {
            Error(new IOException("录制过程文件已保留在 recordings 文件夹。可在文档预览中点击“恢复录制”重试。\n"+ex.Message,ex));
        }
        finally{session.Dispose();SetBusy(false);toolbar.SetStatus("就绪");toolbar.Show();toolbar.RestoreBar();if(exitPending&&!shutdownInProgress){exitPending=false;await ExitAsync();}}
    }

    private void ShowEditor(StepRecord step,IEnumerable<string>? warnings=null,bool useRecordingDefaults=false)
    {
        if(editor is {IsDisposed:false}){editor.Activate();return;}
        editor=new BrowserForm(store.Root,true);
        var target=editor;
        editor.MessageHandler=msg=>HandleMessageAsync(target,msg,step.Id);
        editor.FormClosed+=(_,_)=>{editor=null;toolbar.RestoreBar();};
        _=editor.SetContentAsync(Content("editor",step,warnings,useRecordingDefaults));editor.Show();editor.Activate();
    }
    private void ShowPreview()
    {
        if(busy)return;
        if(settings is {IsDisposed:false}){settings.Activate();return;}
        if(preview is {IsDisposed:false}){preview.Show();preview.Activate();_=preview.SetContentAsync(Content("preview",null));return;}
        preview=new BrowserForm(store.Root,false);var target=preview;
        preview.MessageHandler=msg=>HandleMessageAsync(target,msg,null);
        preview.FormClosed+=(_,_)=>preview=null;
        _=preview.SetContentAsync(Content("preview",null));preview.Show();
    }

    private async Task HandleMessageAsync(BrowserForm browser,JsonElement message,string? editingId)
    {
        // Export must flush an open editor before taking the message gate; its draft uses this same gate.
        if(message.GetProperty("type").GetString()=="export")
        {
            if(editingId==null)await ShowExportAsync(browser);
            return;
        }
        await messageGate.WaitAsync();
        try
        {
            var type=message.GetProperty("type").GetString();
            switch(type)
            {
                case "close": browser.Close();break;
                case "print": browser.PrintDocument();break;
                case "preview": ShowPreview();break;
                case "reveal": Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={store.Root},UseShellExecute=true});break;
                case "edit":
                    var selected=store.Data.Steps.First(s=>s.Id==message.GetProperty("id").GetString());ShowEditor(selected);break;
                case "rename":
                    store.Data.Title=message.GetProperty("title").GetString()??"操作说明";await store.SaveAsync();break;
                case "delete":
                    if(editor is {IsDisposed:false})throw new InvalidOperationException("请先完成当前步骤的编辑。");
                    await store.DeleteStepAsync(message.GetProperty("id").GetString()!);break;
                case "move": await store.MoveStepAsync(message.GetProperty("id").GetString()!,message.GetProperty("direction").GetInt32());break;
                case "recover": await RecoverAsync(browser);break;
                case "recordingPreferences":
                    if(editingId == null || store.Data.Steps.FirstOrDefault(s=>s.Id==editingId)?.Kind == "image") break;
                    try
                    {
                        var exportFormat = message.GetProperty("format").GetString();
                        var exportMuted = message.GetProperty("muted").GetBoolean();
                        if(exportFormat is not ("mp4" or "gif")) throw new InvalidDataException("无效的导出格式。");
                        PreferencesStore.Default.Remember(preferences => { preferences.ExportFormat=exportFormat; preferences.ExportMuted=exportMuted; });
                    }
                    catch(Exception ex)
                    {
                        Log("偏好保存失败：" + ex.Message);
                        await browser.SendAsync(new { type="preferencesError", message="偏好未能保存，下次启动可能恢复上次设置。" });
                    }
                    break;
                case "draft": case "save":
                    if(editingId==null)throw new InvalidOperationException("未选择步骤。");
                    var current=store.Data.Steps.First(s=>s.Id==editingId);
                    var step=JsonSerializer.Deserialize<StepRecord>(JsonSerializer.Serialize(current,ProjectStore.JsonOptions),ProjectStore.JsonOptions)!;
                    var values=message.GetProperty("step");
                    step.Title=values.GetProperty("title").GetString()??"";step.Description=values.GetProperty("description").GetString()??"";
                    if(type=="save")
                    {
                        browser.IsSaving=true;await browser.SendAsync(new{type="busy",message="正在保存…"});
                        if(step.Kind!="image"&&step.OriginalPath is not null)
                        {
                            var gif=message.TryGetProperty("format",out var fmt)&&fmt.GetString()=="gif";
                            var muted=message.TryGetProperty("muted",out var mute)&&mute.GetBoolean();
                            if(gif!=(step.Kind=="gif")||(!gif&&muted!=step.Muted))
                            {
                                using var source=RecordingSession.Open(store.ResolvePath(step.OriginalPath));
                                var relative=$"media/{(gif?"gifs":"videos")}/{step.Id}-{DateTime.Now:HHmmssfff}.{(gif?"gif":"mp4")}";
                                await source.ExportAsync(store.ResolvePath(relative),gif,muted,options:ExportSettings(PreferencesStore.Default.Read(),gif));
                                step.MediaPath=relative;step.Kind=gif?"gif":"video";
                                var outputSize=await RecordingSession.GetExportSizeAsync(store.ResolvePath(relative));
                                step.Width=outputSize.Width;step.Height=outputSize.Height;
                            }
                            step.Muted=gif||muted;
                        }
                        step.IsDraft=false;
                    }
                    await store.UpdateStepAsync(step);
                    if(type=="draft")await browser.SendAsync(new{type="draftSaved",revision=message.TryGetProperty("revision",out var revision)?revision.GetInt32():0});
                    if(type=="save"){browser.IsSaving=false;browser.CloseSaved();toolbar.SetStatus("已保存");}
                    break;
            }
        }
        catch(Exception ex){Log(ex.ToString());await browser.SendAsync(new{type="error",message=ex.Message});if(message.GetProperty("type").GetString()=="draft")throw;}
        finally{browser.IsSaving=false;messageGate.Release();}
    }

    private async Task ShowExportAsync(BrowserForm browser)
    {
        if(exportForm is {IsDisposed:false}){exportForm.Activate();return;}
        if(busy||recording!=null||exiting)
        {
            await browser.SendAsync(new{type="notice",message="请先完成当前截图或录制。"});
            return;
        }
        SetBusy(true);toolbar.SuspendAutoHide(true);browser.IsSaving=true;
        // Leave WebMessageReceived before entering a native modal message loop.
        // WebView2 serializes callbacks; a nested dialog in that callback blocks a new renderer's initialization.
        await Task.Yield();
        try
        {
            if(editor is {IsDisposed:false} currentEditor)
            {
                if(currentEditor.IsSaving)throw new InvalidOperationException("步骤正在保存，请稍后再导出。");
                await currentEditor.FlushDraftAsync();
            }
            ProjectData snapshot;
            await messageGate.WaitAsync();
            try {snapshot=await store.SnapshotAsync();}
            finally {messageGate.Release();}
            if(exitPending)return;
            var mediaBytes=NoteExportService.MediaBytes(store,snapshot);
            var preferred=PreferencesStore.Default.Read().NoteExportFormat;
            var format=Enum.TryParse<ExportFormat>(preferred,true,out var chosen)?chosen:ExportFormat.Word;
            using var form=new ExportForm(snapshot.Steps.Count,mediaBytes,format,
                (kind,progress,token)=>NoteExportService.ExportAsync(store,snapshot,kind,progress,token),
                kind=>PreferencesStore.Default.Remember(p=>p.NoteExportFormat=kind.ToString().ToLowerInvariant()));
            form.TopMost=true;exportForm=form;form.ShowDialog(browser);
        }
        catch(Exception ex){Log(ex.ToString());await browser.SendAsync(new{type="error",message=ex.Message});}
        finally
        {
            exportForm=null;browser.IsSaving=false;SetBusy(false);toolbar.SuspendAutoHide(false);
            if(!toolbar.IsDisposed)toolbar.RestoreBar();
            if(exitPending){exitPending=false;await ExitAsync();}
        }
    }

    private async Task RecoverAsync(BrowserForm browser)
    {
        browser.IsSaving=true;await browser.SendAsync(new{type="busy",message="恢复录制中…"});
        var count=0;var failed=0;
        foreach(var directory in Directory.EnumerateDirectories(Path.Combine(store.Root,"recordings")))
        {
            var id=Path.GetFileName(directory);
            if(store.Data.Steps.Concat(store.Data.DeletedSteps).Any(s=>s.Id==id))continue;
            try
            {
                using var source=RecordingSession.Open(directory);
                var preferences=PreferencesStore.Default.Read();var gif=preferences.ExportFormat=="gif";
                var path=$"media/{(gif?"gifs":"videos")}/{id}.{(gif?"gif":"mp4")}";await source.ExportAsync(store.ResolvePath(path),gif,preferences.ExportMuted,options:ExportSettings(preferences,gif));
                var outputSize=await RecordingSession.GetExportSizeAsync(store.ResolvePath(path));
                await store.AddStepAsync(new StepRecord{Id=id,Title="恢复的录制",Kind=gif?"gif":"video",MediaPath=path,OriginalPath=$"recordings/{id}",DurationSeconds=source.Duration.TotalSeconds,Width=outputSize.Width,Height=outputSize.Height,Muted=gif||preferences.ExportMuted,IsDraft=true});count++;
            }
            catch(Exception ex){failed++;Log("恢复 "+id+": "+ex.Message);}
        }
        await browser.SetContentAsync(Content("preview",null));
        await browser.SendAsync(new{type="notice",message=failed>0?$"已恢复 {count} 个，另有 {failed} 个暂无法恢复。原件已保留，请查看 logs。":count>0?$"已恢复 {count} 个录制":"没有待恢复的录制。"});
    }

    private async Task ExitAsync()
    {
        if(exiting||shutdownInProgress)return;
        if(settings is {IsDisposed:false,Visible:true} openSettings)
        {
            openSettings.Close();
            if(openSettings.Visible)return; // Saving or the user chose to keep their settings draft.
        }
        if(busy){exitPending=true;countdown?.Cancel();exportForm?.Close();return;}
        shutdownInProgress=true;
        try
        {
            if(recording!=null)await StopAsync(false);
            if(editor is {IsDisposed:false})await editor.FlushDraftAsync();
            await messageGate.WaitAsync();messageGate.Release();
            exiting=true;timer.Stop();timer.Dispose();preview?.Close();editor?.CloseSaved();toolbar.Close();projectLock.Dispose();ExitThread();
        }
        catch(Exception ex){Error(ex);}
        finally{shutdownInProgress=false;}
    }
    private void Error(Exception ex){Log(ex.ToString());MessageBox.Show(ex.Message,"步录",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
    private void Log(string text){try{File.AppendAllText(Path.Combine(store.Root,"logs","app.log"),$"{DateTimeOffset.Now:O} {text}\n");}catch{}}
    protected override void Dispose(bool disposing)
    {
        if(disposing)projectLock.Dispose();
        base.Dispose(disposing);
    }
}
