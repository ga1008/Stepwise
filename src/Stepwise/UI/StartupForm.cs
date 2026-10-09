using Stepwise.Core;

namespace Stepwise.UI;

public sealed class StartupForm : Form
{
    private readonly TextBox titleBox = new() { Text = "操作说明", BorderStyle = BorderStyle.None };
    private readonly TextBox folderBox = new() { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White };
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9 };
    private readonly Label brand = new() { Text = "步录", ForeColor = Color.FromArgb(20, 65, 58), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label subtitle = new() { Text = "把每一步，变成清晰的说明。", ForeColor = Color.FromArgb(90, 105, 100), Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };
    private readonly Label titleLabel = new() { Text = "项目名称", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };
    private readonly Label folderLabel = new() { Text = "保存位置", Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };
    private readonly Label helper = new() { Text = "素材、过程文件和文档，自动保存在同一个项目中。", ForeColor = Color.DimGray, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft };
    private readonly Panel titleField = new() { BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
    private readonly Panel folderField = new() { BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
    private readonly Button browse = new() { Text = "选择…", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, AccessibleName = "选择项目目录" };
    private readonly Button start = new() { Text = "创建项目 →", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(25, 110, 94), ForeColor = Color.White, AccessibleName = "创建项目" };
    private readonly LinkLabel resume = new() { Text = "继续已有项目…", Dock = DockStyle.Fill, LinkColor = Color.FromArgb(50, 100, 85), TextAlign = ContentAlignment.TopLeft };
    private readonly List<Font> layoutFonts = [];
    private bool layoutReady;

    public string ProjectTitle => titleBox.Text.Trim();
    public string ParentDirectory => folderBox.Text;
    public string? ExistingProjectRoot { get; private set; }

    public StartupForm()
    {
        Text = "步录 · 新建项目";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 248, 245);
        AutoScaleMode = AutoScaleMode.None;
        titleBox.MaxLength = 120;
        titleBox.AccessibleName = "项目名称";
        folderBox.AccessibleName = "保存位置";
        var preferredDirectory = PreferencesStore.Default.Read().ProjectParentDirectory;
        folderBox.Text = Directory.Exists(preferredDirectory) ? preferredDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        titleBox.Dock = folderBox.Dock = DockStyle.Top;
        titleField.Controls.Add(titleBox);
        folderField.Controls.Add(folderBox);

        layout.Controls.Add(brand, 0, 0); layout.SetColumnSpan(brand, 2);
        layout.Controls.Add(subtitle, 0, 1); layout.SetColumnSpan(subtitle, 2);
        layout.Controls.Add(titleLabel, 0, 2); layout.SetColumnSpan(titleLabel, 2);
        layout.Controls.Add(titleField, 0, 3); layout.SetColumnSpan(titleField, 2);
        layout.Controls.Add(folderLabel, 0, 4); layout.SetColumnSpan(folderLabel, 2);
        layout.Controls.Add(folderField, 0, 5);
        layout.Controls.Add(browse, 1, 5);
        layout.Controls.Add(helper, 0, 6); layout.SetColumnSpan(helper, 2);
        layout.Controls.Add(start, 0, 7); layout.SetColumnSpan(start, 2);
        layout.Controls.Add(resume, 0, 8); layout.SetColumnSpan(resume, 2);
        Controls.Add(layout);
        start.FlatAppearance.BorderSize = 0;
        browse.FlatAppearance.BorderColor = Color.FromArgb(182, 199, 193);
        browse.BackColor = Color.FromArgb(237, 243, 239);
        AcceptButton = start;

        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择保存位置（将在其中创建独立项目文件夹）", UseDescriptionForTitle = true, SelectedPath = folderBox.Text, ShowNewFolderButton = true };
            if (dialog.ShowDialog(this) == DialogResult.OK && Directory.Exists(dialog.SelectedPath))
            {
                folderBox.Text = dialog.SelectedPath;
                SaveParentDirectory();
            }
        };
        start.Click += (_, _) =>
        {
            if (ProjectTitle.Length == 0 || !Directory.Exists(ParentDirectory))
            {
                MessageBox.Show(this, "请填写项目名称并选择有效目录。", "步录");
                return;
            }
            SaveParentDirectory();
            DialogResult = DialogResult.OK;
            Close();
        };
        resume.LinkClicked += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择项目中的 project.json", Filter = "步录项目 (project.json)|project.json",
                CheckFileExists = true, InitialDirectory = folderBox.Text };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            ExistingProjectRoot = Path.GetDirectoryName(dialog.FileName);
            DialogResult = DialogResult.OK;
            Close();
        };

        layoutReady = true;
        ApplyDpiLayout();
    }

    private void SaveParentDirectory()
    {
        try { PreferencesStore.Default.Remember(value => value.ProjectParentDirectory = ParentDirectory); }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Project directory preference save failed: {error}");
            helper.Text = "保存位置未能记住，本次仍可创建项目。";
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpiLayout();
        CenterToScreen();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (layoutReady) ApplyDpiLayout();
    }

    private int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96d);

    private Font DpiFont(float points, FontStyle style = FontStyle.Regular)
    {
        // Pixel fonts prevent a second implicit font-based scale pass in native controls.
        var font = new Font("Microsoft YaHei UI", points * DeviceDpi / 72f, style, GraphicsUnit.Pixel);
        layoutFonts.Add(font);
        return font;
    }

    private void ApplyDpiLayout()
    {
        SuspendLayout();
        layout.SuspendLayout();
        try
        {
            MinimumSize = Size.Empty;
            ClientSize = new Size(Px(620), Px(460));
            MinimumSize = Size;
            Font = DpiFont(10);
            brand.Font = DpiFont(28, FontStyle.Bold);
            helper.Font = DpiFont(9);
            start.Font = DpiFont(10, FontStyle.Bold);

            layout.Padding = new Padding(Px(32));
            layout.ColumnStyles.Clear();
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(100)));
            layout.RowStyles.Clear();
            // 396 logical pixels of rows + 64 pixels of outer padding = 460.
            foreach (var height in new[] { 56, 48, 24, 56, 24, 40, 56, 48, 44 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(height)));

            foreach (Control control in layout.Controls) control.Margin = Padding.Empty;
            subtitle.Padding = new Padding(0, Px(8), 0, 0);
            titleField.Margin = new Padding(0, 0, 0, Px(16));
            folderField.Margin = new Padding(0, 0, Px(12), 0);
            titleField.Padding = folderField.Padding = new Padding(Px(12), Px(9), Px(12), 0);
            titleBox.Margin = folderBox.Margin = Padding.Empty;
            helper.Padding = new Padding(0, Px(10), 0, 0);
            resume.Padding = new Padding(0, Px(14), 0, 0);
        }
        finally
        {
            layout.ResumeLayout(true);
            ResumeLayout(true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in layoutFonts) font.Dispose();
            layoutFonts.Clear();
        }
    }
}
