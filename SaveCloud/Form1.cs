using Newtonsoft.Json.Linq;
using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http; 
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud
{
    public partial class Form1 : Form
    {
        List<string> paths = new List<string>();
        public Form1()
        {
            InitializeComponent();
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            string host = "100.77.234.113";
            string username = "hanzu";
            string password = txtbPassword.Text;
            string remotePath = "./gamesaves";

            btnConnect.Enabled = false;
            lstSaves.Items.Clear();
            lstSaves.Items.Add("Connecting...");

            try
            {
                List<string> fileNames = await Task.Run(() =>
                {
                    var filesFound = new List<string>();

                    using (var sftp = new SftpClient(host, username, password))
                    {
                        sftp.Connect();

                        var items = sftp.ListDirectory(remotePath);

                        foreach (var item in items)
                        {
                            if (item.Name == "." || item.Name == "..") continue;
                            if(item.Name == "paths.txt")
                            {
                                using(var stream = sftp.OpenRead(item.FullName))
                                using(var reader = new StreamReader(stream))
                                {
                                    string line;
                                    while((line = reader.ReadLine()) != null)
                                    {
                                        paths.Add(line);
                                    }
                                }
                            }
                            filesFound.Add(item.Name);
                        }

                        sftp.Disconnect();
                    }
                    return filesFound;
                });

                lstSaves.Items.Clear();

                if (fileNames.Count == 0)
                {
                    lstSaves.Items.Add("No saves found");
                }
                else
                {
                    foreach (var fileName in fileNames)
                    {
                        lstSaves.Items.Add(fileName);
                    }
                }
                lstSaves.Items.Add("Paths:" + paths[0]);
            }
            catch (Exception ex)
            {
                lstSaves.Items.Clear();
                MessageBox.Show("Error: " + ex.Message, "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnConnect.Enabled = true;
            }
        }

        private async void btnSaves_Click(object sender, EventArgs e)
        {
            DirectoryInfo baseSaveDir = new DirectoryInfo(@paths[0]);
            DirectoryInfo[] saveDirList = baseSaveDir.GetDirectories();

            foreach (DirectoryInfo gameDir in baseSaveDir.GetDirectories())
            {
                bool isGame = await IsRealGame(gameDir.Name);

                if(!isGame)
                {
                    foreach(DirectoryInfo subDir in gameDir.GetDirectories())
                    {
                        bool isSubGame = await IsRealGame(subDir.Name);
                        if(isSubGame)
                        {
                            ExtractFiles(subDir);
                        }
                    }
                }
                else
                {
                    ExtractFiles(gameDir);
                }                
            }
        }

        public void ExtractFiles(DirectoryInfo gameDir)
        {
            try
            {
                var matchedFolder = gameDir.GetDirectories("*save*", SearchOption.AllDirectories);

                // prioritize folders named "Saves" or "Save"
                var bestMatch = matchedFolder.OrderBy(d =>
                {
                    // Priority 1: Exact match "Saves"
                    if (d.Name.Equals("Saves", StringComparison.OrdinalIgnoreCase)) return 0;

                    // Priority 2: Exact match "Save"
                    if (d.Name.Equals("Save", StringComparison.OrdinalIgnoreCase)) return 1;

                    // Priority 3: Everything else
                    return 2;
                })
                .ThenBy(d => d.Name.Length) // Prefer shorter names ("MySaves" > "Experimental Saves")
                .FirstOrDefault();

                if (bestMatch != null)
                {
                    lstFiles.Items.Add($"Game: {gameDir.Name} - Save Folder: {bestMatch.FullName}");
                    //foreach (FileInfo saveFile in bestMatch.GetFiles())
                    //{
                    //    lstFiles.Items.Add($"File: {saveFile.Name} - Extension: {saveFile.Extension}");
                    //}
                }
                else
                {
                    lstFiles.Items.Add($"Game: {gameDir.Name} - No save folder found");
                }
            }
            catch (Exception ex)
            {
                lstFiles.Items.Add($"Game: {gameDir.Name} - Error: {ex.Message}");
            }
        }

        private async Task<bool> IsRealGame(string folderName)
        {
            string[] bannedNames = {
                "Common", "Bin", "Binaries", "Config", "Logs", "Redist", "DirectX",
                "DotNet", "VCRedist", "Support", "Data", "Music", "Video", "CrashReports",
                "Install", "Update", "Launcher", "Engine", "System", "Dumps",
                "ShareX", "Arduino", "My Games", "Electronic Arts", "Rockstar Games",
                "Templates", "Visual Studio 2022", "Visual Studio 18", "Code Snippets", "Libraries", "Visualizers",
                "Backup", "Screenshots", "Scripts", "Social Club"
            };

            if (bannedNames.Any(b => b.Equals(folderName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (folderName.Length <= 3) return false;

            string cleanName = SplitCamelCase(folderName);

            string apiKey = "643f9f47bb0a4b01bed819215ed0b538";
            string url = $"https://api.rawg.io/api/games?key={apiKey}&search={folderName}&page_size=1";

            using(HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SaveCloudApp");

                try
                {
                    string response = await client.GetStringAsync(url);
                    JObject json = JObject.Parse(response);
                    if (json["results"].HasValues)
                    {
                        string apiName = json["results"][0]["name"].ToString();
                        Console.WriteLine("Actual folder: " + folderName + " | Game name: " + apiName);
                        if (IsNameSimilar(cleanName, apiName))
                        {
                            return true;
                        }
                    }
                }
                catch(Exception)
                {
                    return false;
                }
            }
            return false;
        }

        private bool IsNameSimilar(string folderName, string apiName)
        {
            if (string.Equals(folderName, apiName, StringComparison.OrdinalIgnoreCase))
                return true;

            string fName = folderName.ToLower().Trim();
            string aName = apiName.ToLower().Trim();

            if (fName == aName) return true;

            string acronym = GetAcronym(apiName);
            if (fName.Replace(" ", "") == acronym) return true;

            if(aName.StartsWith(fName))
            {
                if (aName.Length > fName.Length)
                {
                    char nextChar = aName[fName.Length];

                    char[] separators = { ':', '-', '(', ')', '™', '®' };
                    if (separators.Contains(nextChar)) return true;

                    if(nextChar == ' ')
                    {
                        if (fName.Contains(" ")) return true;
                        if (fName.Length > 9) return true;
                    }
                }
            }

            if (fName.Length > 7)
            {
                int dist = ComputeLevenshteinDistance(fName.Replace(" ", ""), aName.Replace(" ", ""));
                if (dist <= 3) return true;
            }

            return false;
        }

        private string GetAcronym(string gameName)
        {
            // Split by space, colon, or hyphen
            string[] words = gameName.Split(new char[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
            string acronym = "";
            foreach (var word in words)
            {
                if (word.Length > 0)
                {
                    // Take the first letter (or number) of each word
                    acronym += char.ToLower(word[0]);
                }
            }
            return acronym;
        }

        private int ComputeLevenshteinDistance(string fName, string gName)
        {
            if (string.IsNullOrEmpty(fName)) return string.IsNullOrEmpty(gName) ? 0 : gName.Length;
            if (string.IsNullOrEmpty(gName)) return fName.Length;

            int n = fName.Length;
            int m = gName.Length;
            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; d[i, 0] = i++) { }
            for (int j = 0; j <= m; d[0, j] = j++) { }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (gName[j - 1] == fName[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        private string SplitCamelCase(string input)
        {
            return Regex.Replace(input, "(\\B[A-Z])", " $1").Trim();
        }
    }

    //TODO 
    // - create classes for each bundle of operations
    // - upload the save files to the server
    // - add functionality to add the following: paths, games, save file paths
}
