using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgenticBrowser;

public class FormMockup : Form
{
    // ActiveLens Brand Colors
    static readonly Color BgDark = Color.FromArgb(13, 13, 26);
    static readonly Color BgPanel = Color.FromArgb(20, 20, 40);
    static readonly Color BgCard = Color.FromArgb(26, 26, 53);
    static readonly Color BgInput = Color.FromArgb(34, 34, 58);
    static readonly Color BgHover = Color.FromArgb(42, 42, 74);
    static readonly Color BorderDim = Color.FromArgb(46, 46, 80);
    static readonly Color TextPrimary = Color.FromArgb(232, 232, 240);
    static readonly Color TextSecondary = Color.FromArgb(144, 144, 176);
    static readonly Color TextMuted = Color.FromArgb(96, 96, 128);
    static readonly Color AccentCyan = Color.FromArgb(0, 207, 255);
    static readonly Color AccentPurple = Color.FromArgb(139, 92, 246);
    static readonly Color AccentMagenta = Color.FromArgb(233, 30, 170);
    static readonly Color SuccessGreen = Color.FromArgb(52, 211, 153);
    static readonly Color ErrorRed = Color.FromArgb(248, 113, 113);

    private Panel settingsPanel;

    public FormMockup()
    {
        Text = "Active Worker - AI Browser Automation";
        ClientSize = new Size(1440, 920);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Inter", 10F);
        MinimumSize = new Size(1100, 700);
        BackColor = BgDark;
        ForeColor = TextPrimary;

        // ===== MAIN LAYOUT =====
        var mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 260,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 1,
            BackColor = BorderDim,
        };

        // ===== LEFT SIDEBAR =====
        var sidebar = new Panel { Dock = DockStyle.Fill, BackColor = BgPanel };

