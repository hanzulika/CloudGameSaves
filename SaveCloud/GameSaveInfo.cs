using System.Collections.Generic;
using System.IO;

namespace SaveCloud
{
    internal class GameSaveInfo
    {
        public string GameName { get; set; }
        public List<FileInfo> SaveFiles { get; set; } = new List<FileInfo>();
        public bool HasSaves => SaveFiles.Count > 0;
    }
}
