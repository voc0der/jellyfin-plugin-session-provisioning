# Jellyfin Session Provisioning Plugin — Implementation Handoff v2

## Mission

Build a **small, auditable Jellyfin server plugin** that lets an already-authorized Jellyfin administrator provision a normal device/session for an **existing Jellyfin user** without that user's password, SSO browser flow, or Quick Connect interaction.

The intended use case is managed client deployment, especially pre-provisioned Jellyfin MPV Shim installs.

```text
existing Jellyfin user
        +
already-authorized Jellyfin administrator
        +
secondary enrollment secret
        ↓
plugin asks Jellyfin to create a normal session/device
        ↓
Jellyfin returns the target user's normal AccessToken
        ↓
provisioner embeds only that user/device credential into the client
```

This is **not** a replacement for SSO. SSO remains the normal interactive human-login path. This plugin fills the missing managed-device-enrollment path.

---

## Working name

Preferred repo/product direction:

```text
jellyfin-plugin-session-provisioning
```

Suggested description:

> Admin-authorized session provisioning for Jellyfin users.

Other acceptable working names:

```text
jellyfin-plugin-mint-session
jellyfin-plugin-admin-session-provisioning
```

Do not spend implementation time bikeshedding the final public name before the primitive works.

---

# 0. NON-NEGOTIABLE: START FROM JELLYFIN'S OFFICIAL PLUGIN TEMPLATE

**Do not scaffold a blank C# project by hand.**

Start from the official Jellyfin plugin template:

```text
https://github.com/jellyfin/jellyfin-plugin-template
```

Use the repository as a GitHub template or clone/copy it, then rename the example project and namespaces.

Reason:

- it carries Jellyfin's expected project structure;
- it already includes `Directory.Build.props`, build metadata, rulesets, editor settings, VS Code/debug scaffolding, and packaging conventions;
- it avoids wasting time rediscovering plugin-loading assumptions;
- Jellyfin's own template documentation explicitly warns that the referenced Jellyfin package versions must match the installed server version or the plugin may appear `NotSupported`.

### First repository action

Before writing plugin logic:

1. Create repository from `jellyfin/jellyfin-plugin-template`.
2. Rename `Jellyfin.Plugin.Template` to the chosen plugin namespace.
3. Generate a new permanent plugin GUID.
4. Set the plugin display name and description.
5. Determine the **exact Jellyfin server version being targeted**.
6. Pin Jellyfin package references to that version.
7. Build the untouched/renamed plugin.
8. Install it into a disposable/test Jellyfin instance.
9. Confirm Jellyfin loads it successfully.

Only after this baseline works should session-provisioning code be added.

**Do not delete template infrastructure merely because it looks unnecessary until the plugin has successfully built, packaged, installed, and loaded.**

---

# 1. TARGET VERSION RULE

Do not code against whichever Jellyfin APIs happen to autocomplete locally.

At the beginning of development, record:

```text
Target Jellyfin Server: <exact version>
Target Jellyfin.Controller: <same compatible version>
Target Jellyfin.Model: <same compatible version>
Target .NET SDK: <what the current template requires>
```

The current official plugin template is the source of truth for project framework/build expectations.

If later supporting multiple Jellyfin releases, do that deliberately. Version one should target the server actually being used and tested.

---

# 2. THE JELLYFIN PRIMITIVE WE ARE USING

The relevant Jellyfin server interface is `ISessionManager`.

The behavior previously identified is the direct-auth/session path exposed through something equivalent to:

```csharp
Task<AuthenticationResult> AuthenticateDirect(AuthenticationRequest request);
```

The exact signature, namespace, request members, and DI registration **must be verified against the selected Jellyfin server version before implementation**.

Do not duplicate Jellyfin's device/session database logic.

Do not create tokens manually.

Do not edit Jellyfin's database.

Do not copy an existing token.

The plugin should ask Jellyfin's own session machinery to create the session and return Jellyfin's own access token.

