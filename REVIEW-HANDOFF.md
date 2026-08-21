# Jellyfin Session Provisioning Plugin — Review Handoff

You are reviewing a small, security-sensitive Jellyfin server plugin, implemented from
the handoff spec you (ChatGPT) wrote. This document exists so you can review the
*result* rather than re-derive the context.

**What would help most:** find what a self-review missed. The implementation has
already been reviewed once by the agent that wrote it, which caught six real defects —
so the interesting question is what survived that pass. Section 8 lists where the
residual risk is concentrated.

**Round 2:** an external review pass has since been applied — see section 10. Every
finding was reproduced before being fixed, and two documentation claims were tested and
found wrong. Current state: 106 unit tests, 69 live checks.

---

## 1. What it is

One endpoint. An already-authorized Jellyfin administrator provisions a normal device
session for an **existing** Jellyfin user, without that user's password, an SSO browser
flow, or Quick Connect. Intended consumer: a managed-client provisioning service that
bakes the resulting credential into a client install (Jellyfin MPV Shim).

```http
POST /SessionProvisioning/Mint
Authorization: <normal Jellyfin authorization>
X-Session-Provisioning-Secret: <secret>
Content-Type: application/json

{ "userId": "<guid>", "deviceId": "<stable-id>", "deviceName": "Living Room MPV Shim", "appVersion": "3.0.0" }
```

Returns `{ userId, deviceId, deviceName, accessToken }` — the token exactly once.

Jellyfin owns identity, roles, permissions, session/device persistence, token
generation, and revocation. The plugin adds one gated capability on top and stores
nothing.

**Scope discipline:** MPV Shim installer generation was explicitly *not* started, per
the original spec's phase separation.

---

## 2. Current state

```text
repo     github.com/voc0der/jellyfin-plugin-session-provisioning (local only, nothing pushed)
branch   main, 14 commits, clean tree
target   Jellyfin 10.11.11 / net9.0 / targetAbi 10.11.0.0
guid     8d4bcbe8-ddd2-4c3a-ba8f-a7b500943e6b (unique; template's GUID absent from repo)
licence  MIT (voc0der)
size     889 lines plugin, 1007 lines tests, 433 lines smoke script
tests    106 unit, 69 live checks against a real Jellyfin container — all green
build    dotnet build -c Release, 0 warnings (TreatWarningsAsErrors, AllEnabledByDefault)
```

```text
Jellyfin.Plugin.SessionProvisioning/
├── Plugin.cs                        # stateless BasePlugin, no config, no UI
├── PluginServiceRegistrator.cs      # DI registration of the three services below
├── Api/
│   ├── SessionProvisioningController.cs
│   ├── MintSessionRequest.cs        # DataAnnotations validation
│   └── MintSessionResponse.cs
└── Security/
    ├── ProvisioningSecretVerifier.cs  # constant-time hash comparison
    ├── ProvisioningSecretSource.cs    # env var / file, read per request
    ├── MintRateLimiter.cs             # 120/min, fixed window
    └── MintSerializer.cs              # one mint at a time
```

Reviewer docs: `docs/ARCHITECTURE.md` (design + every verified Jellyfin behaviour),
`docs/SECURITY.md` (invariants and threat reasoning), `docs/TESTING.md` (how to
reproduce), `AGENTS.md` (rules for future agents).

---

## 3. Request path (order matters, and is tested)

```text
[ApiController] model validation         → 400   (runs BEFORE the action)
    ↓
[Authorize(Policy = Policies.RequiresElevation)]   Jellyfin elevation — gate one
    ↓
plugin active?           not PluginStatus.Active → 404   (fails closed if no record)
    ↓
rate limit               over 120/min → 429 + Retry-After
    ↓
secret hash configured + X-Session-Provisioning-Secret verified  → 403 — gate two
    ↓
userId non-empty                         → 400
    ↓
serializer               one mint at a time; 30s wait → 503; caller gone → 499
    ↓
RE-CHECK plugin active + secret          → 404 / 403
    ↓
user resolved            unknown → 404
    ↓
ISessionManager.AuthenticateDirect       App fixed to "Jellyfin MPV Shim"
    ↓  AuthenticationException → 404   SecurityException → 409
200 + token once; audit line logs user + device only
```

Authorization filters run before model binding, so an anonymous caller still gets 401;
but an elevated caller sending a malformed body gets 400 before the plugin's own gates
run. The re-check after the wait exists because the earlier answers can be up to the
timeout stale.

The rate limit precedes the secret check deliberately: an elevated caller must not be
able to guess the secret at speed. A valid secret does not bypass it.

---

## 4. Jellyfin behaviour verified from source/packages (not assumed)

All against `v10.11.11` source and the pinned packages. Details and code references in
`docs/ARCHITECTURE.md`.

1. `AuthenticateDirect` = `AuthenticateNewSessionInternal(request, enforcePassword: false)`
   — otherwise Jellyfin's normal login path.
2. `App`, `DeviceId`, `DeviceName`, **and `AppVersion`** are all mandatory
   (`ArgumentException.ThrowIfNullOrEmpty`), so `appVersion` is required in the model.
