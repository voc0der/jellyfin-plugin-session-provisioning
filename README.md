<p align="center">
  <img src="icon.png" alt="Jellyfin Session Provisioning" width="880" />
</p>

# Session Provisioning

<p align="center">
  <a href="https://github.com/voc0der/jellyfin-plugin-session-provisioning/releases/latest">
    <img src="https://img.shields.io/github/v/release/voc0der/jellyfin-plugin-session-provisioning?label=stable%20release" alt="Stable release version" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-session-provisioning/tree/main/tests">
    <img src="https://img.shields.io/badge/coverage-95%25-brightgreen" alt="Code coverage percentage" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-session-provisioning/actions/workflows/codeql.yml">
    <img src="https://img.shields.io/github/actions/workflow/status/voc0der/jellyfin-plugin-session-provisioning/codeql.yml?branch=main&label=codeql" alt="CodeQL status" />
  </a>
  <a href="https://github.com/voc0der/jellyfin-plugin-session-provisioning/issues">
    <img src="https://img.shields.io/github/issues/voc0der/jellyfin-plugin-session-provisioning?color=DAA520" alt="Open issues" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/github/license/voc0der/jellyfin-plugin-session-provisioning?color=97CA00" alt="License" />
  </a>
</p>

Ship a Jellyfin client that is already signed in. An administrator asks the server for a session on behalf of an existing user and gets back that user's normal access token — no password, no SSO browser flow, no Quick Connect tap on the device. Built for handing someone a media box that works the moment they plug it in.

Jellyfin issues the session, lists it beside every other client, and revokes it the usual way. The plugin only asks.

## Installation

Requires Jellyfin 10.11.11. Add this repository under **Dashboard > Plugins > Repositories**, install **Session Provisioning** from the catalog, and restart.

```
https://raw.githubusercontent.com/voc0der/jellyfin-plugin-session-provisioning/main/manifest.json
```

### Manual

1. Download the ZIP from the [releases page](https://github.com/voc0der/jellyfin-plugin-session-provisioning/releases)
2. Extract it into `<jellyfin-data>/plugins/`
3. Restart Jellyfin

#### Building from source

```bash
./build.sh
```

Runs the tests and writes the archive to `artifacts/`.

## Setup

There is no settings page. The plugin holds no state, and the secret is never typed into or shown by the web UI — the server reads its hash from the environment.

Make a secret and hash it on a trusted machine:

```bash
SECRET="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
printf '%s' "$SECRET" | sha256sum
```

Give the hash to Jellyfin, keep the secret for whatever does your provisioning:

```yaml
services:
  jellyfin:
    environment:
      SESSION_PROVISIONING_SECRET_HASH_FILE: /run/secrets/sp-hash
    volumes:
      - ./sp-hash:/run/secrets/sp-hash:ro
```

Until a valid hash is configured, the endpoint mints nothing. `SESSION_PROVISIONING_SECRET_HASH` passes the hash directly if you would rather not mount a file.

Block `/SessionProvisioning/*` at your public reverse proxy. Nothing outside your network should reach it.

## Usage

```bash
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

The token comes back once. It is an ordinary session token carrying that user's own permissions — mint one for an administrator and you get an administrator's session.

Give each managed install its own `deviceId` and keep it: minting again for the same one replaces that install's token instead of leaving a second live credential behind. Revoke like any other client, from the dashboard device list.

## Good to know

- **Two credentials are required, always.** Jellyfin administrator authorization *and* the provisioning secret. Every Jellyfin API key counts as an administrator, so the secret is what keeps this to your provisioning service rather than to every integration you have ever issued a key to.
- **Turning it off:** remove the hash and the next request is refused, no restart needed. Disabling the plugin refuses immediately too.
- **Rate limited** to 120 requests a minute, and one mint runs at a time.
- If the target user is at their session limit, minting is refused and their existing token keeps working. Revoke the old device first.

## Documentation

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) covers the design and the Jellyfin behaviour it depends on, verified against 10.11.11. [docs/SECURITY.md](docs/SECURITY.md) covers the threat model and the invariants. [docs/TESTING.md](docs/TESTING.md) covers reproducing any of it.

## License

[MIT](LICENSE)
