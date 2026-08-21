# Security

## Invariants

These are requirements, not preferences. A change that breaks one of them is a bug even
if every test passes.

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
- the plugin stores no secret and exposes no settings UI
```

## The two gates

A mint request succeeds only when **both** hold:

1. Jellyfin considers the caller elevated (`Policies.RequiresElevation`);
2. the caller presents the correct provisioning secret in
   `X-Session-Provisioning-Secret`.

Neither gate substitutes for the other, and neither may be made conditional (no "skip
the secret on localhost", no "skip elevation for an allowlisted key").

## Why the second gate exists

On Jellyfin 10.11.11, `CustomAuthenticationHandler` assigns the `Administrator` role to
**any valid API key**, not only to admin users
(`authorizationInfo.IsApiKey || user.HasPermission(IsAdministrator)` — see
`ARCHITECTURE.md` §7). Every API key already issued on the server therefore satisfies
`RequiresElevation` on its own.

Without the secondary secret, installing this plugin would silently upgrade every
existing API key — including ones handed to unrelated integrations — into the power to
mint a session for any user, including administrators. The provisioning secret keeps
that capability with the provisioning service alone.

State this plainly when documenting deployment: **the endpoint materially increases the
power of whatever admin credential can reach it.**

## Consequence to state clearly

> A session minted for a Jellyfin administrator is an administrator session. This is
> expected behavior because the plugin provisions a normal session for the requested
> existing user rather than generating a separately scoped playback token.

The plugin does not create users, does not elevate the target user, and maintains no
second RBAC list. `target Bob` gets Bob's existing permissions; `target Alice`, an
admin, gets Alice's existing admin permissions.

## The provisioning secret

Header (fixed, do not rename):

```http
X-Session-Provisioning-Secret: <secret>
```

### Generation

At least 256 bits of randomness, machine-generated:

```sh
openssl rand -base64 32 | tr '+/' '-_' | tr -d '='
```

### Storage

The plugin stores nothing. Only `SHA-256(secret)`, hex-encoded, is supplied by the
deployment environment:

```sh
printf '%s' "$SECRET" | sha256sum
```

```text
SESSION_PROVISIONING_SECRET_HASH        the hash itself
SESSION_PROVISIONING_SECRET_HASH_FILE   path to a root-owned or mounted file holding it
```

The file form takes precedence when both are set, and is the preferred one: the file
can be root-owned with restrictive permissions, and only its *path* ever reaches a log.

There is no plugin configuration and no dashboard page, so the secret is never typed
into, rendered by, or persisted through Jellyfin's web UI, and no admin session can
read it back out of the server.

**Do not name these variables with a `JELLYFIN_` prefix.** Jellyfin logs every
environment variable beginning with `JELLYFIN_`, `DOTNET_`, or `ASPNETCORE_`, with its
value, on every startup (`StartupHelpers.LogEnvironmentInfo`). A prefixed name puts the
hash in the server log at each boot.

Plain SHA-256 is sufficient **because the secret is a uniformly random 256-bit value** —
offline guessing is not a realistic attack against that input. If human-chosen
passphrases are ever accepted, this must change to a password KDF (Argon2id/bcrypt).

Verification compares fixed-size hashes in **constant time**
(`CryptographicOperations.FixedTimeEquals`). A missing or malformed configured hash
means the endpoint refuses everything — fail closed, never fail open.

The plugin never stores, echoes, or recovers the plaintext secret. Rotation is:
generate a new secret, replace the configured hash, update the provisioning service.

## Logging

Never log:

- the caller's Jellyfin token or API key;
- the provisioning secret, or any prefix/suffix of it;
- the configured secret hash;
- the newly minted access token;
- full request headers.

The permitted audit line is the shape of the operation, not its secrets:

```text
Session provisioning succeeded user=<guid> device=<device-id>
```

Failures log the reason category (unauthorized / bad secret / unknown user / invalid
input) and nothing that would help an attacker calibrate a guess.

### Upstream caveat: Jellyfin logs tokens it invalidates

`SessionManager.Logout(Device)` writes the access token in plaintext at INFO
("Logging out access token {0}"). Provisioning reaches it whenever an admin revokes a
device and whenever an existing user+device pair is re-minted, since Jellyfin logs out
the previous device before issuing a new token.

The token logged there is being invalidated at that moment, so the exposure is bounded,
but the consequence stands: **Jellyfin server logs are credential-bearing and must be
handled accordingly** — restricted permissions, and scrubbed before being attached to
bug reports. Nothing the plugin can do prevents this short of not using Jellyfin's own
session machinery, which is not a trade worth making.

`scripts/smoke-test.sh` asserts that every occurrence of a minted token in the logs
comes from that upstream line, and that the plugin contributes none.

## Network defense in depth

Application authorization is mandatory even on a trusted network. Where the deployment
supports it, the endpoint may *additionally* sit behind reverse-proxy source
restrictions, mTLS, a private management network, or firewall policy. These are extra
layers, never replacements — and the plugin itself does not become a home-grown
firewall (no IP allowlists in plugin config).

## Out of scope by design

The generated client installer must never contain a Jellyfin admin API key or session,
the provisioning secret, or anything else capable of minting additional users'
sessions. It carries exactly one target user's device credential.
