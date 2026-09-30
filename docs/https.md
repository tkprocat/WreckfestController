# HTTPS

The controller's web UI and API sign you in with a cookie and take passwords. Over
plain HTTP both cross the network unencrypted. On `127.0.0.1` that is fine; for anything
reachable from another computer, use HTTPS: either **directly** (this page), or through
a **reverse proxy** that ends HTTPS in front of the controller (see
[API.md](API.md#behind-a-reverse-proxy)).

The controller does not care where a certificate comes from - Let's Encrypt, a
commercial or company CA, `mkcert`, self-signed - only where to load it: the Windows
certificate store, or a file.

## Settings

HTTPS is configured in the startup settings file (`user-settings.json`, or
`appsettings.json` next to the exe), under `Api`. These are edited by hand; the desktop
app shows the result on its **Configuration** tab, under **WEB API**.

```jsonc
"Api": {
  "Enabled": true,
  "AllowRemote": true,          // listen on the network, not only 127.0.0.1
  "HttpPort": 5100,
  "HttpsPort": 5101,
  "Https": {
    // EITHER the Windows certificate store...
    "Host": "wf.example.com",   // the name browsers use; matched exactly against the certificate's names
    "Store": "My",              // the store: My, WebHosting, ...
    "Location": "CurrentUser",  // or LocalMachine (default: CurrentUser)

    // ...OR a file (not both):
    // "Path": "C:\\certs\\wf.pfx",   // .pfx/.p12, or a PEM file (.pem/.crt) with its chain
    // "KeyPath": "C:\\certs\\wf.key", // for PEM: the key. Without KeyPath, Path is read as a .pfx
    // "Password": "...",              // if the .pfx or the PEM key has one (plain text: protect this file)

    "PublicHost": "wf.example.com", // optional: where plain-HTTP visitors are sent (see below)
    "PublicPort": 443,              // optional: the HTTPS port browsers use, when NAT maps it (default: HttpsPort)
    "Hsts": false                   // optional, see below
  }
}
```

- **No `Https` section:** HTTP only, as before. The log says so.
- **Exactly one source.** Both, neither, or one only partly given (a `Host` without a
  `Store`, a `KeyPath` without a `Path`) is an error that names the setting. So is
  `"Https": {}`: an empty section never means "HTTP only".
- **Relative paths** are relative to the folder the exe is in, not to where it was
  started from.
- **Ports** must be 1-65535; with `Https`, HTTP and HTTPS must differ.
- `Kestrel:Endpoints`, `urls` and the .NET development certificate fallback are **not**
  used: `Api` alone decides where the API listens.
- **Changing the settings needs a restart.** Replacing the certificate at the configured
  source does not (see *Renewal*).

## If HTTPS is configured but cannot be used

A missing file, a wrong password, an expired certificate, or no matching certificate in
the store **stops the API** - it does not start on plain HTTP instead. The desktop app
keeps running and shows the reason in red under **WEB API**, and in its log. Fix the
setting or the certificate, then restart.

## Which certificates are used

A certificate is only served if:

- it is valid now (not expired, not yet to come);
- it is for server authentication (its Enhanced Key Usage includes Server
  Authentication, or it has no EKU at all);
- its private key is there and **this account can use it**;
- from the store: one of its DNS names is exactly `Host`. Wildcards and longer names
  do not match. Of several, the one issued last wins, so a renewal is picked up
  without any change here.
- from a file: the file's certificate with a private key (the first one, in a `.pfx`
  that holds several). Put **one** server certificate per file. Its names are not
  checked against anything: make sure they are the names browsers use (and
  `PublicHost`).

Clients do not have to trust it for the controller to use it: self-signed and company
CAs work. **Browsers** will warn unless they trust the issuer (see *Self-signed*).

The chain (the intermediate certificates) is sent with the certificate. A `.pfx` from
win-acme includes it; a `.pem` must hold the certificate followed by its chain.

## Renewal

The certificate is checked every **5 minutes**, and a file source is also watched, so a
renewed file is picked up within seconds. A new certificate is used for the next new
connection; no restart. If a reload fails (a half-written file, a wrong key), the
working certificate stays in use, the error shows under **WEB API** and in
`GET /api/https`, and it is tried again sooner. Once the working certificate itself
expires, **new** HTTPS connections are refused - never swapped for plain HTTP.
Connections already open (a page's live-updates connection, say) are not cut; they end
when the browser closes them.

The desktop app turns the certificate line **yellow** 14 days before it expires.

## Plain HTTP when HTTPS is on

With `Https` configured:

- **`GET` and `HEAD` requests from another computer** - page loads, and anything else
  fetched that way outside `/api`, `/hubs` and `/openapi` - are sent to the HTTPS address
  (a temporary `307` redirect).
- **Everything else over plain HTTP from another computer is refused** (`400 Use
  HTTPS`): anything under `/api`, `/hubs` or `/openapi`, and every other method (sign-ins,
  saves). A redirect cannot take back a password that was already sent in the clear.
- **This PC** keeps plain HTTP: `http://127.0.0.1:5100` still works for local tools and
  scripts, as long as the request carries no forwarding headers.
- **Through a trusted reverse proxy** (`Api:TrustedProxies`) that says the browser used
  HTTPS: served as HTTPS. A proxy - even one on this PC - that forwards plain HTTP is
  treated as another computer.

The redirect goes to `PublicHost`, or else the store's `Host`, or else the certificate's
first exact name, with `PublicPort` - **never** to whatever name the request asked for.
If no name is known, remote page loads are refused instead.

**Use one address per browser.** Browsers keep cookies per host name, not per port or
scheme, so remote browsers should always use the HTTPS address, and tools on this PC
`http://127.0.0.1:5100` (a different name from the public one). The sign-in cookie is
marked `Secure` over HTTPS, so it is never sent back over plain HTTP.

**One gap to know:** a relay on this PC that passes requests on *without* adding
`X-Forwarded-For`/`X-Forwarded-Proto` looks exactly like a local tool, and gets plain
HTTP. A reverse proxy should always send those headers.

### HSTS

`"Hsts": true` tells browsers to use HTTPS for this host from then on
(`Strict-Transport-Security: max-age=15552000`, 180 days, without `includeSubDomains`
or preload). It is **off by default**, for a reason: HSTS applies to **every port** of a
host name, so a browser that has learned it turns `http://wf.example.com:5100` into
`https://wf.example.com:5100` - not 5101 - and never reaches the redirect. Turn it on
only when **every** address people use for this host name is plain `https://name` (port
443) and `http://name` (port 80): an old link or bookmark to `http://name:5100` will stop
working, since the browser rewrites it to `https://name:5100`. It also covers every
other HTTP service on the same name. `PublicPort: 443` only changes where the redirect
points; it does not make HSTS safe on its own.

It is never sent for `localhost`, `*.localhost` or loopback addresses. A browser that
has learned HSTS for a name also refuses to click past a certificate warning for it. To
make a browser forget, in Chrome or Edge open `chrome://net-internals/#hsts` (or
`edge://net-internals/#hsts`) and delete the domain.

## Status

- **Desktop app:** Configuration tab, **WEB API**: where it listens, the certificate's
  names, issuer and expiry, or why the API did not start.
- **API:** `GET /api/https` (signed in): the certificate part only - HTTPS on or off,
  names, issuer, expiry, the 14-day warning, `lastRefresh` (the last successful check,
  whether or not the certificate changed) and the last reload error. It cannot say why
  the API did not start: then it does not answer at all. No key, password or path is
  ever shown.
- **Log:** the certificate used at start, each replacement, and each failed reload.

## Getting a certificate

### Let's Encrypt

Free, trusted by every browser, 90 days, renewed automatically by
[win-acme](https://www.win-acme.com/). See **[LetsEncrypt.md](LetsEncrypt.md)**.

### A commercial or company CA

Install the certificate with its private key in the store you name (`My` for the
account running the controller, or `LocalMachine\My` for all), or export it as a `.pfx`
and point `Path` at it. For LocalMachine, the account running the controller needs read
access to the private key (certlm.msc > the certificate > All Tasks > Manage Private
Keys).

### Self-signed

For a LAN, where the browsers that use it can be told to trust it. In PowerShell,
**with the names browsers will use** (a host name, and an IP address if you browse by
IP):

```powershell
$cert = New-SelfSignedCertificate -Subject "CN=wf.lan" `
  -TextExtension @("2.5.29.17={text}DNS=wf.lan&IPAddress=192.168.1.50", "2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
  -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
  -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(2) `
  -FriendlyName "WreckfestController HTTPS"

# The public certificate, for the browsers that should trust it (no private key):
Export-Certificate -Cert $cert -FilePath C:\certs\wf.cer
```

Use `-TextExtension` for the names rather than `-DnsName`: `-DnsName "192.168.1.50"`
stores the address as a *DNS name*, which browsers do not accept for an IP.

Then either use the store (`"Host": "wf.lan", "Store": "My"`), or export a `.pfx`
(`Export-PfxCertificate -Cert $cert -FilePath C:\certs\wf.pfx -Password (Read-Host -AsSecureString)`)
and use `Path` and `Password`.

On each computer whose browser should trust it, import **only `wf.cer`** into *Trusted
Root Certification Authorities* (double-click it > Install Certificate). Never copy the
`.pfx`: it holds the private key.

### On this PC, for testing

The .NET development certificate is trusted for `localhost` on a developer's machine.
`dotnet dev-certs https --check --trust` says which one is trusted. Export **that one**
(certmgr.msc > Personal > Certificates, the `localhost` certificate whose thumbprint the
check printed > All Tasks > Export, with the private key) and use `Path` and `Password`.
Note that `dotnet dev-certs https -ep` exports *a* development certificate, which is not
necessarily the trusted one when several are installed.

## Troubleshooting

| What you see | Why, and what to do |
| --- | --- |
| **WEB API: "did not start: ... was not found (Api:Https:Path)"** | The file is not there. Relative paths start at the exe's folder. |
| **"could not be read: check Api:Https:Password ..."** | A wrong password, a damaged file, or a key that does not match the certificate. |
| **"No usable certificate for wf.example.com ..."** | Nothing in that store has exactly that name and is usable now; the message says why the newest one for the name was not. Check `Store` and `Location` too. |
| **"its private key is missing, or this account may not use it"** | The store holds the certificate without a key, or the account running the controller may not read it (LocalMachine: *Manage Private Keys*). |
| **Browser: "Your connection is not private"** | The browser does not trust the issuer (self-signed, staging), or the name you typed is not on the certificate. |
| **`400 Use HTTPS`** | A plain-HTTP request from another computer. Use the `https://` address. |
| **Browser always goes to https on the wrong port** | HSTS was learned for the name (see *HSTS*); clear it, and keep `Hsts` off unless HTTPS is on 443. |
| **Nothing answers on 5101 from another computer** | `AllowRemote` must be `true`, and Windows Firewall must allow the port (see INSTALL.md). |
