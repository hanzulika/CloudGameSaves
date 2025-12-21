using System;
using System.Windows.Forms;

namespace SaveCloud
{
    public partial class Form1 : Form
    {
        FolderFileExtract extractObject = new FolderFileExtract();
        Cloud cloud = new Cloud();
        Upload upload = new Upload();

        public Form1()
        {
            InitializeComponent();
            Env.Load();
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            lstSaves.Items.Clear();
            lstSaves.Items.Add("Connecting...");

            try
            {
                cloud.SetCredentials();

                bool connected = await cloud.CanConnectAsync();
                if(!connected)
                {
                    lstSaves.Items.Add("Server unreachable");
                    return;
                }

                lstSaves.Items.Add("Reading saves...");
                bool success = await cloud.GetSavePathsAsync();

                if(!success || cloud.FileNames.Count == 0)
                {
                    lstSaves.Items.Add("No saves found");
                }
                else
                {
                    foreach(var fileName in cloud.FileNames)
                    {
                        lstSaves.Items.Add(fileName);
                    }
                }

                if (cloud.Paths.Count > 0)
                    lstSaves.Items.Add("Paths: " + cloud.Paths[0]);
            }
            catch (Exception ex)
            {
                lstSaves.Items.Clear();
                MessageBox.Show("Error: " + ex.Message, "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void btnSaves_Click(object sender, EventArgs e)
        {
            if(cloud.Paths.Count == 0)
            {
                MessageBox.Show("Connect first to load paths.", "No Paths", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            extractObject.SearchThroughPaths(lstFiles, cloud.Paths);  
        }

        private async void btnUpload_Click(object sender, EventArgs e)
        {
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

            lstSaves.Items.Add("Uploading...");

            try
            {
                var result = await upload.UploadGames(cloud.Client, cloud.RemotePath, extractObject.Games);

                lstSaves.Items.Add("--- Upload Complete ---");
                lstSaves.Items.Add($"Success: {result.Success}");
                lstSaves.Items.Add($"Files uploaded: {result.FilesUploaded}");

                if (result.Created.Count > 0)
                {
                    lstSaves.Items.Add($"Created ({result.Created.Count}):");
                    foreach (var game in result.Created)
                        lstSaves.Items.Add($"  + {game}");
                }

                if (result.Updated.Count > 0)
                {
                    lstSaves.Items.Add($"Updated ({result.Updated.Count}):");
                    foreach (var game in result.Updated)
                        lstSaves.Items.Add($"  ~ {game}");
                }

                if (result.Skipped.Count > 0)
                {
                    lstSaves.Items.Add($"Skipped ({result.Skipped.Count}):");
                    foreach (var game in result.Skipped)
                        lstSaves.Items.Add($"  - {game}");
                }

                if (result.Errors.Count > 0)
                {
                    lstSaves.Items.Add($"Errors ({result.Errors.Count}):");
                    foreach (var error in result.Errors)
                        lstSaves.Items.Add($"  ! {error}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Upload failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnAddGame_Click(object sender, EventArgs e)
        {
            extractObject.AddExtraGame(lstFiles);
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
