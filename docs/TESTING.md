# Testing

Never commit real API keys, access tokens, or provisioning secrets to this file. Use
placeholders and environment variables.

## The short version

```sh
./scripts/smoke-test.sh          # builds, deploys to a disposable server, runs everything
./scripts/smoke-test.sh --keep   # same, but leaves the container up for poking
```

That script is the executable form of everything below: it generates a fresh secret,
starts a disposable Jellyfin, installs the plugin, creates an admin/normal/second-admin
user and an API key, runs the full authorization matrix and negative-input cases,
verifies the minted session, rotates and revokes it, and greps the logs for leaks. It
exits non-zero if any check fails. Run it for any change touching auth, the secret
gate, or session creation.

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
SECRET="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
HASH="$(printf '%s' "$SECRET" | sha256sum | cut -d' ' -f1)"
docker run -d --name jf-sp-test -p 8096:8096 \
    -e "SESSION_PROVISIONING_SECRET_HASH=$HASH" \
    jellyfin/jellyfin:10.11.11
```

The plugin has no configuration page; the hash is supplied by the environment. Do not
rename that variable to something starting with `JELLYFIN_`, or Jellyfin will print the
hash in its startup log.

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

Expected — **both** lines matter:

```text
Loaded assembly Jellyfin.Plugin.SessionProvisioning, Version=... from /config/plugins/Session Provisioning_1.0.0.0/Jellyfin.Plugin.SessionProvisioning.dll
Loaded plugin: Session Provisioning 1.0.0.0
```

`Loaded assembly` without `Loaded plugin` means the plugin instance failed to
construct — look for `Error creating ...Plugin` and `has been disabled` just after it.
The endpoint still answers in that state, because ASP.NET finds controllers in any
loaded assembly, so a working `curl` is **not** evidence that the plugin loaded.

A `NotSupported` status instead means `targetAbi` / package versions do not match the
server version.

### Server sanity check

```sh
curl -s http://localhost:8096/System/Info/Public
```

## Test-server fixtures

`scripts/smoke-test.sh` builds these itself. To do it by hand, complete the startup
wizard (`POST /Startup/Configuration`, `POST /Startup/User`, `POST /Startup/RemoteAccess`,
`POST /Startup/Complete`), then authenticate and create users:

```sh
export JF_URL=http://localhost:8096
export JF_ADMIN_TOKEN=...        # never commit
export JF_USER_TOKEN=...         # never commit
export JF_API_KEY=...            # never commit
export SP_SECRET=...             # never commit
export TARGET_USER_ID=...
```

Three things that will otherwise cost an afternoon:

- Kestrel answers `/System/Info/Public` before the rest of the pipeline is ready, so
  the first wizard call needs `curl --retry`.
- Use `docker stop` + `docker start`, not `docker restart`: the old process can answer
  a readiness probe while it is still shutting down.
- When exercising a **minted** token, send only
  `Authorization: MediaBrowser Token="..."`. Adding `Device=`/`Client=` fields makes
  Jellyfin rename the provisioned device to whatever the test runner claims to be.

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
6. Prove the plugin never wrote the token to the logs:

   ```sh
   docker logs jf-sp-test 2>&1 | grep "$MINTED_TOKEN" | grep -v "Logging out access token"
   ```

   Must be empty. A bare `grep -c` will **not** be zero after a revocation or a
   re-mint: Jellyfin's own `SessionManager.Logout` logs the token it is invalidating
   (see `docs/SECURITY.md`). Filter that upstream line out and assert on the rest.

   Two rules for any log-leak check, learned the hard way:

   - **Use `grep -F -- "$PATTERN"`.** A base64url secret can begin with `-`, and
     `grep -c "$SECRET"` then parses the secret as options: it printed nothing whether
     or not the secret was in the log, so a genuine leak read as a pass.
   - **Prove the search works before trusting a negative result.** The suite mints a
     canary device whose ID starts with `-`, then asserts the same search finds it in
     the log. Without that positive control, "the secret never appears" is
     indistinguishable from "the search is broken".

## Concurrency

The suite fires eight simultaneous mints for the same user and `deviceId` and asserts
that all succeed, that exactly one device row exists afterwards, that exactly one
issued token still authenticates, and that nothing reached Jellyfin's exception
middleware. Run this after any change to the mint path: Jellyfin's own device
replacement is not concurrency-safe, and the invariant only holds because the plugin
serializes minting.

## Teardown

```sh
docker rm -f jf-sp-test
```
