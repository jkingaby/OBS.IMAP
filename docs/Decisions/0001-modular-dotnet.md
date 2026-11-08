# ADR-0001: Modular .NET solution

Status: Accepted. Date: 2026-10-25. Accepted by OBS-IT through architecture review approval in this chat.

Context: The IMAP layer must support future interfaces without depending on MCP, with Windows/Linux distribution and a small dependency footprint.

Decision: Use .NET 10 and four projects: Core, Infrastructure, MCP and Host. Use MailKit/MimeKit in Infrastructure, official MCP packages in MCP/Host and Microsoft.Extensions for composition. Core owns transport-independent DTOs, policy and service contracts. Add no database or background synchronization.

Alternatives: A single project is initially simpler but makes dependency boundaries harder to enforce. More domain/application/repository projects add overhead without present requirements. Provider-specific HTTP mail APIs reduce generic IMAP compatibility.

Consequences: Clear compile-time boundaries with a small amount of mapping code. Host is the only composition root. Pin package versions after feasibility testing and review transitive dependencies/licenses. Verify Core can be tested with no MCP or MailKit references.
