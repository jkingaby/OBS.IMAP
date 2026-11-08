# ADR-0007: Opt-in local exact-sender inbox cleanup

Status: Accepted. Date: 2026-10-28.

Decision: Add a local operator CLI cleanup operation outside the eight read-only MCP tools. Require a global feature flag, per-account opt-in with one exact sender and designated Trash folder, and normal local account permission. Match a single parsed envelope From mailbox exactly. Use native UID MOVE only, verify Trash special-use and UIDVALIDITY, reuse bounded search/session/network/credential controls, and never automatically retry a write.

This extends ADR-0004's read-only product scope for an explicit local administrative path. The cleanup command has an independent policy from the local MCP write tools described in Security.md.

The scheduler can run a separate local helper that checks the configured sender and destination against the authorized rule and reports counts or failures. Each run is bounded and rescans remaining INBOX messages, making a later run safe after an ambiguous native MOVE response. No permanent-deletion or broad EXPUNGE fallback is allowed. Provider Trash retention remains external.

Validation includes sender spoof/display-name/substring/multiple-mailbox negatives, authorization denial, destination and stale UID rejection, and a synthetic IMAP wire test proving UID MOVE without COPY/STORE/EXPUNGE.
