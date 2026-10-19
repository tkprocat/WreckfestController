# Installing WreckfestController

This guide covers installing WreckfestController 2.0, connecting it to a Wreckfest
dedicated server, and opening its web site to admins and players.

## Requirements

- Windows 10 or 11, or Windows Server. The controller is a desktop app and runs in a
  signed-in session; it cannot run as a Windows service yet.
- The **x64** versions of the **.NET 10 Desktop Runtime** and the **ASP.NET Core 10
  Runtime**, from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0).
  The x64 .NET 10 SDK includes both. The x86 runtimes are not enough.
- The **Wreckfest dedicated server**, at the build the controller supports (see
  [The hook](#the-hook)).
- **SteamCMD** (optional), for updating the server from the controller.

Building from source needs more: see [README.md](README.md#building-from-source).

## 1. Install

Extract the release ZIP to a folder of its own, such as `C:\WreckfestController\`, and
run `WreckfestController.exe`. Keep the files together: the controller needs the hook DLL
next to the exe. The web site is a separate program, WreckfestPortal (step 4).

On first start it creates its data folder, `%LocalAppData%\WreckfestController\`, with:

| File | Contents |
| --- | --- |
| `controller.db` | Settings, the track catalogue, collections, cups and race results |
| `user-settings.json` | Startup settings that you write by hand (see step 3). The controller never writes this file. |
| `crashes\` | One file per crash, with the full error. Created on the first crash; the newest 20 are kept. |

If the database cannot be opened, the controller starts in **recovery mode**: the
desktop app shows why in a banner, and the API answers 503 to everything, saying so. Once
the cause is fixed, click **RETRY** in the banner, or restart the controller.

To keep the database somewhere else, set `Database:Path` in `user-settings.json` (step
3). The `crashes` folder does not follow it, so a crash is recorded even when the
database's folder cannot be written.

To back up the database, click **BACK UP NOW** on the **Configuration** tab. It writes a
consistent copy to the `backups` folder beside the database while the controller runs;
the controller also backs up there before each database upgrade. There is no backup
endpoint in the web API. The web accounts are the portal's (step 4), not in this database.

## 2. Connect the dedicated server

Open the **Configuration** tab in the desktop app and set:

- **Working Directory**: the dedicated server's folder.
- **Server Executable**: usually `Wreckfest_x64.exe`.
- **Log File Name** (optional): the log file shown on the web site's Server Control
  page when `server_config.cfg` has no usable `log=` line. The `log=` line wins whenever
  it names a file inside the server folder.
- **Server Arguments**: usually `-s server_config=server_config.cfg`.
- **SteamCMD Executable Path** (optional): `steamcmd.exe`, for the Update button.

These are saved in the database. They decide which program the controller runs and
which files it reads, so they can be changed only here, never from the web site.

The controller edits the server's `server_config.cfg`: the web site's Server Config page,
the rotation and cups all write to it. A setting can be changed from the web only when
the file already has an active `key=value` line for it, and the rotation and cups need
the file's `# Event Loop` heading. The file the dedicated server ships with has both.

### The hook

Everything the controller knows about a running server (console output, chat, players,
race results) comes from a hook DLL injected into the server process. Commands are sent
through the hook too. Nothing works until it is injected.

The controller injects the hook itself after every start and restart, a cup's scheduled
restart included, and reattaches to its server when the controller itself is restarted
while the server keeps running. The result of a start or restart says whether the hook
went in. If it did not, inject it by hand: in the desktop app's Process Manager tab,
select the server and click **INJECT**, or use **Inject hook** on the web site's Server
Control page.

The hook reads the game's memory at fixed offsets, which belong to one game build. The
supported build is `WreckfestServer:SupportedBuild` in `appsettings.json`, and the
controller refuses to inject into any other. After a Wreckfest update, wait for a
controller release that supports the new build.

## 3. Enable the API

The API is off by default. The web site (step 4) and scripts use it. To turn it on, create
`%LocalAppData%\WreckfestController\user-settings.json`:

```json
{
  "Api": {
    "Enabled": true,
    "Key": "",
    "AllowRemote": false,
    "TrustedProxies": ["127.0.0.1"],
    "HttpPort": 5100,
    "HttpsPort": 5101,
    "Portal": {
      "ServerId": "vanilla",
      "Secret": "<the same secret as this server has in the portal's settings>"
    }
  }
}
```

Startup settings are read from `appsettings.json` next to the exe, then
`appsettings.{Environment}.json` if there is one, then `user-settings.json`; a later file
wins. Keep yours in `user-settings.json`, because installing a new release replaces
`appsettings.json`. Restart the controller after any change to the file.

Only the startup settings come from these files: `Api`, `Database`, `Assistant`, `Logging`
and `WreckfestServer:SupportedBuild`. The server paths, SteamCMD and voting settings are in
the database, and the `WreckfestServer`, `SteamCmd` and `Vote` sections of
`user-settings.json` are ignored. On the very first start the database takes its voting
defaults from the shipped `appsettings.json` and `appsettings.{Environment}.json`.

| Setting | Meaning |
| --- | --- |
| `Enabled` | `true` starts the API. Anything else, including `"yes"` or a typo, leaves it off and opens no port. |
| `Key` | An API key for scripts, sent as the `X-Api-Key` header. Leave it empty if nothing but the web site will use the API. |
| `AllowRemote` | `false` listens on `127.0.0.1` only, so only this PC can connect, such as a portal on the same PC. `true` listens on every network interface. |
| `TrustedProxies` | The address of the portal (step 4): `127.0.0.1` when it runs on the same PC. An entry that is not an address or range is logged and skipped. |
| `HttpPort`, `HttpsPort` | Default 5100 and 5101, also when left empty. |
| `Portal:ServerId`, `Portal:Secret` | This server's `Id` and `Secret` in the portal's settings (step 4). The portal signs in the people who manage this server and vouches for them with a token signed with the secret; without these, only the `Key` signs in. Set both or neither; the secret needs at least 32 characters. |
| `Https` | A certificate, to serve HTTPS directly: see [docs/https.md](docs/https.md). |

The Configuration tab shows under **WEB API** whether the API is running and where, or
why it failed to start. A port that is not 1-65535, an `AllowRemote` that is not
`true` or `false`, and, when `Https` is set, the same port for HTTP and HTTPS or an
`Https` section that is incomplete or unusable stop the API rather than falling back to
a default or to plain HTTP.

### Several servers on one PC

Each controller manages one dedicated server. To run several, install each controller
in its own folder, and give each its own settings, database and ports. By default, every
controller run by the same Windows user shares `%LocalAppData%\WreckfestController`,
which would give them the same server paths and cups.

1. In each install's `appsettings.json`, point `UserSettingsPath` at a file of its own,
   such as `%LocalAppData%\WreckfestController\server2\user-settings.json`. Check this
   again after installing a new release, since the release replaces `appsettings.json`.
2. In that `user-settings.json`, set `Database:Path` to a database of its own, such as
   `%LocalAppData%\WreckfestController\server2\controller.db`, and give `Api` its own
   `HttpPort` and `HttpsPort`.
3. If the servers share one dedicated-server install folder, give each its own save
   folder: add `--save-dir=<folder>` to its **Server Arguments** on the Configuration
   tab. The folder must exist. Without it, the second server to start stops with "Save
   directory is already in use". Give each server its own `server_config.cfg` with its
   own `steam_port`, `game_port` and `query_port` as well.

The portal (step 4) then puts them all on one site, such as `example.com/vanilla/` and
`example.com/tvtp2/`.

Each controller adds its own id to the command line of every server it starts, such as
`-wfc_controller=3f9a1c07`. The id is shown on the Configuration tab under **WEB API**,
and comes from the database path, so it stays the same across restarts. It shows which
server belongs to which controller in Task Manager's Command line column. A controller
reattaches only to a server carrying its own id, and will not attach to or inject into
another controller's server; the desktop app's Process Manager asks first.

## 4. Set up the web site

The web site is **WreckfestPortal**, a separate small program in front of one or more
controllers. It serves a front page at `/` listing every server, with whether it is up, its
players and its track, and each controller's own site under a path of its own:
`/vanilla/`, `/tvtp2/`. The browser only ever talks to the portal, which passes each
site's API calls and live updates on to its controller. One domain, one certificate and
one proxy route serve them all, and adding a controller means adding it to the portal's
settings. You need the portal for a single controller too: the controller serves only the
API.

Build it from `Portal/WreckfestPortal`:

```
dotnet publish Portal/WreckfestPortal -c Release -o C:\WreckfestPortal
```

It needs the ASP.NET Core 10 Runtime. Keep the `wwwroot` and `app` folders next to the
exe: `wwwroot` is the front page and `app` the controllers' web app.

The portal also holds the web site's accounts, in `data\portal.db` next to the exe (with a
`keys` folder beside it that protects the sign-in cookies; deleting it signs everyone out).
One sign-in covers every server. There are two kinds of account: an **admin** manages every
server and the accounts, and anyone else manages only the servers an admin ticks for them.

List the servers in the portal's `appsettings.json`:

```json
{
  "Urls": "http://127.0.0.1:5000",
  "Portal": {
    "Title": "Monday Night Wrecking",
    "Intro": "Pick a server.",
    "Servers": [
      { "Id": "vanilla", "Name": "Vanilla", "Description": "The base game's tracks and cars.",
        "ApiUrl": "http://127.0.0.1:5100", "Secret": "<a long random string>" },
      { "Id": "tvtp2", "Name": "TVTP2", "Description": "Tracks from the TVTP2 mod.",
        "ApiUrl": "http://127.0.0.1:5102", "Secret": "<another long random string>" }
    ],
    "TrustedProxies": []
  }
}
```

| Setting | Meaning |
| --- | --- |
| `Urls` | Where the portal listens. Use `http://0.0.0.0:5000` if visitors or a proxy reach it from another PC. |
| `Title`, `Intro` | The front page's heading and the line under it. `Intro` is optional. |
| `Servers` | The servers, in the order the front page lists them. `Description` is optional. |
| `Id` | The server's path: `tvtp2` puts its site at `/tvtp2/`. Letters, digits, `-` and `_`, unique, and not `api` or `assets`, which the portal uses itself. |
| `ApiUrl` | Where the portal reaches the controller's API: its address and port, with no path. It is never shown to visitors, so a LAN address is fine. |
| `Secret` | Shared with that controller (its `Api:Portal:Secret`, with `Api:Portal:ServerId` set to the `Id`). At least 32 characters, and a different one for every server. In PowerShell, `[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))` makes one. |
| `DatabasePath` | Where the accounts are kept, if not `data\portal.db` next to the exe. Optional. |
| `TrustedProxies` | A reverse proxy in front of the portal, such as HAProxy (see step 6), as addresses or CIDR ranges. Empty when visitors reach the portal directly. |
| `PollSeconds`, `TimeoutSeconds` | How often the front page's details are refreshed from each server (default 15, 5 to 3600) and how long one has to answer (default 3). |

On every controller (step 3), set `Api:Portal:ServerId` and `Api:Portal:Secret` to match its
entry here, and list the portal in `Api:TrustedProxies`: `127.0.0.1` when it runs on the same
PC, otherwise its address, with `AllowRemote` set to `true` and the port open in Windows
Firewall to the portal only. When someone who may manage a server uses its pages, the portal
sends that controller a short-lived token, signed with the server's secret, that says who
they are; the controller believes nothing else. It also believes what the portal says about
each visitor's address, for its rate limits and logs.

On the front page a server shows **UP** when its game server runs, **DOWN** when the
controller answers but the game server is stopped, and **NO CONNECTION** when the
controller does not answer; its last known details stay on the card, marked as such.
Settings the portal cannot use stop it at startup, with each problem in the log.

To run the portal as a Windows service:

```
sc.exe create WreckfestPortal binPath= "C:\WreckfestPortal\WreckfestPortal.exe" start= auto
sc.exe start WreckfestPortal
```

## 5. Create the first admin account

There is no sign-up page. Create the first admin on the portal's PC:

```
C:\WreckfestPortal\WreckfestPortal.exe create-admin <username> <email>
```

It asks for the password twice (or reads it once from the first line of input). Run it again whenever
you need another admin, for example when every admin is locked out or has lost their
password.

Then open a server's site on the portal, such as `http://127.0.0.1:5000/vanilla/`, and sign
in. Admins add further accounts on the Users page, choosing for each whether it is an admin
or which servers it manages. Someone who signs in to a server they do not manage is told so;
the menu's server list takes them to the ones they do.

- Passwords need at least 10 characters.
- Five failed sign-ins lock an account for 15 minutes. An admin can unlock it sooner.
- Sign-in also allows 10 attempts per minute from each IP address.
- Nobody can delete, lock or demote their own account, and the last admin stays an admin.

## 6. Open the web site to others

Players see the public home page; admins sign in to manage the server. Either way,
anyone connecting from another computer should use **HTTPS**, since sign-in sends a
password and a session cookie. Put a reverse proxy that handles HTTPS in front of the
portal.

### Behind a reverse proxy

A reverse proxy (such as HAProxy on OPNsense, nginx or Caddy) handles HTTPS and passes
requests to the portal over HTTP. It needs a single route: the whole domain to the portal.

1. Set the portal's `Urls` to `http://0.0.0.0:5000`, unless the proxy runs on the same PC.
2. Point the proxy at `http://<portal PC>:5000`, passing the path on unchanged. It must
   pass WebSocket upgrades, which the live updates use (`/<id>/hubs/server`).
3. Have the proxy send `X-Forwarded-For` and `X-Forwarded-Proto`, and list its address in
   the portal's `Portal:TrustedProxies`:

   ```json
   "TrustedProxies": ["192.168.1.1"]
   ```

   CIDR ranges work too (`"10.0.0.0/24"`). The portal then knows each visitor's real IP
   address, for the sign-in limit and the logs, and that they used HTTPS, so the sign-in
   cookie is marked `Secure`; it passes both on to the controllers. It ignores these headers
   from every other address.

4. Open port 5000 in Windows Firewall only to the proxy:

   ```
   netsh advfirewall firewall add rule name="WreckfestPortal" dir=in action=allow protocol=TCP localport=5000 remoteip=192.168.1.1
   ```

In HAProxy:

```
default_backend wreckfest_portal

backend wreckfest_portal
    # Always set by the proxy, never passed on from the visitor: the portal believes them.
    http-request del-header X-Forwarded-For
    option forwardfor
    http-request set-header X-Forwarded-Proto https if { ssl_fc }
    http-request set-header X-Forwarded-Proto http if !{ ssl_fc }
    server portal 192.168.1.10:5000
```

### HTTPS on the controller

The controller can also serve HTTPS itself, for scripts that call its API from another
computer: see [docs/https.md](docs/https.md). Visitors to the web site never connect to a
controller directly.

## 7. Check your settings

On the web site, as an admin:

- **Settings**: the voting mode and its limits.
- **Tracks**: which tracks players can pick with `!track` and `!vote`.
- **Server Config**, **Rotation**, **Collections** and **Cups**: as you need them.

### The AI assistant (optional)

The public site can have an **AI** page where visitors ask about the server in plain
language: who is on, what is next in the rotation, who won, when the next cup is. It is set
up in the controller's `user-settings.json` (step 3), not on the web site, so the key stays
with whoever runs the PC:

```json
"Assistant": {
  "Enabled": true,
  "ApiKey": "<your key>",
  "Model": "gpt-5-mini",
  "BaseUrl": "https://api.openai.com/v1",
  "DailyRequestLimit": 200
}
```

1. Get an API key from [OpenAI](https://platform.openai.com/api-keys), or from any
   OpenAI-compatible service. For OpenRouter set `BaseUrl` to
   `https://openrouter.ai/api/v1`; for a local Ollama, `http://localhost:11434/v1` with any
   key.
2. `Enabled` shows the public AI page. `Model` defaults to `gpt-5-mini`.
3. `DailyRequestLimit` caps the bill (default 200 questions a day). Each visitor can also ask
   at most 10 questions a minute.

Restart the controller after changing them. The controller makes the calls, so the key never
reaches a browser. The model can only look up what the public pages already show.

With a key set, the admin panel has an **AI assistant** page too, whether or not the public
page is on, for everyone who manages the server. There you can ask for changes to the
rotation, collections, cups and server settings in plain language: the assistant proposes
each one, and nothing changes until you click **Apply**. It cannot manage users, start or
stop the server, or see passwords.

## Scripts and the API

Scripts call the same API as the web site and authenticate with the `Key` from step 3:

```bash
curl -H "X-Api-Key: <key>" http://127.0.0.1:5100/api/server/status
```

The full reference is [docs/API.md](docs/API.md). The OpenAPI description is at
`/openapi/v1.json`, for signed-in callers and API-key requests.

## Logging

The desktop app's **Controller Log** tab shows the log. Log levels can be set in
`user-settings.json`:

```json
"Logging": {
  "LogLevel": {
    "Default": "Information",
    "WreckfestController.Services.ServerControl.ServerManager": "Debug"
  }
}
```

## Troubleshooting

**The server does not start.** Check that the working directory and executable on the
Configuration tab are right, and that the controller can write to the server folder.

**No console output, chat or players.** The hook is not injected, or was lost when the
server restarted. Inject it again (see [The hook](#the-hook)). If injecting is refused,
the server's build may not match the supported build.

**A setting cannot be saved from the web.** The error names the key. Add an active
`key=value` line for it to `server_config.cfg`, or remove a later duplicate below
`# Event Loop`, which would override it.

**The web site does not load.** Check that the portal is running; settings it cannot use
stop it at startup, with each problem in its log. A server's page loads but its data does
not (the portal answers 502): check the **WEB API** line on that controller's
Configuration tab. A wrong `Api` setting or a port already in use stops the API, and the
reason is shown there. Check also that the portal's `ApiUrl` for it is right.

**Signed in, but a server's admin pages say you are not signed in, or answer 401.** That
controller does not accept the portal's token: check that its `Api:Portal:ServerId` is the
server's `Id` and its `Api:Portal:Secret` is the same as the portal's `Secret` for it, then
restart it. The Configuration tab's **WEB API** line says when the two settings do not go
together.

**Signed out after every restart.** The portal cannot keep its `keys` folder. Check that the
folder beside its `portal.db` is writable by the account the portal runs as.

**The controller closed unexpectedly.** Look in `%LocalAppData%\WreckfestController\crashes`
for the newest `crash-<date>_<time>-pid<id>.txt`; it names the database, so with several
controllers you can tell which one it was. If that folder cannot be written, the file goes
to `%TEMP%\WreckfestController\crashes`. Attach the file when you report the crash.

**Live updates do not arrive through the proxy.** The proxy must allow WebSocket
upgrades on `/hubs/server`.
