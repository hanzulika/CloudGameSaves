using Renci.SshNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SaveCloud
{
    internal class Cloud
    {
        private string host;
        private string username;
        private string password;
        private string remotePath;
        private SftpClient sftp;

        public List<string> Paths { get; private set; } = new List<string>();
        public List<string> FileNames { get; private set; } = new List<string>();

        public SftpClient Client => sftp;
        public string RemotePath => remotePath;

        public SftpClient GetSftp()
        {
            return sftp;
        }

        public async Task<bool> CanConnectAsync()
        {
            try
            {
                return await Task.Run(() =>
                {
                    sftp = new SftpClient(host, username, password);
                    sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(5);
                    sftp.Connect();
                    return sftp.IsConnected;
                });
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect()
        {
            if (sftp.IsConnected == true)
            {
                sftp.Disconnect();
            }
            sftp.Dispose();
            sftp = null;
        }

        public void SetCredentials()
        {
            host = Env.Get("SAVECLOUD_HOST");
            username = Env.Get("SAVECLOUD_USERNAME");
            password = Env.Get("SAVECLOUD_PASSWORD");
            remotePath = Env.Get("SAVECLOUD_REMOTE_PATH");
        }

        public async Task<bool> GetSavePathsAsync()
        {
            Paths.Clear();
            FileNames.Clear();

            try
            {
                await Task.Run(() =>
                {
                    if (!sftp.IsConnected)
                        throw new InvalidOperationException("Server unreachable");

                    foreach (var item in sftp.ListDirectory(remotePath))
                    {
                        if (item.Name == "." || item.Name == "..") continue;

                        if (item.Name.Equals("paths.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            using (var stream = sftp.OpenRead(item.FullName))
                            using (var reader = new StreamReader(stream))
                            {
                                string line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        Paths.Add(line);
                                }
                            }
                        }
                        FileNames.Add(item.Name);
                    }
                });

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
