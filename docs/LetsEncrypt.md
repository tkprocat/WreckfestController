# Let's Encrypt with win-acme

A free certificate trusted by every browser, renewed automatically every 60 days or so,
for the controller's [HTTPS](https.md). The controller reads the certificate from a file
or the Windows store; [win-acme](https://www.win-acme.com/) (`wacs.exe`) gets it and
renews it. You need a **domain name you control**, pointing (for browsers) at the
controller or its router.

This walkthrough was run for real on 2026-09-30 against Let's Encrypt's staging service
with win-acme 2.2.9, DNS validation through Simply.com, and a `.pfx` file. The parts not
run then are marked.

## 1. Install win-acme

Download the release from [win-acme.com](https://www.win-acme.com/) (or its GitHub
releases) and unzip it, say to `C:\tools\win-acme`.

- For **DNS validation through a provider's API**, you need the **pluggable** build and
  the provider's plugin zip of **the same version** (for example
  `plugin.validation.dns.simply.v2.2.9.1701.zip`). Unzip the plugin into the win-acme
  folder, then unblock it, or Windows may refuse to load it:
  ```powershell
  Get-ChildItem C:\tools\win-acme\*.dll | Unblock-File
  ```
- `wacs.exe --help` lists what your build can do. With the plugin loaded, a section for
  it appears (for Simply: `--validation simply`, `--account`, `--apikey`).

Storing in the *current user's* certificate store needs win-acme's separate **User
Store** plugin; without it, store a certificate as a **file** (as below), or in the
**LocalMachine** store (which needs admin).

## 2. Choose how Let's Encrypt checks the domain

| | **DNS-01** (recommended here) | **HTTP-01** |
| --- | --- | --- |
| How | win-acme adds a TXT record through your DNS provider's API | Let's Encrypt fetches a file from `http://your-domain/` |
| Ports | **none** to get or renew the certificate | public port **80** must reach this PC (not a redirect to 5101) |
| Admin | not needed | win-acme's self-hosted listener needs an **elevated** win-acme |
| Needs | a provider plugin and an API key (or the Script plugin) | port 80 free for win-acme's listener, or shared with IIS or another HTTP.sys listener it can share with (see win-acme's self-hosting docs) |

DNS-01 proves you own the name; it does **not** make the controller reachable. Browsers
still need a route to it: a port forward, a VPN, or a reverse proxy.

*HTTP-01 was not run in this walkthrough.*

## 3. Keep the API key out of the command line history

Typed into a command, the key would stay in PowerShell's history. Instead, create a
file only your account can read, then let PowerShell ask for the key, so it is never
part of a command:

```powershell
$file = "C:\tools\win-acme\simply.key"
New-Item -ItemType File -Force $file | Out-Null
icacls $file /inheritance:r /grant:r "$($env:USERNAME):F" | Out-Null
$secret = Read-Host -AsSecureString -Prompt "API key"
[System.Net.NetworkCredential]::new("", $secret).Password | Set-Content $file -NoNewline
Remove-Variable secret
```

win-acme keeps the key for renewals in its own configuration
(`C:\ProgramData\win-acme`), encrypted with Windows DPAPI **for this machine**: any
admin, or a process running as SYSTEM, on this PC can read it. Keep that folder's
permissions as win-acme sets them. The key file can be deleted after the first run.

## 4. Get a test certificate first (staging)

Staging certificates are not trusted by browsers, and staging's rate limits are far
higher than production's (failed validations count too): use it until the command
works. Store staging in **its own folder**, never the one the controller uses: a staging
renewal writes its `.pfx` wherever it was told, and the controller would serve that
untrusted certificate. Simply.com example:

```powershell
New-Item -ItemType Directory -Force C:\certs\staging\wf.example.com | Out-Null
$key = (Get-Content C:\tools\win-acme\simply.key -Raw).Trim()
C:\tools\win-acme\wacs.exe `
  --baseuri https://acme-staging-v02.api.letsencrypt.org/ `
  --source manual --host wf.example.com `
  --validationmode dns-01 --validation simply --account <Simply account, e.g. UE123456> --apikey $key `
  --store pfxfile --pfxfilepath C:\certs\staging\wf.example.com `
  --accepttos --closeonfinish
```

