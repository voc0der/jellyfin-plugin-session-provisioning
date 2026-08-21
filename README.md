<h1 align="center">Jellyfin Session Provisioning Plugin</h1>

<p align="center">Admin-authorized session provisioning for Jellyfin users.</p>

## What this is

A small, auditable Jellyfin server plugin that lets an **already-authorized Jellyfin
administrator** provision a normal device session for an **existing Jellyfin user**
without that user's password, an SSO browser flow, or Quick Connect interaction.

The intended use case is managed client deployment — for example, pre-provisioned
Jellyfin MPV Shim installs.

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

It is one endpoint with no stored state and no UI: two authorization gates, input
validation, a call into Jellyfin's own session manager, and a token back. Jellyfin
handles device/session display and revocation as it does for any other client.

## What this is not

> Session Provisioning does not provide SSO, create users, or disable Jellyfin
> authentication. It allows an authenticated Jellyfin administrator, subject to an
> additional provisioning secret, to create a normal device session for an existing
> Jellyfin user for managed-client deployment.

Jellyfin owns identity, roles, permissions, session persistence, device persistence,
token generation, and revocation.

## Target version

| | |
|---|---|
| Jellyfin server | 10.11.11 |
| `Jellyfin.Controller` / `Jellyfin.Model` | 10.11.11 |
| `targetAbi` | 10.11.0.0 |
| Framework | net9.0 |

Package references must match the installed server version, or the plugin will show
as `NotSupported`.

## Setup

Generate a secret and its hash on a trusted machine:

```sh
SECRET="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
printf '%s' "$SECRET" | sha256sum      # this hash goes on the server
```

Give the **secret** to the provisioning service and the **hash** to Jellyfin. There is
no settings page: the plugin reads the hash from the environment.

```yaml
# docker compose
services:
  jellyfin:
    image: jellyfin/jellyfin:10.11.11
    environment:
      SESSION_PROVISIONING_SECRET_HASH_FILE: /run/secrets/sp-hash
    volumes:
      - ./sp-hash:/run/secrets/sp-hash:ro
```

`SESSION_PROVISIONING_SECRET_HASH` may be used instead to pass the hash directly; the
`_FILE` form wins if both are set and is preferred. Neither name is `JELLYFIN_`-prefixed
on purpose — Jellyfin prints those variables into its log at startup.

While no usable hash is configured, minting is disabled entirely.

## Usage

```sh
curl -X POST "$JELLYFIN_URL/SessionProvisioning/Mint" \
  -H "Authorization: MediaBrowser Token=\"$JELLYFIN_API_KEY\"" \
  -H "X-Session-Provisioning-Secret: $SECRET" \
  -H 'Content-Type: application/json' \
  -d '{
        "userId": "24a848abe3474a4a90d863fb808eca9c",
        "deviceId": "living-room-mpv-shim-0f2a",
        "deviceName": "Living Room MPV Shim",
        "appVersion": "3.0.0"
      }'
```

```json
{
  "userId": "24a848abe3474a4a90d863fb808eca9c",
  "deviceId": "living-room-mpv-shim-0f2a",
  "deviceName": "Living Room MPV Shim",
  "accessToken": "..."
}
```

Provisioning is rate limited to 120 requests per minute, answering 429 with
`Retry-After` beyond that.

The token is returned once and is a normal Jellyfin session token carrying that user's
existing permissions. Use one stable `deviceId` per managed installation: re-minting the
same user + `deviceId` rotates the token instead of piling up device entries.

One exception: if the target user is at their `MaxActiveSessions` limit, Jellyfin
refuses before replacing anything, so re-minting even that user's own existing device
returns 409 and the previous token keeps working. Revoke the device first.

Revoke exactly as you would any other client — the dashboard device list, or
`DELETE /Devices?id=<deviceId>`.

**Note that this endpoint increases the power of every credential that can reach it:**
on 10.11 any valid Jellyfin API key counts as an administrator, which is why the
separate provisioning secret is mandatory. Block `/SessionProvisioning/*` at your public
reverse proxy (return 404) so it is reachable only from the internal network. See
[docs/SECURITY.md](docs/SECURITY.md).

To turn the capability off: remove the hash (takes effect on the next request, no
restart), or disable the plugin (refused immediately; the route disappears at the next
restart).

## Build and test

```sh
dotnet build -c Release
dotnet test
./scripts/smoke-test.sh     # full authorization matrix against a disposable server
```

The plugin DLL is written to
`Jellyfin.Plugin.SessionProvisioning/bin/Release/net9.0/Jellyfin.Plugin.SessionProvisioning.dll`.
See [docs/TESTING.md](docs/TESTING.md) for installing it into a test server by hand.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — design, and the verified Jellyfin
  10.11.11 API behavior it relies on
- [docs/SECURITY.md](docs/SECURITY.md) — invariants, threat reasoning, logging caveats
- [docs/TESTING.md](docs/TESTING.md) — reproducible test recipes

## License

MIT — see [LICENSE](LICENSE).
