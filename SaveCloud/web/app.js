"use strict";

// ---- bridge -------------------------------------------------------------------------------------------
const host = window.chrome && window.chrome.webview;
const send = (cmd, args = {}) => host ? host.postMessage({ cmd, ...args }) : mockCommand(cmd, args);

let state = null;
let filter = "all";
let query = "";
let setupDismissed = false;

if (host) {
  host.addEventListener("message", e => handle(e.data));
  send("ready");
} else {
  setTimeout(() => handle({ type: "state", data: mockState() }), 50);
}

function handle(msg) {
  if (!msg) return;
  if (msg.type === "state") { state = msg.data; render(); }
  else if (msg.type === "toast") toast(msg.text);
}

// ---- helpers -------------------------------------------------------------------------------------------
const $ = id => document.getElementById(id);
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const icon = (name, cls = "ico") => `<svg class="${cls}" viewBox="0 0 24 24"><use href="#${name}"/></svg>`;

function ago(iso) {
  if (!iso) return "";
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 45) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.round(s / 86400)} d ago`;
  return new Date(iso).toLocaleDateString();
}
function when(iso) {
  if (!iso) return "unknown";
  const d = new Date(iso);
  return `${d.toLocaleDateString(undefined, { day: "numeric", month: "short" })}, ${d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })}`;
}
function size(bytes) {
  if (!bytes) return "";
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / 1048576).toFixed(bytes < 10485760 ? 1 : 0)} MB`;
}
function hue(name) {
  let h = 0;
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return h % 360;
}
function initials(name) {
  const words = name.replace(/[^\p{L}\p{N} ]/gu, " ").split(/\s+/).filter(w => w && !/^(the|of|a|and)$/i.test(w));
  return (words.slice(0, 2).map(w => w[0]).join("") || name.slice(0, 2)).toUpperCase();
}

// ---- status model ----------------------------------------------------------------------------------------
const KIND = {
  synced:       { badge: "ok",    icon: "i-check",    label: "Synced",          group: "synced" },
  conflict:     { badge: "warn",  icon: "i-alert",    label: "Choose a save",   group: "attention" },
  error:        { badge: "bad",   icon: "i-alert",    label: "Sync failed",     group: "attention" },
  missing:      { badge: "warn",  icon: "i-alert",    label: "Saves missing",   group: "attention" },
  running:      { badge: "play",  icon: "i-play",     label: "Playing",         group: "waiting" },
  notInstalled: { badge: "muted", icon: "i-pc",       label: "Not installed",   group: "waiting" },
  waiting:      { badge: "info",  icon: "i-sync",     label: "Waiting",         group: "waiting" },
  pending:      { badge: "info",  icon: "i-cloud-up", label: "Not uploaded yet", group: "waiting" },
  steamCloud:   { badge: "muted", icon: "i-cloud-up", label: "Steam Cloud",     group: "skipped" },
  tooLarge:     { badge: "muted", icon: "i-alert",    label: "Too large",       group: "skipped" },
};
const kindOf = g => KIND[g.kind] || KIND.waiting;

function metaLine(g) {
  switch (g.kind) {
    case "synced": {
      const parts = [];
      if (g.serverRev) parts.push(`v${g.serverRev}`);
      if (g.serverFrom) parts.push(`from ${esc(g.serverFrom)}${g.serverLogin && state.connection.mode === "shared" && g.serverLogin !== state.connection.login ? ` (${esc(g.serverLogin.split("@")[0])})` : ""}`);
      if (g.serverUtc) parts.push(ago(g.serverUtc));
      return parts.join(` <span class="sep">·</span> `);
    }
    case "conflict": return "Changed on this PC and on another one";
    case "running": return "Uploads when you close the game";
    case "notInstalled": return `Install the game here to get its saves${g.serverRev ? ` (v${g.serverRev} on server)` : ""}`;
    default: return esc(g.message || "");
  }
}

// ---- render ------------------------------------------------------------------------------------------------
function render() {
  if (!state) return;
  renderHeader();
  renderSetup();
  renderBanner();
  renderChips();
  renderGrid();
  renderSettings();
}

const CONN = {
  connected:        { cls: "connected", text: c => `Connected${c.login ? " · " + c.login : ""}` },
  searching:        { cls: "searching", text: () => "Looking for your server…" },
  tailscaleMissing: { cls: "bad",  text: () => "Tailscale isn't installed" },
  tailscaleOff:     { cls: "warn", text: () => "Tailscale is turned off" },
  tailscaleLogin:   { cls: "warn", text: () => "Sign in to Tailscale" },
  notFound:         { cls: "bad",  text: () => "Server not found" },
  notAuthorized:    { cls: "warn", text: () => "Waiting for approval" },
  offline:          { cls: "warn", text: () => "Server offline · retrying" },
};