(The tested run used one folder for everything; the separate staging folder is the
lesson from it.)

What happened in the tested run:

- win-acme created the `_acme-challenge` TXT record through the API, then checked
  Simply's name servers: the record took **about a minute** to appear on all three
  ("Preliminary validation failed ... Will retry in 30 seconds" twice). That is normal;
  it retries on its own.
- Let's Encrypt validated, win-acme **deleted the record**, and wrote
  `wf.example.com.pfx` in the folder given: the certificate, its key, and the chain.
- `--account` names the Simply account **and** win-acme's Let's Encrypt account; that is
  harmless.
- Asked for an email for expiry notices, it continued without one. Let's Encrypt no
  longer sends expiry emails anyway; the controller warns 14 days ahead instead.

## 5. The real certificate

The same command **without `--baseuri`**, storing to the folder the controller will use
(`--pfxfilepath C:\certs\wf.example.com`). Optionally add `--pfxpassword` to encrypt
the `.pfx`; the controller then needs the same value in `Api:Https:Password`.
*Not run in this walkthrough: the staging run proved the method.*

Once production works, remove the staging renewal, so it cannot renew into a folder
anyone uses: `wacs.exe --cancel --baseuri https://acme-staging-v02.api.letsencrypt.org/`
(or pick it from `wacs.exe`'s menu, *Manage renewals*).

## 6. Point the controller at it

```jsonc
"Api": {
  "Enabled": true,               // the API is off by default
  "AllowRemote": true,
  "HttpsPort": 5101,
  "Https": {
    "Path": "C:\\certs\\wf.example.com\\wf.example.com.pfx",
    // "Password": "...",        // only with --pfxpassword: the same value
    "PublicHost": "wf.example.com"
  }
}
```

Restart the controller. The Configuration tab's **WEB API** section shows the
certificate. Tested with the staging `.pfx`: served over TLS 1.3 with its chain.

Keep the certificate folder readable only by the accounts that need it: without a
`--pfxpassword`, the `.pfx` holds the private key unprotected. With one, the password
sits in the settings file as plain text: protect that file the same way.

## 7. Renewal

win-acme saves each certificate as a *renewal* and runs `wacs.exe --renew` daily from a
Windows scheduled task. It creates that task on the first run - **only when run as
admin**. Without admin the tested run said:

> Unable to register scheduled task, please run as administrator or equivalent

and saved the renewal without a task. Either run win-acme once as admin to create the
task (it runs as SYSTEM), or create a daily task yourself that runs
`C:\tools\win-acme\wacs.exe --renew` as an account that can read win-acme's
configuration in `C:\ProgramData\win-acme` and write the certificate folder.

When the renewal replaces the `.pfx`, the controller picks it up within seconds (a file
watcher, and a check every 5 minutes): no restart.

To test a renewal, force it for **one** certificate on staging, into the staging folder:
`wacs.exe --renew --force --id <renewal id> --baseuri https://acme-staging-v02.api.letsencrypt.org/`
(the id is shown by *Manage renewals* in `wacs.exe`'s menu; without `--id`, every staging
renewal is forced). Point a test controller at the staging folder, then open a **new**
connection and check the certificate's dates. *Not run in this walkthrough.*

## Storing in the Windows store instead of a file

- **LocalMachine** (needs admin): `--store certificatestore --certificatestore My`
  (set the store explicitly: win-acme may default to `WebHosting`), plus
  `--acl-read "<account running the controller>"` so it may read the key. Then
  `"Host": "wf.example.com", "Store": "My", "Location": "LocalMachine"`.
- **CurrentUser**: needs win-acme's *User Store* plugin (`--store userstore`), which was
  not installed for this walkthrough.

*Store variants were not run in this walkthrough.*
