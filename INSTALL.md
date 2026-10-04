# Installing WreckfestController

This guide covers installing WreckfestController 2.0, connecting it to a Wreckfest
dedicated server, and opening its web site to admins and players.

**Upgrading from 1.x?** 2.0 starts with an empty database and does not import 1.x
settings, cups or WreckfestWeb data. The 2.0 release notes list what to set up again.
1.x's files are left as they are, so you can go back by reinstalling `v1-final`.

## Requirements

- Windows 10 or 11, or Windows Server. The controller is a desktop app and runs in a
  signed-in session; it cannot run as a Windows service yet.
- The **.NET 10 Desktop Runtime** and the **ASP.NET Core 10 Runtime**, from
  [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET 10
  SDK includes both.
- The **Wreckfest dedicated server**, at the build the controller supports (see
  [The hook](#the-hook)).
- **SteamCMD** (optional), for updating the server from the controller.

Building from source needs more: see [README.md](README.md#building-from-source).

## 1. Install

Extract the release ZIP to a folder of its own, such as `C:\WreckfestController\`, and
run `WreckfestController.exe`. Keep the files together: the controller needs the hook DLL
and the `wwwroot` folder next to the exe.

On first start it creates its data folder, `%LocalAppData%\WreckfestController\`, with:

| File | Contents |
| --- | --- |
| `controller.db` | Settings, web accounts, the track catalogue, collections, cups and race results |
| `keys\` | The keys that protect sign-in cookies, encrypted for this Windows user. Deleting the folder signs everyone out. |
| `user-settings.json` | Startup settings that you write by hand (see step 3). The controller never writes this file. |

If the database cannot be opened, the controller starts in **recovery mode**: the
desktop app shows why, sign-in is unavailable and the API answers 503 until it is fixed
and the controller restarted.

To keep the database somewhere else, set `Database:Path` in `user-settings.json` (step
3). The `keys` folder follows it.

## 2. Connect the dedicated server

Open the **Configuration** tab in the desktop app and set:

- **Working Directory**: the dedicated server's folder.
- **Server Executable**: usually `Wreckfest_x64.exe`.
- **Log File Name** (optional): leave it empty to use the `log=` setting in
  `server_config.cfg`.
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

**Inject the hook after every start or restart:** in the desktop app's Process Manager
tab, select the server and click **INJECT**, or use **Inject hook** on the web site's
Server Control page. A restart, including a cup's scheduled restart, starts a new
process without the hook.

The hook reads the game's memory at fixed offsets, which belong to one game build. The
supported build is `WreckfestServer:SupportedBuild` in `appsettings.json`, and the
controller refuses to inject into any other. After a Wreckfest update, wait for a
controller release that supports the new build.

## 3. Enable the web site

The web site and API are off by default. To turn them on, create
`%LocalAppData%\WreckfestController\user-settings.json`:

```json
{
  "Api": {
    "Enabled": true,
    "Key": "",
    "AllowRemote": false,
    "TrustedProxies": [],
    "HttpPort": 5100,
    "HttpsPort": 5101
  }
}
```

Settings in `user-settings.json` override the ones in `appsettings.json`. Keep yours in
`user-settings.json`, because installing a new release replaces `appsettings.json`.
Restart the controller after any change to the file.

| Setting | Meaning |
| --- | --- |
| `Enabled` | `true` starts the web site and API. With `false` no port is opened. |
| `Key` | An API key for scripts, sent as the `X-Api-Key` header. Leave it empty if nothing but browsers will use the API. |
| `AllowRemote` | `false` listens on `127.0.0.1` only, so only this PC can connect. `true` listens on every network interface. |
| `TrustedProxies` | The address of a reverse proxy in front of the controller (see step 5). |
| `HttpPort`, `HttpsPort` | Default 5100 and 5101. Give each controller its own ports when one PC runs several. |
| `Https` | A certificate, to serve HTTPS directly: see [docs/https.md](docs/https.md). |

The Configuration tab shows under **WEB API** whether the API is running and where, or
why it failed to start. A wrong setting stops the API rather than falling back to a
default.

## 4. Create the first admin account

There is no sign-up page. When the web site is enabled and no account exists, the
desktop app offers a **Create admin account** dialog at startup. The Configuration tab
has the same button under **WEB ACCOUNTS**, which also works for recovery if every
admin is locked out or has lost their password.

Then open `http://127.0.0.1:5100/` and sign in. Admins add further accounts on the web
site's Users page. Every account is an admin.

- Passwords need at least 10 characters.
- Five failed sign-ins lock an account for 15 minutes. An admin can unlock it sooner.
- Sign-in also allows 10 attempts per minute from each IP address.

## 5. Open the web site to others

Players see the public home page; admins sign in to manage the server. Either way,
anyone connecting from another computer should use **HTTPS**, since sign-in sends a
password and a session cookie. There are two ways to provide it.

### Behind a reverse proxy

A reverse proxy (such as HAProxy on OPNsense, nginx or Caddy) handles HTTPS and passes
requests to the controller over HTTP.

1. Set `AllowRemote` to `true`, unless the proxy runs on the same PC.
2. Point the proxy at `http://<controller PC>:5100`. It must pass WebSocket upgrades on
   `/hubs/server`, which the live updates use.
3. Have the proxy send `X-Forwarded-For` and `X-Forwarded-Proto`, and list its address in
   `TrustedProxies`:

   ```json
   "TrustedProxies": ["192.168.1.1"]
   ```

   CIDR ranges work too (`"10.0.0.0/24"`). The controller then sees each visitor's real
   IP address, for the sign-in limit and the logs, and knows the visitor used HTTPS, so
   the sign-in cookie is marked `Secure`. It ignores these headers from every other
   address.

   Without this, every visitor shares the proxy's sign-in limit of 10 attempts a
   minute.

4. Open port 5100 in Windows Firewall only to the proxy:

   ```
   netsh advfirewall firewall add rule name="WreckfestController" dir=in action=allow protocol=TCP localport=5100 remoteip=192.168.1.1
   ```

### HTTPS directly

The controller can serve HTTPS itself with a certificate from the Windows certificate
store or a file, and reload it when it is renewed. See [docs/https.md](docs/https.md), and
[docs/LetsEncrypt.md](docs/LetsEncrypt.md) for a free Let's Encrypt certificate. Set
`AllowRemote` to `true` and open `HttpsPort` in Windows Firewall.

## 6. Check your settings

On the web site, as an admin:

- **Settings**: the voting mode and its limits.
- **Tracks**: which tracks players can pick with `!track` and `!vote`.
- **Server Config**, **Rotation**, **Collections** and **Cups**: as you need them.

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

**The web site does not load.** Check the **WEB API** line on the Configuration tab. A
wrong `Api` setting or a port already in use stops the API, and the reason is shown
there.

**Signed out after every restart.** The controller cannot keep its `keys` folder.
Check that `%LocalAppData%\WreckfestController\keys` is writable.

**Live updates do not arrive through the proxy.** The proxy must allow WebSocket
upgrades on `/hubs/server`.