        // Logo bar
        var logoBar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = BgPanel, Padding = new Padding(16, 0, 8, 0) };
        var lblLogo = new GradientLabel
        {
            Text = "Active Worker",
            Font = new Font("Montserrat", 18F, FontStyle.Bold),
            Dock = DockStyle.Left,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 10, 0, 0),
        };
        var btnGear = new Button
        {
            Text = "\u2699",
            Font = new Font("Segoe UI", 16F),
            ForeColor = TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Width = 40,
            Height = 40,
            Dock = DockStyle.Right,
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
        };
        btnGear.FlatAppearance.BorderSize = 0;
        btnGear.FlatAppearance.MouseOverBackColor = BgHover;
        btnGear.Click += (_, _) =>
        {
            settingsPanel.Visible = !settingsPanel.Visible;
        };
        logoBar.Controls.Add(lblLogo);
        logoBar.Controls.Add(btnGear);

        // Gradient accent line under logo
        var gradientLine = new GradientPanel { Dock = DockStyle.Top, Height = 2, Margin = new Padding(16, 0, 16, 0) };

        // Sidebar content
        var sideContent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BgPanel,
            Padding = new Padding(0, 8, 0, 0),
        };
        sideContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        sideContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        sideContent.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));
        sideContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
        sideContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        sideContent.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));

        // Active Tasks header
        var lblActive = new Label
        {
            Text = "  ACTIVE TASKS",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Inter", 8.5F, FontStyle.Bold),
            ForeColor = TextMuted,
        };

        // Task list
        var taskList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = BgPanel,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.None,
            Font = new Font("Inter", 9.5F),
            OwnerDraw = true,
            Margin = new Padding(8, 0, 8, 0),
        };
        taskList.Columns.Add("Status", 22);
        taskList.Columns.Add("Task", 155);
        taskList.Columns.Add("Step", 55);

        var items = new[]
        {
            new { Status = "\u25CF", Task = "Find candidate +1-917...", Step = "4/25", Color = AccentCyan, SubText = "recruit.zoho.com" },
            new { Status = "\u25CF", Task = "Send email to john@...", Step = "Done", Color = SuccessGreen, SubText = "mail.google.com" },
            new { Status = "\u25CF", Task = "Create Freshdesk ticket", Step = "", Color = TextMuted, SubText = "freshdesk.com" },
        };
        foreach (var item in items)
        {
            var li = new ListViewItem(new[] { item.Status, item.Task, item.Step }) { ForeColor = item.Color, Tag = item.SubText };
            taskList.Items.Add(li);
        }
        taskList.Items[0].Selected = true;

        taskList.DrawColumnHeader += (s, e) => { e.DrawDefault = true; };
        taskList.DrawItem += (s, e) => { };
        taskList.DrawSubItem += (s, e) =>
        {
            if (e.Item == null) return;
            var isSelected = e.Item.Selected;
            var bg = isSelected ? BgCard : BgPanel;
            using var bgBrush = new SolidBrush(bg);
            e.Graphics!.FillRectangle(bgBrush, e.Bounds);

            if (e.ColumnIndex == 0)
            {
                using var dotBrush = new SolidBrush(e.Item.ForeColor);
                e.Graphics.DrawString("\u25CF", new Font("Segoe UI", 8F), dotBrush,
                    e.Bounds.Left + 4, e.Bounds.Top + 3);
            }
            else
            {
                using var textBrush = new SolidBrush(e.ColumnIndex == 2 ? e.Item.ForeColor : TextPrimary);
                var font = e.ColumnIndex == 2 ? new Font("Inter", 8.5F) : new Font("Inter", 9.5F);
                e.Graphics.DrawString(e.SubItem?.Text ?? "", font, textBrush,
                    e.Bounds.Left + 2, e.Bounds.Top + 3);
            }
        };

        // Add Task button (gradient)
        var btnAddTask = new GradientButton
        {
            Text = "+ Add Task",
            Dock = DockStyle.Fill,
            Height = 36,
            Font = new Font("Inter", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(10, 4, 10, 4),
        };

        // History header
        var lblHistory = new Label
        {
            Text = "  HISTORY",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Inter", 8.5F, FontStyle.Bold),
            ForeColor = TextMuted,
        };

        // History list
        var historyList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            BackColor = BgPanel,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Inter", 9F),
            OwnerDraw = true,
            Margin = new Padding(8, 0, 8, 8),
        };
        historyList.Columns.Add("", 20);
        historyList.Columns.Add("Task", 140);
        historyList.Columns.Add("Date", 70);
        historyList.DrawColumnHeader += (s, e) => { e.DrawDefault = true; };
        historyList.DrawItem += (s, e) => { };
        historyList.DrawSubItem += (s, e) =>
        {
            if (e.Item == null) return;
            using var bgBrush = new SolidBrush(e.Item.Selected ? BgCard : BgPanel);
            e.Graphics!.FillRectangle(bgBrush, e.Bounds);
            var color = e.ColumnIndex == 0 ? e.Item.ForeColor : (e.ColumnIndex == 2 ? TextMuted : TextPrimary);
            using var textBrush = new SolidBrush(color);
            var font = e.ColumnIndex == 2 ? new Font("Inter", 8F) : new Font("Inter", 9F);
            e.Graphics.DrawString(e.SubItem?.Text ?? "", font, textBrush, e.Bounds.Left + 2, e.Bounds.Top + 4);
        };

        historyList.HeaderStyle = ColumnHeaderStyle.None;
        var histItems = new[]
        {
            new { Icon = "\u2714", Task = "Find candidate...", Date = "Today 14:30", Ok = true },
            new { Icon = "\u2714", Task = "Add note to app...", Date = "Today 14:16", Ok = true },
            new { Icon = "\u2718", Task = "Search Zoho...", Date = "Today 13:45", Ok = false },
            new { Icon = "\u2714", Task = "Gmail compose", Date = "Yest. 16:20", Ok = true },
            new { Icon = "\u2714", Task = "Freshdesk ticket", Date = "Yest. 15:10", Ok = true },
            new { Icon = "\u2718", Task = "Monday status...", Date = "Yest. 11:30", Ok = false },
        };
        foreach (var h in histItems)
        {
            var li = new ListViewItem(new[] { h.Icon, h.Task, h.Date })
            { ForeColor = h.Ok ? SuccessGreen : ErrorRed };
            historyList.Items.Add(li);
        }

        sideContent.Controls.Add(lblActive, 0, 0);
        sideContent.Controls.Add(taskList, 0, 1);
        sideContent.Controls.Add(btnAddTask, 0, 2);
        sideContent.Controls.Add(lblHistory, 0, 3);
        sideContent.Controls.Add(historyList, 0, 4);

        sidebar.Controls.Add(sideContent);
        sidebar.Controls.Add(gradientLine);
        sidebar.Controls.Add(logoBar);

        // ===== RIGHT DETAIL PANEL =====
        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BgDark,
            Padding = new Padding(12, 6, 12, 6),
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 0F));     // Settings (hidden)
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 85F));    // Task header
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));     // Screenshot + Step log
        detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 5F));     // Progress bar
        detail.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));     // Full log

        // --- Settings panel (hidden) ---
        settingsPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgPanel,
            Visible = false,
            Padding = new Padding(16, 12, 16, 12),
        };
        var settingsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        void AddSetting(string label, Control ctrl, int w)
        {
            var lbl = new Label { Text = label, AutoSize = true, ForeColor = TextSecondary, Font = new Font("Inter", 9F), Margin = new Padding(8, 7, 4, 0) };
            ctrl.Width = w;
            ctrl.Height = 26;
            ctrl.Font = new Font("Inter", 9F);
            ctrl.BackColor = BgInput;
            ctrl.ForeColor = TextPrimary;
            ctrl.Margin = new Padding(0, 4, 12, 0);
            settingsFlow.Controls.Add(lbl);
            settingsFlow.Controls.Add(ctrl);
        }
        AddSetting("Claude Key", new TextBox { Text = "sk-ant-api03-xxxx", UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle }, 200);
        AddSetting("Gemini Key", new TextBox { Text = "AIzaSyxxxx", UseSystemPasswordChar = true, BorderStyle = BorderStyle.FixedSingle }, 160);
        var cboM = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput, ForeColor = TextPrimary };
        cboM.Items.AddRange(new object[] { "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "gemini-2.5-flash", "gemini-2.5-pro" });
        cboM.SelectedIndex = 0;
        AddSetting("Model", cboM, 175);
        var cboMd = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = BgInput, ForeColor = TextPrimary };
        cboMd.Items.AddRange(new object[] { "Direct", "Hybrid (Sonnet\u2192Flash)" });
        cboMd.SelectedIndex = 0;
        AddSetting("Mode", cboMd, 155);
        AddSetting("Port", new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 9238, BackColor = BgInput, ForeColor = TextPrimary }, 75);
        AddSetting("Steps", new NumericUpDown { Minimum = 1, Maximum = 100, Value = 25, BackColor = BgInput, ForeColor = TextPrimary }, 65);
        var btnLaunch = MakeButton("Launch Chrome", BgHover, 120);
        btnLaunch.Margin = new Padding(16, 4, 0, 0);
        settingsFlow.Controls.Add(btnLaunch);
        settingsPanel.Controls.Add(settingsFlow);

        // --- Task header bar ---
        var taskHeader = new Panel { Dock = DockStyle.Fill, BackColor = BgDark };

        // Status badge + step
        var statusBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = BgDark,
        };
        var badgePanel = new Panel { Width = 90, Height = 24, BackColor = Color.FromArgb(20, 0, 207, 255), Margin = new Padding(0, 2, 8, 0) };
        var lblBadge = new Label { Text = "\u25CF Running", ForeColor = AccentCyan, Font = new Font("Inter", 9F, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        badgePanel.Controls.Add(lblBadge);
        badgePanel.Paint += (s, e) =>
        {
            using var path = RoundRect(new Rectangle(0, 0, badgePanel.Width, badgePanel.Height), 12);
            badgePanel.Region = new Region(path);
        };
        statusBar.Controls.Add(badgePanel);
        statusBar.Controls.Add(new Label { Text = "Step 4 of 25", ForeColor = TextSecondary, Font = new Font("Inter", 9.5F), AutoSize = true, Margin = new Padding(0, 5, 0, 0) });

        // Buttons
        var btnStop = MakeButton("Stop", Color.FromArgb(192, 57, 43), 75);
        btnStop.Margin = new Padding(20, 2, 4, 0);
        statusBar.Controls.Add(btnStop);
        var btnRerun = MakeButton("Re-run", BgHover, 80);
        btnRerun.Margin = new Padding(0, 2, 0, 0);
        statusBar.Controls.Add(btnRerun);

        // Task instruction text
        var txtTask = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = BgCard,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Inter", 9.5F),
            Text = "open https://recruit.zoho.com/ login with : ralph@ramrecruiting.com\r\n\r\nfind application of candidate with this phone number: +1 (917) 881-8334\r\nadd a note with this call summary:\r\nInterview with Christopher SS Queens for Social Worker Position...",
        };
        taskHeader.Controls.Add(txtTask);
        taskHeader.Controls.Add(statusBar);

        // --- Screenshot + Step Log ---
        var contentSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 700,
            SplitterWidth = 1,
            BackColor = BorderDim,
        };

        // Screenshot
        var screenshotContainer = new Panel { Dock = DockStyle.Fill, BackColor = BgCard, Padding = new Padding(1) };
        var lblSS = new Label { Text = "  Live Screenshot", Dock = DockStyle.Top, Height = 26, ForeColor = TextSecondary, Font = new Font("Inter", 9F, FontStyle.Bold), BackColor = BgCard, TextAlign = ContentAlignment.MiddleLeft };
        var picBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(245, 245, 245) };
        // Draw mock screenshot
        var bmp = new Bitmap(500, 340);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(250, 250, 252));
            g.FillRectangle(new SolidBrush(Color.FromArgb(235, 235, 238)), 0, 0, 500, 38);
            g.DrawString("\u25CF \u25CF \u25CF   recruit.zoho.com/recruit/org894803276/Candidates", new Font("Segoe UI", 8F), Brushes.Gray, 8, 12);
            g.DrawString("Zoho Recruit", new Font("Montserrat", 16F, FontStyle.Bold), new SolidBrush(Color.FromArgb(50, 50, 50)), 24, 55);
            g.DrawString("Candidates", new Font("Inter", 11F), new SolidBrush(Color.FromArgb(100, 100, 100)), 24, 82);
            g.FillRectangle(Brushes.White, 20, 110, 460, 70);
            g.DrawRectangle(new Pen(Color.FromArgb(220, 220, 220)), 20, 110, 460, 70);
            g.DrawString("Christopher Hong", new Font("Inter", 12F, FontStyle.Bold), new SolidBrush(Color.FromArgb(25, 118, 210)), 30, 118);
            g.DrawString("+1 (917) 881-8334   |   Director of Social Services", new Font("Inter", 9F), new SolidBrush(Color.FromArgb(130, 130, 130)), 30, 142);
            g.FillRectangle(new SolidBrush(Color.FromArgb(0, 120, 212)), 24, 195, 120, 34);
            using var roundPath = RoundRect(new Rectangle(24, 195, 120, 34), 6);
            g.FillPath(new SolidBrush(Color.FromArgb(0, 120, 212)), roundPath);
            g.DrawString("Add Note", new Font("Inter", 10F, FontStyle.Bold), Brushes.White, 42, 201);
            g.DrawRectangle(new Pen(Color.FromArgb(255, 60, 60), 2.5f), 20, 191, 128, 42);
        }
        picBox.Image = bmp;

        // Screenshot navigation bar
        var ssNav = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, BackColor = Color.FromArgb(18, 18, 42), FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 2, 0, 0) };
        ssNav.Controls.Add(MakeButton("\u25C0", BgHover, 32));
        ssNav.Controls.Add(new Label { Text = "Screenshot 4 of 4", ForeColor = TextMuted, Font = new Font("Inter", 9F), AutoSize = true, Margin = new Padding(8, 5, 8, 0) });
        ssNav.Controls.Add(MakeButton("\u25B6", BgHover, 32));
        screenshotContainer.Controls.Add(picBox);
        screenshotContainer.Controls.Add(ssNav);
        screenshotContainer.Controls.Add(lblSS);

        // Step-by-step log
        var stepPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgCard, Padding = new Padding(1) };
        var lblStep = new Label { Text = "  Step-by-Step", Dock = DockStyle.Top, Height = 26, ForeColor = TextSecondary, Font = new Font("Inter", 9F, FontStyle.Bold), BackColor = BgCard, TextAlign = ContentAlignment.MiddleLeft };
        var rtbStep = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = BgCard,
            ForeColor = TextSecondary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Cascadia Code", 9F),
        };
        // Populate step log with colors
        rtbStep.Text = "";
        AppendStep(rtbStep, "\u2714", "Step 1", "navigate", "Navigated to recruit.zoho.com", true);
        AppendStep(rtbStep, "\u2714", "Step 2", "click", "Clicked link \"Candidates\"", true);
        AppendStep(rtbStep, "\u2714", "Step 3", "fill", "Filled \"+1 (917) 881-8334\" into search", true);
        AppendStep(rtbStep, "\u25CB", "Step 4", "click_text", "Clicking \"Christopher Hong\"...", false);

        stepPanel.Controls.Add(rtbStep);
        stepPanel.Controls.Add(lblStep);

        contentSplit.Panel1.Controls.Add(screenshotContainer);
        contentSplit.Panel2.Controls.Add(stepPanel);

        // --- Progress bar ---
        var progressPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgDark, Padding = new Padding(0, 1, 0, 1) };
        progressPanel.Paint += (s, e) =>
        {
            var rect = new Rectangle(0, 1, progressPanel.Width, 3);
            using var bgBrush = new SolidBrush(BgCard);
            e.Graphics.FillRectangle(bgBrush, rect);
            // 4/25 = 16% progress
            var progressWidth = (int)(progressPanel.Width * 0.16);
            using var gradBrush = new LinearGradientBrush(
                new Point(0, 0), new Point(progressWidth, 0),
                AccentCyan, AccentPurple);
            e.Graphics.FillRectangle(gradBrush, 0, 1, progressWidth, 3);
        };

        // --- Full output log ---
        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = BgCard, Padding = new Padding(1) };
        var logHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = BgCard };
        var lblLog = new Label { Text = "  Full Output Log", Dock = DockStyle.Left, AutoSize = true, ForeColor = TextSecondary, Font = new Font("Inter", 9F, FontStyle.Bold), Padding = new Padding(0, 5, 0, 0) };
        var btnClearLog = MakeButton("Clear", BgHover, 60);
        btnClearLog.Dock = DockStyle.Right;
        btnClearLog.Height = 24;
        btnClearLog.Margin = new Padding(0, 2, 8, 0);
        logHeader.Controls.Add(lblLog);
        logHeader.Controls.Add(btnClearLog);

        var rtbLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(16, 16, 32),
            ForeColor = TextSecondary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Cascadia Code", 9F),
        };
        AppendLog(rtbLog, "\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550", TextMuted);
        AppendLog(rtbLog, "  Connecting to Chrome on port 9238", TextPrimary);
        AppendLog(rtbLog, "\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550", TextMuted);
        AppendLog(rtbLog, "[Connected! Page: recruit.zoho.com | 3 tabs]", SuccessGreen);
        AppendLog(rtbLog, "[Screenshot size: 22KB]", TextSecondary);
        AppendLog(rtbLog, "", TextSecondary);
        AppendLog(rtbLog, "\u2550\u2550\u2550  Step 1 \u2014 Thinking  \u2550\u2550\u2550", TextMuted);
        AppendLog(rtbLog, "Action: navigate {\"url\":\"https://recruit.zoho.com/...\"}", AccentCyan);
        AppendLog(rtbLog, "[Executing: navigate...]", TextSecondary);
        AppendLog(rtbLog, "Result: Navigated to recruit.zoho.com", SuccessGreen);
        AppendLog(rtbLog, "", TextSecondary);
        AppendLog(rtbLog, "\u2550\u2550\u2550  Step 2 \u2014 Thinking  \u2550\u2550\u2550", TextMuted);
        AppendLog(rtbLog, "Action: click {\"role\":\"link\",\"name\":\"Candidates\"}", AccentCyan);
        AppendLog(rtbLog, "[Executing: click...]", TextSecondary);
        AppendLog(rtbLog, "Result: Clicked link \"Candidates\"", SuccessGreen);

        logPanel.Controls.Add(rtbLog);
        logPanel.Controls.Add(logHeader);

        // Assemble detail
        detail.Controls.Add(settingsPanel, 0, 0);
        detail.Controls.Add(taskHeader, 0, 1);
        detail.Controls.Add(contentSplit, 0, 2);
        detail.Controls.Add(progressPanel, 0, 3);
        detail.Controls.Add(logPanel, 0, 4);

        // Assemble main
        mainSplit.Panel1.Controls.Add(sidebar);
        mainSplit.Panel2.Controls.Add(detail);

        // Status strip
        var status = new StatusStrip { BackColor = Color.FromArgb(10, 10, 20) };
        status.Items.Add(new ToolStripStatusLabel
        {
            Text = "\u25CF Connected (port 9238)  |  3 tasks: 1 running, 1 queued, 1 done",
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TextMuted,
        });

        Controls.Add(mainSplit);
        Controls.Add(status);
    }

    private static Button MakeButton(string text, Color bg, int w)
    {
        var b = new Button
        {
            Text = text,
            Width = w,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = TextPrimary,
            Font = new Font("Inter", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(
            Math.Min(bg.R + 20, 255), Math.Min(bg.G + 20, 255), Math.Min(bg.B + 20, 255));
        return b;
    }

    private static void AppendStep(RichTextBox rtb, string icon, string step, string action, string result, bool done)
    {
        var iconColor = done ? SuccessGreen : AccentCyan;
        var stepColor = done ? TextPrimary : AccentCyan;

        rtb.SelectionColor = iconColor;
        rtb.AppendText($" {icon} ");
        rtb.SelectionColor = stepColor;
        rtb.SelectionFont = new Font("Cascadia Code", 9F, FontStyle.Bold);
        rtb.AppendText(step);
        rtb.SelectionColor = AccentPurple;
        rtb.SelectionFont = new Font("Cascadia Code", 8.5F);
        rtb.AppendText($"  {action}\n");
        rtb.SelectionColor = TextSecondary;
        rtb.SelectionFont = new Font("Cascadia Code", 8.5F);
        rtb.AppendText($"   {result}\n\n");
    }

    private static void AppendLog(RichTextBox rtb, string text, Color color)
    {
        rtb.SelectionColor = color;
        rtb.AppendText(text + "\n");
    }

    internal static GraphicsPath RoundRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// Custom gradient label for brand name
public class GradientLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (string.IsNullOrEmpty(Text)) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        var rect = ClientRectangle;
        using var brush = new LinearGradientBrush(rect,
            Color.FromArgb(0, 207, 255),    // Cyan
            Color.FromArgb(233, 30, 170),   // Magenta
            LinearGradientMode.Horizontal);

        // Add purple midpoint
        var blend = new ColorBlend(3);
        blend.Colors = new[] { Color.FromArgb(0, 207, 255), Color.FromArgb(139, 92, 246), Color.FromArgb(233, 30, 170) };
        blend.Positions = new[] { 0f, 0.5f, 1f };
        brush.InterpolationColors = blend;

        using var format = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(Text, Font, brush, rect, format);
    }
}

