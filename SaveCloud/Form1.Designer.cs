namespace SaveCloud
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.lstSaves = new System.Windows.Forms.ListBox();
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.lstFiles = new System.Windows.Forms.ListBox();
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.connectionToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.connectToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.addPathToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.scanGamesToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.scanGamesToolStripMenuItem1 = new System.Windows.Forms.ToolStripMenuItem();
            this.addGameToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.savesToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.uploadToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(61, 4);
            // 
            // lstSaves
            // 
            this.lstSaves.FormattingEnabled = true;
            this.lstSaves.ItemHeight = 16;
            this.lstSaves.Location = new System.Drawing.Point(12, 78);
            this.lstSaves.Name = "lstSaves";
            this.lstSaves.ScrollAlwaysVisible = true;
            this.lstSaves.Size = new System.Drawing.Size(571, 292);
            this.lstSaves.TabIndex = 3;
            // 
            // lstFiles
            // 
            this.lstFiles.FormattingEnabled = true;
            this.lstFiles.HorizontalScrollbar = true;
            this.lstFiles.ItemHeight = 16;
            this.lstFiles.Location = new System.Drawing.Point(714, 50);
            this.lstFiles.Name = "lstFiles";
            this.lstFiles.ScrollAlwaysVisible = true;
            this.lstFiles.Size = new System.Drawing.Size(414, 292);
            this.lstFiles.TabIndex = 5;
            // 
            // menuStrip1
            // 
            this.menuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.connectionToolStripMenuItem,
            this.scanGamesToolStripMenuItem,
            this.savesToolStripMenuItem});
            this.menuStrip1.Location = new System.Drawing.Point(0, 0);
            this.menuStrip1.Name = "menuStrip1";
            this.menuStrip1.Size = new System.Drawing.Size(1168, 28);
            this.menuStrip1.TabIndex = 9;
            this.menuStrip1.Text = "menuStrip1";
            // 
            // connectionToolStripMenuItem
            // 
            this.connectionToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.connectToolStripMenuItem,
            this.addPathToolStripMenuItem});
            this.connectionToolStripMenuItem.Name = "connectionToolStripMenuItem";
            this.connectionToolStripMenuItem.Size = new System.Drawing.Size(98, 24);
            this.connectionToolStripMenuItem.Text = "Connection";
            // 
            // connectToolStripMenuItem
            // 
            this.connectToolStripMenuItem.Name = "connectToolStripMenuItem";
            this.connectToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.connectToolStripMenuItem.Text = "Connect";
            this.connectToolStripMenuItem.Click += new System.EventHandler(this.btnConnect_Click);
            // 
            // addPathToolStripMenuItem
            // 
            this.addPathToolStripMenuItem.Name = "addPathToolStripMenuItem";
            this.addPathToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.addPathToolStripMenuItem.Text = "Add Path";
            this.addPathToolStripMenuItem.Click += new System.EventHandler(this.btnAddPath_Click);
            // 
            // scanGamesToolStripMenuItem
            // 
            this.scanGamesToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.scanGamesToolStripMenuItem1,
            this.addGameToolStripMenuItem});
            this.scanGamesToolStripMenuItem.Name = "scanGamesToolStripMenuItem";
            this.scanGamesToolStripMenuItem.Size = new System.Drawing.Size(68, 24);
            this.scanGamesToolStripMenuItem.Text = "Games";
            // 
            // scanGamesToolStripMenuItem1
            // 
            this.scanGamesToolStripMenuItem1.Name = "scanGamesToolStripMenuItem1";
            this.scanGamesToolStripMenuItem1.Size = new System.Drawing.Size(224, 26);
            this.scanGamesToolStripMenuItem1.Text = "Scan Games";
            this.scanGamesToolStripMenuItem1.Click += new System.EventHandler(this.btnSaves_Click);
            // 
            // addGameToolStripMenuItem
            // 
            this.addGameToolStripMenuItem.Name = "addGameToolStripMenuItem";
            this.addGameToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.addGameToolStripMenuItem.Text = "Add Game";
            this.addGameToolStripMenuItem.Click += new System.EventHandler(this.btnAddGame_Click);
            // 
            // savesToolStripMenuItem
            // 
            this.savesToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.uploadToolStripMenuItem});
            this.savesToolStripMenuItem.Name = "savesToolStripMenuItem";
            this.savesToolStripMenuItem.Size = new System.Drawing.Size(60, 24);
            this.savesToolStripMenuItem.Text = "Saves";
            // 
            // uploadToolStripMenuItem
            // 
            this.uploadToolStripMenuItem.Name = "uploadToolStripMenuItem";
            this.uploadToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.uploadToolStripMenuItem.Text = "Upload";
            this.uploadToolStripMenuItem.Click += new System.EventHandler(this.btnUpload_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1168, 450);
            this.Controls.Add(this.menuStrip1);
            this.Controls.Add(this.lstFiles);
            this.Controls.Add(this.lstSaves);
            this.Name = "Form1";
            this.Text = "Form1";
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.ListBox lstSaves;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.ListBox lstFiles;
        private System.Windows.Forms.MenuStrip menuStrip1;
        private System.Windows.Forms.ToolStripMenuItem connectionToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem connectToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem addPathToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem scanGamesToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem scanGamesToolStripMenuItem1;
        private System.Windows.Forms.ToolStripMenuItem addGameToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem savesToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem uploadToolStripMenuItem;
    }
}

