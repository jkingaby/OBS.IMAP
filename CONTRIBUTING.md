# Contributing

Use .NET SDK selected by `global.json`. Run locked restore and the complete test suite before submitting a change:

```text
dotnet restore OBS-IT.IMAP.slnx --locked-mode
dotnet test OBS-IT.IMAP.slnx --no-restore --configuration Release
docker build -f docker/integration/Dockerfile -t obs-imap-integration .
docker run --rm obs-imap-integration
```

The Docker command enables real Dovecot tests with synthetic messages and a short-lived test CA. It does not access personal accounts. Keep tests deterministic and test access boundaries, failure cases and actual protocol behavior.

Core must remain independent of MailKit and MCP. Preserve read-only PEEK behavior, cancellation, bounded requests, no content persistence and safe diagnostics. Never add real messages or credentials to fixtures. New dependencies need a documented justification and license review. Update relevant ADRs when changing accepted architecture.

Contributions are submitted under Apache-2.0. Contributors retain their copyrights. OBS-IT branding is not licensed for unrelated products. No CLA or account with OBS-IT is required by this repository.
