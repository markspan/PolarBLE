namespace PolarBLE
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            listBoxDevices = new ListBox();
            lblStatus = new Label();
            btnScan = new Button();
            SuspendLayout();
            // 
            // listBoxDevices
            // 
            listBoxDevices.FormattingEnabled = true;
            listBoxDevices.ItemHeight = 15;
            listBoxDevices.Location = new Point(12, 12);
            listBoxDevices.Name = "listBoxDevices";
            listBoxDevices.Size = new Size(311, 109);
            listBoxDevices.TabIndex = 0;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(99, 135);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(0, 15);
            lblStatus.TabIndex = 1;
            // 
            // btnScan
            // 
            btnScan.Location = new Point(18, 131);
            btnScan.Name = "btnScan";
            btnScan.Size = new Size(75, 23);
            btnScan.TabIndex = 2;
            btnScan.Text = "Scan";
            btnScan.UseVisualStyleBackColor = true;
            btnScan.Click += btnScan_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(335, 168);
            Controls.Add(btnScan);
            Controls.Add(lblStatus);
            Controls.Add(listBoxDevices);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "Form1";
            Text = "Polar H10 to LSL";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ListBox listBoxDevices;
        private Label lblStatus;
        private Button btnScan;
    }
}
