namespace AgenticBrowser;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(1280, 820);
        this.Text = "Agentic Browser - AI Web Automation";
        this.StartPosition = FormStartPosition.CenterScreen;
        this.Font = new Font("Segoe UI", 10F);
        this.MinimumSize = new Size(1000, 700);

        // Main layout: 1 column, 4 rows
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10),
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 140F));  // Settings
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));    // Content (instructions + screenshot)
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45F));   // Buttons
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));    // Output

        // --- Row 0: Settings ---
        var settingsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 2),
        };
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75F));   // "API Key:"
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));   // txtApiKey
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));   // "Model:"
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));  // cboModel
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45F));   // "Port:"
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));   // nudPort
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55F));   // "Steps:"
        settingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));   // nudMaxSteps
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
        settingsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));

        var lblApiKey = new Label { Text = "Claude Key:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        txtApiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "sk-ant-..." };
        var lblModel = new Label { Text = "Model:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        cboModel = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cboModel.Items.AddRange(new object[] { "claude-sonnet-4-6", "claude-haiku-4-5-20251001", "claude-opus-4-6", "gemini-2.5-flash", "gemini-2.5-pro" });
        cboModel.SelectedIndex = 0;
        var lblPort = new Label { Text = "Port:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        nudPort = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1024, Maximum = 65535, Value = 9222, Increment = 1 };
        var lblSteps = new Label { Text = "Steps:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        nudMaxSteps = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 100, Value = 25, Increment = 5 };

        var lblUrl = new Label { Text = "Target URL:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        txtTargetUrl = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "https://example.com (optional)" };
        chkHeadless = new CheckBox
        {
            Text = "Headless (invisible browser)",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        settingsPanel.Controls.Add(lblApiKey, 0, 0);
        settingsPanel.Controls.Add(txtApiKey, 1, 0);
        settingsPanel.Controls.Add(lblModel, 2, 0);
        settingsPanel.Controls.Add(cboModel, 3, 0);
        settingsPanel.Controls.Add(lblPort, 4, 0);
        settingsPanel.Controls.Add(nudPort, 5, 0);
        settingsPanel.Controls.Add(lblSteps, 6, 0);
        settingsPanel.Controls.Add(nudMaxSteps, 7, 0);
        var lblGeminiKey = new Label { Text = "Gemini Key:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        txtGeminiKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "AIza..." };
        var lblMode = new Label { Text = "Mode:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        cboMode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        cboMode.Items.AddRange(new object[] { "Direct", "Hybrid (Sonnet→Flash)" });
        cboMode.SelectedIndex = 0;

        settingsPanel.Controls.Add(lblGeminiKey, 0, 1);
        settingsPanel.Controls.Add(txtGeminiKey, 1, 1);
        settingsPanel.Controls.Add(lblMode, 2, 1);
        settingsPanel.Controls.Add(cboMode, 3, 1);
        settingsPanel.Controls.Add(chkHeadless, 4, 1);
        settingsPanel.SetColumnSpan(chkHeadless, 4);

        settingsPanel.Controls.Add(lblUrl, 0, 2);
        settingsPanel.Controls.Add(txtTargetUrl, 1, 2);
        settingsPanel.SetColumnSpan(txtTargetUrl, 7);

        // --- Row 1: SplitContainer with Instructions (left) + Screenshot (right) ---
        splitContent = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 500,
            BorderStyle = BorderStyle.None,
        };

        // Left panel: Instructions
        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        leftPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lblInstruction = new Label
        {
            Text = "Instructions (describe what you want to do):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        };
        txtInstruction = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 11F),
            PlaceholderText = "Example: Go to Gmail, compose a new email to john@test.com with subject 'Hello' and body 'Hi John, how are you?', then send it.",
            AcceptsReturn = true,
            AcceptsTab = true,
        };
        leftPanel.Controls.Add(lblInstruction, 0, 0);
        leftPanel.Controls.Add(txtInstruction, 0, 1);

        // Right panel: Screenshot preview
        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var lblScreenshot = new Label
        {
            Text = "Live Screenshot (what the AI sees):",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
        };
        picScreenshot = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(40, 40, 40),
            BorderStyle = BorderStyle.FixedSingle,
        };
        rightPanel.Controls.Add(lblScreenshot, 0, 0);
        rightPanel.Controls.Add(picScreenshot, 0, 1);

        // Add panels to SplitContainer
        splitContent.Panel1.Controls.Add(leftPanel);
        splitContent.Panel2.Controls.Add(rightPanel);

        // --- Row 2: Buttons ---
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 4, 0, 4),
        };

        btnLaunchChrome = new Button
        {
            Text = "Launch Chrome",
            Width = 140,
            Height = 36,
            Font = new Font("Segoe UI", 10F),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
        };
        btnLaunchChrome.FlatAppearance.BorderSize = 1;

        btnExecute = new Button
        {
            Text = "Execute",
            Width = 120,
            Height = 36,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
        };
        btnExecute.FlatAppearance.BorderSize = 0;

        btnStop = new Button
        {
            Text = "Stop",
            Width = 80,
            Height = 36,
            Font = new Font("Segoe UI", 10F),
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
        };

        btnClear = new Button
        {
            Text = "Clear",
            Width = 80,
            Height = 36,
            Font = new Font("Segoe UI", 10F),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
        };

        buttonPanel.Controls.Add(btnLaunchChrome);
        buttonPanel.Controls.Add(btnExecute);
        buttonPanel.Controls.Add(btnStop);
        buttonPanel.Controls.Add(btnClear);

        // --- Row 3: Output log ---
        txtOutput = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font("Cascadia Code", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 0, false),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = true,
        };

        // Status strip
        statusStrip = new StatusStrip();
        lblStatus = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        lblIteration = new ToolStripStatusLabel { Text = "", TextAlign = ContentAlignment.MiddleRight };
        statusStrip.Items.Add(lblStatus);
        statusStrip.Items.Add(lblIteration);

        mainPanel.Controls.Add(settingsPanel, 0, 0);
        mainPanel.Controls.Add(splitContent, 0, 1);
        mainPanel.Controls.Add(buttonPanel, 0, 2);
        mainPanel.Controls.Add(txtOutput, 0, 3);

        this.Controls.Add(mainPanel);
        this.Controls.Add(statusStrip);
    }

    #endregion

    private TextBox txtApiKey;
    private TextBox txtGeminiKey;
    private TextBox txtTargetUrl;
    private TextBox txtInstruction;
    private ComboBox cboModel;
    private ComboBox cboMode;
    private NumericUpDown nudPort;
    private NumericUpDown nudMaxSteps;
    private Button btnExecute;
    private Button btnStop;
    private Button btnClear;
    private Button btnLaunchChrome;
    private CheckBox chkHeadless;
    private PictureBox picScreenshot;
    private SplitContainer splitContent;
    private RichTextBox txtOutput;
    private StatusStrip statusStrip;
    private ToolStripStatusLabel lblStatus;
    private ToolStripStatusLabel lblIteration;
}
