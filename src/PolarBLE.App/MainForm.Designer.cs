using PolarBLE.App.Controls;

namespace PolarBLE.App;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    private ListBox listBoxDevices;
    private Button btnScan;
    private Button btnConnect;
    private Label lblStatus;
    private Label lblThroughput;
    private Label lblBatteryCaption;
    private BatteryBar batteryBar;
    private System.Windows.Forms.Timer uiTimer;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        listBoxDevices = new ListBox();
        btnScan = new Button();
        btnConnect = new Button();
        lblStatus = new Label();
        lblThroughput = new Label();
        lblBatteryCaption = new Label();
        batteryBar = new BatteryBar();
        uiTimer = new System.Windows.Forms.Timer(components);
        SuspendLayout();
        //
        // listBoxDevices
        //
        listBoxDevices.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        listBoxDevices.FormattingEnabled = true;
        listBoxDevices.ItemHeight = 15;
        listBoxDevices.Location = new Point(12, 12);
        listBoxDevices.Name = "listBoxDevices";
        listBoxDevices.Size = new Size(436, 139);
        listBoxDevices.TabIndex = 0;
        listBoxDevices.AccessibleName = "Discovered Polar devices";
        listBoxDevices.SelectedIndexChanged += ListBoxDevices_SelectedIndexChanged;
        listBoxDevices.DoubleClick += ListBoxDevices_DoubleClick;
        //
        // btnScan
        //
        btnScan.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnScan.Location = new Point(12, 160);
        btnScan.Name = "btnScan";
        btnScan.Size = new Size(100, 28);
        btnScan.TabIndex = 1;
        btnScan.Text = "&Scan";
        btnScan.UseVisualStyleBackColor = true;
        btnScan.Click += BtnScan_Click;
        //
        // btnConnect
        //
        btnConnect.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        btnConnect.Enabled = false;
        btnConnect.Location = new Point(118, 160);
        btnConnect.Name = "btnConnect";
        btnConnect.Size = new Size(100, 28);
        btnConnect.TabIndex = 2;
        btnConnect.Text = "&Connect";
        btnConnect.UseVisualStyleBackColor = true;
        btnConnect.Click += BtnConnect_Click;
        //
        // lblBatteryCaption
        //
        lblBatteryCaption.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        lblBatteryCaption.AutoSize = true;
        lblBatteryCaption.Location = new Point(299, 167);
        lblBatteryCaption.Name = "lblBatteryCaption";
        lblBatteryCaption.Size = new Size(46, 15);
        lblBatteryCaption.TabIndex = 3;
        lblBatteryCaption.Text = "Battery";
        //
        // batteryBar
        //
        batteryBar.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        batteryBar.Location = new Point(348, 161);
        batteryBar.Name = "batteryBar";
        batteryBar.Size = new Size(100, 26);
        batteryBar.TabIndex = 4;
        batteryBar.Value = -1;
        batteryBar.AccessibleName = "Battery level";
        //
        // lblStatus
        //
        lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        lblStatus.AutoEllipsis = true;
        lblStatus.Location = new Point(12, 196);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(436, 34);
        lblStatus.TabIndex = 5;
        lblStatus.Text = "Idle. Press Scan to look for a Polar strap.";
        //
        // lblThroughput
        //
        lblThroughput.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        lblThroughput.AutoEllipsis = true;
        lblThroughput.ForeColor = SystemColors.GrayText;
        lblThroughput.Location = new Point(12, 232);
        lblThroughput.Name = "lblThroughput";
        lblThroughput.Size = new Size(436, 17);
        lblThroughput.TabIndex = 6;
        //
        // uiTimer
        //
        uiTimer.Interval = 250;
        uiTimer.Tick += UiTimer_Tick;
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(460, 258);
        MinimumSize = new Size(400, 260);
        Controls.Add(lblThroughput);
        Controls.Add(lblStatus);
        Controls.Add(batteryBar);
        Controls.Add(lblBatteryCaption);
        Controls.Add(btnConnect);
        Controls.Add(btnScan);
        Controls.Add(listBoxDevices);
        Name = "MainForm";
        Text = "Polar H10 to LSL";
        FormClosing += MainForm_FormClosing;
        ResumeLayout(false);
        PerformLayout();
    }
}
