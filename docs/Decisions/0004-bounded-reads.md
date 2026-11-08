# ADR-0004: Read-only UID model with bounded work

Status: Accepted. Date: 2026-10-25. Accepted by OBS-IT through architecture review approval in this chat.

Context: Large mailboxes and hostile MIME can exhaust memory; IMAP sequence numbers change as messages are expunged.

Decision: Use account/folder/UIDVALIDITY/UID references, serialized reusable sessions, read-only folders, PEEK and selected MIME-part reads. Search server-side within bounded UID intervals, return authenticated continuations and explicit truncation. Reject oversized attachments and reset stale references. No unrestricted UID-list materialization, raw search syntax, persistent content or index.

Alternatives: Full search then offset pagination is convenient but unbounded. Local indexing improves repeated search but adds storage, privacy and synchronization obligations. Optional ESEARCH/SORT optimizations can be added after generic behavior works.

Consequences: Results use UID order and can have short/empty pages with continuation. Mailbox changes prevent snapshot guarantees. Streaming limits and bounded allocation must be proven, not inferred from async APIs. Read-only tools must leave flags unchanged. See Architecture.md for cursor semantics and Configuration.md for proposed limits.
