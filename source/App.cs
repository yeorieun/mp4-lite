using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("MP4 Lite")]
[assembly: AssemblyDescription("MP4 영상 압축 · 오디오 원본 복사")]
[assembly: AssemblyCompany("MP4 Lite")]
[assembly: AssemblyProduct("MP4 Lite")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace MP4Lite
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) {
                MessageBox.Show(e.Exception.Message, "MP4 Lite", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm(args));
        }
    }

    sealed class Job
    {
        public string Input, Output, Error;
        public ListViewItem Row;
    }

    sealed class QualityCard : RadioButton
    {
        public readonly string Heading, Detail;
        public QualityCard(string heading, string detail)
        {
            Heading = heading; Detail = detail;
            Appearance = Appearance.Button; FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand; AccessibleName = heading + ", " + detail;
            Text = heading; Dock = DockStyle.Fill; Margin = new Padding(0, 0, 10, 0);
            CheckedChanged += delegate { Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Color.FromArgb(91, 74, 230);
            g.Clear(Checked ? Color.FromArgb(240, 237, 255) : Color.White);
            using (var pen = new Pen(Checked ? accent : Color.FromArgb(218, 222, 233), Checked ? 2 : 1))
                g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            float scale = g.DpiX / 96f;
            int pad = (int)(16 * scale), dot = (int)(14 * scale);
            using (var pen = new Pen(Checked ? accent : Color.FromArgb(160, 168, 187), 2))
                g.DrawEllipse(pen, Width - pad - dot, pad + 2, dot, dot);
            if (Checked) using (var brush = new SolidBrush(accent)) g.FillEllipse(brush, Width - pad - dot + 4 * scale, pad + 2 + 4 * scale, dot - 8 * scale, dot - 8 * scale);
            using (var bold = new Font(Font.FontFamily, 11, FontStyle.Bold))
                TextRenderer.DrawText(g, Heading, bold, new Rectangle(pad, pad - 2, Width - pad * 3 - dot, 28 * (int)Math.Ceiling(scale)), Enabled ? Color.FromArgb(35, 42, 64) : SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, Font, new Rectangle(pad, (int)(46 * scale), Width - pad * 2, Height - (int)(46 * scale)), Enabled ? Color.FromArgb(105, 113, 132) : SystemColors.GrayText, TextFormatFlags.Left | TextFormatFlags.WordBreak);
            if (Focused) ControlPaint.DrawFocusRectangle(g, new Rectangle(5, 5, Width - 10, Height - 10));
        }
    }

    sealed class MainForm : Form
    {
        static readonly Color Ink = Color.FromArgb(30, 39, 59), Muted = Color.FromArgb(108, 117, 136), Accent = Color.FromArgb(91, 74, 230);
        readonly List<Job> jobs = new List<Job>();
        readonly Engine engine = new Engine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine"));
        readonly ListView list = new ListView();
        readonly Label empty = new Label(), count = new Label(), status = new Label(), substatus = new Label(), percent = new Label();
        readonly ProgressBar bar = new ProgressBar();
        readonly Button add = new Button(), remove = new Button(), start = new Button(), cancel = new Button(), folder = new Button(), browse = new Button();
        readonly CheckBox sourceFolder = new CheckBox();
        readonly TextBox output = new TextBox();
        readonly QualityCard[] cards = new QualityCard[3];
        CancellationTokenSource cancellation;
        bool busy, closeAfterCancel;
        string customFolder = "", lastFolder = "";

        public MainForm(string[] args)
        {
            Text = "MP4 Lite · 영상 압축";
            Font = new Font("맑은 고딕", 9F);
            BackColor = Color.FromArgb(246, 247, 251); ForeColor = Ink;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(920, 744); MinimumSize = new Size(820, 700);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26, 18, 26, 18), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            Controls.Add(root);
            BuildHeader(root); BuildQueue(root); BuildQuality(root); BuildDestination(root); BuildFooter(root);
            AllowDrop = true;
            DragEnter += DropEnter; DragDrop += DropFiles;
            Shown += delegate { AddFiles(args.Where(File.Exists)); };
            FormClosing += OnClosing;
        }

        static Label LabelOf(string text, float size, Color color, bool bold)
        { return new Label { Text = text, AutoSize = true, ForeColor = color, Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty }; }
        static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text; button.AccessibleName = text; button.AutoSize = false;
            button.Size = new Size(110, 36); button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(216, 221, 232);
            button.BackColor = primary ? Accent : Color.White; button.ForeColor = primary ? Color.White : Ink;
            button.Cursor = Cursors.Hand; button.Margin = new Padding(8, 0, 0, 0);
        }
        void BuildHeader(TableLayoutPanel root)
        {
            var head = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var title = LabelOf("MP4 Lite", 27, Ink, true); title.Location = new Point(0, 0); head.Controls.Add(title);
            var desc = LabelOf("영상은 가볍게, 소리는 그대로.", 10, Muted, false); desc.Location = new Point(2, 53); head.Controls.Add(desc);
            var chip = new Label { Text = "●  오디오 원본 유지", TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(225, 244, 236), ForeColor = Color.FromArgb(23, 119, 83), Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) };
            var chipBox = new Panel { Dock = DockStyle.Right, Width = 165, Padding = new Padding(0, 15, 0, 42) };
            chipBox.Controls.Add(chip); head.Controls.Add(chipBox); root.Controls.Add(head, 0, 0);
        }
        void BuildQueue(TableLayoutPanel root)
        {
            var box = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 16), ColumnCount = 1, RowCount = 2 };
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tools = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            count.Text = "01  압축할 파일"; count.AutoSize = true; count.Font = new Font(Font, FontStyle.Bold); count.Location = new Point(0, 9); tools.Controls.Add(count);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 242, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
            StyleButton(add, "+ 파일 추가", true); StyleButton(remove, "목록에서 빼기", false);
            buttons.Controls.Add(add); buttons.Controls.Add(remove); tools.Controls.Add(buttons); box.Controls.Add(tools, 0, 0);
            var listBox = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(1), Margin = Padding.Empty };
            listBox.Paint += delegate(object sender, PaintEventArgs e) { using (var pen = new Pen(Color.FromArgb(220, 224, 233))) e.Graphics.DrawRectangle(pen, 0, 0, listBox.Width - 1, listBox.Height - 1); };
            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.BorderStyle = BorderStyle.None;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable; list.GridLines = false; list.AllowDrop = true; list.ShowItemToolTips = true;
            list.Columns.Add("파일 이름", 325); list.Columns.Add("원본 크기", 100); list.Columns.Add("진행 상태", 230); list.Columns.Add("결과 크기", 120);
            list.AccessibleName = "압축할 MP4 파일 목록";
            list.Resize += delegate { if (list.Columns.Count == 4) { double s = DeviceScale; list.Columns[1].Width = (int)(94 * s); list.Columns[2].Width = (int)(218 * s); list.Columns[3].Width = (int)(115 * s); list.Columns[0].Width = Math.Max(160, list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width - list.Columns[3].Width - 8); } };
            empty.Text = "MP4 파일을 여기에 놓아 주세요\r\n\r\n여러 파일을 한 번에 추가할 수 있습니다";
            empty.TextAlign = ContentAlignment.MiddleCenter; empty.ForeColor = Muted; empty.Dock = DockStyle.Fill; empty.BackColor = Color.White; empty.AllowDrop = true;
            empty.Cursor = Cursors.Hand;
            listBox.Controls.Add(list); listBox.Controls.Add(empty); empty.BringToFront(); box.Controls.Add(listBox, 0, 1); root.Controls.Add(box, 0, 1);
            add.Click += delegate { ChooseFiles(); }; empty.Click += delegate { if (!busy) ChooseFiles(); };
            remove.Click += delegate { if (!busy) { foreach (ListViewItem item in list.SelectedItems) { jobs.Remove((Job)item.Tag); list.Items.Remove(item); } UpdateCount(); } };
            list.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete && !busy) { remove.PerformClick(); e.Handled = true; } };
            list.DoubleClick += delegate { if (list.SelectedItems.Count == 1) ShowJob((Job)list.SelectedItems[0].Tag); };
            list.DragEnter += DropEnter; list.DragDrop += DropFiles; empty.DragEnter += DropEnter; empty.DragDrop += DropFiles;
        }
        float DeviceScale { get { using (Graphics g = CreateGraphics()) return g.DpiX / 96F; } }
        void BuildQuality(TableLayoutPanel root)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, RowCount = 3, ColumnCount = 1 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 84)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.Controls.Add(LabelOf("02  낮출 화질 선택", 9, Ink, true), 0, 0);
            var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            cards[0] = new QualityCard("720p · 가볍게", "일상적인 공유 · 적당한 화질");
            cards[1] = new QualityCard("480p · 작게", "추천 · 화질보다 용량 우선");
            cards[2] = new QualityCard("360p · 아주 작게", "작은 화면용 · 가장 낮은 화질");
            for (int i = 0; i < 3; i++) choices.Controls.Add(cards[i], i, 0);
            cards[2].Margin = Padding.Empty; cards[1].Checked = true;
            panel.Controls.Add(choices, 0, 1);
            var hint = LabelOf("비율·프레임률 유지  /  작은 영상은 확대하지 않음  /  모든 오디오 트랙 그대로 복사", 8.5F, Muted, false); hint.Margin = new Padding(0, 9, 0, 0); panel.Controls.Add(hint, 0, 2);
            root.Controls.Add(panel, 0, 2);
        }
        void BuildDestination(TableLayoutPanel root)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 2 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            sourceFolder.Text = "03  원본 위치의 ‘압축완료’ 폴더에 저장"; sourceFolder.AutoSize = true; sourceFolder.Checked = true; sourceFolder.Margin = new Padding(0, 1, 0, 0);
            panel.Controls.Add(sourceFolder, 0, 0); panel.SetColumnSpan(sourceFolder, 2);
            output.Text = "각 원본 폴더 → 압축완료"; output.ReadOnly = true; output.BackColor = Color.White; output.Dock = DockStyle.Fill; output.Margin = new Padding(0, 5, 0, 0); output.AccessibleName = "저장 폴더";
            StyleButton(browse, "폴더 선택", false); browse.Dock = DockStyle.Top;
            panel.Controls.Add(output, 0, 1); panel.Controls.Add(browse, 1, 1);
            browse.Click += delegate { ChooseFolder(); };
            sourceFolder.CheckedChanged += delegate {
                if (!sourceFolder.Checked && customFolder.Length == 0) { ChooseFolder(); if (customFolder.Length == 0) sourceFolder.Checked = true; }
                output.Text = sourceFolder.Checked ? "각 원본 폴더 → 압축완료" : customFolder;
            };
            root.Controls.Add(panel, 0, 3);
        }
        void BuildFooter(TableLayoutPanel root)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 4 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 12)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            status.Text = "파일을 추가하면 준비가 끝납니다"; status.AutoEllipsis = true; status.Dock = DockStyle.Fill; status.Font = new Font(Font, FontStyle.Bold);
            percent.Text = "0%"; percent.TextAlign = ContentAlignment.TopRight; percent.Dock = DockStyle.Fill; percent.ForeColor = Accent;
            panel.Controls.Add(status, 0, 0); panel.Controls.Add(percent, 1, 0);
            bar.Dock = DockStyle.Fill; bar.Margin = Padding.Empty; bar.Maximum = 1000; panel.Controls.Add(bar, 0, 1); panel.SetColumnSpan(bar, 2);
            substatus.Text = "원본은 보관하고 새 MP4 파일을 만듭니다."; substatus.ForeColor = Muted; substatus.Dock = DockStyle.Fill; substatus.AutoEllipsis = true; substatus.Padding = new Padding(0, 5, 0, 0); panel.Controls.Add(substatus, 0, 2); panel.SetColumnSpan(substatus, 2);
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 282));
            StyleButton(folder, "결과 폴더 열기", false); folder.Width = 138; folder.Height = 40; folder.Margin = Padding.Empty; folder.Enabled = false;
            row.Controls.Add(folder, 0, 0);
            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            StyleButton(start, "압축 시작", true); start.Width = 160; start.Height = 40; start.Enabled = false; start.Font = new Font(Font, FontStyle.Bold);
            StyleButton(cancel, "취소", false); cancel.Width = 104; cancel.Height = 40; cancel.Enabled = false;
            right.Controls.Add(start); right.Controls.Add(cancel); row.Controls.Add(right, 1, 0);
            panel.Controls.Add(row, 0, 3); panel.SetColumnSpan(row, 2); root.Controls.Add(panel, 0, 4);
            start.Click += async delegate { await StartJobs(); }; cancel.Click += delegate { CancelJobs(); };
            folder.Click += delegate { if (Directory.Exists(lastFolder)) Process.Start(new ProcessStartInfo(lastFolder) { UseShellExecute = true }); };
        }

        void ChooseFiles()
        {
            using (var d = new OpenFileDialog { Filter = "MP4 영상 (*.mp4)|*.mp4", Multiselect = true, Title = "압축할 MP4 파일 선택", CheckFileExists = true })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }
        void ChooseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "압축한 MP4를 저장할 폴더를 선택하세요", ShowNewFolderButton = true, SelectedPath = customFolder })
                if (d.ShowDialog(this) == DialogResult.OK) { customFolder = d.SelectedPath; sourceFolder.Checked = false; output.Text = customFolder; }
        }
        void DropEnter(object sender, DragEventArgs e)
        { e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        void DropFiles(object sender, DragEventArgs e)
        { if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) AddFiles((string[])e.Data.GetData(DataFormats.FileDrop)); }
        void AddFiles(IEnumerable<string> paths)
        {
            int ignored = 0;
            foreach (string raw in paths)
            {
                if (!String.Equals(Path.GetExtension(raw), ".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(raw)) { ignored++; continue; }
                string path = Path.GetFullPath(raw);
                if (jobs.Any(j => String.Equals(j.Input, path, StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try { size = new FileInfo(path).Length; } catch (IOException) { ignored++; continue; } catch (UnauthorizedAccessException) { ignored++; continue; }
                var job = new Job { Input = path };
                job.Row = new ListViewItem(new[] { Path.GetFileName(path), Bytes(size), "대기", "—" }) { Tag = job, ToolTipText = path };
                jobs.Add(job); list.Items.Add(job.Row);
            }
            UpdateCount();
            if (ignored > 0) substatus.Text = "MP4가 아니거나 읽을 수 없는 파일 " + ignored + "개를 제외했습니다.";
        }
        void UpdateCount()
        {
            count.Text = "01  압축할 파일" + (jobs.Count == 0 ? "" : "  ·  " + jobs.Count + "개");
            empty.Visible = jobs.Count == 0; start.Enabled = !busy && jobs.Count > 0; remove.Enabled = !busy && jobs.Count > 0;
            if (!busy) status.Text = jobs.Count == 0 ? "파일을 추가하면 준비가 끝납니다" : jobs.Count + "개 파일이 준비되었습니다";
        }
        static string Bytes(long value)
        {
            double size = value; string[] units = { "B", "KB", "MB", "GB", "TB" }; int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return size.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
        }
        void SafeUI(Action action)
        { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
        void SetBusy(bool value)
        {
            busy = value; add.Enabled = !value; remove.Enabled = !value && jobs.Count > 0; start.Enabled = !value && jobs.Count > 0;
            cancel.Enabled = value; browse.Enabled = !value; sourceFolder.Enabled = !value;
            foreach (var c in cards) c.Enabled = !value;
            start.Text = value ? "압축 중…" : "압축 시작";
        }
        async Task StartJobs()
        {
            if (busy || jobs.Count == 0) return;
            try { engine.CheckInstalled(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "엔진 확인", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            Quality quality = Quality.Presets[Array.FindIndex(cards, c => c.Checked)];
            bool useSource = sourceFolder.Checked; string destination = customFolder;
            Job[] batch = jobs.ToArray();
            foreach (var job in batch) { job.Output = null; job.Error = null; job.Row.SubItems[2].Text = "대기"; job.Row.SubItems[3].Text = "—"; job.Row.ForeColor = Ink; }
            cancellation = new CancellationTokenSource(); CancellationToken token = cancellation.Token;
            SetBusy(true); bar.Value = 0; percent.Text = "0%";
            int succeeded = 0, failed = 0, finished = 0, larger = 0; long totalBefore = 0, totalAfter = 0;
            bool wasCancelled = false;
            try
            {
                await Task.Run(delegate {
                    for (int i = 0; i < batch.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        Job job = batch[i]; int index = i;
                        string dir = useSource ? Path.Combine(Path.GetDirectoryName(job.Input), "압축완료") : destination;
                        try
                        {
                            int lastProgress = -1;
                            ConversionResult result = engine.ConvertFile(job.Input, dir, quality, token, delegate(int p, string phase) {
                                if (p == lastProgress) return; lastProgress = p;
                                SafeUI(delegate {
                                    job.Row.SubItems[2].Text = p < 100 ? "압축 중 " + p + "%" : "완료";
                                    int all = (int)((index * 1000L + p * 10L) / batch.Length);
                                    bar.Value = Math.Max(0, Math.Min(1000, all)); percent.Text = (all / 10.0).ToString("0") + "%";
                                    status.Text = (index + 1) + " / " + batch.Length + "  ·  " + Path.GetFileName(job.Input);
                                    substatus.Text = phase;
                                });
                            });
                            succeeded++; totalBefore += result.Before; totalAfter += result.After;
                            if (result.After >= result.Before) larger++;
                            job.Output = result.Path;
                            SafeUI(delegate {
                                job.Row.SubItems[2].Text = result.After < result.Before ? "완료 · " + ((1.0 - (double)result.After / result.Before) * 100).ToString("0") + "% 감소" : "완료 · 용량 감소 없음";
                                job.Row.SubItems[3].Text = Bytes(result.After); job.Row.ForeColor = Color.FromArgb(26, 124, 89);
                                job.Row.ToolTipText = result.Path + "\r\n두 번 클릭하면 결과 파일 위치를 엽니다.";
                                lastFolder = Path.GetDirectoryName(result.Path); folder.Enabled = true;
                            });
                        }
                        catch (OperationCanceledException) { SafeUI(delegate { job.Row.SubItems[2].Text = "취소됨"; }); throw; }
                        catch (Exception ex)
                        {
                            failed++; job.Error = ex.Message;
                            SafeUI(delegate { job.Row.SubItems[2].Text = "실패 · 두 번 클릭해 확인"; job.Row.ForeColor = Color.FromArgb(180, 52, 64); job.Row.ToolTipText = job.Error; });
                        }
                        finished++;
                        int done = finished;
                        SafeUI(delegate { bar.Value = (int)(done * 1000L / batch.Length); percent.Text = ((double)done / batch.Length * 100).ToString("0") + "%"; });
                    }
                });
            }
            catch (OperationCanceledException) { wasCancelled = true; }
            catch (Exception ex) { failed++; substatus.Text = ex.Message; }
            finally
            {
                cancellation.Dispose(); cancellation = null; SetBusy(false);
                if (wasCancelled) foreach (var j in batch) if (j.Output == null && j.Error == null && j.Row.SubItems[2].Text == "대기") j.Row.SubItems[2].Text = "진행 안 함";
                status.Text = (wasCancelled ? "취소됨" : "작업 완료") + "  ·  성공 " + succeeded + "개" + (failed > 0 ? "  /  실패 " + failed + "개" : "");
                string summary = succeeded > 0 ? Bytes(totalBefore) + " → " + Bytes(totalAfter) + "  ·  오디오 원본 유지" : "원본 파일은 그대로 보관됩니다.";
                if (larger > 0) summary += "  ·  " + larger + "개는 용량 감소 없음";
                if (failed > 0) summary += "  ·  실패 항목을 두 번 클릭하세요";
                substatus.Text = summary;
                if (!wasCancelled) { bar.Value = 1000; percent.Text = "100%"; }
                if (closeAfterCancel) Close();
            }
        }
        void CancelJobs()
        {
            if (cancellation == null) return;
            cancellation.Cancel(); cancel.Enabled = false; substatus.Text = "취소 중… 임시 파일을 정리합니다.";
        }
        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!busy) return;
            e.Cancel = true;
            if (MessageBox.Show(this, "진행 중인 압축을 취소하고 닫을까요?\r\n이미 완료한 파일은 보관됩니다.", "압축 중", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            { closeAfterCancel = true; CancelJobs(); }
        }
        void ShowJob(Job job)
        {
            if (!String.IsNullOrEmpty(job.Output) && File.Exists(job.Output))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Engine.Quote(job.Output)) { UseShellExecute = true });
            else if (!String.IsNullOrEmpty(job.Error))
            {
                using (var d = new Form { Text = "변환 오류 · " + Path.GetFileName(job.Input), Size = new Size(750, 420), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false })
                {
                    var text = new TextBox { Text = job.Error, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White };
                    d.Controls.Add(text); d.ShowDialog(this);
                }
            }
        }
    }
}