3. `UserId` alone resolves the target; unknown users surface as `AuthenticationException`
   and are translated to 404 rather than leaking "Invalid username or password".
4. **Any valid Jellyfin API key gets the `Administrator` role**
   (`CustomAuthenticationHandler`), so every API key on the server satisfies
   `RequiresElevation`. This is the concrete justification for the second secret.
5. `Policies` lives in `MediaBrowser.Common.Api` on 10.11, not `Jellyfin.Api.Constants`.
6. Jellyfin logs `JELLYFIN_`/`DOTNET_`/`ASPNETCORE_` env vars **with values** at startup,
   so the secret variables are deliberately unprefixed
   (`SESSION_PROVISIONING_SECRET_HASH`, `..._HASH_FILE`).
7. `SessionManager.Logout` logs the access token it is invalidating at INFO — upstream,
   unavoidable on the path we must use, documented, and the smoke suite asserts every
   occurrence comes from that line and none from the plugin.
8. Plugin controllers are registered from loaded assemblies **once at startup**, so a
   disabled plugin keeps serving routes until restart — hence the in-process lifecycle
   gate. Disabling writes `Disabled` to disk but leaves the in-memory status at
   `Restart`, which `IsEnabledAndSupported` still counts as enabled; the gate requires
   `Active` exactly.
9. A plugin deriving from the non-generic `BasePlugin` must call `SetAttributes`
   itself, or `PluginManager` throws on `instance.Version` and disables the plugin —
   while the controller keeps answering.

---

## 5. Defects found in self-review, all fixed and covered

Each was found by probing a running server, not by reading code:

| # | Defect | Impact |
|---|---|---|
| 1 | `catch (SecurityException)` bound to `System.Security`'s type; Jellyfin throws `MediaBrowser.Controller.Net.SecurityException` | 409 unreachable; session-cap/device-restriction refusals escaped to middleware as **403 — indistinguishable from a bad secret** |
| 2 | **Concurrency**: 8 simultaneous mints for one user+deviceId → 7×200 + 1×500, **4 device rows, 4 simultaneously valid tokens** | revoking the device left 3 working credentials; broke the rotation guarantee the design rests on |
| 3 | Log-leak assertion used `grep -c "$SECRET"`; a base64url secret can start with `-` | grep parsed it as options and returned empty **whether or not the secret was present** — a real leak would have passed |
| 4 | `[^\p{C}]` rejected emoji device names (`\p{C}` ⊃ `\p{Cs}`; non-BMP chars are surrogate pairs) | valid device names 400'd |
| 5 | Assembly version left at template's `0.0.0.0` vs manifest `1.0.0.0` | dashboard showed 0.0.0.0; PluginManager rewrites manifests to reconcile |
| 6 | No rate limiting | unbounded secret-guessing / session-machinery churn by an elevated caller |

Fixes: correct exception type; `MintSerializer`; `grep -F --` **plus a positive control**
(a canary device ID starting with `-` must be found by the same search before any
negative assertion is trusted) **plus** `check()` failing on empty results;
`[^\p{Cc}\p{Cf}]`; version pinned with a drift check; `MintRateLimiter`.

---

## 6. Deliberate decisions (do not report as omissions)

- **No plugin configuration, no dashboard page, no stored secret.** The hash comes from
  the environment; the secret is never typed into or displayed by the web UI.
- **Hash-only storage, plain SHA-256** — valid because the secret is a uniformly random
  256-bit machine value. If human passphrases are ever accepted this must become Argon2id.
- **No caller identity in the audit line** — Jellyfin already records provisioning via
  `AuthenticationResultEventArgs` in the activity log; a parallel audit trail would
  duplicate what Jellyfin owns.
- **Smoke suite not in CI** — owner's call; CI runs the 101 unit tests.
- **`App` fixed to "Jellyfin MPV Shim"** — not caller-supplied, to avoid a generic
  client-impersonation API.
- **Minting an admin target yields an admin session** — expected: the plugin provisions
  a normal session for the requested user rather than a scoped playback token.
- **`RemoteEndPoint` records the provisioner's IP**, overwritten on the device's first
  real connection. Known, documented, left alone.
- **Recommended proxy posture**: public/proxied `/SessionProvisioning/*` → 404; reachable
  only internally.

---

## 7. How to verify anything here

```sh
dotnet build -c Release     # 0 warnings expected
dotnet test                 # 101 unit tests
./scripts/smoke-test.sh     # 64 checks vs disposable jellyfin/jellyfin:10.11.11 (needs docker)
./scripts/smoke-test.sh --keep   # leaves the server up at :8096 for poking
```

The smoke suite generates a fresh secret per run, provisions admin/normal/second-admin
users plus an API key, and covers: the full authorization matrix, negative input,
successful provisioning for normal and admin targets, API-key callers, device-reuse
rotation, the eight-way concurrency race, revocation, log-leak checks with a positive
control, 409 mapping, unicode names, rate limiting, and the plugin lifecycle (secret
removed / plugin disabled / plugin deleted). Non-zero exit on any failure.

