namespace Stepwise.UI;

internal sealed class DemoForm : Form
{
    public DemoForm()
    {
        Text="步录 · 验收画板";BackColor=Color.FromArgb(236,242,238);
        Bounds=Screen.PrimaryScreen!.WorkingArea;WindowState=FormWindowState.Maximized;
        Font=new Font("Microsoft YaHei UI",12);
        var card=new Panel{BackColor=Color.White,Size=new Size(820,480)};
        card.Controls.Add(new Label{Text="操作记录 · 验收画板",Font=new Font(Font.FontFamily,26,FontStyle.Bold),Location=new Point(44,35),AutoSize=true,ForeColor=Color.FromArgb(21,73,61)});
        card.Controls.Add(new Label{Text="在这里练习截图与录制，每一次操作都能成为一个步骤。",Location=new Point(48,108),AutoSize=true,ForeColor=Color.DimGray});
        card.Controls.Add(new Label{Text="01   选择区域",Location=new Point(48,176),AutoSize=true});
        card.Controls.Add(new Label{Text="02   填写说明",Location=new Point(48,224),AutoSize=true});
        card.Controls.Add(new Label{Text="03   保存文档",Location=new Point(48,272),AutoSize=true});
        var button=new Button{Text="示例操作：切换状态",Location=new Point(470,175),Size=new Size(280,64),BackColor=Color.FromArgb(26,110,90),ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
        var state=new Label{Text="等待操作",Location=new Point(480,275),AutoSize=true};button.Click+=(_,_)=>state.Text="已完成 · "+DateTime.Now.ToString("HH:mm:ss");
        card.Controls.Add(button);card.Controls.Add(state);
        card.Controls.Add(new Label{Text="本页仅用于验证捕获、文字编辑与文档输出。",Location=new Point(48,396),AutoSize=true,ForeColor=Color.Gray});
        Controls.Add(card);Resize+=(_,_)=>card.Location=new Point(Math.Max(0,(ClientSize.Width-card.Width)/2),Math.Max(0,(ClientSize.Height-card.Height)/2));
    }
}
