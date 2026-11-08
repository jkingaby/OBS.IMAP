# MVP scope and implementation roadmap

Status: initial read-only MVP implemented locally on 2026-10-25. Core/configuration, IMAP reads, shared tools, both hosts, packaging and CI definitions are present. Feasibility.md records executed checks. Public distribution and testing a particular OAuth provider/ChatGPT workspace remain operator release/deployment steps.

The release MVP includes all eight read tools in Architecture.md, multiple accounts, password and externally provisioned OAuth2-token authentication, both transports, remote account authorization, bounded server-side search, selected MIME retrieval, attachment metadata/data and header-based thread hints. No complete thread reconstruction, refresh-token onboarding, writes, SMTP, indexing or UI.

| Milestone | Deliverable | Independently testable exit criteria |
| --- | --- | --- |
| 0: design | Review package and proposed ADRs | Requirements traced to design; OBS-IT reviews decisions and scope |
| 1: feasibility | Disposable prototypes and dependency/version record | MCP SDK builds on .NET 10; both transports initialize; real target client OAuth; MailKit bounded fetch, PEEK, DNS pinning/SNI, cancellation and UID search proven |
| 2: core/config | Solution, contracts, validation, credential providers and policies | xUnit tests cover duplicates, insecure TLS, malformed IDs, secret paths, denied accounts and limit bounds; no live server required |
| 3: IMAP reads | Serialized reusable sessions, folders, search and MIME | Disposable IMAP fixture proves auth, folder hierarchy, stable UID paging, UIDVALIDITY changes, flags unchanged, MIME/attachment decoding, reconnect and cancellation |
| 4: local MCP | Shared tools and stdio Host | MCP client contract tests cover schemas, safe errors, cursors, truncation and stdout cleanliness; Codex smoke test on Windows/Linux |
| 5: remote MCP | OAuth resource server, HTTPS deployment guide | HTTP integration tests for 401/403, issuer/audience/expiry, subject/account isolation, Origin/Host/proxy validation and rate limits; target ChatGPT direct smoke test |
| 6: release | Binaries, container, examples and operations docs | Clean-machine Windows/Linux install tests; non-root container; checksums/SBOM/notices; no secrets/content in artifacts or diagnostics; compatibility matrix populated |

Milestone 1 may use throwaway test code after approval; it is not permission to skip the security acceptance criteria. If bounded MIME handling or client OAuth interoperability fails, revise the ADR before building around an unproven path.

## Automated testing and CI plan

Use xUnit for pure Core policy and validation tests, Infrastructure MIME fixtures and credential tests, MCP contract tests, and integration tests against a pinned disposable Dovecot test image with synthetic mail. Avoid production mail in tests. Local Windows tests can target the same Linux test container where Docker is available; CI Linux integration is mandatory, Windows unit/build coverage mandatory.

Fixtures include invalid credentials, expired OAuth token behavior (fake SASL endpoint where Dovecot OAuth configuration is impractical), TLS failure, disconnection mid-fetch, huge headers, nested MIME, dishonest size declarations, binary attachments, duplicate Message-IDs, expunges and sparse/high UID folders. Provider OAuth end-to-end smoke tests are separate from protocol doubles and require operator-owned test accounts; never claim a fake verifies a real provider.

Test cursor tampering, changed filters, expiry, cross-principal replay and UIDVALIDITY reset. Large-mailbox tests generate at least 100,000 synthetic messages, measure peak memory/network bytes and assert bounded per-request work rather than fragile timing alone. Include an adversarial IMAP response fixture to verify allocation limits against a server that lies. Assert sentinels for credentials and email bodies never appear in captured logs/errors.

GitHub Actions on pull requests: restore locked dependencies, build and test Windows/Linux, Linux disposable-server tests, Markdown link checks and secret scanning. Store test reports with no message content. Release automation on approved version tags publishes self-contained win-x64 and linux-x64 binaries first; document runtime/architecture support and add arm64 only when tested. Use a non-root .NET 10 runtime container, read-only filesystem and mounted config/secrets; no persistent mailbox volume. Pin dependencies and base images, generate SBOM and checksums, scan vulnerabilities and record exceptions. Restrict release token permissions; prefer short-lived publishing identity and provenance attestations.

## Review decisions

Confirm read-only MVP, external OAuth authorization-server prerequisite for remote use, operator-managed OAuth2 mail tokens initially, proposed limits, and Apache-2.0 recommendation. Public release also needs LICENSE, notices, CONTRIBUTING, a security reporting policy/channel and release notes. Later milestones can add refresh adapters, OS secret stores, writes with dedicated authorization, or optimized ESEARCH/SORT after measured demand.
