# SaveCloud

Keeps your PC game saves in sync across computers through your own server, like Steam Cloud but for any game.

- **Finds games automatically** using the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
  (save locations for ~22,000 Windows games, sourced from PCGamingWiki). Steam, Epic, GOG, Program Files and
  `X:\Games` libraries are detected; other game folders can be added in Settings.
- **At sign-in** it downloads saves that another PC uploaded since last time. Unchanged games aren't downloaded again.
- **When a game closes** it uploads that game's saves. A 5-minute background check catches games whose
  process it couldn't track.
- **Never silently overwrites.** If a game's saves changed on this PC *and* on the server since the last sync,
  it's flagged as a conflict and you pick which copy to keep. Before anything local is replaced, the current files
  are zipped to `%LocalAppData%\SaveCloud\Backups`. The server keeps the last 10 versions of every game.
- **Portable paths.** Saves are stored as `<winDocuments>/My Games/…` and similar, so they restore correctly on a
  PC with a different username, a OneDrive-redirected Documents folder, or the game on another drive.

## Setup

1. **Server:** follow [docs/SERVER_SETUP.md](docs/SERVER_SETUP.md) (a small SFTP-only container on Proxmox).
2. **Each PC:** build or download `SaveCloud.exe` (see below), run it, then open **Settings**:
   - Server address, port, username `savecloud`, server folder `/data`
   - **Generate key…**, then add the shown public key on the server (SERVER_SETUP.md, step 7)
   - **Test connection**, and check that the fingerprint matches the one from server setup
3. Start with the PC that has your **newest** saves: it uploads everything. On every other PC, games whose local
   saves differ show up as conflicts the first time. Use **Use server's save** for those (the local copies are
   backed up first).

SaveCloud adds itself to Windows startup (per user, no admin) and runs in the system tray. Double-click the tray
icon for the status window.

## Requirements

Windows 10 22H2 or Windows 11 (.NET Framework 4.8.1 is built in). Key generation uses the built-in OpenSSH Client
(Settings → System → Optional features).

## Build

```bash
dotnet build SaveCloud/SaveCloud.csproj -c Release
```

Output: `SaveCloud/bin/Release/net481/`. Copy the whole folder. Each push to GitHub also builds on Windows and
attaches the folder as a workflow artifact.

Tests (any OS):

```bash
dotnet test SaveCloud.Tests
```

SFTP integration tests run when `SAVECLOUD_SFTP_HOST`, `SAVECLOUD_SFTP_PORT`, `SAVECLOUD_SFTP_USER` and
`SAVECLOUD_SFTP_KEY` point at a server configured like SERVER_SETUP.md.

## Known limits

- **Registry saves are not synced.** A few (mostly older) games store saves in the registry.
- **Xbox / Game Pass (UWP) games** keep saves in protected containers that can't be copied.
- **Steam Cloud games installed through Steam are skipped by default**, so the two sync systems don't fight over
  the same files. Turn this off in Settings if you want both.
- **Settings files (graphics, keybinds) are not synced by default.** They're usually specific to each PC's hardware.
- **Both PCs on at the same time:** the other PC picks up a new save within 15 minutes, or immediately with
  **Sync now**. Don't play the same game on two PCs at once; that will produce a conflict (which is safe, but needs a choice).
- **Starting a game before the sign-in sync finishes** means it may load the older save. The next upload is then
  flagged as a conflict instead of overwriting the newer server copy.
- Games the manifest doesn't know aren't detected yet.

## Layout

```
SaveCloud/Core/      platform-independent logic (manifest, scanning, sync protocol), unit-tested on .NET 8
SaveCloud/Platform/  Windows specifics: known folders, Steam/Epic/GOG discovery, process monitor, DPAPI, startup
SaveCloud/UI/        tray app, status window, settings
SaveCloud.Tests/     tests: two simulated PCs sharing one server, path expansion, SFTP integration
docs/                server setup
```
