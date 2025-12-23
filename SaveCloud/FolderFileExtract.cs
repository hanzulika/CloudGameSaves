using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud
{
    internal class FolderFileExtract
    {
        public List<GameSaveInfo> Games { get; private set; } = new List<GameSaveInfo>();

        public async Task AddExtraGame()
        {
            using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
            {
                folderDialog.Description = "Select a game save folder";
                folderDialog.ShowNewFolderButton = false;

                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = folderDialog.SelectedPath;
                    DirectoryInfo selectedDir = new DirectoryInfo(selectedPath);

                    await VerifyGames(selectedDir);
                }
            }
        }
        public async Task SearchThroughPaths(List<string> paths)
        {
            Games.Clear();

            foreach (String path in paths)
            {
                DirectoryInfo baseSaveDir = new DirectoryInfo(path);
                DirectoryInfo[] saveDirList = baseSaveDir.GetDirectories();

                foreach (DirectoryInfo gameDir in baseSaveDir.GetDirectories())
                {
                    await VerifyGames(gameDir);
                }
            }
            Logger.LogInfo($"Search complete. Found {Games.Count} games.");
        }

        public async Task VerifyGames(DirectoryInfo gameDir)
        {
            bool isGame = await IsRealGame(gameDir.Name);

            if (!isGame)
            {
                foreach (DirectoryInfo subDir in gameDir.GetDirectories())
                {
                    bool isSubGame = await IsRealGame(subDir.Name);
                    if (isSubGame)
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
        private void ExtractFiles(DirectoryInfo gameDir)
        {
            try
            {
                if (Games.Any(g => g.GameName.Equals(gameDir.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    Logger.LogInfo($"Skipping duplicate: {gameDir.Name}");
                    return;
                }

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

                var gameInfo = new GameSaveInfo
                {
                    GameName = gameDir.Name
                };


                if (bestMatch != null)
                {
                    gameInfo.SaveFiles = bestMatch.GetFiles("*", SearchOption.AllDirectories).ToList();
                    Logger.LogGameFound(gameDir.Name, bestMatch.FullName, gameInfo.SaveFiles.Count);
                }
                else
                {
                    Logger.LogGameNoSaves(gameDir.Name);
                }

                Games.Add(gameInfo);
            }
            catch (Exception ex)
            {
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
                "Backup", "Screenshots", "Scripts", "Social Club", "Mono"
            };

            if (bannedNames.Any(b => b.Equals(folderName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (folderName.Length <= 3) return false;

            string cleanName = SplitCamelCase(folderName);

            string apiKey = Env.Get("RAWG_API_KEY");
            string url = $"https://api.rawg.io/api/games?key={apiKey}&search={cleanName}&page_size=1";

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("User-Agent", "SaveCloudApp");

                try
                {
                    string response = await client.GetStringAsync(url);
                    JObject json = JObject.Parse(response);
                    if (json["results"].HasValues)
                    {
                        string apiName = json["results"][0]["name"].ToString();
                        Console.WriteLine("Actual folder: " + cleanName + " | Game name: " + apiName);
                        if (IsNameSimilar(cleanName, apiName))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception)
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

            if (aName.StartsWith(fName))
            {
                if (aName.Length > fName.Length)
                {
                    char nextChar = aName[fName.Length];

                    char[] separators = { ':', '-', '(', ')', '™', '®' };
                    if (separators.Contains(nextChar)) return true;

                    if (nextChar == ' ')
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
            if (string.IsNullOrEmpty(input))
                return input;

            int upperCount = input.Count(char.IsUpper);
            int letterCount = input.Count(char.IsLetter);
            if(letterCount > 0 && (double) upperCount / letterCount > 0.7)
            {
                return input;
            }

            string result = Regex.Replace(input, "([a-z])([A-Z])", "$1 $2");

            result = Regex.Replace(result, "([A-Z]+)([A-Z][a-z])", "$1 $2");

            return result.Trim();
        }
    }
}