Existing plugins such as SSO provide useful precedent for calling Jellyfin's direct session machinery after some other authority has established that issuing a session is permitted. They are architectural references, not dependencies.

---

# 3. SECURITY MODEL

The plugin exposes a powerful capability, so its security boundary must be deliberately narrow.

A mint/provision request succeeds only when **both** are true:

```text
1. Jellyfin considers the caller elevated/admin-authorized
2. the caller presents the separate session-provisioning secret
```

Conceptually:

```text
Jellyfin admin/API authority
        +
Session Provisioning secret
        ↓
POST /SessionProvisioning/Mint
        ↓
create normal session for requested existing user
```

The secondary secret exists specifically so that another consumer possessing an otherwise-valid Jellyfin API key cannot automatically use this new capability.

## No duplicate RBAC

Do **not** maintain a second list of which target users may be admins.

The plugin should respect Jellyfin's existing user model:

```text
target Bob is a normal user
→ minted token has Bob's existing permissions

target Alice is a Jellyfin administrator
→ minted token has Alice's existing permissions
```

The plugin does not create administrators and does not elevate the target user.

The important authorization statement is:

> An already-authorized Jellyfin administrator is authorizing enrollment of a device/session for an already-existing Jellyfin user.

That avoids the chicken/egg problem naturally: without existing admin authority, the mint endpoint cannot be used to bootstrap an admin session.

---

# 4. ADMIN AUTHORIZATION

Prefer Jellyfin's existing elevated authorization policy on the endpoint, e.g. the current-version equivalent of:

```csharp
[Authorize(Policy = Policies.RequiresElevation)]
```

However, **verify this behavior on the exact target Jellyfin version** before considering the endpoint secure.

Tests must explicitly prove:

```text
anonymous request              → rejected
ordinary user session          → rejected
valid admin user session       → reaches secondary-secret gate
intended Jellyfin API-key path → reaches secondary-secret gate, if supported by target version
```

Do not assume API-key/elevation semantics merely because a previous Jellyfin version behaved that way.

---

# 5. SECONDARY PROVISIONING SECRET

Recommended request header:

```http
X-Session-Provisioning-Secret: <random-secret>
```

Alternative shorter name is acceptable, but choose one name and keep it stable.

## Secret generation

For a machine-generated capability secret, generate at least 256 random bits.

Example conceptual generation:

```text
32 random bytes → base64url/hex representation
```

## Storage

Do not store the plaintext secret in plugin configuration.

For a uniformly random 256-bit machine secret, storing:

```text
SHA-256(secret)
```

is sufficient for verification because offline guessing is not realistic against a high-entropy random value.

If human-created passwords/passphrases are ever accepted instead, use a password KDF such as Argon2id/bcrypt rather than plain SHA-256.

Compare fixed-size hashes using constant-time equality.

## Logging

Never log:

- the Jellyfin caller token/API key;
- the provisioning secret;
- the provisioning-secret hash unless needed for explicit debug setup;
- the newly minted target access token;
- full request headers.

Safe audit log example:

```text
Session provisioning succeeded user=<guid> device=<device-id>
```

---

# 6. NETWORK DEFENSE IN DEPTH

Application authorization is mandatory even if the network is trusted.

Separately, if deployment infrastructure already supports it, the endpoint may also be constrained by:

```text
reverse proxy source restriction
mTLS
private management network
firewall policy
```

Treat these as additional layers, not replacements for Jellyfin admin authorization or the secondary secret.

The plugin itself should not become a home-grown firewall.

---

# 7. INITIAL API CONTRACT

Keep version one to **one dangerous write endpoint**.

Suggested route:

```http
POST /SessionProvisioning/Mint
```

Example request:

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

The plugin should hard-code or tightly control the application identity initially:

```text
App = "Jellyfin MPV Shim"
```

Do not create an arbitrary `App` impersonation API just because `AuthenticationRequest` permits it.

