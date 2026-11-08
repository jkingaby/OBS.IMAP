# Threat model and security requirements

Status: implemented initial MVP controls, 2026-10-25. Verification and deployment-specific checks are recorded in Feasibility.md. This states intended boundaries, not a third-party security certification.

## Assets and trust boundaries

Protect mail credentials, message data, account membership, OAuth tokens, and host network access. Trust the deployment operator and protected configuration, not email senders, MCP arguments, remote users, DNS answers, or an IMAP server's metadata. An operator with host administration can read secrets and change policy; the MVP does not defend against a compromised host or offer strong tenant isolation. Prefer separate deployments for unrelated owners.

| Threat | Required control and verification |
| --- | --- |
| Remote mailbox disclosure | Require authenticated HTTP access and server-side principal-to-account allowlists; test list and direct access denial across two identities |
| Cross-account cursor or session confusion | Bind cursor to principal/account/folder/filter; per-account serialized sessions; test replay and simultaneous folder operations |
| Credential theft | Secret references only; restricted OS permissions; no secret-bearing command arguments, protocol logs, exceptions or response fields; test sentinel secret redaction |
| Malicious host configuration / SSRF | No tool accepts endpoints. Validate operator-defined endpoint policy and actual resolved IP before every connection; enforce egress policy and test DNS changes |
| DNS rebinding and proxy spoofing | Explicit AllowedHosts and Origin allowlist; reject untrusted supplied Origin, validate trusted proxy forwarding only; loopback default |
| Oversized or malicious MIME | Bound wire bytes, decoded bytes, header length, part count/depth, JSON/base64 output and execution time; adversarial fixtures |
| Prompt injection | Label message and attachment content as untrusted data; static tool metadata; never interpret mail as access policy, instructions, URLs to fetch or commands |
| Resource exhaustion | Concurrency, queue, request rate and payload limits; bounded UID search and timeouts; large/sparse folder tests |
| Silent mutation | Read-only folder selection and PEEK; verify Seen and all other flags unchanged before/after retrieval |
| Transport interception | Mandatory TLS, hostname and chain validation; no accept-all callback, no opportunistic downgrade; invalid-certificate tests |

## Remote authorization

Remote mode is opt-in and fails startup without validated authentication configuration. Use OAuth-protected resource discovery and an operator-selected authorization server. Validate issuer, audience/resource, signature, allowed algorithms, token lifetime and required scopes on every request. Use stable issuer+subject mappings to explicit account IDs; unknown identities and accounts fail closed. Filter discovery to authorized accounts, and reauthorize every retrieval. Avoid returning different existence details for unauthorized accounts.

Keep MCP access tokens separate from mail provider OAuth tokens. Never pass a client token through to IMAP. Sessions/cursors are not authorization. Do not allow anonymous mailbox tools, including through a tunnel. Metadata needed for OAuth discovery may be public without mailbox information. Protect MCP initialization and tool discovery where compatible with the negotiated protocol; verify client behavior in the transport spike.

Recommended deployment: loopback backend, HTTPS reverse proxy, trusted proxy list and explicit external resource URL. A container may bind its internal interface only with authenticated backend and documented proxy/network isolation. A public tunnel URL supplies reachability, not mailbox authorization. Secure MCP Tunnel is optional and needs separate identity verification tests. Browser login redirects at a generic access proxy may be incompatible with MCP OAuth clients.

## Outbound network policy

Require exact configured host/port pairs; reject URL syntax, userinfo, embedded control characters and disallowed ports. Resolve all A/AAAA results and reject loopback, link-local, multicast, unspecified, reserved and private addresses by default, including IPv4-mapped IPv6. Private/on-premises mail requires explicit host-scoped CIDR exceptions. Pin the validated IP for the socket connection while retaining the original hostname for SNI and certificate validation; test the actual MailKit connection path before approval. Revalidate reconnects, disable referrals to unapproved destinations, and recommend an OS/container egress firewall as defense in depth. Configuration validation alone does not defeat DNS rebinding.

## Secrets and content handling

Start with environment-backed credentials and operator-provisioned read-only secret files, resolved through a provider abstraction. These are not an encrypted vault: encryption at rest comes from OS/disk/orchestrator controls. Do not create custom encryption with a key beside the ciphertext. Validate file locations under a configured secret root, deny traversal and symlink escapes, cap file size, and require restrictive ACLs/modes where verifiable. An account reference cannot name an arbitrary file path. Restart to rotate initially; minimize lifetime and copies of secrets, recognizing managed strings cannot guarantee zeroization.

No persistence of messages or attachments, no temp-file spooling, no protocol tracing, no telemetry by default. Reverse proxies, crash dumps, swap and AI clients may retain data independently; operators must configure those systems. Operation logs contain only tool operation, outcome, duration, safe error code and correlation ID. They omit account/identity and argument values. Set retention in the stderr log collector. Never log query strings, sender addresses, subject lines, tokens or bodies.

HTML is returned only on explicit request as inert text, never rendered or fetched. Do not resolve remote images, open attachment files, unpack archives or execute active content. MIME filenames and headers are untrusted and length-limited. Prompt-injection labeling reduces ambiguity but cannot guarantee safe downstream model behavior; client permissions remain essential.

## Write authorization

Local MCP write tools require an operator-controlled per-account and per-operation opt-in. The stdio identity must have the account grant. HTTP identities have read access only. The separate `accounts cleanup-inbox` command follows its exact-sender policy and explicit local operator opt-in. See [ADR-0007](Decisions/0007-local-inbox-cleanup.md).

Move and Trash validate the UID and UIDVALIDITY, require native UID MOVE, and use existing destinations. Draft creation and SMTP submission bound recipients and text size and use the configured From address. SMTP uses the configured endpoint, DNS pinning, mandatory TLS and provider authentication. Confirmation defaults to required and uses MCP form elicitation. Enabling any write permission requires trusted configuration. Write operations are logged using the same content-free audit fields as reads and have no automatic retry.