function renderHeader() {
  const c = state.connection;
  const info = CONN[c.state] || CONN.searching;
  const conn = $("conn");
  conn.dataset.state = info.cls;
  $("connText").textContent = info.text(c);
  conn.title = c.server ? `Server: ${c.server}` : (c.message || "");

  const busy = !!state.busy;
  $("syncBtn").classList.toggle("busy", busy);
  $("syncBtn").disabled = busy || c.state !== "connected";
  $("syncLabel").textContent = busy ? state.busy : "Sync now";
  $("lastSync").textContent = state.lastSyncUtc ? `Last synced ${ago(state.lastSyncUtc)}` : "";
}

function renderSetup() {
  const c = state.connection;
  const show = c.state !== "connected" && !setupDismissed && !state.everConnected;
  $("setup").hidden = !show;
  if (!show) return;

  const order = ["tailscaleMissing", "tailscaleOff", "tailscaleLogin", "searching", "notFound", "offline", "notAuthorized", "connected"];
  const at = order.indexOf(c.state);
  const step = (title, sub, done, now, fail) =>
    `<li class="${done ? "done" : fail ? "fail" : now ? "now" : "todo"}"><span class="state">${done ? icon("i-check") : fail ? "!" : ""}</span><span><b>${title}</b><small>${sub}</small></span></li>`;

  const tsDone = at > 0;
  const loginDone = at > 2;
  const foundDone = at >= 6;
  $("steps").innerHTML = [
    step("Tailscale is installed", "Connects this PC to your save server privately, from anywhere.", tsDone, false, c.state === "tailscaleMissing"),
    step("Tailscale is on and signed in", c.tsLogin ? `Signed in as ${esc(c.tsLogin)}` : "Use the account the server was shared with.", loginDone, false, at === 1 || at === 2),
    step("Found your SaveCloud server", foundDone && c.server ? esc(c.server) : c.state === "notFound" ? "No server answered. Ask the owner to share the “savecloud” device with you." : c.state === "offline" ? "The server didn't answer; it may be restarting." : "Searching your Tailscale network…", foundDone, c.state === "searching", c.state === "notFound" || c.state === "offline"),
    step("Access approved", c.state === "notAuthorized" ? "The owner needs to add your login to the server." : "Your login is on the server's list.", at >= 7, false, c.state === "notAuthorized"),
  ].join("");

  const titles = {
    tailscaleMissing: ["Install Tailscale to start", "SaveCloud reaches your server through Tailscale. It's free and takes a minute."],
    tailscaleOff: ["Turn Tailscale on", "Open Tailscale from the system tray and click Connect."],
    tailscaleLogin: ["Sign in to Tailscale", "Open Tailscale and sign in. SaveCloud continues by itself."],
    searching: ["Finding your server", "This only takes a moment."],
    notFound: ["Can't find a SaveCloud server", "Make sure Tailscale is connected and the server was shared with your account."],
    offline: ["Server isn't answering", "SaveCloud keeps retrying in the background."],
    notAuthorized: ["Almost there", "Send your login to the server owner. Once they add it, this window closes by itself."],
  };
  const [t, lead] = titles[c.state] || titles.searching;
  $("setupTitle").textContent = t;
  $("setupLead").textContent = lead;

  const a = [];
  if (c.state === "tailscaleMissing") a.push(`<button class="btn primary" data-cmd="installTailscale">Download Tailscale</button>`);
  if (c.state === "tailscaleOff" || c.state === "tailscaleLogin") a.push(`<button class="btn primary" data-cmd="openTailscale">Open Tailscale</button>`);
  if (c.state === "notAuthorized" && c.login) a.push(`<span class="copy">${esc(c.login)}<button class="btn small" data-copy="${esc(c.login)}">Copy</button></span>`);
  if (c.state !== "searching") a.push(`<button class="btn" data-cmd="retry">${icon("i-sync")}Try again</button>`);
  $("setupActions").innerHTML = a.join("");
}