Example response:

```json
{
  "userId": "...",
  "deviceId": "...",
  "deviceName": "Living Room MPV Shim",
  "accessToken": "..."
}
```

The access token is expected to be returned **once in the response** and must never be copied into ordinary logs.

---

# 8. REQUEST VALIDATION

Validate before touching `ISessionManager`:

```text
userId
  - required
  - syntactically valid GUID
  - resolves to an existing Jellyfin user

deviceId
  - required
  - nonblank
  - bounded length
  - conservative allowed character set if practical

deviceName
  - required
  - nonblank
  - bounded length

appVersion
  - optional or required by design
  - bounded length
  - never interpreted as code/path

secondary secret
  - required
  - verified before session minting
```

Use Jellyfin user IDs rather than usernames as the primary target identifier. Usernames can change.

---

# 9. DEVICE-ID SEMANTICS

`deviceId` is persistent provisioning state.

Use:

```text
one stable unique deviceId per managed logical installation
```

Examples:

```text
living-room-mpv-shim-<uuid>
parents-laptop-mpv-shim-<uuid>
```

A reinstall/rebuild of the **same managed installation** should normally reuse the existing device ID.

A distinct device/install should receive a new one.

Do not generate a new random device ID every time the package builder is rerun or Jellyfin's device/session list can fill with junk.

Before relying on token-rotation/replacement behavior for reused device IDs, test exactly what the target Jellyfin version does.

---

# 10. MINIMAL PROJECT STRUCTURE

Do not turn this into an enterprise architecture exercise.

Starting from Jellyfin's official template, aim for something approximately like:

```text
Jellyfin.Plugin.SessionProvisioning/
├── Jellyfin.Plugin.SessionProvisioning.csproj
├── Plugin.cs
├── Configuration/
│   └── PluginConfiguration.cs
├── Api/
│   ├── SessionProvisioningController.cs
│   ├── MintSessionRequest.cs
│   └── MintSessionResponse.cs
├── Security/
│   └── ProvisioningSecretVerifier.cs
└── ...template-required files...
```

Do not add persistence layers, repository abstractions, event buses, hosted services, or background workers unless a real requirement appears.

The ideal plugin should be small enough that another Jellyfin administrator can audit the meaningful security-sensitive code in a few minutes.

---

# 11. CONFIGURATION

Minimum configuration concept:

```csharp
public string? ProvisioningSecretHash { get; set; }
```

Potential later configuration:

```text
Enabled
ProvisioningSecretHash
```

Do not add user allowlists unless a real use case appears. Jellyfin already owns user/admin authorization.

## Bootstrap decision

Choose one explicit way to establish/rotate the secret:

### Preferred early-development approach

Allow the admin to configure the **hash**, not the plaintext secret, through the plugin's normal configuration mechanism.

This keeps the first implementation simple and prevents the plugin from needing to display/recover secrets.

A nicer config page or one-time secret generator can be added later.

Do not block the core session-mint smoke test on fancy configuration UX.

---

# 12. FIRST IMPLEMENTATION MILESTONE

The first milestone is intentionally boring:

```text
official template baseline loads
        ↓
controller route exists
        ↓
Jellyfin elevation policy works
        ↓
secondary-secret gate works
        ↓
user GUID resolves
        ↓
ISessionManager direct session call succeeds
        ↓
AccessToken returned once
```

Nothing else.

Specifically **do not start**:

- custom MPV Shim installers;
- mTLS packaging;
- UI polish;
- plugin catalog publishing;
- multiple app/client types;
- token dashboards;
- automation integrations.

until the primitive works end-to-end with a command-line request against a disposable server/user.

---

# 13. SECURITY SMOKE-TEST MATRIX

Use a disposable Jellyfin test instance where possible.

Minimum cases:

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

If Jellyfin API keys are part of the intended caller path, add explicit cases for valid and invalid API keys.

After successful minting:

