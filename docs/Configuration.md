# Configuration

Status: implemented initial MVP. Complete examples are in `examples/local.json` and `examples/remote.json`; see Operations.md for startup and OAuth deployment.

Use Microsoft.Extensions.Configuration with an explicit absolute JSON file path (maximum 1 MiB). The root has `Connector`, `Local` and optionally `Remote` sections. Environment variables supply referenced credentials only; there are no environment configuration overrides, dynamic account tools or hot reload. Invalid configuration fails startup with a generic safe diagnostic. Core validators supply field names without values. Duplicate account IDs, unknown credential providers, malformed policy and unsupported security/authentication combinations are rejected.

An account object inside `Connector.Accounts` (property names bind case-insensitively):

```json
{
  "id": "personal",
  "displayName": "Personal mail",
  "host": "imap.example.net",
  "port": 993,
  "security": "SslOnConnect",
  "username": "user@example.net",
  "authentication": "Password",
  "credentialReference": "env:OBS_IMAP_PERSONAL_PASSWORD"
}
```

Account IDs: case-sensitive ASCII letters/digits/hyphen/underscore, 1–64 characters. Hostnames and usernames remain operator configuration, never tool arguments. Default security is implicit TLS on 993; explicitly configured StartTls requires TLS negotiation to succeed, normally on 143. No plaintext mode or StartTlsWhenAvailable. Custom internal CAs use the platform trust store, never disabled validation.

Authentication is either Password (including provider app passwords) or OAuth2 using a referenced access token via MailKit SASL OAuth2. Reject automatic fallback from OAuth2 to password. Password login depends on provider policy. Access tokens expire: initially operators provision/refresh them externally and restart the connector; document this limitation prominently. Do not claim turnkey Gmail/Microsoft consent or refresh support. A later provider extension can supply refreshed tokens behind the credential abstraction.

Credential providers: `env:NAME`, `wincred:obs-imap/ACCOUNT_ID` on Windows, or `file:ALIAS` mapped by trusted operator configuration to a file under the fixed secret root. Check missing, empty and oversized values without including them in errors. Recommended containers use read-only mounted secrets; Windows desktop users can use the account setup commands below. Never put secret values in example configuration, Git, Docker build arguments or image layers.

Local mode uses a fixed identity and an explicit permitted account list (default all accounts in that local configuration, documented as full local-user authority). Remote mode requires issuer, audience, trusted metadata/JWKS locations, read scope and issuer+subject account mappings; omitted mappings grant nothing. Discovery endpoints and signing keys must not be selected by incoming tool arguments. Prevent arbitrary key-discovery URL fetching from untrusted token headers.

`Connector.SecretRoot` is an absolute directory; `Connector.SecretFiles` maps aliases to relative paths within it, for example `{ "personal": "personal-token" }` with `CredentialReference: "file:personal"`. Every ancestor must be protected: Linux ownership must be root or the service UID, directories cannot be group/other writable, and files cannot grant group/other access. Linux owner verification requires kernel/libc `statx` support and fails closed if unavailable. Windows rejects sensitive permissions/ownership for identities other than the service user, SYSTEM, Administrators and TrustedInstaller. Symlinks/reparse points and Linux special files are rejected. Shared temp/workspace directories can therefore be refused even if the individual file is restricted. Prefer a dedicated protected directory or environment injection. Same-user/administrator compromise remains outside the boundary.

## Local MCP writes

The four write tools are available over local stdio. Each operation defaults to disabled. Add a `Writes` block to an existing account in the protected configuration, then restart the connector and reconnect the MCP client. `Local.AllowedAccountIds` also applies. Remove the block or turn off individual flags and restart to revoke access. Discovery lists a tool only if a locally granted account enables it.

Example account properties to add alongside the existing IMAP settings:

```json
{
  "Writes": {
    "Move": true,
    "Trash": true,
    "CreateDraft": true,
    "Send": true,
    "TrashFolder": "Trash",
    "DraftsFolder": "Drafts",
    "FromAddress": "user@example.net",
    "RequireSendConfirmation": true,
    "Smtp": {
      "Host": "smtp.example.net",
      "Port": 465,
      "Security": "SslOnConnect",
      "Username": "user@example.net",
      "Authentication": "Password",
      "CredentialReference": "env:OBS_SMTP_PERSONAL_PASSWORD"
    }
  }
}
```

