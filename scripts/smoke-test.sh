#!/usr/bin/env bash
# End-to-end security smoke test for the Session Provisioning plugin.
#
# Builds the plugin, installs it into a disposable Jellyfin container, provisions
# test users/tokens, then runs the full authorization matrix and the post-mint
# verification (token works, session is visible, revocation kills it, no token in
# logs).
#
# The provisioning secret is generated fresh on each run and never written to the
# repository. Nothing here should ever be pointed at a production server.
#
# Usage: scripts/smoke-test.sh [--keep]
#   --keep   leave the container running afterwards for manual poking

set -euo pipefail

JELLYFIN_VERSION="${JELLYFIN_VERSION:-10.11.11}"
CONTAINER="${CONTAINER:-jf-sp-smoke}"
PORT="${PORT:-8096}"
JF="http://localhost:${PORT}"
PLUGIN_GUID="8d4bcbe8-ddd2-4c3a-ba8f-a7b500943e6b"
PLUGIN_DIR_NAME="Session Provisioning_1.0.0.0"
KEEP=0
[ "${1:-}" = "--keep" ] && KEEP=1

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RL_LIMIT=$(grep -oE 'DefaultPermitLimit = [0-9]+' "$(dirname "${BASH_SOURCE[0]}")/../Jellyfin.Plugin.SessionProvisioning/Security/MintRateLimiter.cs" | grep -oE '[0-9]+')
WORK="$(mktemp -d)"
PASS=0
FAIL=0

cleanup() {
    rm -rf "$WORK"
    if [ "$KEEP" -eq 0 ]; then
        docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    else
        echo "Container '$CONTAINER' left running at $JF"
    fi
}
trap cleanup EXIT

check() { # label expected actual
    if [ -z "$3" ]; then
        # An empty result means the check itself failed to run -- a broken assertion,
        # not a passing one. Never let that read as success.
        printf '  \033[31mFAIL\033[0m  %-52s produced no result (assertion broken)\n' "$1"
        FAIL=$((FAIL + 1))
        return
    fi
    if [ "$2" = "$3" ]; then
        printf '  \033[32mPASS\033[0m  %-52s %s\n' "$1" "$3"
        PASS=$((PASS + 1))
    else
        printf '  \033[31mFAIL\033[0m  %-52s expected %s, got %s\n' "$1" "$2" "$3"
        FAIL=$((FAIL + 1))
    fi
}

jq_get() { python3 -c "import json,sys; print(json.load(sys.stdin)$1)"; }

wait_for_http() { # seconds
    local deadline=$((SECONDS + ${1:-90}))
    while [ "$SECONDS" -lt "$deadline" ]; do
        curl -sf -m 2 "$JF/System/Info/Public" >/dev/null 2>&1 && return 0
        sleep 1
    done
    echo "timed out waiting for $JF" >&2
    return 1
}

wait_for_log() { # pattern seconds
    local deadline=$((SECONDS + ${2:-90}))
    local logs
    while [ "$SECONDS" -lt "$deadline" ]; do
        logs="$(docker logs "$CONTAINER" 2>&1)"
        grep -q "$1" <<<"$logs" && return 0
        sleep 1
    done
    return 1
}

echo "==> Building plugin"
dotnet build -c Release "$REPO_ROOT/Jellyfin.Plugin.SessionProvisioning/Jellyfin.Plugin.SessionProvisioning.csproj" | tail -1

echo "==> Generating provisioning secret"
SECRET="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
HASH="$(printf '%s' "$SECRET" | sha256sum | cut -d' ' -f1)"

echo "==> Starting disposable Jellyfin ${JELLYFIN_VERSION}"
docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
HASH_PATH=/run/secrets/sp-hash
docker run -d --name "$CONTAINER" -p "${PORT}:8096" \
    -e "SESSION_PROVISIONING_SECRET_HASH_FILE=${HASH_PATH}" \
    "jellyfin/jellyfin:${JELLYFIN_VERSION}" >/dev/null

wait_for_http

echo "==> Installing secret hash file"
printf '%s\n' "$HASH" > "$WORK/sp-hash"
docker exec "$CONTAINER" mkdir -p "$(dirname "$HASH_PATH")"
docker cp "$WORK/sp-hash" "$CONTAINER:$HASH_PATH"