1. Use the returned token against a harmless authenticated Jellyfin endpoint.
2. Confirm Jellyfin identifies the session as the requested target user.
3. Confirm the device/session appears through Jellyfin's normal administration view/API.
4. Revoke/delete the device/session through Jellyfin's normal mechanism.
5. Confirm the token stops authenticating.
6. Search Jellyfin/plugin logs and prove the token was never written there.

---

# 14. NEGATIVE SECURITY TESTS

Also test malformed and hostile input:

```text
empty device ID
very long device ID
very long device name
newline/control characters
empty target GUID
malformed GUID
removed user
wrong provisioning-secret length
random secret
missing Authorization header
normal non-admin token
```

A failure must not mint a session as a side effect.

---

# 15. MPV SHIM INTEGRATION — PHASE TWO ONLY

Jellyfin MPV Shim 3.x adds Quick Connect, but managed provisioning intentionally avoids requiring user interaction.

Once the plugin endpoint is proven:

1. Perform one legitimate MPV Shim 3.x login.
2. Inspect the exact current credential/config format that MPV Shim writes.
3. Determine exactly which values are required to restore that session.
4. Reproduce that structure in the installer builder.

Do **not** guess the `users.json` schema from memory.

Do **not** fork MPV Shim unless an actual blocker requires it.

The desired provisioning pipeline is:

```text
admin selects existing Jellyfin user
        ↓
provisioning service gets/creates stable deviceId
        ↓
provisioning service calls plugin
        ↓
receives target user's Jellyfin AccessToken
        ↓
builds custom installer
        ├── jellyfin-mpv-shim
        ├── Jellyfin URL/config
        ├── stable device identity/state
        ├── target-user session credential
        └── mTLS client material
        ↓
user installs
        ↓
client starts already enrolled
```

Provisioning authority stays server-side.

The generated installer must **not** contain:

```text
Jellyfin admin API key/session
session-provisioning secret
anything capable of minting additional users' sessions
```

---

# 16. RELATIONSHIP TO EXISTING PROJECTS

Do not reimplement these products:

### Jellyfin SSO

Interactive/external identity authentication. It establishes who a human is, then obtains a Jellyfin session.

Our plugin assumes the administrator has already decided who the target user is and is provisioning a managed device.

### Quick Connect

Excellent interactive device authorization flow, but still requires human approval.

Our use case is zero-touch managed deployment.

### Share Links

Temporary/guest sharing model. Useful technical precedent for server-side session creation, but not our identity/security model.

### Wizarr / jfa-go style provisioning

User/account creation and invitation management.

Our target user already exists.

A concise README distinction should eventually say something like:

> Session Provisioning does not provide SSO, create users, or disable Jellyfin authentication. It allows an authenticated Jellyfin administrator, subject to an additional provisioning secret, to create a normal device session for an existing Jellyfin user for managed-client deployment.

---

# 17. CLAUDE / CODE-AGENT REPOSITORY SCAFFOLDING

Assume the coding agent is competent but **do not assume a giant long-running frontier model will infer every architectural constraint correctly**.

Make the repo self-explanatory.

Recommended root files:

```text
CLAUDE.md
AGENTS.md
docs/ARCHITECTURE.md
docs/SECURITY.md
docs/TESTING.md
```

Do not duplicate giant amounts of prose across them.

## `CLAUDE.md`

Keep it short, operational, and checked into git.

Suggested initial contents:

```markdown
# CLAUDE.md

## Project
This is a Jellyfin server plugin for admin-authorized provisioning of normal Jellyfin user device sessions.

## Ground rules
- Start from and preserve Jellyfin's official plugin-template conventions.
- Target the exact Jellyfin server/package version recorded in the project.
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
3. Inspect the current Jellyfin plugin template conventions and package versions in this repo.
4. Verify any Jellyfin API signature against the version actually referenced by the project before coding against it.

## Validation
After meaningful changes:
- `dotnet build`
- run the relevant tests
- never claim auth/session behavior works without an integration test against Jellyfin

## Scope discipline
Prefer the smallest change that proves the current milestone. Do not add UI, installer generation, background services, generalized token management, or unrelated features unless explicitly requested.
```

