# Installation and operations

## Local stdio

Build with .NET SDK 10.0.401 or use a platform-specific release artifact. Windows and Linux x64 are the initial binary targets. Self-contained releases include .NET; source builds need the SDK. Linux binaries still require the OS libraries supported by .NET (including ICU/OpenSSL); use the container when those are unavailable.

```text
dotnet restore OBS-IT.IMAP.slnx --locked-mode
dotnet publish src/OBS.IMAP.Host/OBS.IMAP.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:RestoreLockedMode=true -o artifacts/win-x64
dotnet publish src/OBS.IMAP.Host/OBS.IMAP.Host.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:RestoreLockedMode=true -o artifacts/linux-x64
```

Copy `examples/local.json` to a protected absolute path and replace its server and username. Set the referenced credential variable in the process environment using your OS secret provisioning system. Do not put a password in the JSON, executable arguments or Codex TOML. Start `obs-imap --config /absolute/path/accounts.json`; stdio is the default. Register it using `examples/codex.toml`, or the `codex mcp add` pattern in README. All diagnostics use stderr, and stdout belongs to MCP.

The host accepts only `--config`, `--transport stdio|http`, and `--help`. Configuration is loaded once; restart for account, access-policy or secret rotation. There is no background refresh, sync or database. A local client has the authority of the operating-system user who starts the connector; restrict `Local.AllowedAccountIds` to the necessary accounts.

For a complete distributable archive rather than a bare binary, use PowerShell 7.4+ and `./scripts/Publish.ps1 -RuntimeIdentifier win-x64` (or `linux-x64`). It refuses to overwrite an existing package directory. Packages include dependency/runtime license notices, an SPDX 2.3 inventory and file/archive checksums. The inventory explicitly excludes operating-system libraries and container base contents. Verify checksums before installing. Native single-file extraction can require a private writable temporary directory; configure `DOTNET_BUNDLE_EXTRACT_BASE_DIR` when the normal temporary directory is unavailable. The framework-dependent production container does not use single-file extraction.

## Remote HTTP

Provision a compatible external OAuth authorization server first. It must issue signed JWT access tokens with the configured issuer, audience, stable `sub`, and `mail.read` in a space-delimited `scope` or `scp` claim. Supported signing algorithms are RSA/ECDSA SHA-256/384/512, never `none` or symmetric keys. Configure client registration, PKCE, redirect URIs, token lifetime and least-privilege consent in that server. The connector does not implement those authorization-server functions and does not accept opaque tokens.

Use `examples/remote.json` and replace the HTTPS resource URL, authority, audience, public host and subject grant. Unknown subjects cannot access tools. Each grant maps to explicit account IDs; tool arguments cannot change identity. Keep incoming MCP tokens separate from the mail provider's password or OAuth2 token.

Terminate HTTPS at an operator-controlled reverse proxy, forwarding to the private backend listener. Preserve the configured public Host header, stream responses without buffering, and allow MCP methods/content types. The backend deliberately ignores forwarded identity/scheme/IP headers. Use a private network or loopback between proxy and backend; direct backend traffic is HTTP and must never cross an untrusted network. Firewall the backend. Auth remains required even on private interfaces. Do not put an HTML login redirect in front of MCP in place of OAuth resource authorization.

`AllowedOrigins: []` rejects every supplied browser Origin; requests without Origin are permitted to reach authentication. Add only the exact HTTPS origin required by a verified client. This is a CSRF/DNS-rebinding guard, not blanket cross-origin browser support. Host validation and authentication also apply to loopback. OAuth protected resource discovery is public at `/.well-known/oauth-protected-resource`; mailbox methods are never anonymous.

```text
docker compose -f docker/compose.yml build
docker compose -f docker/compose.yml up -d
```

Compose mounts redacted `examples/remote.json`; replace its values before use. It reads a local `.env` (copy `.env.example`) for the account credential and exposes port 8080 only on host loopback. The container runs as the non-root .NET app user with a read-only filesystem and dropped capabilities. Configure the reverse proxy on the host, or adapt the network and mounts deliberately. For mounted secret files, grant read access only to the service UID and use the configured secret root/alias provider. The image contains no user credentials and has no mailbox volume. Do not put `.env` into the image or source control.

ChatGPT access depends on the target workspace's custom MCP feature and authorization-server compatibility. Use a reachable HTTPS endpoint or a supported Secure MCP Tunnel; a tunnel does not grant mailbox authorization. Follow [the official-source compatibility notes](ClientCompatibility.md). No universal ChatGPT support claim is made without testing the target workspace.

## Maintenance and troubleshooting

Read tool errors expose a safe code and generated correlation ID. `authentication_failed` calls for checking provider credentials/token expiry; `access_denied` calls for account grants or outbound network policy; `stale_reference` calls for rediscovering the folder UIDVALIDITY; `invalid_cursor` calls for restarting the search. `limit_exceeded` means narrow the search, select less content, or review bounded limits. `provider_unavailable` covers safe transport/provider failures; certificate failures must be fixed by installing the correct CA chain in the OS trust store, never by disabling validation. A 401/403 HTTP response points to MCP token validity or scope/account identity policy, not mail credentials.

Structured operation logs contain the tool operation, outcome, elapsed time, safe error code and generated correlation ID only. They go to stderr and omit argument values, identities, provider exceptions and message data. Use synthetic test accounts for diagnosis. OS/network metrics can confirm connectivity without logging IMAP content. Configure proxy access logs to avoid authorization headers and request bodies. Restrict crash dumps, swap and host access. Downstream AI clients have their own retention rules.

