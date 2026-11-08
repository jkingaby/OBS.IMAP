# Client compatibility and research

Research date: 2026-10-25. These are documentary findings about target clients. The official .NET MCP client verifies the implementation's stdio/HTTP behavior, but a particular Codex session, ChatGPT workspace and external OAuth provider still need deployment tests. Recheck official client documentation before deployment.

## Codex

Official documentation describes local process registration and Streamable HTTP configuration, including OAuth login and environment-sourced bearer credentials. The README command follows that pattern and uses implemented product flags. No OpenAI SDK is required inside this connector. [Official Codex MCP documentation](https://developers.openai.com/codex/mcp/).

## ChatGPT

OpenAI documents custom MCP server connections on the web, streaming HTTP and SSE, configurable OAuth authentication, and workspace restrictions. We choose Streamable HTTP; we do not build legacy SSE merely because it is supported by a client. Availability and permission to install/use a custom server depend on the user's workspace and security settings; this package does not promise all plans, surfaces or modes. [Official custom MCP connection guide](https://developers.openai.com/api/docs/guides/custom-mcp-server).

A direct deployment needs a reachable HTTPS URL and compatible OAuth setup. ChatGPT does not launch this product's stdio executable. Do not assume a localhost URL is reachable or a Codex bearer-token configuration is portable to ChatGPT. Verify discovery, client registration, callback configuration and tool results with the target workspace before advertising compatibility.

OpenAI also documents Secure MCP Tunnel for connecting private servers. Treat it as an optional client-side deployment choice with its own identity and policy requirements, not a product dependency. Validate the documented tunnel authentication behavior before recommending a configuration; preserve the connector's explicit mailbox authorization boundary. [Official Secure MCP Tunnel guide](https://developers.openai.com/api/docs/guides/secure-mcp-tunnels).

A generic HTTPS tunnel is another reachability option; OpenAI's quickstart illustrates one for development. Such a tunnel can expose the service publicly and does not replace OAuth. Never copy an unauthenticated demo deployment for real email. [Official MCP quickstart](https://developers.openai.com/plugins/build/app-quickstart).

## SDK and IMAP feasibility

The official C# SDK separates the base hosting/stdio package from ASP.NET Core HTTP support. Use shared tool definitions and select one transport at startup. Its current transport guide describes stateless HTTP and legacy compatibility options; pin versions and test negotiated protocol revisions rather than relying on moving defaults. [SDK package guide](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md), [transport guide](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html).

MCP authorization defines a resource-server boundary with discovery and audience-bound tokens. Delegate authorization-server responsibilities to operator infrastructure while implementing required resource metadata and token validation in the host. The selected provider must demonstrate a registration mechanism supported by the target client. [Official MCP authorization specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).

MailKit exposes typed search queries and asynchronous UID-based body-part retrieval. These support the proposed design, but documented APIs alone do not establish bounded allocation behavior or every server's extensions. The implementation spike must prove bounded streams, PEEK, TLS hostname preservation, cancellation, and UIDVALIDITY handling. [SearchQuery](https://mimekit.net/docs/html/T_MailKit_Search_SearchQuery.htm), [GetBodyPartAsync](https://mimekit.net/docs/html/M_MailKit_IMailFolder_GetBodyPartAsync.htm).

## Compatibility acceptance matrix

| Target | Required evidence before release |
| --- | --- |
| Codex local, Windows/Linux | Spawn, initialize, list tools, search/read, cancel, stderr-only diagnostics |
| Codex remote | HTTPS, OAuth login/discovery, account isolation, expired-token denial |
| ChatGPT direct | Target workspace installation, OAuth handshake, tool discovery and bounded results |
| ChatGPT Secure MCP Tunnel | Availability, documented identity validation and negative access tests |
| IMAP providers | TLS password and externally provisioned OAuth2 token; document provider restrictions |

Record client version/date, SDK version, negotiated protocol, deployment/auth method and observed failures. A failed optional tunnel test does not block the direct authenticated deployment, but must remain explicitly unsupported.