That is enough. Do not stuff the entire handoff into `CLAUDE.md`.

If agent instructions grow, split narrowly-scoped rules into `.claude/rules/` rather than growing one enormous file.

## `AGENTS.md`

Use this as a model-neutral companion for Codex/other coding agents.

It can contain essentially the same non-negotiable engineering constraints as `CLAUDE.md`, either directly or by pointing agents to the canonical docs.

Suggested strategy:

```markdown
# AGENTS.md

Read `CLAUDE.md`, `docs/ARCHITECTURE.md`, and `docs/SECURITY.md` before changing authentication/session code.

The security invariants in those files are requirements, not suggestions.

Do not broaden the plugin's capability without explicit instruction.
```

Avoid maintaining two large conflicting instruction files.

---

# 18. `docs/ARCHITECTURE.md`

Keep the durable architecture here rather than in agent prompts.

It should capture:

```text
purpose
threat boundary
request flow
Jellyfin services/interfaces used
plugin route
configuration model
device-ID semantics
phase separation between plugin and external installer builder
```

Include this flow diagram:

```text
trusted provisioner
   │
   ├── Jellyfin admin authorization
   ├── provisioning secret
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

---

# 19. `docs/SECURITY.md`

This file matters more than a fancy README.

Record the invariants explicitly:

```text
- no anonymous minting
- no ordinary-user minting
- secondary secret is always mandatory
- no manual token generation
- no DB editing
- no logging sensitive credentials
- target privileges are whatever Jellyfin already assigns that target user
- caller/admin authority and target-user privilege are separate concepts
- provisioning credentials never enter generated client installers
```

Also state the consequence clearly:

> A session minted for a Jellyfin administrator is an administrator session. This is expected behavior because the plugin provisions a normal session for the requested existing user rather than generating a separately scoped playback token.

Document that the endpoint materially increases the power of whatever admin credential can reach it, which is why the independent provisioning secret exists.

---

# 20. `docs/TESTING.md`

Put reproducible commands here as they become known.

The goal is for a future Claude/Codex session to be able to run:

```text
build
install plugin into test Jellyfin
restart Jellyfin
curl negative auth case
curl positive auth + secret case
validate returned session
revoke session
validate revocation
```

Do not make agents rediscover the deployment path every session.

Never commit real API keys, access tokens, or provisioning secrets into this file.

Use placeholders/env vars.

---

# 21. GIT / AGENT WORKFLOW

Use small commits around meaningful milestones.

Suggested progression:

```text
1. template: rename official Jellyfin plugin template
2. build: pin target Jellyfin version and prove plugin loads
3. security: add provisioning-secret verification
4. api: add admin-protected mint endpoint skeleton
5. session: mint normal target-user session through Jellyfin
6. tests: add negative/positive integration coverage
7. docs: record verified API behavior and curl examples
8. packaging: only later, start MPV Shim provisioning work
```

Do not let a coding agent rewrite half the template while simultaneously implementing auth and packaging. Each step should leave a buildable/reviewable repository.

---

# 22. QUESTIONS THE CODING AGENT MUST ANSWER FROM SOURCE, NOT GUESS

Before implementing the session call, inspect the exact target Jellyfin packages/source and answer:

```text
1. What is the exact current namespace/signature of ISessionManager.AuthenticateDirect?
2. Which AuthenticationRequest fields are mandatory?
3. How is the target user identified?
4. What device/session record is created?
5. What happens if the same target user + deviceId already exists?
6. What exact authorization policy should protect a plugin controller endpoint?
7. Does the intended Jellyfin API-key authorization path satisfy that policy on this version?
8. What is the normal revocation path for the resulting session/device?
```

Put verified answers into `docs/ARCHITECTURE.md` or `docs/SECURITY.md` with code references/comments where useful.

If source behavior differs from this handoff, **the target Jellyfin source wins**. Update the documentation before proceeding.

---

# 23. WHAT NOT TO DO

Do not:

```text
- authenticate with the target user's password
- automate SSO browser clicks
- automate Quick Connect as a substitute for the intended primitive
- write directly to Jellyfin SQLite
- manufacture token strings yourself
- copy an administrator token into client packages
- ship the provisioning secret in client packages
- disable Jellyfin auth on the endpoint
- expose an anonymous mint route
- create guest users as a workaround
- create a parallel user permission system
- broaden the endpoint into a generic "authenticate as anyone" toolkit
- add installer logic before the plugin primitive is proven
- invent Jellyfin API signatures from memory
```

---

# 24. DEFINITION OF DONE — PLUGIN MVP

The plugin MVP is done when all of these are proven:

```text
✓ based on official Jellyfin plugin template
✓ builds cleanly against the exact target Jellyfin version
✓ loads cleanly into that Jellyfin server
✓ mint endpoint requires Jellyfin elevated/admin authorization
✓ mint endpoint independently requires provisioning secret
✓ wrong/missing either credential cannot create a session
✓ existing normal user can be targeted
✓ existing administrator user can be targeted
✓ resulting session has exactly the target user's Jellyfin permissions
✓ Jellyfin itself creates/persists the session/device/access token
✓ no direct DB manipulation
✓ no target-user password
✓ no SSO interaction
✓ no Quick Connect interaction
✓ resulting session can be revoked through normal Jellyfin mechanisms
✓ revocation invalidates the token
✓ secrets/tokens do not appear in logs
✓ code remains small enough to audit easily
```

Only then move to MPV Shim package generation.

---

# 25. FIRST PROMPT FOR CLAUDE/CODEX

Use something like this after creating the repo from the official template:

```text
Read CLAUDE.md, AGENTS.md, docs/ARCHITECTURE.md, docs/SECURITY.md, and this repository's existing Jellyfin plugin-template files before making changes.

