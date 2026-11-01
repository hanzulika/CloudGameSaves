using SaveCloud.Core;
using SaveCloud.Core.Sync;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SaveCloud.UI
{
    /// <summary>Lists every game with its sync status, and is where conflicts get resolved.</summary>
    public sealed class StatusForm : Form
    {
        private readonly Func<SyncEngine> _engine;
        private readonly Action<string, bool> _resolve;
        private readonly Action<string> _ignore;
        private readonly ListView _list;
        private readonly TextBox _log;
        private readonly Label _busy;
        private readonly Button _keepLocal, _useServer, _ignoreBtn;

        public StatusForm(Func<SyncEngine> engine, Action syncNow, Action<string, bool> resolve, Action<string> ignore)
        {
            _engine = engine;
            _resolve = resolve;
            _ignore = ignore;

            Text = "SaveCloud";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Size = new Size(980, 640);
            MinimumSize = new Size(700, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
            };
            _list.Columns.Add("Game", 300);
            _list.Columns.Add("Status", 260);
            _list.Columns.Add("Local saves", 110);
            _list.Columns.Add("Server", 170);
            _list.Columns.Add("Last sync", 120);
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4), WrapContents = false };
            var syncBtn = new Button { Text = "Sync now", AutoSize = true };
            syncBtn.Click += (s, e) => syncNow();
            _keepLocal = new Button { Text = "Keep this PC's save", AutoSize = true, Enabled = false };
            _keepLocal.Click += (s, e) => Resolve(true);
            _useServer = new Button { Text = "Use server's save", AutoSize = true, Enabled = false };
            _useServer.Click += (s, e) => Resolve(false);
            _ignoreBtn = new Button { Text = "Never sync this game", AutoSize = true, Enabled = false };
            _ignoreBtn.Click += (s, e) => Ignore();
            var backups = new Button { Text = "Open local backups", AutoSize = true };
            backups.Click += (s, e) => Process.Start("explorer.exe", AppPaths.BackupDir);
            _busy = new Label { AutoSize = true, Padding = new Padding(8, 8, 0, 0), ForeColor = SystemColors.GrayText };
            buttons.Controls.AddRange(new Control[] { syncBtn, _keepLocal, _useServer, _ignoreBtn, backups, _busy });

            _log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 8.5f),
                WordWrap = false,
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);
            Controls.Add(buttons);
            Load += (s, e) => split.SplitterDistance = (int)(ClientSize.Height * 0.62);

            LoadLogTail();
            Logger.LineWritten += OnLogLine;
            FormClosed += (s, e) => Logger.LineWritten -= OnLogLine;

            RefreshGames();
        }

        public void SetBusy(string what) => _busy.Text = what == null ? "" : what + "…";

        public void SelectGame(string game)
        {
            foreach (ListViewItem item in _list.Items)
                if ((string)item.Tag == game)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                    _list.Focus();
                    return;
                }
        }

        public void RefreshGames()
        {
            var engine = _engine();
            if (engine == null || IsDisposed) return;

            string selected = _list.SelectedItems.Count > 0 ? (string)_list.SelectedItems[0].Tag : null;
            var games = engine.Snapshot();

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var g in games)
            {
                var item = new ListViewItem(g.Name) { Tag = g.Name };
                item.SubItems.Add(StatusText(g));
                item.SubItems.Add(g.LocalFiles == 0 ? (g.Installed ? "none yet" : "–") : $"{g.LocalFiles} files, {FormatSize(g.LocalBytes)}");
                item.SubItems.Add(g.ServerRevision == null ? "not uploaded" : $"rev {g.ServerRevision} from {g.ServerFrom}");
                item.SubItems.Add(g.State?.LastSyncUtc?.ToLocalTime().ToString("g") ?? "never");
                if (g.State?.Conflict != null) item.BackColor = Color.FromArgb(255, 236, 200);
                else if (g.LastResult?.Action == SyncAction.Error) item.BackColor = Color.FromArgb(255, 215, 215);
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            if (selected != null) SelectGame(selected);
            Text = $"SaveCloud – {games.Count} games, {games.Count(g => g.State?.Conflict != null)} conflicts";
            UpdateButtons();
        }

        private static string StatusText(SyncEngine.GameView g)
        {
            if (g.State?.Conflict != null)
            {
                var c = g.State.Conflict;
                string newer = c.LocalNewestUtc > c.RemoteNewestUtc ? "this PC's files are newer" : $"{c.RemoteMachine}'s files are newer";
                return $"CONFLICT – {newer}";
            }
            if (g.LastResult != null && g.LastResult.Action != SyncAction.InSync)
                return g.LastResult.Action + (string.IsNullOrEmpty(g.LastResult.Message) ? "" : ": " + g.LastResult.Message);
            if (g.LastResult?.Action == SyncAction.InSync || g.State?.LastSyncUtc != null) return "Up to date";
            return "Not synced yet";
        }

        private static string FormatSize(long bytes)
            => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

        private string SelectedGame => _list.SelectedItems.Count > 0 ? (string)_list.SelectedItems[0].Tag : null;

        private void UpdateButtons()
        {
            var game = SelectedGame;
            var view = game == null ? null : _engine()?.Snapshot().FirstOrDefault(g => g.Name == game);
            bool conflict = view?.State?.Conflict != null;
            _keepLocal.Enabled = conflict && view.LocalFiles > 0;
            _useServer.Enabled = conflict && view.ServerRevision != null;
            _ignoreBtn.Enabled = game != null;
        }

        private void Resolve(bool keepLocal)
        {
            var game = SelectedGame;
            if (game == null) return;
            var view = _engine()?.Snapshot().FirstOrDefault(g => g.Name == game);
            var c = view?.State?.Conflict;
            string detail = c == null ? "" :
                $"\n\nThis PC: last saved {c.LocalNewestUtc.ToLocalTime():g}\n{c.RemoteMachine}: last saved {c.RemoteNewestUtc.ToLocalTime():g}";
            string question = keepLocal
                ? $"Upload this PC's save of \"{game}\" and make it the newest version?\n\nThe server keeps the other version in its history.{detail}"
                : $"Replace this PC's save of \"{game}\" with the server's?\n\nThis PC's current files are first backed up to the local backups folder.{detail}";
            if (MessageBox.Show(this, question, "Resolve conflict", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _resolve(game, keepLocal);
        }

        private void Ignore()
        {
            var game = SelectedGame;
            if (game == null) return;
            if (MessageBox.Show(this, $"Stop syncing \"{game}\" on this PC? Nothing is deleted; you can undo this in Settings.",
                    "SaveCloud", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _ignore(game);
        }

        private void LoadLogTail()
        {
            try
            {
                if (!File.Exists(Logger.LogFilePath)) return;
                using (var fs = new FileStream(Logger.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                {
                    var lines = r.ReadToEnd().Split('\n');
                    _log.Text = string.Join("\r\n", lines.Skip(Math.Max(0, lines.Length - 300)).Select(l => l.TrimEnd('\r')));
                }
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
            }
            catch (IOException) { }
        }

        private void OnLogLine(string line)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                if (_log.TextLength > 200_000) _log.Text = _log.Text.Substring(100_000);
                _log.AppendText(line + "\r\n");
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window just hides it; the app keeps running in the tray.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