- `move_message` accepts a message reference and an existing destination folder in the same account. Moving to a recognized Trash destination also requires `Trash`.
- `trash_message` uses the configured Trash folder or the server's Trash special-use folder. Trash retention follows provider policy.
- `create_draft` uses the configured Drafts folder or the server's Drafts special-use folder. It accepts plain text, subject, and up to 50 recipient addresses. A draft may have an empty recipient list.
- `send_message` requires at least one recipient and an SMTP configuration. `FromAddress` defaults to the IMAP username if it is a bare email address. SMTP accepts `Password` or externally provisioned `OAuth2` and uses a credential reference. Use the provider's TLS submission settings, typically `SslOnConnect` on 465 or `StartTls` on 587. A private SMTP endpoint requires its own `Writes.Smtp.AllowedPrivateNetworks` exception.

Send confirmation defaults to required. A client that supports MCP form elicitation displays the From address, recipients, subject, and body. The user must select **Send this email** within the operation timeout. Clients without compatible elicitation receive `confirmation_required`. Set `RequireSendConfirmation` to `false` only for an account where the operator intends to let the client submit directly.

Move and Trash require native IMAP MOVE. A successful write returns a destination message reference when supplied by the IMAP server, or a Message-ID after SMTP accepts submission. A `write_outcome_unknown` error calls for checking the mailbox or provider before repeating the operation. Draft and send currently accept plain text and To recipients.

## Limits

| Setting | Default / hard ceiling proposal |
| --- | --- |
| Search page | 50 / 200 summaries |
| UID search work | 10,000 UID interval width; 20 intervals per request |
| Active connections | 1 per account; 8 globally |
| Waiting requests | 32 globally; reject overload |
| Connect/auth deadline | 15 seconds, bounded by overall deadline |
| Overall tool deadline | 30 seconds; maximum 120 seconds |
| Idle session eviction | 2 minutes |
| Source message size | 25 MiB advertised maximum; enforce wire limits as well |
| Selected MIME-part wire bytes | 8 MiB per request |
| Decoded text | 256 KiB returned, with truncation indicator |
| Decoded attachment | 2 MiB; oversized data rejected |
| Total serialized tool response | 4 MiB including base64 and metadata |
| Header data | 64 KiB aggregate; 8 KiB per field |
| MIME structure | 200 parts; depth 20 |
| Folder page | 100 / 500 entries |
| Continuation lifetime | 15 minutes; invalid after process restart |
| HTTP request body | 64 KiB |
| Remote rate | 60 requests/minute per principal, burst 10; global cap 120/minute |

Defaults are defined in `ConnectorLimits` and can be lowered through `Connector.Limits` using those exact property names, such as `MaxSearchPageSize`. Configured limits cannot exceed validated hard ceilings. The protocol guard also enforces `MaxProtocolBytes` (16 MiB per operation), aggregate literal bytes (8 MiB), metadata literal/line limits, and lexical nesting before MailKit parses data. Secrets have a 16 KiB cap. A 100,000-message fixture measures bounded summary-search allocations; this does not guarantee every provider or deployment's performance.

Folder enumeration uses server LIST followed by output pagination; the inbound protocol budget bounds the full LIST response. If a folder tree exceeds that budget, narrow the parent or receive `limit_exceeded`; the connector does not persist a folder index. Remote limits are fixed initially: 64 KiB HTTP body, 120 requests/minute globally, and a per-identity token bucket of 10 burst/1 token per second. HTTP rate rejection is 429. No limit accepts an unlimited value.

### Windows Credential Manager

Windows also supports `wincred:obs-imap/ACCOUNT_ID`. The target is restricted to the connector namespace and a valid account ID. Use the local `accounts` setup commands in [Operations](Operations.md) to provision or migrate a password without putting it in JSON, environment variables, command arguments or MCP calls. Generic credentials persist for the current Windows user on this machine; they are not available automatically to other service identities or Linux containers. Native credential blobs are capped at 2,560 bytes (UTF-16), further constrained by the configured secret limit on retrieval. Native buffers are cleared after use; managed strings cannot guarantee zeroization.

Credential values are read for each new IMAP authentication, so an existing vault reference can be rotated without restarting. Account configuration remains static and reference changes require restart.

## Local cleanup permission

Inbox cleanup is disabled by default. The local operator must explicitly set:

```json
{
  "Connector": {
    "EnableInboxCleanup": true,
    "Accounts": [{
      "id": "personal",
      "inboxCleanup": {
        "enabled": true,
        "sender": "sender@example.test",
        "trashFolder": "Trash"
      }
    }]
  }
}
```

This is a partial illustration; retain all normal account fields. The setting grants only the local `accounts cleanup-inbox` command permission to move exact-sender matches from INBOX to a validated Trash destination. It does not enable writes through MCP or HTTP. Existing `Local.AllowedAccountIds` restrictions still apply. Restart is unnecessary for the one-shot CLI, which reloads configuration each invocation. See [cleanup operations](Operations.md#local-inbox-cleanup-automation).
