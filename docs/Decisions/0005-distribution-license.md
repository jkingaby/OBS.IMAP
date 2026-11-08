# ADR-0005: Independent distribution and Apache-2.0 license

Status: Accepted. Date: 2026-10-25. Accepted by OBS-IT through architecture review approval in this chat.

Context: OBS-IT intends a public open-source product that users can run without OBS-IT infrastructure. License selection must precede public distribution.

Decision: Apache-2.0, approved by OBS-IT. Compared with MIT's concise permissive terms, Apache-2.0 provides an explicit patent grant and patent-related termination provisions, at the cost of more detailed notice obligations. Neither establishes trademark permission. The repository LICENSE carries the license grant; original third-party license/notice files are included in distribution packages.

Distribution: Self-contained Windows/Linux executables, Docker, public GitHub source, semantic versions and release notes. No registration, telemetry service, proprietary gateway or hosted dependency owned by OBS-IT. External mail, identity and optional tunnel infrastructure are operator-selected.

Consequences: LICENSE/NOTICE materials, contribution policy, security reporting instructions, reproducible publishing scripts, dependency inventories and checksums are present. Signed public release provenance and hosting configuration remain publication steps. No public repository or release is created by the implementation task.
