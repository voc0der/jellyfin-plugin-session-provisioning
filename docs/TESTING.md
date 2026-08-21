# Testing

Never commit real API keys, access tokens, or provisioning secrets to this file. Use
placeholders and environment variables.

## Build

```sh
dotnet build -c Release
```

Warnings are errors (`TreatWarningsAsErrors`), so a clean build means 0 warnings. The
artifact is:

```text
Jellyfin.Plugin.SessionProvisioning/bin/Release/net9.0/Jellyfin.Plugin.SessionProvisioning.dll
```

## Unit tests

```sh
dotnet test
```

## Disposable Jellyfin server

Verified working recipe against the pinned target version.

```sh
docker pull jellyfin/jellyfin:10.11.11
docker rm -f jf-sp-test 2>/dev/null
docker run -d --name jf-sp-test -p 8096:8096 jellyfin/jellyfin:10.11.11
```

### Install the plugin

Build a plugin directory containing the DLL and a `meta.json`, then copy it in.

```sh
PLUGIN_DIR="Session Provisioning_1.0.0.0"
mkdir -p "/tmp/$PLUGIN_DIR"
cp Jellyfin.Plugin.SessionProvisioning/bin/Release/net9.0/Jellyfin.Plugin.SessionProvisioning.dll "/tmp/$PLUGIN_DIR/"
cat > "/tmp/$PLUGIN_DIR/meta.json" <<'JSON'
{
    "category": "General",
    "changelog": "",
    "description": "Admin-authorized session provisioning for Jellyfin users.",
    "guid": "8d4bcbe8-ddd2-4c3a-ba8f-a7b500943e6b",
    "name": "Session Provisioning",
    "overview": "Admin-authorized session provisioning for Jellyfin users.",
    "owner": "voc0der",
    "targetAbi": "10.11.0.0",
    "timestamp": "2026-08-21T00:00:00Z",
    "version": "1.0.0.0",
    "status": "Active",
    "autoUpdate": false,
    "imagePath": ""
}
JSON

docker cp "/tmp/$PLUGIN_DIR" "jf-sp-test:/config/plugins/$PLUGIN_DIR"
docker restart jf-sp-test
```

`docker cp` is used rather than a `-v` bind mount on purpose: in some sandboxed
environments the Docker daemon resolves host paths in a different filesystem namespace,
so the bind mount silently lands on an empty directory and the plugin never appears.

### Confirm it loaded

```sh
docker logs jf-sp-test 2>&1 | grep -i "session provisioning"
```

Expected:

```text
Loaded assembly Jellyfin.Plugin.SessionProvisioning, Version=... from /config/plugins/Session Provisioning_1.0.0.0/Jellyfin.Plugin.SessionProvisioning.dll
Loaded plugin: Session Provisioning 1.0.0.0
```

A `NotSupported` status here means `targetAbi` / package versions do not match the
server version.

### Server sanity check

```sh
curl -s http://localhost:8096/System/Info/Public
```

## Test-server fixtures

To be filled in as the smoke tests are built out: completing the startup wizard,
creating an admin user, a normal user, and an API key, then exporting:

```sh
export JF_URL=http://localhost:8096
export JF_ADMIN_TOKEN=...        # never commit
export JF_USER_TOKEN=...         # never commit
export JF_API_KEY=...            # never commit
export SP_SECRET=...             # never commit
export TARGET_USER_ID=...
```

## Security smoke-test matrix

Run every row. A failure must not mint a session as a side effect.

| Caller | Secondary secret | Target | Expected |
|---|---|---|---|
| anonymous | absent | normal user | reject |
| anonymous | valid | normal user | reject |
| ordinary Jellyfin user | valid | normal user | reject |
| admin | absent | normal user | reject |
| admin | invalid | normal user | reject |
| admin | valid | nonexistent GUID | reject |
| admin | valid | normal user | mint normal-user session |
| admin | valid | existing admin user | mint that admin user's normal session |
| valid API key | valid | normal user | mint (API keys carry the Administrator role on 10.11) |
| valid API key | absent/invalid | normal user | reject |

### Negative input cases

```text
empty device ID
very long device ID
very long device name
newline/control characters in device name
empty target GUID
malformed GUID
deleted user
wrong provisioning-secret length
random secret
missing Authorization header
normal non-admin token
```

## Post-mint verification

After a successful mint:

1. Use the returned token against a harmless authenticated endpoint:

   ```sh
   curl -s -H "Authorization: MediaBrowser Token=\"$MINTED_TOKEN\"" "$JF_URL/Users/Me"
   ```

2. Confirm Jellyfin reports the **requested target user**.
3. Confirm the device/session appears in Jellyfin's normal administration view
   (`GET /Devices`, dashboard device list).
4. Revoke it through Jellyfin's normal mechanism (`DELETE /Devices?id=<deviceId>`).
5. Confirm the token no longer authenticates (401).
6. Prove the token never reached the logs:

   ```sh
   docker logs jf-sp-test 2>&1 | grep -c "$MINTED_TOKEN"   # must be 0
   ```

## Teardown

```sh
docker rm -f jf-sp-test
```
