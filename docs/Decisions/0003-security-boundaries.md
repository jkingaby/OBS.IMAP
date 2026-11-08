# ADR-0003: Operator secrets and explicit remote authorization

Status: Accepted. Date: 2026-10-25. Accepted by OBS-IT through architecture review approval in this chat.

Context: IMAP credentials must never come from tools, and authentication to MCP does not itself authorize every mailbox.

Decision: Credential abstraction with environment and restricted mounted-file providers first. No homegrown encrypted vault. Password and externally supplied OAuth2 access tokens initially; refresh adapters later. Remote Host acts as OAuth resource server using operator-selected authorization infrastructure and stable issuer+subject account allowlists. Core service policy rechecks account access. Startup fails when remote authentication is absent. Inbound MCP and outbound IMAP tokens are separate.

Alternatives: OS vaults offer stronger local integration but diverge across platforms. Bundling an authorization server expands product scope substantially. Static API keys simplify some Codex setups but do not establish ChatGPT OAuth interoperability. Anonymous public access is unacceptable for this product.

Consequences: Operators must provision secrets, rotate mail tokens and supply a compatible OAuth server for remote use, potentially self-hosted. Local mode remains independent of that infrastructure. Files/env are not inherently encrypted. Tunnel reachability does not replace policy. Verify token discovery, registration and audience handling before promising client support.
