using System.Text.Json;
using Stepwise.Core;
using Stepwise.Media;

namespace Stepwise;

internal static class SelfTest
{
    public static void Run(string[] args)
    {
        var parent=args.SkipWhile(x=>x!="--self-test").Skip(1).FirstOrDefault()??Path.Combine(AppContext.BaseDirectory,"verification");
        Directory.CreateDirectory(parent);
        var checks=new List<string>();string? root=null;
        try
        {
            var store=ProjectStore.Create(parent,"自动验收_中文路径");root=store.Root;
            using(var bitmap=new Bitmap(960,540))
            using(var g=Graphics.FromImage(bitmap))
            {
                g.Clear(Color.FromArgb(242,247,244));
                using var font=new Font("Microsoft YaHei UI",30);
                g.DrawString("步录 · 每一步，都清晰",font,Brushes.DarkSlateGray,60,120);
                g.DrawString("中文路径 / 离线文档 / 原始素材",new Font("Microsoft YaHei UI",18),Brushes.Teal,60,215);
                bitmap.Save(store.ResolvePath("media/images/test.png"),System.Drawing.Imaging.ImageFormat.Png);
            }
            var a=new StepRecord{Id="step-a",Title="打开设置 <script>alert(1)</script>",Description="第一行\n第二行 & 中文标点。",MediaPath="media/images/test.png",OriginalPath="media/images/test.png",IsDraft=false,Width=960,Height=540};
            var b=new StepRecord{Id="step-b",Title="核对结果",Description="确认所有信息清晰可见。",MediaPath="media/images/test.png",OriginalPath="media/images/test.png",IsDraft=false};
            store.AddStepAsync(a).GetAwaiter().GetResult();store.AddStepAsync(b).GetAwaiter().GetResult();
            Assert(store.Data.Steps.Select(s=>s.Number).SequenceEqual(new[]{1,2}),"连续编号",checks);
            store.MoveStepAsync("step-b",-1).GetAwaiter().GetResult();Assert(store.Data.Steps[0].Id=="step-b","调整顺序",checks);
            store.DeleteStepAsync("step-b").GetAwaiter().GetResult();Assert(store.Data.Steps.Count==1&&store.Data.DeletedSteps.Count==1&&File.Exists(store.ResolvePath(a.MediaPath)),"软删除保留素材",checks);
            var html=File.ReadAllText(Path.Combine(root,"document.html"));
            Assert(!html.Contains("<script>alert(1)</script>")&&html.Contains("&lt;script&gt;"),"HTML转义",checks);
            Assert(html.Contains("media/images/test.png")&&!html.Contains("cdn."),"离线相对资源",checks);
            var rejected=false;try{store.ResolvePath("../outside.txt");}catch(InvalidDataException){rejected=true;}Assert(rejected,"路径边界",checks);
            var reopened=ProjectStore.Open(root);Assert(reopened.Data.Steps.Single().Description==a.Description,"中文持久化重开",checks);
            File.WriteAllText(Path.Combine(root,"project.json"),"broken json");
            var recovered=ProjectStore.Open(root);Assert(recovered.Data.Steps.Count>0,"损坏清单备份恢复",checks);
            CheckScreenshotCommit(parent, checks);
            File.WriteAllText(Path.Combine(parent,"self-test-result.json"),JsonSerializer.Serialize(new{passed=true,checks,project=root},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(parent,"self-test-result.json"),JsonSerializer.Serialize(new{passed=false,checks,project=root,error=ex.ToString()}));Environment.ExitCode=1;}
    }
    private static void Assert(bool condition,string name,List<string> checks){if(!condition)throw new InvalidOperationException(name);checks.Add(name);}

    private static void CheckScreenshotCommit(string parent, List<string> checks)
    {
        var store = ProjectStore.Create(parent, "标注截图提交验收");
        using var original = new Bitmap(320, 180);
        using (var g = Graphics.FromImage(original)) g.Clear(Color.White);
        using var annotated = (Bitmap)original.Clone();
        using (var g = Graphics.FromImage(annotated))
        using (var pen = new Pen(Color.Red, 5)) g.DrawRectangle(pen, 40, 30, 120, 70);
        var id = Guid.NewGuid().ToString("N");
        var layers = "{\"version\":1,\"width\":320,\"height\":180,\"layers\":[{\"type\":\"rectangle\",\"color\":\"#ff0000\"}]}";
        var step = ScreenshotWriter.SaveAsync(store, original, annotated, layers, id).GetAwaiter().GetResult();
        using var saved = new Bitmap(store.ResolvePath(step.MediaPath));
        using var raw = new Bitmap(store.ResolvePath(step.OriginalPath!));
        Assert(saved.Size == original.Size && raw.GetPixel(40, 30).ToArgb() == Color.White.ToArgb() && saved.GetPixel(40, 30).R > 200 && saved.GetPixel(40, 30).G < 80, "标注成品与原图分别保存且尺寸不变", checks);
        var opened = ProjectStore.Open(store.Root);
        Assert(opened.Data.Steps.Single().AnnotationPath == step.AnnotationPath && File.ReadAllText(store.ResolvePath(step.AnnotationPath!)) == layers, "笔迹元数据重开保留", checks);
        var before = Directory.GetFiles(store.ResolvePath("media/images/")).Order().ToArray();
        var rejected = false;
        try { ScreenshotWriter.SaveAsync(store, original, annotated, layers, id).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && before.SequenceEqual(Directory.GetFiles(store.ResolvePath("media/images/")).Order()), "重复确认不覆盖已有截图", checks);
        var failingId = Guid.NewGuid().ToString("N");
        store.Data.Title = new string('测', 201);
        rejected = false;
        try { ScreenshotWriter.SaveAsync(store, original, annotated, layers, failingId).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected && before.SequenceEqual(Directory.GetFiles(store.ResolvePath("media/images/")).Order()) && store.Data.Steps.Count == 1, "步骤提交失败回收本次文件且不增加编号", checks);
    }
}
