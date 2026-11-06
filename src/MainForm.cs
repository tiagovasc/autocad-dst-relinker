using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SheetSetRelinker
{
    public sealed class MainForm : Form
    {
        private readonly Color blue = Color.FromArgb(30, 103, 191);
        private readonly Label fileSummary = new Label();
        private readonly Label status = new Label();
        private readonly TextBox destination = new TextBox();
        private readonly TextBox outputName = new TextBox();
        private readonly TextBox find = new TextBox();
        private readonly TextBox replace = new TextBox();
        private readonly RadioButton alreadyCopied = new RadioButton();
        private readonly RadioButton copy = new RadioButton();
        private readonly DataGridView drawings = new DataGridView();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Button validate = new Button();
        private readonly Button create = new Button();
        private readonly Button cancel = new Button();
        private readonly Button viewLog = new Button();
        private readonly Button support = new Button();
        private readonly Panel input = new Panel();
        private readonly Dictionary<string, string> sourceOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DstDocument document;
        private string inputPath;
        private RunLog log;
        private CancellationTokenSource cancellation;
        private bool busy;
        private bool initializing;
        public string LastOutput { get; private set; }
        public string StatusText { get { return status.Text; } }
        public bool IsBusy { get { return busy; } }

        public MainForm(string initialPath = null)
        {
            Text = "Sheet Set Relinker";
            Rectangle screen = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1000, screen.Width - 60), Math.Min(800, screen.Height - 80));
            MinimumSize = new Size(850, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(247, 249, 252);
            AllowDrop = true;
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 18), ColumnCount = 1, RowCount = 5 };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            Controls.Add(shell);
            var heading = new Panel { Dock = DockStyle.Fill };
            heading.Controls.Add(new Label { Text = "Sheet Set Relinker", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0) });
            heading.Controls[0].Font = new Font("Segoe UI", 22F, FontStyle.Bold);
            heading.Controls.Add(new Label { Text = "Update DWG paths and names without rebuilding sheets.", AutoSize = true, ForeColor = Color.DimGray, Location = new Point(1, 43) });
            var about = new LinkLabel { Text = "About && licenses", AutoSize = true, LinkColor = blue, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            about.Click += delegate { ShowLicenses(); }; heading.Controls.Add(about);
            heading.Resize += delegate { about.Location = new Point(heading.Width - about.Width, 14); };
            shell.Controls.Add(heading, 0, 0);
            input.Dock = DockStyle.Fill; input.AutoScroll = true;
            shell.Controls.Add(input, 0, 1);
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, Height = 560, ColumnCount = 1, RowCount = 9 };
            input.Resize += delegate { fields.Height = Math.Max(560, input.ClientSize.Height); };
            foreach (int h in new[] { 90, 26, 27, 38, 32, 45 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            input.Controls.Add(fields);
            var drop = new DropPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), BackColor = Color.White };
            var dropLabel = new Label { Text = "Drop a DST file here", AutoSize = true, Location = new Point(22, 25), Font = new Font(Font, FontStyle.Bold) };
            drop.Controls.Add(dropLabel);
            var browseDst = MakeButton("Browse…", delegate { using (var d = new OpenFileDialog { Filter = "AutoCAD sheet sets (*.dst)|*.dst", Title = "Choose a sheet set" }) if (d.ShowDialog(this) == DialogResult.OK) LoadFileAsync(d.FileName); });
            browseDst.Dock = DockStyle.None; browseDst.Anchor = AnchorStyles.Top | AnchorStyles.Right; browseDst.SetBounds(790, 19, 120, 40);
            drop.Controls.Add(browseDst);
            drop.Resize += delegate { browseDst.Left = drop.ClientSize.Width - browseDst.Width - 20; };
            fields.Controls.Add(drop, 0, 0);
            fileSummary.Text = "Choose one existing sheet set (.dst) to begin.";
            fileSummary.ForeColor = Color.DimGray; fileSummary.Dock = DockStyle.Fill; fileSummary.AutoEllipsis = true;
            fields.Controls.Add(fileSummary, 0, 1);
            fields.Controls.Add(new Label { Text = "Destination folder", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold) }, 0, 2);
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            destination.Dock = DockStyle.Fill; destination.Margin = new Padding(0, 1, 10, 0);
            folderRow.Controls.Add(destination);
            folderRow.Controls.Add(MakeButton("Browse…", delegate { using (var d = new FolderBrowserDialog { Description = "Choose the folder for the new DST and DWG files", SelectedPath = destination.Text }) if (d.ShowDialog(this) == DialogResult.OK) destination.Text = d.SelectedPath; }));
            fields.Controls.Add(folderRow, 0, 3);
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            alreadyCopied.Text = "DWGs already copied"; alreadyCopied.Checked = true; alreadyCopied.AutoSize = true;
            copy.Text = "Copy DWGs for me"; copy.AutoSize = true; copy.Margin = new Padding(24, 3, 3, 3);
            modes.Controls.Add(alreadyCopied); modes.Controls.Add(copy); fields.Controls.Add(modes, 0, 4);
            var changes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, Margin = Padding.Empty };
            changes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int w in new[] { 40, 150, 65, 150, 75 }) changes.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            changes.Controls.Add(new Label { Text = "Drawing names", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 10, 0, 0) });
            changes.Controls.Add(new Label { Text = "Find", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
            find.Dock = DockStyle.Fill; find.Margin = new Padding(0, 6, 12, 0); changes.Controls.Add(find);
            changes.Controls.Add(new Label { Text = "Replace", AutoSize = true, Margin = new Padding(0, 10, 0, 0) });
            replace.Dock = DockStyle.Fill; replace.Margin = new Padding(0, 6, 12, 0); changes.Controls.Add(replace);
            changes.Controls.Add(MakeButton("Apply", delegate { ApplyReplacement(find.Text, replace.Text); })); fields.Controls.Add(changes, 0, 5);
            drawings.Dock = DockStyle.Fill; drawings.BackgroundColor = Color.White; drawings.BorderStyle = BorderStyle.FixedSingle;
            drawings.AllowUserToAddRows = false; drawings.AllowUserToDeleteRows = false; drawings.AllowUserToResizeRows = false;
            drawings.RowHeadersVisible = false; drawings.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            drawings.SelectionMode = DataGridViewSelectionMode.CellSelect; drawings.MultiSelect = false;
            drawings.ColumnHeadersHeight = 34; drawings.RowTemplate.Height = 32;
            drawings.EnableHeadersVisualStyles = false; drawings.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 243, 247);
            drawings.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(55, 65, 81);
            drawings.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 234, 250); drawings.DefaultCellStyle.SelectionForeColor = Color.Black;
            drawings.GridColor = Color.FromArgb(228, 232, 238); drawings.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            drawings.Columns.Add(new DataGridViewTextBoxColumn { Name = "current", HeaderText = "Current DWG", ReadOnly = true, FillWeight = 46 });
            drawings.Columns.Add(new DataGridViewTextBoxColumn { Name = "new", HeaderText = "New filename", FillWeight = 46 });
            drawings.Columns.Add(new DataGridViewTextBoxColumn { Name = "sheets", HeaderText = "Sheets", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 60 });
            drawings.Columns.Add(new DataGridViewTextBoxColumn { Name = "check", HeaderText = "Check", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 105 });
            drawings.Columns.Add(new DataGridViewButtonColumn { Name = "locate", HeaderText = "Source", Text = "Locate…", UseColumnTextForButtonValue = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 80, Visible = false });
            fields.Controls.Add(drawings, 0, 6);
            var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(0, 12, 0, 0), Margin = Padding.Empty };
            outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputRow.Controls.Add(new Label { Text = "Output DST", AutoSize = true, Margin = new Padding(0, 5, 0, 0) });
            outputName.Dock = DockStyle.Fill; outputName.Margin = Padding.Empty; outputRow.Controls.Add(outputName); fields.Controls.Add(outputRow, 0, 7);
            fields.Controls.Add(new Label { Text = "Original files stay unchanged. Use copies of the original DWGs with unchanged layouts.", AutoSize = true, ForeColor = Color.DimGray, Margin = Padding.Empty }, 0, 8);
            status.Text = "Ready. Drop a DST file to begin."; status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Padding = new Padding(0, 8, 0, 0);
            shell.Controls.Add(status, 0, 2);
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 2, 0, 8); shell.Controls.Add(progress, 0, 3);
            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, Padding = new Padding(0, 8, 0, 0), Margin = Padding.Empty };
            foreach (int w in new[] { 100, 170 }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int w in new[] { 90, 110, 190 }) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            ConfigureButton(viewLog, "View log", delegate { ShowLog(); });
            ConfigureButton(support, "Save support report", delegate { SaveSupport(); });
            ConfigureButton(cancel, "Cancel", delegate { if (cancellation != null) cancellation.Cancel(); });
            ConfigureButton(validate, "Validate", async delegate { await ValidateJobAsync(); });
            ConfigureButton(create, "Create updated DST", async delegate { await CreateJobAsync(); });
            create.BackColor = blue; create.ForeColor = Color.White; create.FlatAppearance.BorderSize = 0;
            create.EnabledChanged += delegate { create.BackColor = create.Enabled ? blue : Color.FromArgb(222, 228, 236); create.ForeColor = create.Enabled ? Color.White : Color.FromArgb(110, 120, 132); };
            actions.Controls.Add(viewLog, 0, 0); actions.Controls.Add(support, 1, 0); actions.Controls.Add(cancel, 3, 0); actions.Controls.Add(validate, 4, 0); actions.Controls.Add(create, 5, 0); shell.Controls.Add(actions, 0, 4);
            validate.Enabled = false; create.Enabled = false; cancel.Visible = false; viewLog.Enabled = support.Enabled = false;
            destination.TextChanged += delegate { InvalidateValidation(); }; outputName.TextChanged += delegate { InvalidateValidation(); };
            drawings.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { if (e.ColumnIndex == 1) InvalidateValidation(); };
            copy.CheckedChanged += delegate { drawings.Columns["locate"].Visible = copy.Checked; InvalidateValidation(); };
            drawings.CellContentClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0 && e.ColumnIndex == 4) LocateSource(e.RowIndex); };
            DragEnter += FileDragEnter; DragDrop += FileDrop;
            foreach (Control c in new Control[] { drop, dropLabel, fileSummary, drawings }) { c.AllowDrop = true; c.DragEnter += FileDragEnter; c.DragDrop += FileDrop; }
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; cancellation.Cancel(); status.Text = "Cancelling safely. Wait for the operation to stop, then close the window."; } };
            FormClosed += delegate { if (log != null) log.Dispose(); };
            if (!String.IsNullOrWhiteSpace(initialPath)) Shown += delegate { LoadFileAsync(initialPath); };
        }
        private Button MakeButton(string text, EventHandler action) { var b = new Button(); ConfigureButton(b, text, action); return b; }
        private void ConfigureButton(Button b, string text, EventHandler action) { b.Text = text; b.Dock = DockStyle.Fill; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(210, 216, 224); b.BackColor = Color.White; b.Margin = new Padding(0, 0, 8, 0); b.Cursor = Cursors.Hand; b.Click += action; }
        private void FileDragEnter(object sender, DragEventArgs e) { e.Effect = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        private void FileDrop(object sender, DragEventArgs e) { AcceptFiles((string[])e.Data.GetData(DataFormats.FileDrop)); }
        public void AcceptFiles(string[] paths) { if (busy) return; if (paths == null || paths.Length != 1 || !String.Equals(Path.GetExtension(paths[0]), ".dst", StringComparison.OrdinalIgnoreCase)) { SetStatus("Drop one .dst sheet-set file.", true); return; } LoadFileAsync(paths[0]); }
        private void SetStatus(string message, bool error) { status.Text = message; status.ForeColor = error ? Color.FromArgb(170, 40, 40) : Color.FromArgb(45, 60, 76); }
        private void StartLog() { RunLog replacementLog = RunLog.Create(); RunLog previous = log; log = replacementLog; if (previous != null) previous.Dispose(); viewLog.Enabled = support.Enabled = true; }
        private void SetBusy(bool value) { if (value) busy = true; input.Enabled = !value; validate.Enabled = !value && document != null; create.Enabled = false; support.Enabled = !value && log != null; cancel.Visible = value; if (value) { if (cancellation != null) cancellation.Dispose(); cancellation = new CancellationTokenSource(); progress.Value = 0; } else { busy = false; } }
        private void InvalidateValidation() { if (initializing || busy) return; create.Enabled = false; progress.Value = 0; foreach (DataGridViewRow row in drawings.Rows) { row.Cells["check"].Value = "Unchecked"; row.Cells["check"].Style.ForeColor = Color.DimGray; } if (document != null) SetStatus("Click Validate to check these settings.", false); }
        public async void LoadFileAsync(string path)
        {
            if (busy) return;
            try {
                StartLog(); SetBusy(true); document = null; drawings.Rows.Clear(); sourceOverrides.Clear(); LastOutput = null;
                SetStatus("Reading sheet set…", false);
                var loaded = await Task.Run(delegate { return DstDocument.Load(path); });
                cancellation.Token.ThrowIfCancellationRequested();
                document = loaded; inputPath = Path.GetFullPath(path); initializing = true;
                foreach (var d in document.Drawings) { int i = drawings.Rows.Add(d.FileName, d.FileName, d.SheetCount, "Unchecked"); drawings.Rows[i].Tag = d.SourcePath; drawings.Rows[i].Cells[0].ToolTipText = d.SourcePath; }
                destination.Text = Path.GetDirectoryName(inputPath); outputName.Text = Path.GetFileNameWithoutExtension(inputPath) + "-relinked.dst";
                fileSummary.Text = Path.GetFileName(path) + "  •  " + document.Drawings.Count + " drawings  •  " + document.SheetCount + " sheets";
                log.Info("loaded", inputPath + "; sheets=" + document.SheetCount + "; subsets=" + document.SubsetCount + "; sha256=" + document.SourceSha256);
                SetStatus("Choose the destination and edit any new filenames, then Validate.", false);
            } catch (Exception e) { if (log != null) log.Error(e); SetStatus(e is OperationCanceledException ? "Cancelled." : e.Message, true); fileSummary.Text = "No sheet set loaded."; document = null; }
            finally { initializing = false; SetBusy(false); }
        }
        public void ApplyReplacement(string oldText, string newText) { drawings.EndEdit(); if (String.IsNullOrEmpty(oldText)) { SetStatus("Enter the text to find first.", true); return; } foreach (DataGridViewRow row in drawings.Rows) row.Cells["new"].Value = Convert.ToString(row.Cells["new"].Value).Replace(oldText, newText ?? ""); InvalidateValidation(); }
        private void LocateSource(int index) { var row = drawings.Rows[index]; using (var d = new OpenFileDialog { Filter = "AutoCAD drawings (*.dwg)|*.dwg", Title = "Locate original " + row.Cells[0].Value, FileName = Convert.ToString(row.Cells[0].Value) }) if (d.ShowDialog(this) == DialogResult.OK) { sourceOverrides[(string)row.Tag] = d.FileName; row.Cells[0].ToolTipText = d.FileName; InvalidateValidation(); } }
        private Job ReadJob() { drawings.EndEdit(); var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (DataGridViewRow r in drawings.Rows) map.Add((string)r.Tag, Convert.ToString(r.Cells["new"].Value)); return new Job { Document = document, InputPath = inputPath, DestinationFolder = destination.Text.Trim(), OutputName = outputName.Text.Trim(), Mapping = map, CopyDrawings = copy.Checked, SourceOverrides = new Dictionary<string, string>(sourceOverrides, StringComparer.OrdinalIgnoreCase) }; }
        public async Task<bool> ValidateJobAsync()
        {
            if (busy || document == null) return false;
            bool valid = false;
            try { var job = ReadJob(); SetBusy(true); SetStatus("Checking drawings and output…", false); var result = await Task.Run(delegate { return Engine.Validate(job, log, cancellation.Token); }); foreach (DataGridViewRow r in drawings.Rows) { string check; r.Cells["check"].Value = result.Rows.TryGetValue((string)r.Tag, out check) ? check : "Unchecked"; r.Cells["check"].Style.ForeColor = result.IsValid ? Color.FromArgb(30, 120, 65) : Color.FromArgb(160, 75, 20); } valid = result.IsValid; SetStatus(valid ? "Checks passed. Ready to create the updated DST." : String.Join("  •  ", result.Issues.Take(2)) + (result.Issues.Count > 2 ? "  See log for all issues." : ""), !valid); }
            catch (Exception e) { if (log != null) log.Error(e); SetStatus(e is OperationCanceledException ? "Cancelled." : e.Message, true); }
            finally { SetBusy(false); create.Enabled = valid; }
            return valid;
        }
        public async Task<bool> CreateJobAsync()
        {
            if (busy || document == null) return false;
            bool success = false;
            try { var job = ReadJob(); SetBusy(true); SetStatus("Validating again before creating files…", false); var token = cancellation.Token; LastOutput = await Task.Run(delegate { return Engine.Run(job, log, delegate(int value, string message) { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)delegate { progress.Value = Math.Max(0, Math.Min(100, value)); SetStatus(message, false); }); }, token); }); progress.Value = 100; SetStatus("Created " + Path.GetFileName(LastOutput) + ". Open this new DST in AutoCAD to check the sheets.", false); success = true; }
            catch (Exception e) { if (log != null) log.Error(e); SetStatus(e is OperationCanceledException ? "Cancelled. Original files unchanged; any partial copies are listed in the log." : e.Message + "  See log for details.", true); }
            finally { SetBusy(false); }
            return success;
        }
        private void SaveSupport() { if (log == null || busy) return; try { using (var d = new SaveFileDialog { Filter = "Support report (*.json)|*.json", FileName = "SheetSetRelinker-support.json", Title = "Save a support report (review before sharing)" }) if (d.ShowDialog(this) == DialogResult.OK) File.Copy(log.SupportPath, d.FileName, true); } catch (Exception e) { SetStatus(e.Message, true); } }
        private void ShowLog() { if (log == null) return; try { using (var viewer = new Form { Text = "Detailed local log — contains project paths", Size = new Size(960, 650), StartPosition = FormStartPosition.CenterParent, Font = Font }) { var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F), Text = log.ReadText() }; viewer.Controls.Add(text); viewer.ShowDialog(this); } } catch (Exception e) { SetStatus(e.Message, true); } }
        private void ShowLicenses() { var assembly = typeof(MainForm).Assembly; var content = new System.Text.StringBuilder("Sheet Set Relinker " + RunLog.Version + "\r\nhttps://github.com/tiagovasc/autocad-dst-relinker\r\n\r\n"); foreach (string name in assembly.GetManifestResourceNames().OrderBy(n => n)) using (var stream = assembly.GetManifestResourceStream(name)) using (var reader = new StreamReader(stream)) content.AppendLine(name).AppendLine(reader.ReadToEnd()).AppendLine(); using (var viewer = new Form { Text = "About & licenses", Size = new Size(850, 600), StartPosition = FormStartPosition.CenterParent }) { viewer.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 10F), Text = content.ToString() }); viewer.ShowDialog(this); } }
        public void ConfigureForTest(string folder, string output, bool copyFiles) { destination.Text = folder; outputName.Text = output; copy.Checked = copyFiles; alreadyCopied.Checked = !copyFiles; }
        public void RenderTo(string path) { using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); } }
    }
    internal sealed class DropPanel : Panel
    {
        public DropPanel() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (var pen = new Pen(Color.FromArgb(170, 190, 212), 1)) { pen.DashStyle = DashStyle.Dash; e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); } }
    }
}
