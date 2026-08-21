# CLAUDE.md

## Project

This is a Jellyfin server plugin for admin-authorized provisioning of normal Jellyfin
user device sessions.

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
- Keep the plugin small and auditable; avoid new abstractions/dependencies unless necessary.
- Do not begin MPV Shim installer work until the curl/API smoke test passes end-to-end.

## Before editing

1. Read `docs/ARCHITECTURE.md`.
2. Read `docs/SECURITY.md`.
3. Inspect the current Jellyfin plugin-template conventions and package versions in this repo.
4. Verify any Jellyfin API signature against the version actually referenced by the
   project before coding against it. `docs/ARCHITECTURE.md` records what has already
   been verified against 10.11.11, and how.

## Validation

After meaningful changes:

- `dotnet build -c Release` (warnings are errors; keep it at 0 warnings)
- run the relevant tests (`dotnet test`)
- never claim auth/session behavior works without an integration test against Jellyfin
  (see `docs/TESTING.md` for the disposable-server recipe)

## Scope discipline

Prefer the smallest change that proves the current milestone. Do not add UI, installer
generation, background services, generalized token management, or unrelated features
unless explicitly requested.