On shutdown, stop client traffic then terminate the process/container. Pending operations have cancellation/deadline limits and sessions are disposed. A restart invalidates all in-memory cursors and reconnects lazily. Rotate secrets through their owner and restart; revoke MCP grants/tokens through the identity provider as needed. Back up configuration and secret provisioning, not mailbox copies. Read-only operation does not replace provider backups.

## Validation

Run the complete disposable Linux environment:

```text
docker build -f docker/integration/Dockerfile -t obs-imap-integration .
docker run --rm obs-imap-integration
```

It creates a one-day CA trusted only inside the disposable container, starts Dovecot on private loopback, seeds synthetic mail, then enables the real-server tests. The production image never includes this fixture or CA. Plain host tests report Dovecot tests as skipped unless `OBS_IMAP_INTEGRATION=1` is explicitly set with the fixture available.

## Windows account setup

Windows desktop users can save passwords in Windows Credential Manager instead of forwarding environment variables. Run these commands in an interactive terminal, using the same Windows user that runs the MCP connector:

```text
obs-imap accounts add --config C:\private\obs-imap\accounts.json
obs-imap accounts update-password personal --config C:\private\obs-imap\accounts.json
obs-imap accounts test personal --config C:\private\obs-imap\accounts.json
```

Use the newly built executable (or `dotnet src/OBS.IMAP.Host/bin/Release/net10.0/obs-imap.dll` from the checkout). Existing release archives are not rebuilt by a source build.

`add` prompts for account ID, display name, hostname, port, TLS mode, username, and a hidden password/app password. It creates or updates the specified JSON configuration, stores only a credential reference there, and tests TLS, authentication and read-only INBOX access. It does not retrieve message bodies. Existing explicit local account permissions and remote grants are preserved; grant a new account explicitly when `Local.AllowedAccountIds` is present. Use existing configuration for private-network exceptions; the wizard does not bypass endpoint restrictions.

`update-password` works on an existing password-authenticated account, including accounts currently using `env:` or `file:`. It saves the entered password to Windows Credential Manager and changes that account's reference to `wincred:obs-imap/personal` for account `personal`. Restart the connector after changing configuration/references. Once the reference already uses Credential Manager, updated passwords are read on the next IMAP connection; an existing authenticated session may remain until idle eviction. The old environment variable or secret file is not deleted automatically.

Setup saves before testing: a failed connection test leaves the account and credential available for correction. Credential targets use account IDs within the current Windows user's vault. `add` refuses to overwrite an existing vault target; choose a distinct account ID across configurations or use `update-password` deliberately. If saving configuration fails after storing a credential, the vault entry remains; use Windows Credential Manager to remove it before retrying `add`.

Password prompts require an interactive terminal and do not accept redirected input or password command arguments. Setup is separate from MCP startup and is not exposed as a mailbox tool. Keep passwords out of chat. Credential Manager access depends on the Windows user/logon context; services must provision credentials under their own identity. Linux and containers retain environment and protected-file providers. OAuth2 consent and refresh are still outside this setup flow.

`test` distinguishes a missing/invalid/inaccessible stored credential from a server login rejection, and reports safe error codes for TLS, network and policy failures. A login rejection can require an app password or OAuth2 depending on provider policy.

## Local inbox cleanup automation

A separate, opt-in local operator command supports narrowly configured inbox cleanup. It has independent configuration from [local MCP writes](Configuration.md#local-mcp-writes).

```text
obs-imap accounts cleanup-inbox personal --config C:\private\obs-imap\accounts.json
obs-imap accounts cleanup-inbox personal --config C:\private\obs-imap\accounts.json --apply
```

The default is a preview. `--apply` moves eligible messages from INBOX to the configured folder with the server's Trash special-use attribute. Enable `Connector.EnableInboxCleanup` and an account's `InboxCleanup.Enabled`, `Sender` (one exact address) and `TrashFolder`. Local account grants also apply. The sender check parses the envelope's From list and requires exactly one mailbox matching the configured address, case-insensitively; substring search results, display-name impersonations and multiple From mailboxes do not qualify. This matches the claimed sender address, not authenticated sender provenance.

The command requires native IMAP MOVE. It never falls back to COPY/STORE/EXPUNGE, empties Trash or creates a destination folder. It rechecks UIDVALIDITY after opening INBOX for writes. Writes are not automatically retried. A timeout/disconnection can leave a partially completed outcome: use a fresh scan next run, and never assume an ambiguous operation moved nothing. Trash retention is controlled by the mail provider.

Search uses bounded UID intervals and principal/policy/mode-bound continuations within the process, with at most 100 candidates per page, 100 pages per invocation and a five-minute overall deadline. New arrivals above the first captured UIDNEXT are handled on the next run. Output is JSON with matched/moved counts and `complete`; an incomplete run leaves backlog for the next run. A concurrent external expunge can make the reported accepted move count exceed the number actually moved. No message bodies are retrieved. CLI result counts and safe errors serve as local operation audit output; protect any scheduler logs.

For scheduling, `scripts/Invoke-InboxCleanup.ps1` additionally checks an expected sender and the `Trash` destination before invoking the executable. Supply absolute `-ConfigPath`, `-ConnectorPath`, `-AccountId`, `-ExpectedSender`, and `-Apply`. It requires PowerShell 7.4+. Run under the same Windows user as Credential Manager provisioning. Keep a separate runner output directory to avoid locks from a long-running MCP process during builds. For example, `dotnet publish src/OBS.IMAP.Host/OBS.IMAP.Host.csproj --configuration Debug --no-restore --no-self-contained --output artifacts/cleanup-runner` prepares a framework-dependent runner.

An hourly Codex heartbeat can call that helper. Its availability depends on the local app/computer and access permissions. It should stay quiet on empty successful runs and report moved counts, failures or required user action. This is a local automation, not an always-on hosted service. To stop cleanup, pause/delete the automation or disable the account rule/global flag.