For this first task, do not implement session minting yet.

1. Confirm the repository is still structurally aligned with the current official Jellyfin plugin template.
2. Rename the template project/namespaces to Jellyfin.Plugin.SessionProvisioning (or the repository's chosen final namespace).
3. Determine and record the exact Jellyfin server/package version this project targets.
4. Make the smallest changes necessary for the renamed plugin to build.
5. Do not remove template build/package/debug infrastructure unless it is demonstrably obsolete for the target template version.
6. Run the build and report the exact result.
7. Stop after the baseline plugin builds; do not implement the API endpoint yet.
```

Second task, after baseline is proven:

```text
Implement only the secondary provisioning-secret verifier and its configuration plumbing. Do not call ISessionManager yet. Add tests where practical, build, and stop.
```

Third task:

```text
Implement the smallest admin-protected POST /SessionProvisioning/Mint endpoint that validates input and resolves the requested existing Jellyfin user, but does not mint a session yet. Prove anonymous/non-admin/secret failures as far as the available test harness permits. Build and stop.
```

Fourth task:

```text
Inspect the exact target Jellyfin source/packages to verify ISessionManager direct-auth/session APIs. Document the verified signatures and behavior, then wire the endpoint to Jellyfin's own session machinery. Do not invent token generation or touch the database. Run the full smoke-test matrix.
```

This staged approach is intentional. It keeps Claude/Codex from trying to solve scaffolding, auth, secret storage, Jellyfin internals, testing, and client packaging in one giant speculative change.

---

# Core design rule

> Jellyfin owns identity, roles, permissions, session persistence, device persistence, token generation, and revocation. The plugin adds one narrowly gated administrative capability: authorize Jellyfin to provision a normal session for an existing user.

Keep that sentence true and the design stays sane.
