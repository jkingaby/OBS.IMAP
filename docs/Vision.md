# Product vision

Status: proposed for review, 2026-10-25.

OBS-IT IMAP Connector lets individuals and small teams grant AI clients deliberate, limited access to existing mailboxes without moving their mailbox into a new hosted service. Operators control deployment, credentials, account selection, and access policy. The connector remains usable independently of OBS-IT and independent of any particular AI vendor.

Primary audiences are technical individuals running local Codex, administrators hosting a private remote endpoint, and developers embedding the transport-independent IMAP services in future applications. A managed multi-tenant mail platform is outside the initial product boundary.

Success means an operator can install a standalone binary on Windows or Linux, configure two different IMAP accounts, use the same read tools locally and remotely, and remove the connector without mailbox changes or retained message content. Remote users must see only their explicitly assigned accounts. Large folders must not cause unbounded downloads or allocations.

Privacy has a practical limit: tool results are delivered to the selected AI client and may be processed or retained under that client's policies. Self-hosting keeps credentials and connector operation under operator control; it does not make information returned to a cloud AI stay local. Tunnel and identity providers introduce their own trust relationships.

The MVP favors explicit retrieval over background synchronization and predictable bounded operations over unlimited searches. Email content is evidence supplied by an untrusted sender, never instructions that change connector configuration or permissions.

Excluded initially: sending email, mailbox writes, attachment execution or preview, malware scanning, local indexing, web administration, provider-specific consent UIs, and automatic OAuth refresh workflows. Generic OAuth2 access-token authentication is included; provider onboarding and renewal remain operator responsibilities until a later milestone.
