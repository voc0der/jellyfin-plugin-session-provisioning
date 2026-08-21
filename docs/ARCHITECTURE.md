# Architecture

## Purpose

Let an already-authorized Jellyfin administrator provision a normal device session for
an **existing** Jellyfin user, without that user's password, an SSO browser flow, or
Quick Connect interaction. The intended consumer is a managed-client provisioning
service that bakes the resulting credential into a client installation.

Jellyfin owns identity, roles, permissions, session persistence, device persistence,
token generation, and revocation. This plugin adds one narrowly gated administrative
capability on top of them.

## Threat boundary

```text
trusted provisioner
   │
   ├── Jellyfin admin authorization      (Jellyfin's own elevation policy)
   ├── provisioning secret               (this plugin's independent gate)
   │
   ▼
Session Provisioning plugin
   │
   ├── Jellyfin elevation check
   ├── secondary-secret check
   ├── resolve existing target user
   │
   ▼
Jellyfin ISessionManager
   │
   ▼
normal Jellyfin device/session
   │
   ▼
target-user AccessToken
```

Both gates are mandatory. See `SECURITY.md` for the invariants and their consequences.

## Route

```http
POST /SessionProvisioning/Mint
Authorization: <normal Jellyfin authorization>
X-Session-Provisioning-Secret: <secret>
Content-Type: application/json

{
  "userId": "<jellyfin-user-guid>",
  "deviceId": "<stable-device-id>",
  "deviceName": "Living Room MPV Shim",
  "appVersion": "3.x"
}
```

The `App` identity is controlled by the plugin (`Jellyfin MPV Shim`) rather than taken
from the request. `AuthenticationRequest` permits arbitrary `App` values; exposing that
would turn this into a generic client-impersonation API.

`RemoteEndPoint` on the created session is the **provisioning caller's** address, not
the device's — the device has not connected yet. Jellyfin replaces it via
`LogSessionActivity` the first time the client actually uses the token.

Rate limiting: 120 requests per minute process-wide, applied before the secret check,
answering 429 with `Retry-After`. Minting is also serialized — one at a time — so a
request that waits more than 30 seconds for the one in flight gets 503. See
`SECURITY.md`.

Response returns the access token exactly once:

```json
{
  "userId": "...",
  "deviceId": "...",
  "deviceName": "Living Room MPV Shim",
  "accessToken": "..."
}
```

## Verified Jellyfin surface (10.11.11)

Everything below was verified against the pinned packages and the `v10.11.11` source
tag, not from memory. Re-verify on any version bump.

Verification commands used:

- reflection over `Jellyfin.Controller` / `Jellyfin.Model` 10.11.11 and
  `MediaBrowser.Common` 10.11.11 (a throwaway console project referencing the exact
  pinned package versions);
- `Emby.Server.Implementations/Session/SessionManager.cs`,
  `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`, and
  `Jellyfin.Api/Auth/CustomAuthenticationHandler.cs` at tag `v10.11.11`.

### 1. Session-minting entry point

```csharp
// MediaBrowser.Controller.Session.ISessionManager, MediaBrowser.Controller 10.11.11.0
Task<AuthenticationResult> AuthenticateDirect(AuthenticationRequest request);
```

`SessionManager.AuthenticateDirect` delegates to
`AuthenticateNewSessionInternal(request, enforcePassword: false)`. That is the whole
difference from `AuthenticateNewSession` — no password is required, everything else
(device access checks, session limits, device creation, token issuance, event
publication) is Jellyfin's normal login path.

`ISessionManager` is a server singleton and is injectable into a plugin controller.

### 2. Mandatory `AuthenticationRequest` fields

`AuthenticateNewSessionInternal` begins with:

```csharp
ArgumentException.ThrowIfNullOrEmpty(request.App);
ArgumentException.ThrowIfNullOrEmpty(request.DeviceId);
ArgumentException.ThrowIfNullOrEmpty(request.DeviceName);
ArgumentException.ThrowIfNullOrEmpty(request.AppVersion);
```

So `App`, `DeviceId`, `DeviceName`, and **`AppVersion` are all required** —
`appVersion` is not optional in our request model. `Password` / `PasswordSha1` are
unused on this path. `RemoteEndPoint` is optional and is recorded on the session.

Full property set: `Username`, `UserId`, `Password`, `PasswordSha1`, `App`,
`AppVersion`, `DeviceId`, `DeviceName`, `RemoteEndPoint`.

