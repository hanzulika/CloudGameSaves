using SaveCloud.Core;
using SaveCloud.Core.Remote;
using SaveCloud.Platform;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SaveCloud.UI
{
    public sealed class SettingsForm : Form
    {
        private readonly AppConfig _config;
        private readonly TextBox _host, _user, _remoteRoot, _keyPath, _passphrase, _password, _ignored;
        private readonly NumericUpDown _port, _maxSize, _history;
        private readonly CheckBox _startup, _config_, _skipSteam;
        private readonly Label _fingerprint;
        private readonly ListBox _roots;
        private readonly List<string> _rootList;
        private string _pinned;

        public SettingsForm(AppConfig config)
        {
            _config = config;
            _pinned = config.HostKeyFingerprint;
            _rootList = new List<string>(config.ExtraRoots ?? new List<string>());

            Text = "SaveCloud settings";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Header(grid, "Save server (SFTP)");
            _host = Row(grid, "Server address", config.Host, "Hostname or IP, e.g. savecloud.tailnet-name.ts.net or 192.168.1.50");
            _port = Number(grid, "Port", config.Port, 1, 65535);
            _user = Row(grid, "Username", config.Username);
            _remoteRoot = Row(grid, "Server folder", config.RemoteRoot, "Folder on the server for all saves (inside the SFTP jail). Default /data");

            _keyPath = Row(grid, "SSH private key", config.PrivateKeyPath, "Recommended. Leave empty to use a password instead.");
            var keyButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) => BrowseKey();
            var gen = new Button { Text = "Generate key…", AutoSize = true };
            gen.Click += (s, e) => GenerateKey();
            keyButtons.Controls.AddRange(new Control[] { browse, gen });
            grid.Controls.Add(keyButtons, 2, grid.RowCount - 1);

            _passphrase = Row(grid, "Key passphrase", Secrets.Unprotect(config.PrivateKeyPassphraseProtected) ?? "");
            _passphrase.UseSystemPasswordChar = true;
            _password = Row(grid, "Password", Secrets.Unprotect(config.PasswordProtected) ?? "", "Only used when no key is set.");
            _password.UseSystemPasswordChar = true;

            _fingerprint = new Label { AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
            AddRow(grid, "Server identity", _fingerprint);
            var fpButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var test = new Button { Text = "Test connection", AutoSize = true };
            test.Click += async (s, e) => await TestConnection(test);
            var forget = new Button { Text = "Forget", AutoSize = true };
            forget.Click += (s, e) => { _pinned = ""; UpdateFingerprint(); };
            fpButtons.Controls.AddRange(new Control[] { test, forget });
            grid.Controls.Add(fpButtons, 2, grid.RowCount - 1);
            UpdateFingerprint();

            Header(grid, "Games");
            _roots = new ListBox { Width = 374, Height = 70, HorizontalScrollbar = true };
            AddRow(grid, "Extra game folders", _roots);
            var editRoots = new Button { Text = "Edit…", AutoSize = true };
            editRoots.Click += (s, e) => EditRoots();
            grid.Controls.Add(editRoots, 2, grid.RowCount - 1);
            RefreshRoots();
            Hint(grid, "Steam, Epic, GOG, Program Files and X:\\Games are found automatically. Add other folders that contain installed games.");

            _ignored = Row(grid, "Never sync", string.Join(Environment.NewLine, config.IgnoredGames ?? new List<string>()));
            _ignored.Multiline = true;
            _ignored.Height = 60;
            _ignored.ScrollBars = ScrollBars.Vertical;
            Hint(grid, "One game name per line, exactly as shown in the status window.");

            _skipSteam = Check(grid, "Skip Steam games that already use Steam Cloud", config.SkipSteamCloudGames);
            _config_ = Check(grid, "Also sync settings files (graphics, keybinds) – usually PC-specific", config.IncludeConfigFiles);
            _maxSize = Number(grid, "Max saves per game (MB)", config.MaxGameSizeMB, 1, 100000);
            _history = Number(grid, "Old versions kept on server", config.HistoryToKeep, 1, 1000);
            _startup = Check(grid, "Start SaveCloud when I sign in to Windows", config.StartWithWindows);

            var ok = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
            ok.Click += (s, e) => { if (Commit()) { DialogResult = DialogResult.OK; Close(); } };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Bottom };
            bottom.Controls.AddRange(new Control[] { cancel, ok });
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(grid);
            Controls.Add(bottom);
        }

        // ---- layout helpers ----------------------------------------------------------------------------

        private static void Header(TableLayoutPanel g, string text)
        {
            g.RowCount++;
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4) };
            g.Controls.Add(l, 0, g.RowCount - 1);
            g.SetColumnSpan(l, 3);
        }

        private static void Hint(TableLayoutPanel g, string text)
        {
            g.RowCount++;
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = SystemColors.GrayText };
            g.Controls.Add(l, 1, g.RowCount - 1);
            g.SetColumnSpan(l, 2);
        }

        private static void AddRow(TableLayoutPanel g, string label, Control c)
        {
            g.RowCount++;
            g.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 8, 3) }, 0, g.RowCount - 1);
            g.Controls.Add(c, 1, g.RowCount - 1);
        }

        private static TextBox Row(TableLayoutPanel g, string label, string value, string hint = null)
        {
            var t = new TextBox { Text = value ?? "", Width = 374 };
            AddRow(g, label, t);
            if (hint != null) new ToolTip().SetToolTip(t, hint);
            return t;
        }

        private static NumericUpDown Number(TableLayoutPanel g, string label, int value, int min, int max)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 100 };
            AddRow(g, label, n);
            return n;
        }

        private static CheckBox Check(TableLayoutPanel g, string text, bool value)
        {
            g.RowCount++;
            var c = new CheckBox { Text = text, Checked = value, AutoSize = true };
            g.Controls.Add(c, 1, g.RowCount - 1);
            g.SetColumnSpan(c, 2);
            return c;
        }

        // ---- actions -----------------------------------------------------------------------------------

        private void UpdateFingerprint()
            => _fingerprint.Text = string.IsNullOrEmpty(_pinned) ? "not pinned yet (pinned on first connect)" : "SHA256:" + _pinned;

        private void RefreshRoots()
        {
            _roots.Items.Clear();
            foreach (var r in _rootList) _roots.Items.Add(r);
        }

        private void EditRoots()
        {
            using (var f = new GamesPathForm(_rootList))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _rootList.Clear();
                _rootList.AddRange(f.Paths);
                RefreshRoots();
            }
        }

        private void BrowseKey()
        {
            using (var d = new OpenFileDialog { Title = "Select your SSH private key", Filter = "All files|*.*" })
            {
                string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
                if (Directory.Exists(sshDir)) d.InitialDirectory = sshDir;
                if (d.ShowDialog(this) == DialogResult.OK) _keyPath.Text = d.FileName;
            }
        }

        private void GenerateKey()
        {
            string path = AppPaths.DefaultKeyFile;
            if (File.Exists(path) && MessageBox.Show(this, $"A SaveCloud key already exists at\n{path}\n\nReplace it? The server will then need the new public key.",
                    "Generate key", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                if (File.Exists(path + ".pub")) ShowPublicKey(File.ReadAllText(path + ".pub").Trim());
                _keyPath.Text = path;
                return;
            }
            try
            {
                string pub = KeyGenerator.Generate(path);
                _keyPath.Text = path;
                _passphrase.Text = "";
                ShowPublicKey(pub);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Generate key", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowPublicKey(string pub)
        {
            Clipboard.SetText(pub);
            using (var f = new Form
            {
                Text = "Public key for this PC",
                Size = new Size(640, 220),
                StartPosition = FormStartPosition.CenterParent,
                Font = Font,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
            })
            {
                var info = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 50,
                    Padding = new Padding(8),
                    Text = "Copied to the clipboard. Add this line to the server's authorized keys for the savecloud user " +
                           "(see docs/SERVER_SETUP.md, \"Adding a PC\"), then click Test connection.",
                };
                var box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Text = pub, WordWrap = true };
                f.Controls.Add(box);
                f.Controls.Add(info);
                f.ShowDialog(this);
            }
        }

        private async Task TestConnection(Button button)
        {
            button.Enabled = false;
            string pinnedBefore = _pinned;
            string seen = null;
            try
            {
                var t = new SftpTransport(_host.Text.Trim(), (int)_port.Value, _user.Text.Trim(),
                    string.IsNullOrWhiteSpace(_keyPath.Text) ? null : _keyPath.Text.Trim(),
                    _passphrase.Text, _password.Text, _remoteRoot.Text.Trim(), _pinned);
                t.HostKeyFirstSeen += fp => seen = fp;
                await Task.Run(() =>
                {
                    using (var store = new RemoteStore(t))
                    {
                        store.Connect();
                        store.ReadAllHeads();
                    }
                });

                if (seen != null)
                {
                    var answer = MessageBox.Show(this,
                        $"Connected. The server identifies itself as:\n\nSHA256:{seen}\n\n" +
                        "Does this match the fingerprint from your server setup? If yes, SaveCloud will only ever talk to this server.",
                        "Confirm server identity", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer == DialogResult.Yes) _pinned = seen;
                    else _pinned = pinnedBefore;
                }
                else
                {
                    MessageBox.Show(this, "Connected successfully.", "Test connection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Connection failed:\n\n" + ex.Message, "Test connection", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateFingerprint();
                button.Enabled = true;
            }
        }

        private bool Commit()
        {
            string key = _keyPath.Text.Trim();
            if (key.Length > 0 && !File.Exists(key))
            {
                MessageBox.Show(this, "The SSH key file doesn't exist.", "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            bool serverChanged = _config.Host != _host.Text.Trim() || _config.Port != (int)_port.Value;
            _config.Host = _host.Text.Trim();
            _config.Port = (int)_port.Value;
            _config.Username = _user.Text.Trim();
            _config.RemoteRoot = string.IsNullOrWhiteSpace(_remoteRoot.Text) ? "/data" : _remoteRoot.Text.Trim();
            _config.PrivateKeyPath = key;
            _config.PrivateKeyPassphraseProtected = Secrets.Protect(_passphrase.Text);
            _config.PasswordProtected = Secrets.Protect(_password.Text);
            // A different server address means a different host key; the old pin would just block it.
            _config.HostKeyFingerprint = serverChanged && _pinned == _config.HostKeyFingerprint ? "" : _pinned ?? "";
            _config.ExtraRoots = _rootList.ToList();
            _config.IgnoredGames = _ignored.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
            _config.SkipSteamCloudGames = _skipSteam.Checked;
            _config.IncludeConfigFiles = _config_.Checked;
            _config.MaxGameSizeMB = (int)_maxSize.Value;
            _config.HistoryToKeep = (int)_history.Value;
            _config.StartWithWindows = _startup.Checked;
            return true;
        }
    }
}
