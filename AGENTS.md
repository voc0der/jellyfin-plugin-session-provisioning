# AGENTS.md

Canonical engineering constraints for anyone — human or coding agent — changing this
repository. The security invariants are requirements, not suggestions.

## Project

A Jellyfin server plugin for admin-authorized provisioning of normal Jellyfin user
device sessions. One gated endpoint, no stored state, no UI.

## Target version (verified)

```text
Target Jellyfin Server:     10.11.11
Target Jellyfin.Controller: 10.11.11
Target Jellyfin.Model:      10.11.11
Target framework:           net9.0
build.yaml targetAbi:       10.11.0.0
```

Package references must match the installed server version or the plugin loads as
`NotSupported`.

## Ground rules

- Start from and preserve Jellyfin's official plugin-template conventions.
- Target the exact Jellyfin server/package version recorded above.
- Do not invent or manually persist Jellyfin access tokens.
- Use Jellyfin's own session/device machinery (`ISessionManager`) for session creation.
- Do not bypass the endpoint's Jellyfin elevated/admin authorization.
- Every mint request must also pass the independent provisioning-secret check.
- Never log caller credentials, provisioning secrets, or minted access tokens.
- Do not duplicate Jellyfin RBAC or maintain a second admin-user allowlist.
- Keep the plugin stateless: no plugin configuration, no dashboard page, no secret
  ever typed into or displayed by the web UI. The secret hash comes from the
  deployment environment.
- Keep the plugin small and auditable; avoid new abstractions/dependencies unless
  necessary.
- Do not begin MPV Shim installer work until the smoke test passes end-to-end.

## Before editing

1. Read `docs/ARCHITECTURE.md`.
2. Read `docs/SECURITY.md`.
3. Inspect the plugin-template conventions and package versions already in this repo.
4. Verify any Jellyfin API signature against the version actually referenced by the
   project before coding against it. `docs/ARCHITECTURE.md` records what has already
   been verified against 10.11.11, and how it was verified. If Jellyfin's source
   disagrees with any document here, the source wins — update the document first.

## Validation

After meaningful changes:

- `dotnet build -c Release` (warnings are errors; keep it at 0 warnings)
- `dotnet test`
- `scripts/smoke-test.sh` for anything touching auth, the secret gate, or session
  creation — never claim that behavior works without running it

## Scope discipline

Prefer the smallest change that proves the current milestone. Do not add UI, installer
generation, background services, generalized token management, or unrelated features
unless explicitly requested. Do not broaden the plugin's capability without explicit
instruction.
