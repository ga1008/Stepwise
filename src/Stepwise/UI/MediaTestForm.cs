using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Stepwise.Media;

namespace Stepwise.UI;

internal sealed class MediaTestForm : Form
{
    public MediaTestForm(string[] args)
    {
        Text="步录 · 媒体验收";Size=new Size(820,540);StartPosition=FormStartPosition.CenterScreen;BackColor=Color.White;
        var label=new Label{Text="真实录屏 / 系统声音 / 麦克风\n正在验证 MP4、静音与 GIF 输出",Dock=DockStyle.Fill,Font=new Font("Microsoft YaHei UI",22),TextAlign=ContentAlignment.MiddleCenter};Controls.Add(label);
        var parent=args.SkipWhile(x=>x!="--media-test").Skip(1).First();Directory.CreateDirectory(parent);
        Shown+=async(_,_)=>
        {
            var checks=new List<string>();
            try
            {
                await Task.Delay(300);
                using var tone=new WaveOutEvent();tone.Init(new SignalGenerator{Gain=0.05,Frequency=440,Type=SignalGeneratorType.Sin});
                using var session=await RecordingSession.StartAsync(new Rectangle(Location,new Size(800,500)),Path.Combine(parent,"recording"),new RecordingOptions());
                tone.Play();
                for(int i=0;i<6;i++){label.Text=$"真实录屏与音频验收\n{i+1} / 6";label.ForeColor=i%2==0?Color.Teal:Color.DarkSlateBlue;await Task.Delay(1000);}
                tone.Stop();await session.StopAsync();checks.Add("真实屏幕录制停止");
                await session.ExportAsync(Path.Combine(parent,"audio.mp4"),false,false);checks.Add("有声MP4");
                await session.ExportAsync(Path.Combine(parent,"silent.mp4"),false,true);checks.Add("静音MP4");
                await session.ExportAsync(Path.Combine(parent,"animation.gif"),true,true);checks.Add("GIF");
                using var reopened=RecordingSession.Open(session.WorkingDirectory);
                await reopened.ExportAsync(Path.Combine(parent,"recovered.mp4"),false,false);checks.Add("过程文件重新导出");
                File.WriteAllText(Path.Combine(parent,"media-test-result.json"),JsonSerializer.Serialize(new{passed=true,checks,duration=session.Duration.TotalSeconds,warnings=session.Warnings},new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(parent,"media-test-result.json"),JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()}));}
            Close();
        };
    }
}
