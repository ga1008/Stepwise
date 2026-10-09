using Stepwise.Core;
using Stepwise.UI;

namespace Stepwise;

internal static class Program
{
    internal static Icon AppIcon {get;}=File.Exists(Path.Combine(AppContext.BaseDirectory,"Assets","stepwise.ico"))?new Icon(Path.Combine(AppContext.BaseDirectory,"Assets","stepwise.ico")):SystemIcons.Application;
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException+=(_,e)=>MessageBox.Show("操作没有完成，已保存的素材仍在项目目录。\n\n"+e.Exception.Message,"步录",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        FileStream? projectLock=null;
        try
        {
            if(args.Contains("--self-test")){SelfTest.Run(args);return;}
            if(args.Contains("--preferences-test")){PreferencesSelfTest.Run(args);return;}
            if(args.Contains("--word-export-test")){Environment.ExitCode=WordExportSelfTest.Run(TestDirectory(args,"--word-export-test"));return;}
            if(args.Contains("--note-export-test")){Application.Run(new NoteExportSelfTest(TestDirectory(args,"--note-export-test")));return;}
            if(args.Contains("--hotkey-test")){Environment.ExitCode=HotkeySelfTest.Run(TestDirectory(args,"--hotkey-test"));return;}
            if(args.Contains("--export-settings-test")){Environment.ExitCode=ExportSettingsSelfTest.Run(TestDirectory(args,"--export-settings-test"));return;}
            if(args.Contains("--media-test")){Application.Run(new MediaTestForm(args));return;}
            string? existingRoot=null,parentDirectory=null;
            var title="步录验收项目";
            if(args.Length>=2&&args[0]=="--open")existingRoot=args[1];
            else if(args.Length>=2&&args[0]=="--project-parent")parentDirectory=args[1];
            else
            {
                using var start=new StartupForm();
                if(start.ShowDialog()!=DialogResult.OK)return;
                existingRoot=start.ExistingProjectRoot;parentDirectory=start.ParentDirectory;title=start.ProjectTitle;
            }
            ProjectStore store;
            if(existingRoot is not null)
            {
                // Open regenerates HTML, so acquire the writer lock before touching project contents.
                projectLock=AcquireProjectLock(existingRoot);
                store=ProjectStore.Open(existingRoot);
            }
            else
            {
                // Create allocates a fresh unique directory; retain its lock before starting any UI.
                store=ProjectStore.Create(parentDirectory!,title);
                projectLock=AcquireProjectLock(store.Root);
            }
            if(args.Contains("--demo")){var canvas=new DemoForm();canvas.Show();}
            using var context=new RecorderContext(store,projectLock,args.Contains("--preview"),args.Contains("--inspect-toolbar"));
            projectLock=null; // Ownership transfers only after the context constructor succeeds.
            Application.Run(context);
        }
        catch(Exception ex){MessageBox.Show(ex.Message,"步录 · 无法启动",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        finally{projectLock?.Dispose();}
    }

    private static string TestDirectory(string[] args,string flag) => args.SkipWhile(x=>x!=flag).Skip(1).FirstOrDefault()
        ?? throw new ArgumentException("验收必须指定隔离目录。");

    private static FileStream AcquireProjectLock(string root)
    {
        try{return new FileStream(Path.Combine(Path.GetFullPath(root),".project.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException ex) when((ex.HResult&0xffff) is 32 or 33)
        {throw new IOException("该项目正在另一个步录窗口中使用。请先关闭那个窗口，再继续编辑。",ex);}
    }
}
