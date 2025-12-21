using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SaveCloud
{
    internal class Upload
    {
        public async Task<UploadResult> UploadGames(SftpClient sftp, string remotePath, List<GameSaveInfo> games)
        {
            var result = new UploadResult();

            if (!sftp.IsConnected)
            {
                result.Success = false;
                result.Message = "Not connected to server";
                return result;
            }

            return await Task.Run(() =>
            {
                foreach (var game in games)
                {
                    try
                    {
                        var gameRemotePath = remotePath + "/" + game.GameName;

                        // Check if directory exists
                        bool dirExists = sftp.Exists(gameRemotePath);
                        if (!dirExists)
                        {
                            sftp.CreateDirectory(gameRemotePath);
                            result.Created.Add(game.GameName);
                        }
                        else
                        {
                            result.Updated.Add(game.GameName);
                        }

                        if(game.HasSaves)
                        {
                            foreach (var saveFile in game.SaveFiles)
                            {
                                var remoteFilePath = gameRemotePath + "/" + saveFile.Name;

                                using (var fileStream = saveFile.OpenRead())
                                {
                                    sftp.UploadFile(fileStream, remoteFilePath, true);
                                }

                                // Verify file was uploaded
                                if (sftp.Exists(remoteFilePath))
                                {
                                    result.FilesUploaded++;
                                }
                                else
                                {
                                    result.Errors.Add($"{game.GameName}/{saveFile.Name} - Upload verification failed");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{game.GameName} - {ex.Message}");
                    }
                }

                result.Success = result.Errors.Count == 0 && result.FilesUploaded > 0;
                result.Message = result.Success 
                    ? $"Uploaded {result.FilesUploaded} files" 
                    : (result.FilesUploaded == 0 && result.Errors.Count == 0) 
                        ? "No files to upload" 
                        : "Completed with errors";

                return result;
            });
        }
    }

    internal class UploadResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int FilesUploaded { get; set; }
        public List<string> Created { get; set; } = new List<string>();
        public List<string> Updated { get; set; } = new List<string>();
        public List<string> Skipped { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }
}
