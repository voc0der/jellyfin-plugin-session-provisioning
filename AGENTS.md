# AGENTS.md

Read `CLAUDE.md`, `docs/ARCHITECTURE.md`, and `docs/SECURITY.md` before changing
authentication or session code.

The security invariants in those files are requirements, not suggestions.

Do not broaden the plugin's capability without explicit instruction.

Verify Jellyfin API signatures against the pinned package version (10.11.11) rather
than from memory. `docs/ARCHITECTURE.md` records the already-verified surface and the
commands used to verify it.
