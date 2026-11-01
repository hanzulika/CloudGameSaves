# SaveCloud

Keeps your PC game saves in sync across computers through your own server, like Steam Cloud but for any game.

- **No setup per PC.** Install, and it finds your server through Tailscale and signs in with your Tailscale
  account. No addresses, passwords or keys.
- **Finds games automatically** using the [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
  (save locations for ~22,000 Windows games, sourced from PCGamingWiki). Steam, Epic, GOG, Program Files and
  `X:\Games` libraries are detected; other folders can be added in Settings.
- **At sign-in** it downloads saves that another PC uploaded since last time. Unchanged games aren't downloaded again.
- **When a game closes** it uploads that game's saves. A background check every 5 minutes catches games it
  couldn't track.
- **Never silently overwrites.** If a game was played on two PCs since the last sync, you pick which save to keep.
  Local files are zipped to `%LocalAppData%\SaveCloud\Backups` before anything is replaced, and the server keeps
  the last 10 versions of every game.
- **Portable paths.** Saves restore correctly on a PC with a different username, a OneDrive Documents folder, or
  the game on another drive.

## Using it

1. Install [Tailscale](https://tailscale.com/download/windows) and sign in with an account that can see the server.
2. Download **SaveCloudSetup.exe** from the
   [latest release](https://github.com/hanzulika/CloudGameSaves/releases/latest) and run it (no admin needed).
3. That's it. If the window says *Waiting for approval*, send the login it shows to the server owner.

Start with the PC that has your **newest** saves. On other PCs, games whose saves differ show up once as
"Choose a save".

SaveCloud starts with Windows and lives in the system tray. Closing the window keeps it syncing.

## Server

A small Python service on a Proxmox container, reachable only over Tailscale: see
[server/DEPLOY.md](server/DEPLOY.md) (and [docs/SERVER_SETUP.md](docs/SERVER_SETUP.md) for the container itself).
Access is controlled by an allowlist of Tailscale logins. Saves are currently **shared by everyone on the
allowlist** (`--mode shared`); `--mode per-user` gives each login its own.

## Known limits

- **Registry saves, Xbox/Game Pass (UWP) games**, and games missing from the manifest aren't synced.
- **Steam Cloud games installed through Steam are skipped by default** so the two systems don't fight. This can
  be changed in Settings.
- **Settings files (graphics, keybinds) aren't synced by default**, since they're usually PC-specific.
- **Shared mode:** if two people play the same game, their saves collide. It's flagged as a choice, never
  silently merged.
- **Starting a game before the sign-in sync finishes** may load the older save; the next upload then becomes a
  choice instead of overwriting.
- The window needs the WebView2 runtime (built into Windows 11 and current Windows 10). Syncing works without it.

## Development

```bash
dotnet test SaveCloud.Tests                      # .NET 8, any OS; includes end-to-end tests against the Python server
python3 -m unittest discover -s server/tests     # server tests
dotnet build SaveCloud/SaveCloud.csproj -c Release
```

The UI is plain HTML/CSS/JS in `SaveCloud/ui/`. Open `index.html` in a browser to see it with sample data
(`?state=notAuthorized`, `?state=tailscaleMissing`, … for the setup screens).

CI builds and tests on Windows, builds the installer, and publishes a release for every push to `master`.

```
SaveCloud/Core/      platform-independent logic (manifest, scanning, sync, server client), unit-tested
SaveCloud/Platform/  Windows specifics: known folders, game libraries, Tailscale, process monitor, startup
SaveCloud/UI/        tray app, WebView2 window;  SaveCloud/ui/  the HTML interface
server/              the Python server, systemd unit, installer, tests
installer/           Inno Setup script for SaveCloudSetup.exe
```
