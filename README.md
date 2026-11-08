# OBS-IT IMAP Connector

**Your email. Your server. Your credentials. Your control.**

Open-source, self-hosted IMAP connector for AI clients using the Model Context Protocol (MCP).

## Project status

Version 0.1.0 includes read-only access by default, opt-in local mail operations, Windows and Linux x64 build targets, Docker packaging, and configuration examples.

Automated tests verify both transports, account isolation, token validation, hostile protocol responses, MIME limits and real Dovecot reads. A 100,000-message fixture checks bounded search and pagination. See [verification evidence](docs/Feasibility.md) for executed checks and compatibility limits.

The product supports Windows and Linux, multiple IMAP accounts, local stdio connections, and authenticated remote Streamable HTTP. It requires no OBS-IT service, registration, subscription, or OpenAI API key. Users separately supply their mail provider and AI client access.

## Usage

Windows users can run `obs-imap accounts add --config C:\private\obs-imap\accounts.json` for interactive setup with a hidden password stored in Windows Credential Manager. To migrate an existing account, use `obs-imap accounts update-password personal --config <absolute-path>`. See [Windows setup and connection testing](docs/Operations.md#windows-account-setup).

1. Build/unpack a Windows or Linux standalone artifact, or run the Docker image. See [installation and operations](docs/Operations.md).
2. Copy [examples/local.json](examples/local.json) to a protected absolute path and define your accounts; supply credentials through secret references.
3. Launch in stdio mode for a local client, or configure OAuth and HTTPS for remote access.

Local registration:

```text
codex mcp add obs-imap -- /absolute/path/obs-imap --transport stdio --config /absolute/path/accounts.json
```

Windows uses an absolute path to `obs-imap.exe`. Keep credentials out of this command and the client configuration; [examples/codex.toml](examples/codex.toml) shows environment-variable forwarding. Remote deployments expose `/mcp` behind HTTPS and require tokens from an operator-selected authorization server. See [client compatibility](docs/ClientCompatibility.md) before planning ChatGPT access.

## Mail operations

The eight default tools are `list_accounts`, `list_folders`, `get_folder_info`, `search_messages`, `get_message_headers`, `get_message`, `get_message_structure`, and `get_attachment`. Message references include account, folder, UIDVALIDITY and UID. Continue while a continuation exists, even after an empty page. Reads use PEEK and leave flags unchanged. HTML is opt-in inert text. Attachments are explicit capped base64 data.

Local stdio can also expose `move_message`, `trash_message`, `create_draft`, and `send_message`. Each operation requires an explicit [per-account opt-in](docs/Configuration.md#local-mcp-writes). Sending uses authenticated SMTP with TLS and requires user confirmation by default. HTTP exposes the read tools. The local operator [inbox cleanup command](docs/Operations.md#local-inbox-cleanup-automation) has its own exact-sender policy.

OAuth2 mail authentication accepts externally provisioned access tokens; consent and refresh are not automated. No content is persisted by the connector. Data returned to a cloud AI client is subject to that client's policies.

## Build and test

```text
dotnet restore OBS-IT.IMAP.slnx --locked-mode
dotnet test OBS-IT.IMAP.slnx --no-restore --configuration Release
docker build -f docker/integration/Dockerfile -t obs-imap-integration .
docker run --rm obs-imap-integration
```

Use the SDK in `global.json`. Host tests explicitly skip real-server tests when the fixture is absent; Docker enables them with its own short-lived CA and synthetic mail. [Publish.ps1](scripts/Publish.ps1) creates standalone packages, notices, SPDX inventories and checksums. See [operations](docs/Operations.md) for the non-root Docker image and protected remote deployment.

## Documentation

- [Vision](docs/Vision.md)
- [Architecture and proposed solution structure](docs/Architecture.md)
- [Threat model and security requirements](docs/Security.md)
- [Configuration](docs/Configuration.md)
- [Installation and operations](docs/Operations.md)
- [MVP and independently testable milestones](docs/Roadmap.md)
- [Client compatibility and research](docs/ClientCompatibility.md)
- [Architectural decisions](docs/Decisions/README.md)

Stack: .NET 10, ASP.NET Core, official .NET MCP SDK, MailKit/MimeKit, Microsoft.Extensions dependency injection/configuration/logging, xUnit, Docker, and GitHub Actions. Central dependency versions and normal/runtime-specific NuGet locks are checked in.

## License

Licensed under [Apache-2.0](LICENSE). See [NOTICE](NOTICE), [third-party notices](THIRD-PARTY-NOTICES.md), [contribution guidance](CONTRIBUTING.md), and [private security reporting](SECURITY.md). OBS-IT marks and third-party rights remain with their owners.
