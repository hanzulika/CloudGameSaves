using SaveCloud.Core;
using SaveCloud.Core.Scan;
using SaveCloud.Core.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace SaveCloud.Tests
{
    public class SaveArchiveTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "savecloud-tests", "zip-" + Guid.NewGuid().ToString("N"));

        public SaveArchiveTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Theory]
        [InlineData("<winAppData>/Game/slot1.sav")]
        [InlineData("<winDocuments>/My Games/100% Orange/<osUserName>/save.dat")]
        [InlineData("<base>/Saves/%3C literal.sav")]
        public void EntryNames_RoundTrip_AndContainNoAngleBrackets(string template)
        {
            string entry = SaveArchive.EntryName(template);
            Assert.DoesNotContain("<", entry);
            Assert.DoesNotContain(">", entry);
            Assert.Equal(template, SaveArchive.TemplateOf(entry));
        }

        /// <summary>Regression: on .NET Framework, zip entries named "&lt;winAppData&gt;/..." threw "Illegal characters in path".</summary>
        [Fact]
        public void CreateAndRestore_WithTokenTemplates()
        {
            string src = Path.Combine(_dir, "src.sav");
            File.WriteAllText(src, "progress");
            var files = new List<SaveFile> { new SaveFile { ActualPath = src, Template = "<winAppData>/Game/slot1.sav" } };

            var built = SaveArchive.Create(files, Path.Combine(_dir, "a.zip"));
            Assert.Equal("<winAppData>/Game/slot1.sav", built.Files.Single().Path);
            using (var z = ZipFile.OpenRead(built.Path))
                Assert.Equal("%3CwinAppData%3E/Game/slot1.sav", z.Entries.Single().FullName);

            string target = Path.Combine(_dir, "restored", "slot1.sav");
            var head = new Core.Remote.RemoteHead { Files = built.Files };
            var written = SaveArchive.Restore(built.Path, head,
                t => t == "<winAppData>/Game/slot1.sav" ? target : null, null, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(new[] { target }, written);
            Assert.Equal("progress", File.ReadAllText(target));
        }
    }
}