function renderBanner() {
  const conflicts = state.games.filter(g => g.kind === "conflict").length;
  const errors = state.games.filter(g => g.kind === "error").length;
  const b = $("banner");
  if (conflicts) {
    b.className = "banner";
    b.innerHTML = `${icon("i-alert")}<span class="grow"><b>${conflicts} ${conflicts === 1 ? "game was" : "games were"} played on two PCs.</b> Pick which save to keep; nothing is overwritten until you do.</span><button class="btn warn small" data-filter="attention">Show</button>`;
  } else if (errors) {
    b.className = "banner bad";
    b.innerHTML = `${icon("i-alert")}<span class="grow"><b>${errors} ${errors === 1 ? "game" : "games"} couldn't sync.</b> SaveCloud will retry automatically.</span><button class="btn small" data-filter="attention">Show</button>`;
  } else if (state.games.some(g => g.kind === "steamCloud") && state.settings?.SkipSteamCloudGames) {
    const n = state.games.filter(g => g.kind === "steamCloud").length;
    b.className = "banner info";
    b.innerHTML = `${icon("i-cloud-up")}<span class="grow"><b>${n} Steam ${n === 1 ? "game is" : "games are"} left to Steam Cloud.</b> SaveCloud can sync ${n === 1 ? "it" : "them"} too. Steam may then occasionally ask which save to keep when you launch a game.</span><button class="btn small" data-filter="skipped">See them</button><button class="btn primary small" data-sync-steam="1">Sync them too</button>`;
  } else if (state.connection.state === "notAuthorized" && state.everConnected) {
    b.className = "banner";
    b.innerHTML = `${icon("i-alert")}<span class="grow">This server no longer accepts <b>${esc(state.connection.login)}</b>. Ask the owner to add it back.</span>`;
  } else {
    b.innerHTML = "";
  }
  b.hidden = !b.innerHTML;
}

function counts() {
  const c = { all: state.games.length, attention: 0, synced: 0, waiting: 0, skipped: 0 };
  for (const g of state.games) c[kindOf(g).group]++;
  return c;
}

function renderChips() {
  const c = counts();
  const chips = [["all", "All"], ["attention", "Needs you"], ["synced", "Synced"], ["waiting", "Waiting"], ["skipped", "Not synced"]];
  $("chips").innerHTML = chips
    .filter(([k]) => k === "all" || c[k] || filter === k)
    .map(([k, label]) => `<button class="chip${k === "attention" ? " attn" : ""}" role="tab" aria-selected="${filter === k}" data-filter="${k}">${label}<span class="n">${c[k]}</span></button>`)
    .join("");
}

function visibleGames() {
  const q = query.trim().toLowerCase();
  const rank = g => ({ attention: 0, synced: 1, waiting: 2, skipped: 3 }[kindOf(g).group]);
  return state.games
    .filter(g => filter === "all" || kindOf(g).group === filter)
    .filter(g => !q || g.name.toLowerCase().includes(q))
    .sort((a, b) => rank(a) - rank(b) || (b.activityUtc || "").localeCompare(a.activityUtc || "") || a.name.localeCompare(b.name));
}

function cover(g) {
  const h = hue(g.name);
  const art = `<div class="art" style="background:linear-gradient(135deg,hsl(${h} 38% 30%),hsl(${(h + 40) % 360} 45% 16%))">${esc(initials(g.name))}</div>`;
  const img = g.steamId ? `<img loading="lazy" alt="" src="https://cdn.cloudflare.steamstatic.com/steam/apps/${g.steamId}/header.jpg" onerror="this.remove()">` : "";
  return art + img;
}

function card(g) {
  const k = kindOf(g);
  const details = [];
  if (g.localFiles) details.push(`${g.localFiles} ${g.localFiles === 1 ? "file" : "files"}`, size(g.localBytes));
  let extra = "";
  if (g.kind === "steamCloud") {
    extra = `<div class="choice one"><button data-also="${esc(g.name)}"><b>Also sync with SaveCloud</b><small>For copies not bought on Steam, or PCs without Steam</small></button></div>`;
  }
  if (g.kind === "conflict" && g.conflict) {
    const localNewer = new Date(g.conflict.localUtc) > new Date(g.conflict.remoteUtc);
    extra = `<div class="choice">
      <button data-resolve="local" data-game="${esc(g.name)}"><b>This PC</b><small>Saved ${when(g.conflict.localUtc)}</small>${localNewer ? `<small class="newer">Newer</small>` : ""}</button>
      <button data-resolve="server" data-game="${esc(g.name)}"><b>${esc(g.conflict.remoteMachine || "Server")}</b><small>Saved ${when(g.conflict.remoteUtc)}</small>${!localNewer ? `<small class="newer">Newer</small>` : ""}</button>
    </div>`;
  }
  return `<article class="card ${esc(g.kind)}" data-game="${esc(g.name)}">
    <div class="cover">${cover(g)}<span class="badge ${k.badge}">${icon(k.icon)}${k.label}</span>
      <button class="more" title="More" data-menu="${esc(g.name)}">${icon("i-more")}</button></div>
    <div class="body">
      <div class="title" title="${esc(g.name)}">${esc(g.name)}</div>
      <div class="meta">${metaLine(g)}</div>
      ${details.length ? `<div class="meta">${details.join(` <span class="sep">·</span> `)}</div>` : ""}
      ${extra}
    </div>
  </article>`;
}