echo "==> Installing plugin"
# docker cp rather than a bind mount: in sandboxed environments the daemon may
# resolve host paths in a different namespace, silently mounting an empty dir.
mkdir -p "$WORK/$PLUGIN_DIR_NAME"
cp "$REPO_ROOT/Jellyfin.Plugin.SessionProvisioning/bin/Release/net9.0/Jellyfin.Plugin.SessionProvisioning.dll" "$WORK/$PLUGIN_DIR_NAME/"
cat > "$WORK/$PLUGIN_DIR_NAME/meta.json" <<JSON
{
    "category": "General",
    "changelog": "",
    "description": "Admin-authorized session provisioning for Jellyfin users.",
    "guid": "$PLUGIN_GUID",
    "name": "Session Provisioning",
    "overview": "Admin-authorized session provisioning for Jellyfin users.",
    "owner": "voc0der",
    "targetAbi": "10.11.0.0",
    "timestamp": "2026-01-01T00:00:00Z",
    "version": "1.0.0.0",
    "status": "Active",
    "autoUpdate": false,
    "imagePath": ""
}
JSON
docker cp "$WORK/$PLUGIN_DIR_NAME" "$CONTAINER:/config/plugins/$PLUGIN_DIR_NAME"
# stop/start rather than restart: with restart, the first readiness probe can be
# answered by the old process that is still shutting down.
docker stop "$CONTAINER" >/dev/null
docker start "$CONTAINER" >/dev/null
wait_for_http

LOADED=$(wait_for_log "Loaded plugin: Session Provisioning" 30 && echo yes || echo no)
check "plugin loads on ${JELLYFIN_VERSION}" "yes" "$LOADED"
# The dashboard reports the ASSEMBLY version; build.yaml drives the manifest. Leaving
# Directory.Build.props at the template's 0.0.0.0 makes them disagree.
MANIFEST_VERSION=$(grep '^version:' "$REPO_ROOT/build.yaml" | cut -d'"' -f2)
check "assembly version matches build.yaml"  "$MANIFEST_VERSION" \
    "$(docker logs "$CONTAINER" 2>&1 | grep -o "Loaded plugin: Session Provisioning [0-9.]*" | tail -1 | awk '{print $NF}')"

echo "==> Preparing fixtures"
CLIENT_AUTH='MediaBrowser Client="smoketest", Device="smoketest", DeviceId="smoketest-runner", Version="1.0.0"'
ah() { echo "Authorization: MediaBrowser Token=\"$1\", Client=\"smoketest\", Device=\"smoketest\", DeviceId=\"smoketest-runner\", Version=\"1.0.0\""; }

# Kestrel answers /System/Info/Public before the rest of the pipeline is ready, so
# the first wizard calls need retries rather than a bare request.
RETRY=(--retry 10 --retry-delay 1 --retry-all-errors)
curl -s "${RETRY[@]}" -X POST "$JF/Startup/Configuration" -H 'Content-Type: application/json' \
    -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -s "${RETRY[@]}" "$JF/Startup/User" >/dev/null
curl -s "${RETRY[@]}" -X POST "$JF/Startup/User" -H 'Content-Type: application/json' \
    -d '{"Name":"spadmin","Password":"sp-admin-pw-1234"}' >/dev/null
curl -s "${RETRY[@]}" -X POST "$JF/Startup/RemoteAccess" -H 'Content-Type: application/json' \
    -d '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
curl -s "${RETRY[@]}" -X POST "$JF/Startup/Complete" >/dev/null

ADMIN_JSON=$(curl -s "${RETRY[@]}" -X POST "$JF/Users/AuthenticateByName" -H "Authorization: $CLIENT_AUTH" \
    -H 'Content-Type: application/json' -d '{"Username":"spadmin","Pw":"sp-admin-pw-1234"}')
ADMIN_TOKEN=$(echo "$ADMIN_JSON" | jq_get '["AccessToken"]')

BOB_ID=$(curl -s -X POST "$JF/Users/New" -H "$(ah "$ADMIN_TOKEN")" -H 'Content-Type: application/json' \
    -d '{"Name":"bob","Password":"bob-pw-1234"}' | jq_get '["Id"]')
BOB_TOKEN=$(curl -s "${RETRY[@]}" -X POST "$JF/Users/AuthenticateByName" -H "Authorization: $CLIENT_AUTH" \
    -H 'Content-Type: application/json' -d '{"Username":"bob","Pw":"bob-pw-1234"}' | jq_get '["AccessToken"]')