---

## 8. Where the residual risk is — please look hardest here

1. **Anything a passing test can hide.** Defect #3 above is the cautionary case: the
   assertion reported success in both directions. Are the other negative assertions
   (`token not logged before revocation`, `failures minted nothing`, `no server errors`)
   similarly capable of passing vacuously?
2. **Concurrency beyond the one case fixed.** `MintSerializer` only covers requests
   through this endpoint. A normal client login racing a mint for the same device still
   goes through Jellyfin's unlocked read-delete-create. Is the residual window
   acceptable, and is there a defensible way to narrow it from a plugin?
3. **Gate ordering.** Rate limit before secret is intentional; it also means a flood of
   bad requests can 429 a legitimate provisioning call. Correct trade?
4. **Fail-closed completeness.** Every refusal path should be unable to mint as a side
   effect. `IsPluginActive`, `GetConfiguredHash`, and the secret verifier all fail
   closed by construction — is there a path that fails *open* under an unexpected
   exception (e.g. `IPluginManager.Plugins` throwing)?
5. **`MintRateLimiter` is process-global**, so it is also a denial-of-service surface
   against legitimate provisioning by anyone who can reach the endpoint. Given the
   endpoint already requires elevation + secret, is 120/min the right number?
6. **The 503 path.** A serializer timeout returns 503 with no `Retry-After`. Should it
   have one, and is 30s sensible?
7. **Version/ABI drift.** Resolved since: version, ABI, package version and plugin
   GUID all live in `Directory.Build.props`, `meta.json` is generated from them, and
   both `build.sh` and the smoke suite assert the archive matches. `targetAbi`
   (10.11.0.0) is still broader than the pinned package (10.11.11) by choice.
8. **Threat model sanity check.** Does anything here meaningfully increase risk beyond
   the stated position — "this endpoint increases the power of every credential that can
   reach it, which is why the second secret and the proxy posture exist"?

---

## 9. What would be most useful back

Ranked findings with a concrete failure scenario each (inputs/state → wrong outcome),
separating: correctness bugs; security-model gaps; places where a test asserts less than
it appears to; and anything in `docs/` that is now inaccurate. Style and structure
feedback is welcome but lowest priority — the plugin is deliberately small and flat, and
should stay auditable in a few minutes by a Jellyfin administrator.


---

## 10. Round-2 review: what was found and done

All six code/test findings were reproduced first, then fixed.

| # | Finding | Status |
|---|---|---|
| 1 | Kill-switch checks go stale while a request queues behind the serializer | **Fixed.** Plugin status and secret re-checked immediately after acquiring the slot; early checks kept so invalid callers cannot occupy the queue. Covered by unit tests for secret-removed, secret-rotated, and plugin-disabled while queued. |
| 2 | A disconnected queued request could still mint | **Fixed.** `EnterAsync` takes a `CancellationToken`; the controller passes `HttpContext.RequestAborted`; an abandoned wait returns 499 and mints nothing. Cancellation stays best-effort once `AuthenticateDirect` starts — no cancellation overload exists. |
| 3 | "failures minted nothing" was vacuous — baseline captured after the rejections | **Fixed.** Baseline moved before the rejection matrix, with an immediate check that rejections changed the device count by zero. |
| 4 | Rotation/revocation could pass with a non-working replacement token | **Fixed.** The replacement is now authenticated (expect 200) before revocation, and the log check requires ≥1 upstream occurrence instead of only `TOKEN_HITS == TOKEN_UPSTREAM`. |
| 5 | Same-device rotation is not unconditional under `MaxActiveSessions` | **Confirmed and documented.** Verified live: cap 1 → re-mint of the user's own device returns 409, old token still authenticates. Documented in `MintSessionRequest`, README, and ARCHITECTURE §5; covered by two live checks. The cap is not worked around — revoke the device first. |
| 6 | Controller contention test proved nothing | **Fixed.** It now counts `AuthenticateDirect` calls while the gate is held; the old assertion held even if both callers entered together. `MintSerializer` singleton registration also asserted. |

Documentation corrections, each tested rather than assumed:

- **Model validation ordering** — correct, `[ApiController]` validation precedes the
  action. Section 3 above now reflects it.
- **`grep "$MINTED_TOKEN"` in TESTING.md** — correct, fixed to `grep -F --`.
- **Plugin-directory removal** — the table was wrong, and worse than reported: a mint
  after `rm -rf` of the plugin directory returned **200 and created a device**, because
  `PluginManager` keeps an in-memory record still marked `Active`. Table corrected;
  disabling via the API remains the supported immediate revocation path.
- **CodeQL on `master`** — correct, now `main`.

Two notes back:

- The stale-gate window (#1) is proven by unit tests rather than live ones, because
  making a real `AuthenticateDirect` slow enough to queue behind is not something the
  smoke harness can arrange deterministically.
- The rate limiter is deliberately *not* re-checked after the wait: it is an abuse
  bound, not a kill switch, and a request that already holds a permit has not consumed
  a second one.