function renderGrid() {
  const grid = $("grid");
  const games = visibleGames();
  const empty = $("empty");
  if (!state.games.length && state.scanning) {
    grid.innerHTML = Array.from({ length: 8 }, () => `<div class="skeleton"></div>`).join("");
    empty.hidden = true;
    return;
  }
  if (!games.length) {
    grid.innerHTML = "";
    empty.hidden = false;
    empty.innerHTML = state.games.length
      ? `<div><h2>Nothing here</h2><p>No games match this filter.</p></div>`
      : `<div><svg class="logo big" viewBox="0 0 64 64"><use href="#logo"/></svg><h2>No game saves found yet</h2><p>SaveCloud looks for ~22,000 games in Steam, Epic, GOG and common game folders.<br>Installed somewhere else? Add the folder in Settings.</p></div>`;
    return;
  }
  empty.hidden = true;
  const top = grid.scrollTop;
  grid.innerHTML = games.map(card).join("");
  grid.scrollTop = top;
}

function renderSettings() {
  const s = state.settings || {};
  document.querySelectorAll("[data-setting]").forEach(el => {
    if (document.activeElement === el) return;
    if (el.type === "checkbox") el.checked = !!s[el.dataset.setting];
    else el.value = s[el.dataset.setting] ?? "";
  });
  if (document.activeElement !== $("serverUrl")) $("serverUrl").value = s.ServerUrl || "";

  const roots = s.ExtraRoots || [];
  $("roots").innerHTML = roots.length
    ? roots.map(r => `<li><span title="${esc(r)}">${esc(r)}</span><button class="btn small" data-unroot="${esc(r)}">Remove</button></li>`).join("")
    : `<li class="none">No extra folders</li>`;
  const ign = s.IgnoredGames || [];
  $("ignored").innerHTML = ign.length
    ? ign.map(n => `<li><span>${esc(n)}</span><button class="btn small" data-unignore="${esc(n)}">Sync again</button></li>`).join("")
    : `<li class="none">Every detected game is synced</li>`;

  const c = state.connection;
  const rows = [
    ["Status", (CONN[c.state] || CONN.searching).text(c)],
    ["Server", c.server || "—"],
    ["Signed in as", c.login || c.tsLogin || "—"],
    ["Saves", c.mode === "per-user" ? "Private to your login" : c.mode === "shared" ? "Shared by everyone on this server" : "—"],
  ];
  $("serverInfo").innerHTML = rows.map(([k, v]) => `<dt>${k}</dt><dd>${esc(v)}</dd>`).join("");
  $("version").textContent = state.version ? `SaveCloud ${state.version} · ${state.machine || ""}` : "";
}