ALICE_ID=$(curl -s -X POST "$JF/Users/New" -H "$(ah "$ADMIN_TOKEN")" -H 'Content-Type: application/json' \
    -d '{"Name":"alice","Password":"alice-pw-1234"}' | jq_get '["Id"]')
ALICE_POLICY=$(curl -s "$JF/Users/$ALICE_ID" -H "$(ah "$ADMIN_TOKEN")" \
    | python3 -c 'import json,sys; p=json.load(sys.stdin)["Policy"]; p["IsAdministrator"]=True; print(json.dumps(p))')
curl -s -X POST "$JF/Users/$ALICE_ID/Policy" -H "$(ah "$ADMIN_TOKEN")" \
    -H 'Content-Type: application/json' -d "$ALICE_POLICY" >/dev/null

curl -s -X POST "$JF/Auth/Keys?app=provisioner" -H "$(ah "$ADMIN_TOKEN")" >/dev/null
API_KEY=$(curl -s "$JF/Auth/Keys" -H "$(ah "$ADMIN_TOKEN")" | jq_get '["Items"][0]["AccessToken"]')

mint() { # auth-header secret body -> prints status code, body in $WORK/last.json
    local args=(-s -o "$WORK/last.json" -w '%{http_code}' -X POST "$JF/SessionProvisioning/Mint" -H 'Content-Type: application/json')
    [ -n "$1" ] && args+=(-H "$1")
    [ -n "$2" ] && args+=(-H "X-Session-Provisioning-Secret: $2")
    args+=(-d "$3")

    # Deliberately not curl --retry: that treats 429 as a transient error and retries
    # it away, which would hide exactly the rate-limiting behaviour under test. Retry
    # only when there was no HTTP response at all (server still coming up).
    local status attempt=0
    while :; do
        status=$(curl "${args[@]}" || true)
        [ "$status" != "000" ] && [ "$status" != "503" ] && break
        attempt=$((attempt + 1))
        [ "$attempt" -ge 10 ] && break
        sleep 1
    done
    printf '%s' "$status"
}
tokenauth() { echo "Authorization: MediaBrowser Token=\"$1\""; }
body() { echo "{\"userId\":\"$1\",\"deviceId\":\"$2\",\"deviceName\":\"${3-Living Room MPV Shim}\",\"appVersion\":\"${4-3.0.0}\"}"; }

echo "==> Authorization matrix"
check "anonymous, no secret"                "401" "$(mint "" "" "$(body "$BOB_ID" dev-x)")"
check "anonymous, valid secret"             "401" "$(mint "" "$SECRET" "$(body "$BOB_ID" dev-x)")"
check "ordinary user, valid secret"         "403" "$(mint "$(ah "$BOB_TOKEN")" "$SECRET" "$(body "$BOB_ID" dev-x)")"
check "admin, no secret"                    "403" "$(mint "$(ah "$ADMIN_TOKEN")" "" "$(body "$BOB_ID" dev-x)")"
check "admin, wrong secret"                 "403" "$(mint "$(ah "$ADMIN_TOKEN")" "wrong-secret" "$(body "$BOB_ID" dev-x)")"
check "admin, secret prefix"                "403" "$(mint "$(ah "$ADMIN_TOKEN")" "${SECRET:0:20}" "$(body "$BOB_ID" dev-x)")"
check "api key, no secret"                  "403" "$(mint "Authorization: MediaBrowser Token=\"$API_KEY\"" "" "$(body "$BOB_ID" dev-x)")"
check "admin, valid secret, unknown user"   "404" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body 11111111111111111111111111111111 dev-x)")"

echo "==> Negative input"
check "empty device id"                     "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" '')")"
check "overlong device id"                  "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" "$(printf 'd%.0s' $(seq 1 200))")")"
check "device id with spaces"               "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" 'dev id')")"
check "empty device name"                   "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" dev-x '')")"
check "overlong device name"                "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" dev-x "$(printf 'n%.0s' $(seq 1 200))")")"
check "newline in device name"              "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" '{"userId":"'"$BOB_ID"'","deviceId":"dev-x","deviceName":"living\nroom","appVersion":"3.0.0"}')"
check "empty target guid"                   "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body 00000000-0000-0000-0000-000000000000 dev-x)")"
check "malformed guid"                      "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body not-a-guid dev-x)")"
check "missing app version"                 "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" dev-x 'Device' '')")"
check "empty body"                          "400" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" '{}')"