### 3. How the target user is identified

```csharp
User user = null;
if (!request.UserId.IsEmpty()) { user = _userManager.GetUserById(request.UserId); }
user ??= _userManager.GetUserByName(request.Username);
if (user is null) { throw new AuthenticationException("Invalid username or password entered."); }
```

Setting `UserId` alone is sufficient and is what this plugin does — user IDs are stable
across renames. Note the failure mode: an unknown user surfaces as
`AuthenticationException`, which the plugin must translate into a clean 4xx rather than
leaking Jellyfin's "Invalid username or password entered." wording.

Two further Jellyfin-owned rejections happen after user resolution, and the plugin must
not attempt to bypass either:

- `_deviceManager.CanAccessDevice(user, request.DeviceId)` — `SecurityException` when
  the target user's policy restricts which devices they may use;
- `user.MaxActiveSessions` — `SecurityException` ("User is at their maximum number of
  sessions") when the target user is at their session cap.

### 4. What gets created

`GetAuthorizationToken(user, deviceId, app, appVersion, deviceName)`:

1. queries existing devices matching `{ DeviceId, UserId }`;
2. **logs out every match** (`Logout(auth)`), i.e. revokes the prior token for that
   user+device pair;
3. `_deviceManager.CreateDevice(new Device(user.Id, app, appVersion, deviceName, deviceId))`;
4. returns `device.AccessToken`.

Then `LogSessionActivity(...)` creates the `SessionInfo`, and the result is
`AuthenticationResult { User, SessionInfo, AccessToken, ServerId }`
(`MediaBrowser.Controller.Authentication`). An `AuthenticationResultEventArgs` event is
published, so normal Jellyfin activity logging sees the provisioning.

### 5. Re-minting the same `deviceId`

Answered by step 2 above: minting again for the **same user + same `deviceId`**
invalidates the previous token and issues a new one. It does not accumulate devices.
A different `deviceId` creates an additional device entry — hence the stable-device-id
rule in "Device-ID semantics".

**Two conditions qualify that guarantee.**

First, the target user must not be at their session cap.
`AuthenticateNewSessionInternal` checks `MaxActiveSessions` *before*
`GetAuthorizationToken` replaces the device token, and the session being replaced
counts toward the cap — so a user at their limit gets 409 even when re-minting a device
they already own, and the previous token stays valid. Verified: cap of 1, re-mint of
the same device, 409, old token still authenticating. Revoke the device first rather
than raising the cap; the plugin must not work around a Jellyfin policy.

Second, **the guarantee holds only for serialized calls.** `GetAuthorizationToken` reads the
matching devices, logs them out, and creates a replacement with no lock around the
sequence, so concurrent calls for the same user and device each observe the original
set, delete it, and insert their own row. Measured against a real server, eight
simultaneous mints produced:

```text
7 x 200 and 1 x 500
4 device rows for one deviceId
4 simultaneously valid tokens
3 [ERR] lines in the server log
```

Four live credentials for one logical device is a security problem, not just untidy
bookkeeping: an administrator revoking that device removes one row and leaves the
others working. The plugin therefore serializes minting (`MintSerializer`); with that
in place the same test yields 8 x 200, one device row, and exactly one surviving
token. Serialization only covers requests through this endpoint — a normal client
login racing a mint for the same device is Jellyfin's own behavior, and out of reach
from here.

### 6. Endpoint authorization policy

```csharp
// Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs (v10.11.11)
options.AddPolicy(
    Policies.RequiresElevation,
    policy => policy.AddAuthenticationSchemes(AuthenticationSchemes.CustomAuthentication)
        .RequireClaim(ClaimTypes.Role, UserRoles.Administrator));
```

On 10.11 the constants live in **`MediaBrowser.Common.Api.Policies`** (not
`Jellyfin.Api.Constants`), so a plugin can reference `Policies.RequiresElevation`
through the pinned `Jellyfin.Controller` dependency chain.

### 7. Do API keys satisfy that policy? Yes.

```csharp
// Jellyfin.Api/Auth/CustomAuthenticationHandler.cs (v10.11.11)
var role = UserRoles.User;
if (authorizationInfo.IsApiKey
    || (authorizationInfo.User?.HasPermission(PermissionKind.IsAdministrator) ?? false))
{
    role = UserRoles.Administrator;
}
```

**Any valid Jellyfin API key is treated as `Administrator`** and therefore passes
`RequiresElevation`. This is precisely why the independent provisioning secret exists:
without it, every existing API-key holder on the server would silently gain the ability
to mint sessions for any user. See `SECURITY.md`.

For an API-key caller, `authorizationInfo.UserId` is `Guid.Empty` — there is no caller
user. Audit logging must tolerate that.

### 8. Revocation path

```csharp
Task Logout(string accessToken);   // ISessionManager
Task Logout(Device device);        // ISessionManager
Task RevokeUserTokens(Guid userId, string currentAccessToken);
```

Operationally, an admin revokes through Jellyfin's normal Devices/Sessions
administration (dashboard device list, or the `/Devices` API), which deletes the device
record and its access token. The plugin implements no revocation of its own — it must
not, or it would be duplicating Jellyfin state.

### 13. Plugin services and dependency injection

`IPluginServiceRegistrator` (`MediaBrowser.Controller.Plugins`) lets a plugin add its
own services to Jellyfin's container, which `PluginServiceRegistrator` uses to register
`ProvisioningSecretSource`, `MintRateLimiter`, and `MintSerializer` as singletons. The
controller takes them as constructor parameters.

They were originally `static` fields on the controller. Injecting them matters for more
than tidiness: the rate limiter and the serializer are only meaningful if every request
shares one instance, and a controller whose collaborators are static cannot be unit
tested at all. `PluginServiceRegistratorTests` builds the controller out of a real
`ServiceCollection`, so a constructor parameter that nothing registers fails a unit
test rather than 500-ing every request on a live server.

## Configuration model: none

The plugin holds **no state**. There is no `PluginConfiguration`, no dashboard page,
and no stored secret — nothing about it is settable through Jellyfin's web UI.

The one value it needs, the SHA-256 hash of the provisioning secret, comes from the
deployment environment:

```text
SESSION_PROVISIONING_SECRET_HASH        hex-encoded SHA-256 of the secret
SESSION_PROVISIONING_SECRET_HASH_FILE   path to a file containing that hash
```

The file form wins if both are set, and the value is re-read on every request, so
rotating a mounted file takes effect without restarting Jellyfin. Anything missing,
blank, or unreadable disables minting (see `SECURITY.md`).

Neither name carries a `JELLYFIN_` prefix, deliberately — see §10 below.

### 9. Jellyfin logs the tokens it invalidates

```csharp
// Emby.Server.Implementations/Session/SessionManager.cs (v10.11.11)
public async Task Logout(Device device)
{
    CheckDisposed();
    _logger.LogInformation("Logging out access token {0}", device.AccessToken);
```

Jellyfin writes an access token in plaintext to its own log, at INFO, whenever a device
is logged out. Provisioning hits this on two paths: an admin revoking a device, and
re-minting an existing user+device pair (`GetAuthorizationToken` logs out the previous
device first). The token is being invalidated at that moment, which limits the damage,
but Jellyfin server logs must still be treated as credential-bearing.

This is upstream behavior on the path the plugin is required to use. The plugin itself
never logs a token, and `scripts/smoke-test.sh` asserts that every appearance of a
minted token in the logs comes from this upstream line and none from plugin activity.

### 10. Jellyfin echoes JELLYFIN_-prefixed environment variables into the log

```csharp
// Jellyfin.Server/Helpers/StartupHelpers.cs (v10.11.11)
private static readonly string[] _relevantEnvVarPrefixes = { "JELLYFIN_", "DOTNET_", "ASPNETCORE_" };
...
logger.LogInformation("Environment Variables: {EnvVars}", relevantEnvVars);
```

Every variable starting with one of those prefixes is printed at startup, with its
value. A variable named `JELLYFIN_SESSION_PROVISIONING_SECRET_HASH` would therefore put
the hash in the server log on every boot — verified by observing exactly that. Hence
the unprefixed names above, and hence the preference for the `_FILE` form, where only
the path is ever logged.

### 11. A configuration-less plugin must set its own assembly attributes

`BasePlugin.Version`, `AssemblyFilePath`, and `DataFolderPath` are populated only by
`SetAttributes`, which Jellyfin's generic `BasePlugin<TConfigurationType>` calls from
its constructor. A plugin deriving from the non-generic `BasePlugin` — as this one does,
having no configuration — must do that itself, or
`PluginManager.CreatePluginInstance` throws `NullReferenceException` on
`instance.Version` and marks the plugin `Malfunctioned`:

```text
Error creating Jellyfin.Plugin.SessionProvisioning.Plugin
System.NullReferenceException: Object reference not set to an instance of an object.
   at Emby.Server.Implementations.Plugins.PluginManager.CreatePluginInstance(Type type)
Plugin /config/plugins/Session Provisioning_1.0.0.0 has been disabled.
```

The controller still answers in that state, because ASP.NET discovers controllers from
the loaded assembly regardless — so "the endpoint works" is not evidence the plugin
loaded. Assert on `Loaded plugin: Session Provisioning` in the log instead.

### 12. Plugin lifecycle vs. endpoint availability

Controller routes and plugin state are **separate lifecycles**, and this matters for a
security-sensitive endpoint. The mechanism, in order:

1. `PluginManager.LoadAssemblies()` skips any plugin whose `IsEnabledAndSupported` is
   false (`_supported && Manifest.Status >= PluginStatus.Active`), logging
   `Skipping disabled plugin ...`. A disabled plugin's assembly is never loaded.
2. `ApplicationHost.GetApiPluginAssemblies()` collects every loaded assembly containing
   a `ControllerBase` subclass, and `Startup.ConfigureServices` passes them to
   `AddJellyfinApi` — **once, at startup**.
3. So route registration is a startup-time snapshot of the loaded assemblies. It does
   not depend on the plugin *instance* being constructed successfully, and nothing
   un-registers a route while the process runs.

Consequences, all verified live:

| Event | Route mapped before restart | Mint before restart | After restart |
|---|---|---|---|
| plugin instance fails to construct (`Malfunctioned`) | yes | refused by the gate below | route gone |
| plugin disabled via `POST /Plugins/{id}/{version}/Disable` | yes | refused by the gate below | route gone |
| plugin directory removed (files deleted under a running server) | yes | **still mints** | route gone |

The route stays mapped in every "before restart" case — ASP.NET has no way to drop it —
so the plugin has to answer for itself.

Deleting the plugin directory out from under a running server is the one case the gate
does **not** catch: `PluginManager` keeps its in-memory `LocalPlugin` record, still
marked `Active`, so the check passes and minting continues until restart. Verified
directly — a mint after `rm -rf` of the plugin directory returned 200 and created a
device. Disabling through the API is the supported way to revoke the capability
immediately; removing files is not, and pretending otherwise in this table was wrong.

Disabling deserves a closer look. `PluginManager.DisablePlugin` writes `Disabled` to the
manifest on disk, then `ProcessAlternative` sets the **in-memory** status to
`PluginStatus.Restart`. Because the enum orders `Restart` (1) above `Active` (0),
`IsEnabledAndSupported` remains true for the rest of the process's life — correct for an
ordinary plugin winding down, wrong for a capability an administrator has just revoked.

The plugin therefore applies its own **lifecycle gate** before either authorization gate:
`SessionProvisioningController.IsPluginActive()` looks itself up in `IPluginManager` and
requires `Manifest.Status == PluginStatus.Active` exactly, failing closed if no record
matches. Disabling the plugin stops minting immediately, without waiting for a restart;
`scripts/smoke-test.sh` proves the pre-restart, post-restart, re-enable, and
directory-removed cases.

This is upstream architecture, not a bug to fight: Jellyfin cannot unload an assembly's
controllers without restarting. The plugin's answer is to refuse in-process rather than
to pretend the route disappeared.

## Device-ID semantics

`deviceId` is persistent provisioning state, owned by the provisioning service:

```text
one stable unique deviceId per managed logical installation
    living-room-mpv-shim-<uuid>
    parents-laptop-mpv-shim-<uuid>
```

- A reinstall/rebuild of the *same* managed installation reuses its existing device ID
  (which, per §5 above, rotates the token and leaves exactly one device entry).
- A genuinely distinct installation gets a new one.
- Never generate a fresh random device ID per package build, or Jellyfin's device list
  fills with junk.

## Phase separation

This repository is the **plugin only**: one gated endpoint that returns a token.

The installer builder (fetching a token, assembling an MPV Shim package, embedding
client config and mTLS material) is a separate phase and a separate program. Nothing in
this repository should grow toward it until the plugin primitive is proven end to end.
Provisioning authority stays server-side: generated installers carry only the single
target user's device credential, never an admin key or the provisioning secret.
