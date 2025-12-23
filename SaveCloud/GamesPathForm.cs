using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud
{
    public partial class GamesPathForm : Form
    {
        public List<string> Paths { get; private set; }
        public GamesPathForm(List<string> existingPaths)
        {
            InitializeComponent();
            Paths = new List<string>(existingPaths);
        }

        private void GamesPathForm_Load(object sender, EventArgs e)
        {
            RefreshList();
        }

        private void RefreshList()
        {
            lstPaths.Items.Clear();
            foreach(var path in Paths)
            {
                lstPaths.Items.Add(path);
            }
            btnRemove.Enabled = false;
        }

        private void lstPaths_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnRemove.Enabled = lstPaths.SelectedIndex >= 0;
        }

        private void btnManualAdd_Click(object sender, EventArgs e)
        {
            string newPath = txtbManual.Text.Trim();

            if(string.IsNullOrEmpty(newPath))
            {
                MessageBox.Show("Please enter a path.", "Empty Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (Paths.Contains(newPath))
            {
                MessageBox.Show("Path already exists.", "Duplicate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Paths.Add(newPath);
            RefreshList();
            txtbManual.Clear();
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using(FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select a folder containing games";
                folderDialog.ShowNewFolderButton = false;

                if(folderDialog.ShowDialog() == DialogResult.OK)
                {
                    string newPath = folderDialog.SelectedPath;

                    if(Paths.Contains(newPath))
                    {
                        MessageBox.Show("Path already exists.", "Duplicate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    Paths.Add(newPath);
                    RefreshList();
                }
            }
        }

        private void btnRemove_Click(object sender, EventArgs e)
        {
            if(lstPaths.SelectedIndex >= 0)
            {
                Paths.RemoveAt(lstPaths.SelectedIndex);
                RefreshList();
            }  
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