DEVICES_BEFORE=$(curl -s "$JF/Devices" -H "$(ah "$ADMIN_TOKEN")" | jq_get '["Items"].__len__()')

echo "==> Successful provisioning"
check "admin, valid secret, normal user"    "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-livingroom-1)")"
MINTED_TOKEN=$(jq_get '["AccessToken"]' < "$WORK/last.json")
check "admin, valid secret, admin user"     "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$ALICE_ID" alice-laptop-1)")"
ALICE_TOKEN=$(jq_get '["AccessToken"]' < "$WORK/last.json")
check "api key, valid secret, normal user"  "200" "$(mint "Authorization: MediaBrowser Token=\"$API_KEY\"" "$SECRET" "$(body "$BOB_ID" bob-kitchen-1)")"

DEVICES_AFTER=$(curl -s "$JF/Devices" -H "$(ah "$ADMIN_TOKEN")" | jq_get '["Items"].__len__()')
check "failures minted nothing (3 new devices)" "3" "$((DEVICES_AFTER - DEVICES_BEFORE))"

echo "==> Minted session behaves like a normal session"
ME=$(curl -s "$JF/Users/Me" -H "$(tokenauth "$MINTED_TOKEN")")
check "token identifies target user"        "bob" "$(echo "$ME" | jq_get '["Name"]')"
check "token carries target permissions"    "False" "$(echo "$ME" | jq_get '["Policy"]["IsAdministrator"]')"
check "admin target keeps admin rights"     "True" "$(curl -s "$JF/Users/Me" -H "$(tokenauth "$ALICE_TOKEN")" | jq_get '["Policy"]["IsAdministrator"]')"
check "device visible to admin"             "Living Room MPV Shim" \
    "$(curl -s "$JF/Devices" -H "$(ah "$ADMIN_TOKEN")" | python3 -c 'import json,sys; print(next((d["Name"] for d in json.load(sys.stdin)["Items"] if d["Id"]=="bob-livingroom-1"), "missing"))')"
check "app identity is plugin-controlled"   "Jellyfin MPV Shim" \
    "$(curl -s "$JF/Devices" -H "$(ah "$ADMIN_TOKEN")" | python3 -c 'import json,sys; print(next((d["AppName"] for d in json.load(sys.stdin)["Items"] if d["Id"]=="bob-livingroom-1"), "missing"))')"

echo "==> Re-minting the same device rotates rather than accumulates"
check "re-mint same user+device"            "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-livingroom-1)")"
REMINTED_TOKEN=$(jq_get '["AccessToken"]' < "$WORK/last.json")
check "new token differs"                   "different" "$([ "$REMINTED_TOKEN" != "$MINTED_TOKEN" ] && echo different || echo same)"
check "old token no longer authenticates"   "401" "$(curl -s -o /dev/null -w '%{http_code}' "$JF/Users/Me" -H "$(tokenauth "$MINTED_TOKEN")")"
check "device count unchanged by re-mint"   "$DEVICES_AFTER" "$(curl -s "$JF/Devices" -H "$(ah "$ADMIN_TOKEN")" | jq_get '["Items"].__len__()')"

echo "==> Revocation through normal Jellyfin mechanisms"
curl -s -X DELETE "$JF/Devices?id=bob-livingroom-1" -H "$(ah "$ADMIN_TOKEN")" >/dev/null
check "revoked token stops working"         "401" "$(curl -s -o /dev/null -w '%{http_code}' "$JF/Users/Me" -H "$(tokenauth "$REMINTED_TOKEN")")"

echo "==> Secrets stay out of the logs"
# Positive control first. "The secret never appears in the logs" only means something
# if the search would have found it. The canary deviceId deliberately starts with '-',
# the exact shape that made grep parse the pattern as an option and report nothing --
# a leaked secret would then have been reported as absent.
CANARY="-canary-${RANDOM}${RANDOM}"
check "canary mint accepted"                "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" "$CANARY")")"
LOGS=$(docker logs "$CONTAINER" 2>&1)
CANARY_HITS=$(echo "$LOGS" | grep -cF -- "$CANARY" || true)
check "log search can find a planted string" "yes" \
    "$([ "${CANARY_HITS:-0}" -ge 1 ] && echo yes || echo no)"
