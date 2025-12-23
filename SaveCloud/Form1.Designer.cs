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
            this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            this.menuStrip1 = new System.Windows.Forms.MenuStrip();
            this.connectionToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.connectToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.scanGamesToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.addGameToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.savesToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.loadingSpinner = new System.Windows.Forms.PictureBox();
            this.progressLabel = new System.Windows.Forms.Label();
            this.addGamesPathToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.loadingSpinner)).BeginInit();
            this.SuspendLayout();
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(61, 4);
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
            this.menuStrip1.Size = new System.Drawing.Size(526, 28);
            this.menuStrip1.TabIndex = 9;
            this.menuStrip1.Text = "menuStrip1";
            // 
            // connectionToolStripMenuItem
            // 
            this.connectionToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.connectToolStripMenuItem});
            this.connectionToolStripMenuItem.Name = "connectionToolStripMenuItem";
            this.connectionToolStripMenuItem.Size = new System.Drawing.Size(98, 24);
            this.connectionToolStripMenuItem.Text = "Connection";
            // 
            // connectToolStripMenuItem
            // 
            this.connectToolStripMenuItem.Name = "connectToolStripMenuItem";
            this.connectToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.connectToolStripMenuItem.Text = "Refresh";
            this.connectToolStripMenuItem.Click += new System.EventHandler(this.btnConnect_Click);
            // 
            // scanGamesToolStripMenuItem
            // 
            this.scanGamesToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.addGameToolStripMenuItem,
            this.addGamesPathToolStripMenuItem});
            this.scanGamesToolStripMenuItem.Name = "scanGamesToolStripMenuItem";
            this.scanGamesToolStripMenuItem.Size = new System.Drawing.Size(68, 24);
            this.scanGamesToolStripMenuItem.Text = "Games";
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
            this.savesToolStripMenuItem.Name = "savesToolStripMenuItem";
            this.savesToolStripMenuItem.Size = new System.Drawing.Size(60, 24);
            this.savesToolStripMenuItem.Text = "Saves";
            // 
            // loadingSpinner
            // 
            this.loadingSpinner.Image = global::SaveCloud.Properties.Resources.LoadingSpinner;
            this.loadingSpinner.Location = new System.Drawing.Point(17, 26);
            this.loadingSpinner.Name = "loadingSpinner";
            this.loadingSpinner.Size = new System.Drawing.Size(100, 50);
            this.loadingSpinner.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.loadingSpinner.TabIndex = 10;
            this.loadingSpinner.TabStop = false;
            this.loadingSpinner.Visible = false;
            // 
            // progressLabel
            // 
            this.progressLabel.AutoSize = true;
            this.progressLabel.Location = new System.Drawing.Point(128, 44);
            this.progressLabel.Name = "progressLabel";
            this.progressLabel.Size = new System.Drawing.Size(0, 16);
            this.progressLabel.TabIndex = 11;
            // 
            // addGamesPathToolStripMenuItem
            // 
            this.addGamesPathToolStripMenuItem.Name = "addGamesPathToolStripMenuItem";
            this.addGamesPathToolStripMenuItem.Size = new System.Drawing.Size(224, 26);
            this.addGamesPathToolStripMenuItem.Text = "Add Games Path";
            this.addGamesPathToolStripMenuItem.Click += new System.EventHandler(this.btnAddPath_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(526, 229);
            this.Controls.Add(this.progressLabel);
            this.Controls.Add(this.loadingSpinner);
            this.Controls.Add(this.menuStrip1);
            this.Name = "Form1";
            this.Text = "Form1";
            this.menuStrip1.ResumeLayout(false);
            this.menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.loadingSpinner)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private System.Windows.Forms.MenuStrip menuStrip1;
        private System.Windows.Forms.ToolStripMenuItem connectionToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem connectToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem scanGamesToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem addGameToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem savesToolStripMenuItem;
        private System.Windows.Forms.PictureBox loadingSpinner;
        private System.Windows.Forms.Label progressLabel;
        private System.Windows.Forms.ToolStripMenuItem addGamesPathToolStripMenuItem;
    }
}

