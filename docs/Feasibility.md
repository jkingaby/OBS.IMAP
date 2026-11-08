# Verification evidence and compatibility boundaries

Date: 2026-10-25. Status: initial read-only MVP implemented and tested locally; no public release published.

OBS-IT approved the architecture and authorized implementation. Four production projects implement the service and both transports; the isolated probe remains regression coverage.

## Verified locally

Windows x64 and Ubuntu 24.04 Linux containers, .NET SDK 10.0.401/runtime 10.0.12, Docker 29.6.2 and Dovecot 2.3.21. Dependencies include MCP SDK 2.3.0, MailKit 4.18.2, JwtBearer 10.0.12, test SDK 18.11.0, xUnit 2.9.3 and runner 4.0.1. Normal/runtime-specific NuGet locks capture dependency graphs. Warnings are errors. Docker bases use verified immutable digests.

The original five protocol tests remain passing:

1. A spawned stdio server initializes, discovers its sole diagnostic tool and returns the expected tool result through the official SDK client.
2. The same tool initializes, lists and runs over actual loopback Streamable HTTP.
3. A synthetic IMAP fixture observes EXAMINE, UID SEARCH and partial BODY.PEEK commands; the client receives the expected UIDVALIDITY and selected part bytes.
4. Cancellation interrupts an IMAP connection waiting for a greeting.
5. A preconnected loopback socket retains the configured IMAP hostname in TLS SNI and rejects an untrusted certificate using default certificate validation.

Production suites add configuration/identifier/limit validation, account authorization, principal-bound HMAC cursors, sparse and 150,000-UID pagination, IPv4/IPv6 restrictions, secret providers and MIME limits. Actual adversarial MailKit servers declare 2 GB literals or recursively nested BODYSTRUCTURE including zero-length literals. The guard rejects them before payload allocation/recursive parsing. Plaintext/PREAUTH is confined to synthetic fixtures; production is TLS-only.

Production stdio tests initialize the actual host. HTTP tests use Kestrel and signed JWTs to check account isolation, concurrent identities, discovery/challenges, Host/Origin and rate limits. Negatives cover wrong issuer/audience/expiry/signature, unsigned tokens, missing scope and symmetric-algorithm confusion. These verify the resource server, not an arbitrary provider's browser consent flow.

The disposable Dovecot suite passes eight tests with zero skips: all eight reads, unchanged flags and accurate unread counts, Unicode/plain/HTML and attachment decoding, pagination/filtering, stale UIDVALIDITY and cross-principal cursor rejection, bad-password safe errors, hostname rejection and required STARTTLS. A complete production stdio-to-MCP-to-Dovecot test retrieves actual fixture content and checks private diagnostics. A separate 100,000-message folder verifies bounded UID work, descending continuation and empty pages with continuation. Three summary searches allocate less than 64 MiB after folder warmup without downloading bodies. The one-day CA and CRL endpoint exist only inside the disposable container; certificate and revocation validation stay enabled.

Windows standalone startup passes. Linux standalone startup passes in a non-root, read-only, no-network container with the installed .NET runtime hidden by an empty mount. The production chiseled container starts as UID 1654 under the same restrictions. Both archives include notices, file/archive SHA256 checksums and SPDX 2.3 inventories validated against the official schema. The inventory covers application/NuGet/runtime packs, not OS or container base-image contents.

## Reproduce

```powershell
dotnet restore OBS-IT.IMAP.slnx --locked-mode
dotnet test OBS-IT.IMAP.slnx --no-restore
docker build -f docker/integration/Dockerfile -t obs-imap-integration .
docker run --rm obs-imap-integration
```

Use .NET 10 SDK and NuGet access. Local verification used workspace-scoped DOTNET_CLI_HOME/NUGET_PACKAGES under ignored `.local/`; these are optional. The sandbox required elevated execution for network package restore and child-process/socket tests. No packages are vendored. CI is configured to run the same locked restore and tests on Windows and Linux, but no remote CI run has occurred.

