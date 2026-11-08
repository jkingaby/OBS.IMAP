# ADR-0006: Windows desktop credential setup

Status: Accepted. Date: 2026-10-28. User approved the proposed Windows Credential Manager provider and local setup/test commands.

Decision: Extend the credential abstraction with `wincred:obs-imap/ACCOUNT_ID` using native generic credentials under the current Windows user. Add local interactive `accounts add`, `accounts update-password` and noninteractive `accounts test` commands. Password input is hidden and never supplied as an argument or MCP input. Keep environment/file providers for existing deployments. Preserve existing account grants during setup.

This extends ADR-0003's initial environment/file provider selection. It does not add a custom encrypted vault, dynamic MCP account administration or OAuth onboarding. Account configuration changes still require restart; vault password changes are picked up at the next authentication. Existing sessions remain authenticated until closed. Vault targets are shared by account ID within one Windows user's credential set.

Validation: synthetic native save/read/rotation/collision tests and CLI subprocess tests; no production credentials were changed. Windows service identities and interactive user identities need separate provisioning.
