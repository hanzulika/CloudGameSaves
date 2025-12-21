using System;
using System.Windows.Forms;

namespace SaveCloud
{
    public partial class Form1 : Form
    {
        FolderFileExtract extractObject = new FolderFileExtract();
        Cloud cloud = new Cloud();
        Upload upload = new Upload();
        //Logger logger = new Logger();

        public Form1()
        {
            InitializeComponent();
            Env.Load();
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            loadingSpinner.Visible = true;
            try
            {
                cloud.SetCredentials();
                progressLabel.Text = "Connecting to server";
                bool connected = await cloud.CanConnectAsync();
                Logger.LogConnection(connected);
                

                progressLabel.Text = "Acquiring save paths";
                bool success = await cloud.GetSavePathsAsync();
                Logger.LogSaves(success, cloud);
                
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message, "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (cloud.Paths.Count == 0)
            {
                MessageBox.Show("Connect first to load paths.", "No Paths", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            progressLabel.Text = "Finding games";
            await extractObject.SearchThroughPaths(lstFiles, cloud.Paths);

            if (!cloud.Client.IsConnected)
            {
                MessageBox.Show("Connect first.", "Not Connected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (extractObject.Games.Count == 0)
            {
                MessageBox.Show("Scan for games first.", "No Games", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                progressLabel.Text = "Uploading saves";
                var result = await upload.UploadGames(cloud.Client, cloud.RemotePath, extractObject.Games);
                Logger.LogUpload(result);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Upload failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                loadingSpinner.Visible = false;
                progressLabel.Text = "";
            }
        }

        private async void btnAddGame_Click(object sender, EventArgs e)
        {
            await extractObject.AddExtraGame(lstFiles);
        }

        private void btnAddPath_Click(object sender, EventArgs e)
        {
            //cloud.AddExtraPath();
        }

        //TODO 
        // - add functionality to add the following: paths, save file paths
        // - make sure every save would go back exactly to where it was found
        // - load the saves back from the server
        // - automate the game detection + save upload processes
        // - try to make the app a process (with low resource consumption) that starts on windows and detects if a game save has been changed
    }
}
