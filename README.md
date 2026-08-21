<h1 align="center">Jellyfin Session Provisioning Plugin</h1>

<p align="center">Admin-authorized session provisioning for Jellyfin users.</p>

## What this is

A small, auditable Jellyfin server plugin that lets an **already-authorized Jellyfin
administrator** provision a normal device session for an **existing Jellyfin user**
without that user's password, an SSO browser flow, or Quick Connect interaction.

The intended use case is managed client deployment — for example, pre-provisioned
[Jellyfin MPV Shim](https://github.com/jellyfin/jellyfin-desktop) installs.

```text
existing Jellyfin user
        +
already-authorized Jellyfin administrator
        +
secondary provisioning secret
        ↓
plugin asks Jellyfin to create a normal session/device
        ↓
Jellyfin returns the target user's normal AccessToken
```

## What this is not

> Session Provisioning does not provide SSO, create users, or disable Jellyfin
> authentication. It allows an authenticated Jellyfin administrator, subject to an
> additional provisioning secret, to create a normal device session for an existing
> Jellyfin user for managed-client deployment.

Jellyfin owns identity, roles, permissions, session persistence, device persistence,
token generation, and revocation. This plugin adds one narrowly gated administrative
capability on top of them.

## Target version

| | |
|---|---|
| Jellyfin server | 10.11.11 |
| `Jellyfin.Controller` / `Jellyfin.Model` | 10.11.11 |
| `targetAbi` | 10.11.0.0 |
| Framework | net9.0 |

Package references must match the installed server version, or the plugin will show
as `NotSupported`.

## Status

Early development. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md),
[docs/SECURITY.md](docs/SECURITY.md), and [docs/TESTING.md](docs/TESTING.md).

## Build

```sh
dotnet build -c Release
```

The plugin DLL is written to
`Jellyfin.Plugin.SessionProvisioning/bin/Release/net9.0/Jellyfin.Plugin.SessionProvisioning.dll`.

## License

GPL-3.0 — see [LICENSE](LICENSE).
