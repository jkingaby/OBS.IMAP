# Reporting security issues

Do not open a public issue containing credentials, personal email, tokens, or an exploitable vulnerability.

For a public GitHub distribution, use the repository's **Security → Advisories → Report a vulnerability** private reporting feature. OBS-IT must enable private vulnerability reporting before publishing the repository. If private reporting is unavailable, contact the repository owner through an established private channel and send only a request for secure contact details first.

Include affected version, deployment/transport, minimal synthetic reproduction and impact. Do not send real mailbox content. Rotate exposed credentials immediately through their provider. Only the current initial release is intended to be supported; there is no historical release support commitment yet.

See [the threat model](docs/Security.md) for deployment requirements and product boundaries. Do not disable TLS validation or expose anonymous mailbox tools when troubleshooting.