# The plugin must never log a token. Jellyfin itself does log the token it is
# invalidating, in SessionManager.Logout ("Logging out access token {0}"), which our
# revoke and re-mint paths both trigger -- so assert that every occurrence comes from
# that upstream line and none from plugin activity.
TOKEN_HITS=$(echo "$LOGS" | grep -cF -- "$REMINTED_TOKEN" || true)
TOKEN_UPSTREAM=$(echo "$LOGS" | grep -F -- "$REMINTED_TOKEN" | grep -c "Logging out access token" || true)
check "token only logged by upstream logout" "$TOKEN_HITS" "$TOKEN_UPSTREAM"
check "token not logged before revocation"  "0" "$(echo "$LOGS" | grep -F -- "$REMINTED_TOKEN" | grep -vc "Logging out access token" || true)"
check "provisioning secret never logged"    "0" "$(echo "$LOGS" | grep -cF -- "$SECRET" || true)"
check "secret hash never logged"            "0" "$(echo "$LOGS" | grep -cF -- "$HASH" || true)"
check "audit line present"                  "yes" \
    "$(echo "$LOGS" | grep -q "Session provisioning succeeded user=" && echo yes || echo no)"

echo "==> Jellyfin's own refusals map to 409"
# Cap the target user at one session, then mint twice for different devices. Jellyfin
# raises MediaBrowser.Controller.Net.SecurityException, which must surface as 409 --
# catching System.Security.SecurityException instead let it escape to Jellyfin's
# ExceptionMiddleware, which answered 403 and looked exactly like a bad secret.
CAP_USER=$(curl -s "${RETRY[@]}" -X POST "$JF/Users/New" -H "$(ah "$ADMIN_TOKEN")" -H 'Content-Type: application/json' \
    -d '{"Name":"capped","Password":"capped-pw-1234"}' | jq_get '["Id"]')
CAP_POLICY=$(curl -s "$JF/Users/$CAP_USER" -H "$(ah "$ADMIN_TOKEN")" \
    | python3 -c 'import json,sys; p=json.load(sys.stdin)["Policy"]; p["MaxActiveSessions"]=1; print(json.dumps(p))')
curl -s "${RETRY[@]}" -X POST "$JF/Users/$CAP_USER/Policy" -H "$(ah "$ADMIN_TOKEN")" \
    -H 'Content-Type: application/json' -d "$CAP_POLICY" >/dev/null
check "first session within cap"            "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$CAP_USER" capped-dev-1)")"
check "session cap exceeded -> 409"         "409" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$CAP_USER" capped-dev-2)")"

DEV_POLICY=$(curl -s "$JF/Users/$CAP_USER" -H "$(ah "$ADMIN_TOKEN")" \
    | python3 -c 'import json,sys; p=json.load(sys.stdin)["Policy"]; p["MaxActiveSessions"]=0; p["EnableAllDevices"]=False; p["EnabledDevices"]=["some-other-device"]; print(json.dumps(p))')
curl -s "${RETRY[@]}" -X POST "$JF/Users/$CAP_USER/Policy" -H "$(ah "$ADMIN_TOKEN")" \
    -H 'Content-Type: application/json' -d "$DEV_POLICY" >/dev/null
check "device restriction -> 409"           "409" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$CAP_USER" capped-dev-3)")"
check "409 logged by the plugin, not the middleware" "yes" \
    "$(docker logs "$CONTAINER" 2>&1 | grep -q "Session provisioning refused by Jellyfin" && echo yes || echo no)"

echo "==> Unicode device names"
check "emoji device name accepted"          "200" \
    "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" '{"userId":"'"$BOB_ID"'","deviceId":"emoji-dev","deviceName":"Living Room \ud83d\udcfa","appVersion":"3.0.0"}')"
check "bidi override still rejected"        "400" \
    "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" '{"userId":"'"$BOB_ID"'","deviceId":"bidi-dev","deviceName":"Living \u202eRoom","appVersion":"3.0.0"}')"

echo "==> Lifecycle: secret availability"
# The hash is re-read per request, so removing the file must disable minting with no
# restart, and restoring it must bring the capability straight back.
docker exec "$CONTAINER" rm -f "$HASH_PATH"
check "hash file removed -> mint refused"   "403" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-1)")"
docker cp "$WORK/sp-hash" "$CONTAINER:$HASH_PATH"
check "hash file restored -> mint works"    "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-1)")"
docker exec "$CONTAINER" sh -c "printf 'not-a-valid-hash\n' > $HASH_PATH"
check "malformed hash -> mint refused"      "403" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-1)")"
docker cp "$WORK/sp-hash" "$CONTAINER:$HASH_PATH"

