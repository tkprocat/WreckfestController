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
| Needs | a provider plugin and an API key (or the Script plugin) | nothing else runs on port 80 on this PC |

DNS-01 proves you own the name; it does **not** make the controller reachable. Browsers
still need a route to it: a port forward, a VPN, or a reverse proxy.

*HTTP-01 was not run in this walkthrough.*

## 3. Keep the API key out of the command line history

Put the key in a file only your account can read, and let the command read it from
there:

```powershell
Set-Content C:\tools\win-acme\simply.key -NoNewline -Value '<the API key from the provider>'
```

win-acme stores it encrypted (Windows DPAPI, per account) for renewals; the file can be
deleted after the first run.

## 4. Get a test certificate first (staging)

Staging certificates are not trusted by browsers, but staging has no rate limits:
use it until the command works. Simply.com example, storing a `.pfx`:

```powershell
New-Item -ItemType Directory -Force C:\certs\wf.example.com | Out-Null
$key = (Get-Content C:\tools\win-acme\simply.key -Raw).Trim()
C:\tools\win-acme\wacs.exe `
  --baseuri https://acme-staging-v02.api.letsencrypt.org/ `
  --source manual --host wf.example.com `
  --validationmode dns-01 --validation simply --account <Simply account, e.g. UE123456> --apikey $key `
  --store pfxfile --pfxfilepath C:\certs\wf.example.com `
  --accepttos --closeonfinish
```

What happened in the tested run:

- win-acme created the `_acme-challenge` TXT record through the API, then checked
  Simply's name servers: the record took **about a minute** to appear on all three
  ("Preliminary validation failed ... Will retry in 30 seconds" twice). That is normal;
  it retries on its own.
- Let's Encrypt validated, win-acme **deleted the record**, and wrote
  `C:\certs\wf.example.com\wf.example.com.pfx`: the certificate, its key, and the chain.
- `--account` names the Simply account **and** win-acme's Let's Encrypt account; that is
  harmless.
- Asked for an email for expiry notices, it continued without one. Let's Encrypt no
  longer sends expiry emails anyway; the controller warns 14 days ahead instead.

## 5. The real certificate

The same command without `--baseuri` (and, for a file others can read, with
`--pfxpassword`). *Not run in this walkthrough: the staging run proved the method.*

## 6. Point the controller at it

```jsonc
"Api": {
  "AllowRemote": true,
  "HttpsPort": 5101,
  "Https": {
    "Path": "C:\\certs\\wf.example.com\\wf.example.com.pfx",
    "PublicHost": "wf.example.com"
  }
}
```

Restart the controller. The Configuration tab's **WEB API** section shows the
certificate. Tested with the staging `.pfx`: served over TLS 1.3 with its chain.

Keep the certificate folder readable only by the accounts that need it: without a
`--pfxpassword`, the `.pfx` holds the private key unprotected.

## 7. Renewal

win-acme saves each certificate as a *renewal* and runs `wacs.exe --renew` daily from a
Windows scheduled task. It creates that task on the first run - **only when run as
admin**. Without admin the tested run said:

> Unable to register scheduled task, please run as administrator or equivalent

and saved the renewal without a task. Either run win-acme once as admin to create the
task, or create a daily task yourself that runs `C:\tools\win-acme\wacs.exe --renew` as
**the same account** that created the renewal (win-acme's saved secrets are per
account).

When the renewal replaces the `.pfx`, the controller picks it up within seconds (a file
watcher, and a check every 5 minutes): no restart.

To test a renewal: on staging, force it for the one certificate
(`wacs.exe --renew --force --baseuri https://acme-staging-v02.api.letsencrypt.org/`),
then open a **new** connection and check the certificate's dates. *Not run in this
walkthrough.*

## Storing in the Windows store instead of a file

- **LocalMachine** (needs admin): `--store certificatestore --certificatestore My`
  (set the store explicitly: win-acme may default to `WebHosting`), plus
  `--acl-read "<account running the controller>"` so it may read the key. Then
  `"Host": "wf.example.com", "Store": "My", "Location": "LocalMachine"`.
- **CurrentUser**: needs win-acme's *User Store* plugin (`--store userstore`), which was
  not installed for this walkthrough.

*Store variants were not run in this walkthrough.*
