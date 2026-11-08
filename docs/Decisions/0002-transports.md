# ADR-0002: Shared tools, selectable stdio and Streamable HTTP

Status: Accepted. Date: 2026-10-25. Accepted by OBS-IT through architecture review approval in this chat.

Context: Local Codex should not require a web server; remote clients need network transport and authorization.

Decision: One executable selects exactly one transport at startup. Default stdio, protocol-only stdout and logging to stderr. Remote mode uses ASP.NET Core and official MCP Streamable HTTP, preferably stateless, sharing all tools. Keep account connection reuse independent of HTTP transport sessions. No legacy SSE, UI, sampling or resource subscriptions initially.

Alternatives: Separate executables reduce package overlap but duplicate startup and releases. HTTP-only imposes unnecessary local setup. Legacy SSE adds session and proxy complexity.

Consequences: Test both transports against a pinned SDK and actual clients; do not assume SDK defaults or latest protocol revisions are universally supported. Secure remote mode before exposing email. Compatibility findings and sources are in ClientCompatibility.md.