## Deployment-specific checks and limitations

- No production mailbox was accessed. Provider password/app-password/OAuth2 acceptance depends on provider policy. Dovecot password/TLS success does not establish provider OAuth2 onboarding. Tokens are provisioned externally; automated consent/refresh is outside scope.
- The official .NET MCP client verifies protocol behavior. A user-configured Codex session and target ChatGPT workspace have not been connected. Access settings, protocol revisions, callback registration and optional tunnel identity behavior need deployment smoke tests.
- No operator OAuth issuer, public HTTPS endpoint or target workspace was supplied. JWT validation is tested; browser authorization with the selected identity provider remains an operator check. No anonymous workaround is allowed.
- At initial verification, the GitHub Actions definitions and equivalent local commands were present. Remote CI and release publishing were outside that verification run.
- Synthetic adversarial and allocation tests are not exhaustive fuzzing or independent penetration testing. Profile aggregate concurrency under the intended workload.

Use the production Host for deployment, never the diagnostic probe. No OBS-IT infrastructure is required. Client/identity/tunnel services remain operator-selected trust relationships.

## Windows credential setup verification — 2026-10-28

On Windows x64 with .NET SDK 10.0.402, the Release solution test run passed 98 tests; eight Dovecot tests were skipped because the disposable fixture was absent. New checks exercise native Windows Credential Manager Unicode password storage/readback, rotation without provider restart, missing credentials, target collisions and size rejection, plus reference validation and CLI subprocess help/missing-credential/redirected-input behavior. The synthetic vault entry is deleted after the test. Interactive wizard input and a production-provider login with newly stored credentials remain manual checks. Existing release archives and the running MCP registration were not replaced.

## Local cleanup verification — 2026-10-28

The final Debug solution suite passed 117 tests with eight Dovecot skips, including 16 focused cleanup cases and CLI denial checks. Local links in 21 Markdown documents passed validation. The Release build was blocked by assemblies locked by the running MCP connector, so a separate Debug publish was used for the local framework-dependent cleanup runner. Docker Desktop's Linux engine was unavailable; no new Dovecot run is claimed.

New coverage checks exact parsed sender matching, display-name and substring impersonation rejection, single-mailbox requirements, global/account/principal permission denial, stale UIDVALIDITY and Trash validation. A synthetic native MOVE wire test confirms targeted UID MOVE and no COPY/STORE/broad EXPUNGE.

## Read-path verification — 2026-11-08

On Windows x64 with .NET SDK 10.0.403, the Release solution suite passed 120 tests. The synthetic IMAP fixture covers multiple personal namespaces, overlapping LIST results, fresh read-only selection, and the bounded UID fallback. Docker's Linux engine was unavailable, so eight Dovecot tests were skipped, including the extended fixture case for named inline text attachments.

The registered native MCP connector returned INBOX metadata and an empty result for a unique subject probe. The isolated Release build also passed two fresh searches on the same stdio session and a folder discovery call against the configured account. No message bodies, attachment bytes, or credentials were written to verification logs or artifacts.

## Local MCP write verification — 2026-11-08

The Windows Release solution suite passed 129 tests. Synthetic IMAP wire tests exercised UID MOVE, rejection of stale UIDVALIDITY and missing MOVE support, and APPEND with the Draft flag. The synthetic SMTP fixture tested TLS, authentication, submission and connection loss after DATA. Stdio tests exercised tool discovery, per-account denial, approval, decline, unsupported elicitation and explicit opt-out. HTTP discovery was verified with writes enabled in account configuration.

Eight Dovecot tests were skipped because Docker's Linux engine was unavailable. A native read-only INBOX metadata call passed. The rebuilt server exposed eight read-only tools with the existing configuration and passed the same metadata check. All write and submission tests used synthetic messages and credentials.