echo "==> Lifecycle: plugin disabled"
PLUGIN_VERSION=1.0.0.0
curl -s "${RETRY[@]}" -X POST "$JF/Plugins/$PLUGIN_GUID/$PLUGIN_VERSION/Disable" -H "$(ah "$ADMIN_TOKEN")" >/dev/null
# Jellyfin registers plugin controllers once at startup, so the route is still mapped
# here. The plugin's own lifecycle gate is what must refuse.
check "disabled pre-restart -> mint refused"  "404" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-2)")"

docker stop "$CONTAINER" >/dev/null && docker start "$CONTAINER" >/dev/null
wait_for_http
check "disabled post-restart -> route gone"   "404" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-2)")"
check "disabled plugin assembly not loaded"  "yes" \
    "$(docker logs "$CONTAINER" 2>&1 | grep -q "Skipping disabled plugin .* of Session Provisioning" && echo yes || echo no)"

curl -s "${RETRY[@]}" -X POST "$JF/Plugins/$PLUGIN_GUID/$PLUGIN_VERSION/Enable" -H "$(ah "$ADMIN_TOKEN")" >/dev/null
docker stop "$CONTAINER" >/dev/null && docker start "$CONTAINER" >/dev/null
wait_for_http
check "re-enabled -> mint works again"      "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-3)")"

echo "==> Rate limiting"
# Wrong-secret requests are cheap and mint nothing, so they are the safe way to fill
# the window. The limiter sits ahead of the secret check precisely so that this kind
# of flood is bounded.
#
# Fire them in parallel: sent one at a time, filling a 120-permit window can take
# longer than the window itself on a server that is still running startup tasks, and
# the permits replenish underneath the test.
# A generated request script keeps the quoting sane; curl config files need every
# embedded quote escaped, and the JSON body is full of them.
cat > "$WORK/rl-request.sh" <<REQ
#!/usr/bin/env bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST "$JF/SessionProvisioning/Mint" \\
    -H $(printf %q "$(ah "$ADMIN_TOKEN")") \\
    -H 'X-Session-Provisioning-Secret: wrong-secret' \\
    -H 'Content-Type: application/json' \\
    -d $(printf %q "$(body "$BOB_ID" rl-probe)")
REQ
chmod +x "$WORK/rl-request.sh"
seq 1 $((RL_LIMIT + 20)) | xargs -P 8 -I{} "$WORK/rl-request.sh" > "$WORK/rl-status.txt" 2>/dev/null || true
check "flood is rate limited"               "yes" \
    "$([ "$(grep -c '^429' "$WORK/rl-status.txt" || true)" -gt 0 ] && echo yes || echo no)"
check "flood was not blocked wholesale"     "yes" \
    "$([ "$(grep -c '^403' "$WORK/rl-status.txt" || true)" -gt 0 ] && echo yes || echo no)"

# A correct secret must not bypass the limiter, and the refusal must say how long to wait.
curl -s -D "$WORK/rl-headers.txt" -o /dev/null -w '%{http_code}' -X POST "$JF/SessionProvisioning/Mint" \
    -H "$(ah "$ADMIN_TOKEN")" -H "X-Session-Provisioning-Secret: $SECRET" \
    -H 'Content-Type: application/json' -d "$(body "$BOB_ID" rl-probe-2)" > "$WORK/rl-valid.txt"
check "valid secret does not bypass limiter" "429" "$(cat "$WORK/rl-valid.txt")"
check "429 carries Retry-After"             "yes" \
    "$(grep -qi '^retry-after:' "$WORK/rl-headers.txt" && echo yes || echo no)"

docker stop "$CONTAINER" >/dev/null && docker start "$CONTAINER" >/dev/null
wait_for_http
check "restart clears the window"           "200" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" rl-after-restart)")"

echo "==> Lifecycle: plugin removed"
docker exec "$CONTAINER" rm -rf "/config/plugins/$PLUGIN_DIR_NAME"
docker stop "$CONTAINER" >/dev/null && docker start "$CONTAINER" >/dev/null
wait_for_http
check "plugin removed -> route gone"        "404" "$(mint "$(ah "$ADMIN_TOKEN")" "$SECRET" "$(body "$BOB_ID" bob-lifecycle-4)")"

echo
echo "==> $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