// ---- interactions -------------------------------------------------------------------------------------------
document.addEventListener("click", e => {
  const t = e.target.closest("button, [data-filter], .conn");
  closeMenu(e);
  if (!t) return;
  if (t.dataset.filter) { filter = t.dataset.filter; renderChips(); renderGrid(); return; }
  if (t.dataset.cmd) { send(t.dataset.cmd); if (t.dataset.cmd === "retry") setupDismissed = false; return; }
  if (t.dataset.copy) { copy(t.dataset.copy); return; }
  if (t.dataset.resolve) { confirmResolve(t.dataset.game, t.dataset.resolve === "local"); return; }
  if (t.dataset.menu) { openMenu(t, t.dataset.menu); e.stopPropagation(); return; }
  if (t.dataset.unroot) { send("removeRoot", { path: t.dataset.unroot }); return; }
  if (t.dataset.unignore) { send("unignore", { game: t.dataset.unignore }); return; }
  if (t.dataset.also) { send("alsoSync", { game: t.dataset.also }); return; }
  if (t.dataset.syncSteam) { send("setSetting", { key: "SkipSteamCloudGames", value: false }); toast("Adding Steam Cloud games…"); return; }
  switch (t.id) {
    case "syncBtn": send("syncNow"); break;
    case "settingsBtn": case "conn": openDrawer(true); break;
    case "closeDrawer": openDrawer(false); break;
    case "addRoot": send("addRoot"); break;
    case "setupDismiss": setupDismissed = true; renderSetup(); break;
  }
});
$("scrim").addEventListener("click", () => openDrawer(false));
$("search").addEventListener("input", e => { query = e.target.value; renderGrid(); });
document.addEventListener("keydown", e => {
  if (e.key === "Escape") { openDrawer(false); closeDialog(false); closeMenu(); }
  if ((e.ctrlKey && e.key === "f") || (e.key === "/" && document.activeElement.tagName !== "INPUT")) { e.preventDefault(); $("search").focus(); }
});
document.querySelectorAll("[data-setting]").forEach(el => el.addEventListener("change", () => {
  const v = el.type === "checkbox" ? el.checked : el.type === "number" ? Number(el.value) : el.value;
  send("setSetting", { key: el.dataset.setting, value: v });
}));
$("serverUrl").addEventListener("change", e => send("setSetting", { key: "ServerUrl", value: e.target.value.trim() }));

function openDrawer(open) {
  $("drawer").classList.toggle("open", open);
  $("drawer").setAttribute("aria-hidden", String(!open));
  $("scrim").hidden = !open;
}

let menuEl = null;
function openMenu(btn, game) {
  closeMenu();
  const g = state.games.find(x => x.name === game);
  if (!g) return;
  const r = btn.getBoundingClientRect();
  menuEl = document.createElement("div");
  menuEl.className = "menu";
  menuEl.innerHTML =
    (g.localFiles ? `<button data-act="openFolder">${icon("i-pc")}Open save folder</button>` : "") +
    (g.kind !== "conflict" && state.connection.state === "connected" ? `<button data-act="syncGame">${icon("i-sync")}Sync this game now</button>` : "") +
    `<button data-act="ignore">${icon("i-x")}Don't sync this game</button>`;
  document.body.appendChild(menuEl);
  const left = Math.min(r.right - 190, window.innerWidth - 200);
  menuEl.style.left = `${Math.max(8, left)}px`;
  menuEl.style.top = `${r.bottom + 6}px`;
  menuEl.addEventListener("click", ev => {
    const act = ev.target.closest("button")?.dataset.act;
    closeMenu();
    if (act === "openFolder") send("openFolder", { game });
    if (act === "syncGame") send("syncGame", { game });
    if (act === "ignore") confirm(`Stop syncing ${game}?`, `<p>Its saves stay where they are on this PC and on the server. You can turn it back on in Settings.</p>`, "Stop syncing")
      .then(ok => ok && send("ignore", { game }));
  });
}
function closeMenu(e) {
  if (menuEl && (!e || !menuEl.contains(e.target))) { menuEl.remove(); menuEl = null; }
}

let dialogResolve = null;
function confirm(title, bodyHtml, yes = "OK") {
  $("confirmTitle").textContent = title;
  $("confirmBody").innerHTML = bodyHtml;
  $("confirmYes").textContent = yes;
  $("confirm").hidden = false;
  $("confirmYes").focus();
  return new Promise(res => { dialogResolve = res; });
}
function closeDialog(result) {
  if (!dialogResolve) return;
  $("confirm").hidden = true;
  dialogResolve(result);
  dialogResolve = null;
}
$("confirmYes").addEventListener("click", () => closeDialog(true));
$("confirmNo").addEventListener("click", () => closeDialog(false));

function confirmResolve(game, keepLocal) {
  const g = state.games.find(x => x.name === game);
  const other = g?.conflict?.remoteMachine || "the other PC";
  const body = keepLocal
    ? `<p>This PC's save becomes the newest version everywhere. ${esc(other)}'s version stays in the server's history.</p>`
    : `<p>This PC gets ${esc(other)}'s save. This PC's current files are backed up first, so nothing is lost.</p>`;
  confirm(keepLocal ? `Keep this PC's save of ${game}?` : `Use ${other}'s save of ${game}?`, body, keepLocal ? "Keep this PC's" : `Use ${other}'s`)
    .then(ok => ok && send("resolve", { game, keepLocal }));
}