// Gradient accent panel
public class GradientPanel : Panel
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(0, 207, 255), Color.FromArgb(233, 30, 170),
            LinearGradientMode.Horizontal);
        var blend = new ColorBlend(3);
        blend.Colors = new[] { Color.FromArgb(0, 207, 255), Color.FromArgb(139, 92, 246), Color.FromArgb(233, 30, 170) };
        blend.Positions = new[] { 0f, 0.5f, 1f };
        brush.InterpolationColors = blend;
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}

// Gradient button
public class GradientButton : Button
{
    public GradientButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = ClientRectangle;
        using var path = FormMockup.RoundRect(rect, 8);  // Need to make RoundRect internal/public
        using var brush = new LinearGradientBrush(rect,
            Color.FromArgb(0, 207, 255), Color.FromArgb(233, 30, 170),
            LinearGradientMode.Horizontal);
        var blend = new ColorBlend(3);
        blend.Colors = new[] { Color.FromArgb(0, 207, 255), Color.FromArgb(139, 92, 246), Color.FromArgb(233, 30, 170) };
        blend.Positions = new[] { 0f, 0.5f, 1f };
        brush.InterpolationColors = blend;
        e.Graphics.FillPath(brush, path);

        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(Text, Font, Brushes.White, rect, format);
    }
}
