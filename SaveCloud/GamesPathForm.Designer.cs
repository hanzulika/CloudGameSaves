namespace SaveCloud
{
    partial class GamesPathForm
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
            this.btnManualAdd = new System.Windows.Forms.Button();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnRemove = new System.Windows.Forms.Button();
            this.txtbManual = new System.Windows.Forms.TextBox();
            this.lstPaths = new System.Windows.Forms.ListBox();
            this.btnSave = new System.Windows.Forms.Button();
            this.btnCancel = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // btnManualAdd
            // 
            this.btnManualAdd.Location = new System.Drawing.Point(474, 64);
            this.btnManualAdd.Name = "btnManualAdd";
            this.btnManualAdd.Size = new System.Drawing.Size(98, 23);
            this.btnManualAdd.TabIndex = 0;
            this.btnManualAdd.Text = "Add Manually";
            this.btnManualAdd.UseVisualStyleBackColor = true;
            this.btnManualAdd.Click += new System.EventHandler(this.btnManualAdd_Click);
            // 
            // btnBrowse
            // 
            this.btnBrowse.Location = new System.Drawing.Point(474, 105);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(98, 23);
            this.btnBrowse.TabIndex = 1;
            this.btnBrowse.Text = "Browse";
            this.btnBrowse.UseVisualStyleBackColor = true;
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);
            // 
            // btnRemove
            // 
            this.btnRemove.Enabled = false;
            this.btnRemove.Location = new System.Drawing.Point(474, 149);
            this.btnRemove.Name = "btnRemove";
            this.btnRemove.Size = new System.Drawing.Size(98, 23);
            this.btnRemove.TabIndex = 2;
            this.btnRemove.Text = "Remove";
            this.btnRemove.UseVisualStyleBackColor = true;
            this.btnRemove.Click += new System.EventHandler(this.btnRemove_Click);
            // 
            // txtbManual
            // 
            this.txtbManual.Location = new System.Drawing.Point(29, 22);
            this.txtbManual.Name = "txtbManual";
            this.txtbManual.Size = new System.Drawing.Size(511, 22);
            this.txtbManual.TabIndex = 3;
            // 
            // lstPaths
            // 
            this.lstPaths.FormattingEnabled = true;
            this.lstPaths.ItemHeight = 16;
            this.lstPaths.Location = new System.Drawing.Point(29, 64);
            this.lstPaths.Name = "lstPaths";
            this.lstPaths.Size = new System.Drawing.Size(424, 308);
            this.lstPaths.TabIndex = 4;
            this.lstPaths.SelectedIndexChanged += new System.EventHandler(this.lstPaths_SelectedIndexChanged);
            // 
            // btnSave
            // 
            this.btnSave.Location = new System.Drawing.Point(340, 394);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(98, 23);
            this.btnSave.TabIndex = 5;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.btnSave_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.Location = new System.Drawing.Point(455, 394);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(98, 23);
            this.btnCancel.TabIndex = 6;
            this.btnCancel.Text = "Cancel";
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // GamesPathForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(580, 438);
            this.Controls.Add(this.btnCancel);
            this.Controls.Add(this.btnSave);
            this.Controls.Add(this.lstPaths);
            this.Controls.Add(this.txtbManual);
            this.Controls.Add(this.btnRemove);
            this.Controls.Add(this.btnBrowse);
            this.Controls.Add(this.btnManualAdd);
            this.Name = "GamesPathForm";
            this.Text = "GamesPathForm";
            this.Load += new System.EventHandler(this.GamesPathForm_Load);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnManualAdd;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.Button btnRemove;
        private System.Windows.Forms.TextBox txtbManual;
        private System.Windows.Forms.ListBox lstPaths;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Button btnCancel;
    }
}