function copy(text) {
  navigator.clipboard?.writeText(text).then(() => toast("Copied"), () => send("copy", { text }));
  if (!navigator.clipboard) send("copy", { text });
}

let toastTimer;
function toast(text) {
  const t = $("toast");
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 2600);
}

setInterval(() => state && (renderHeader(), renderGrid()), 60000); // keep "x min ago" fresh

// ---- browser preview (no host) ---------------------------------------------------------------------------------
function mockState() {
  const p = new URLSearchParams(location.search);
  const conn = p.get("state") || "connected";
  const hrs = h => new Date(Date.now() - h * 3600e3).toISOString();
  return {
    version: "2.0 preview", machine: "HANZU", everConnected: conn === "connected", busy: p.get("busy"), scanning: false,
    lastSyncUtc: hrs(0.05),
    connection: { state: conn, login: "hanzuioan5@gmail.com", tsLogin: "hanzuioan5@gmail.com", server: conn === "connected" || conn === "notAuthorized" ? "http://100.99.12.7:8765" : "", mode: "shared" },
    settings: { StartWithWindows: true, SkipSteamCloudGames: true, IncludeConfigFiles: false, HistoryToKeep: 10, ServerUrl: "", ExtraRoots: ["D:\\Emulators\\Games"], IgnoredGames: ["Desktop Goose"] },
    games: [
      { name: "Hollow Knight", steamId: 367520, kind: "conflict", localFiles: 4, localBytes: 812000, serverRev: 14, serverFrom: "HANZULAPTOP", conflict: { localUtc: hrs(26), remoteUtc: hrs(2), remoteMachine: "HANZULAPTOP" }, activityUtc: hrs(2) },
      { name: "Elden Ring", steamId: 1245620, kind: "synced", localFiles: 2, localBytes: 28400000, serverRev: 31, serverFrom: "HANZU", serverUtc: hrs(3), activityUtc: hrs(3) },
      { name: "Stardew Valley", steamId: 413150, kind: "running", localFiles: 6, localBytes: 3100000, serverRev: 52, serverFrom: "HANZU", activityUtc: hrs(0.1) },
      { name: "Celeste", steamId: 504230, kind: "synced", localFiles: 3, localBytes: 96000, serverRev: 9, serverFrom: "HANZULAPTOP", serverLogin: "friend@gmail.com", serverUtc: hrs(30), activityUtc: hrs(30) },
      { name: "Hades II", steamId: 1145350, kind: "synced", localFiles: 5, localBytes: 4500000, serverRev: 18, serverFrom: "HANZU", serverUtc: hrs(50), activityUtc: hrs(50) },
      { name: "Grand Theft Auto: San Andreas", kind: "synced", localFiles: 8, localBytes: 1600000, serverRev: 3, serverFrom: "HANZU", serverUtc: hrs(200) },
      { name: "The Witcher 3: Wild Hunt", steamId: 292030, kind: "notInstalled", serverRev: 40 },
      { name: "Terraria", steamId: 105600, kind: "error", localFiles: 12, localBytes: 5400000, message: "Server error 507: server disk is full" },
      { name: "Undertale", steamId: 391540, kind: "pending", localFiles: 1, localBytes: 12000, message: "Uploads in a moment" },
      { name: "Baldur's Gate 3", steamId: 1086940, kind: "steamCloud", message: "Steam Cloud already syncs this game", localFiles: 14, localBytes: 210000000 },
      { name: "Cyberpunk 2077", steamId: 1091500, kind: "steamCloud", message: "Steam Cloud already syncs this game", localFiles: 40, localBytes: 96000000 },
      { name: "Minecraft Dungeons", kind: "synced", localFiles: 2, localBytes: 220000, serverRev: 6, serverFrom: "HANZULAPTOP", serverUtc: hrs(90) },
    ],
  };
}
function mockCommand(cmd, args) {
  if (cmd === "resolve") {
    const g = state.games.find(x => x.name === args.game);
    g.kind = "synced"; g.serverRev++; g.serverUtc = new Date().toISOString(); delete g.conflict;
    render(); toast(`${args.game}: ${args.keepLocal ? "uploaded this PC's save" : "restored the other save"}`);
  } else if (cmd === "syncNow") {
    state.busy = "Syncing"; render();
    setTimeout(() => { state.busy = null; state.lastSyncUtc = new Date().toISOString(); render(); toast("Everything is up to date"); }, 1500);
  } else toast(`(${cmd})`);
}
