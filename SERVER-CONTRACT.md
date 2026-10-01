> Snapshot of `docs/client-integration.md` from OpenVPN Pilot Server **v1.0.0**, kept here only while
> server mode is being built, so the work can continue without the server repository next to this one.
> The original is authoritative: https://github.com/OpenVPN-Pilot/OVP-Server/blob/v1.0.0/docs/client-integration.md
> Links in it are relative to the server repository. Remove this file before the pull request into `dev`.

# Integrating the client

Everything a client needs to talk to OpenVPN Pilot Server: how to find out how to sign in, which
headers every call carries, how a session is kept alive, how the local copy stays in step, what every
error means, and what to do when the server says the account is gone.

The server never starts a tunnel. It hands out profiles, the credentials they need and each person's
own favourites, shortcuts and settings; connecting stays entirely the client's job, exactly as it is for
a profile that was imported from a file.

Swagger at `/swagger` on a running server shows the same API with every field, when the operator has
it switched on. This page is the contract; where the two ever disagree, this page is wrong and gets fixed.

## Contents

- [Transport](#transport)
- [First contact](#first-contact)
- [Mandatory headers](#mandatory-headers)
- [Signing in](#signing-in)
- [Staying signed in](#staying-signed-in)
- [The wipe directive](#the-wipe-directive)
- [Errors](#errors)
- [Roles](#roles)
- [Synchronisation](#synchronisation)
- [Profiles and tags](#profiles-and-tags)
- [The shared vault](#the-shared-vault)
- [Favourites, shortcuts and settings](#favourites-shortcuts-and-settings)
- [User administration](#user-administration)
- [Endpoint reference](#endpoint-reference)
- [Mapping onto the client's model](#mapping-onto-the-clients-model)

## Transport

- **HTTPS only.** Passwords, refresh tokens, configurations with private keys and vault entries travel
  in plain text inside TLS, so the server refuses anything that did not arrive over HTTPS
  (`transport.https_required`). Validate the certificate the normal way through the operating system's
  trust store. Never switch validation off; an operator with a private authority installs it on the
  machine, as for any other internal service.
- **JSON** with camelCase property names, UTF-8. Request bodies are `application/json`; errors are
  `application/problem+json`.
- **Nothing is cached.** Every `/api` response says `Cache-Control: no-store`; a client must not keep
  responses in an HTTP cache either, since they carry tokens, keys and passwords.
- **Times** are ISO 8601 with an offset, always UTC from the server, for example
  `2026-09-18T09:23:09.4177416+00:00`.
- **Identifiers** are GUIDs. Profiles created on the server have ids the client has never seen; see
  [Mapping](#mapping-onto-the-clients-model).
- All paths start with `/api/v1`. The version is in the path and in a header, and a server that
  speaks a different version refuses rather than guessing.
- **Body size.** A batch import may be up to 64 MiB, every other request up to 30 MB. A larger body is
  refused with 413 `request.too_large`. The server may close the connection while a client is still
  sending, so the client may see a reset connection instead of the answer; keep batches under the
  limit rather than relying on the answer.

## First contact

`GET /api/v1/server/info` is anonymous and the only API call that needs no `X-Pilot-*` headers.
Ask it when the user enters a server address, and again at every start.

```json
{
  "name": "OpenVPN Pilot Server",
  "version": "1.0.0",
  "apiVersion": "1",
  "minimumClientVersion": "1.9.0",
  "authMode": "ldap",
  "passwordRequired": true,
  "entra": null
}
```

| Field | Use |
| --- | --- |
| `name` | Always `OpenVPN Pilot Server`. Anything else means the address points somewhere else. |
| `apiVersion` | `1`. A client that does not speak it says so instead of carrying on. |
| `minimumClientVersion` | Older clients are refused with 426 `pilot.client_outdated`. Tell the user to update before they try to sign in. |
| `authMode` | `none`, `file`, `ldap` or `entra`. Decides the sign in screen, see below. |
| `passwordRequired` | `false` in `none` and `entra`: show no password field. |
| `entra` | Only in mode `entra`: `tenantId`, `clientId`, `scope` and `authority` for the Microsoft sign in. |

## Mandatory headers

Every other call under `/api/v1` carries these, including sign in and refresh:

| Header | Value | Refused with |
| --- | --- | --- |
| `X-Pilot-Client-Version` | The client's own version, for example `1.9.0`. A suffix such as `-beta.1` is allowed and ignored when comparing. | 400 `pilot.header_missing` / `pilot.header_invalid`, 426 `pilot.client_outdated` |
| `X-Pilot-Api-Version` | `1` | 400 `pilot.api_version_unsupported` |
| `X-Pilot-Client-Id` | A GUID generated once per installation and kept for good, next to the settings. Not per user and not per server. | 400 `pilot.header_invalid` |
| `X-Pilot-Platform` | `windows`, `macos` or `linux` | 400 `pilot.header_invalid` |
| `X-Pilot-Timestamp` | The moment the request is sent, ISO 8601 UTC, for example `2026-09-18T09:23:09Z` | 400 `pilot.clock_skew` when it is further off the server's clock than the operator allows, five minutes by default |
| `X-Pilot-Request-Id` | Optional. 8 to 64 letters, digits or dashes. | never; an unusable value is replaced |

The client id matters beyond identification: tokens are bound to it. An access or refresh token used
with another client id is refused with `auth.client_mismatch`, so a token copied off one machine is
worthless on another. Keep the id stable across updates and reinstallation of the same user profile.

`pilot.clock_skew` is a problem with the machine's clock, not with the server. Its detail names both
times; show that to the user rather than retrying.

Every response carries `X-Pilot-Request-Id` (the one sent, or a new one), `X-Pilot-Server-Version`
and `X-Pilot-Api-Version`. Put the request id into the client's own log and into any error shown to the
user: the operator finds every server log line of that request by it.

## Signing in

### Modes `none`, `file` and `ldap`

```http
POST /api/v1/auth/login
{ "username": "alice", "password": "..." }
```

In mode `none` the password is ignored and may be left out. The answer to every successful sign in,
refresh and Entra exchange has the same shape:

```json
{
  "accessToken": "eyJhbGciOi...",
  "accessTokenExpiresAt": "2026-09-18T09:38:09+00:00",
  "refreshToken": "wS3m...",
  "refreshTokenExpiresAt": "2026-10-18T09:23:09+00:00",
  "user": {
    "id": "01a0b3d3-6064-75e9-a940-c28df3732a05",
    "username": "alice",
    "displayName": "Alice Example",
    "role": "admin",
    "provider": "file"
  }
}
```

A wrong name or password is 401 `auth.invalid_credentials`, and the two cannot be told apart. An LDAP
account outside the group the operator allows is 403 `auth.forbidden`. More attempts a minute from one
address than the operator allows (10 by default, sign in and Entra exchange together) are 429
`request.too_many` with `Retry-After` in seconds.

Sign in, Entra exchange, refresh, sign out and `server/info` do not look at an `Authorization` header.
A client that sends its old access token along by default does no harm there.

### Mode `entra`

The client signs in with Microsoft itself, as a public client with the authorisation code flow and
PKCE in the system browser (MSAL does this), using the values from `server/info`:

- authority: `entra.authority`
- client id: `entra.clientId`
- scope: `entra.scope`

It then trades the Entra access token for this server's tokens:

```http
POST /api/v1/auth/entra/exchange
{ "accessToken": "<the Entra access token>" }
```

The answer is the same token response as above. From here on the client uses only this server's
tokens; the Entra token is not needed again until Entra asks for a new sign in (see
`auth.reauthentication_required` below). `POST /api/v1/auth/login` in this mode answers 400
`auth.mode_mismatch`, and the exchange in any other mode does the same.

### Storing the tokens

- The **refresh token** is a credential. Store it in the operating system keystore through the
  existing `ISecretStore`, for example under `server/{server id}/refresh`, never in `settings.json`.
- The **access token** can live in memory only.
- `user.role` decides what the interface offers; see [Roles](#roles).

`GET /api/v1/auth/me` answers the same `user` object for the current token.

## Staying signed in

- An access token lives 15 minutes by default (`accessTokenExpiresAt` says exactly). Send it as
  `Authorization: Bearer <token>`.
- Refresh shortly before it expires, or when a call answers 401 `auth.token_expired`, then repeat the
  call once:

  ```http
  POST /api/v1/auth/refresh
  { "refreshToken": "wS3m..." }
  ```

- **A refresh token is single use.** Every refresh returns a new pair; store the new refresh token
  before using the new access token. Presenting a refresh token a second time is taken as theft: the
  whole session is ended and the answer is 401 `auth.refresh_token_reused`. The client must therefore
  never refresh twice at the same time. Put the refresh behind a single lock and let every other call
  wait for its result. Two refreshes with the same token that arrive at the same moment count as reuse
  too: one gets the new pair, the other `auth.refresh_token_reused`, and the session ends for both.
- Refresh is limited to 30 calls a minute per installation (`X-Pilot-Client-Id`), separately from sign
  in, so a team behind one address is never throttled. A 429 here means the client refreshes in a loop.
- 401 `auth.token_revoked` on an ordinary call means the account changed (for example its role) and
  the token was withdrawn. Refresh once and repeat; the new token carries the new role.
- 401 `auth.refresh_token_invalid` (unknown, expired or signed out) or `auth.refresh_token_reused`:
  sign in again. Nothing is erased.
- 401 `auth.reauthentication_required`: the server wants the user to prove who they are again. In
  mode `entra` this happens after the period the operator sets, eight hours by default, and it is how
  a disabled Entra account is noticed. It also happens after the operator switched the server to
  another sign in mode. Sign in again; nothing is erased.
- 503 `auth.provider_unavailable`: the directory or Entra cannot be reached. Keep what is cached,
  keep working offline and try again later.
- `POST /api/v1/auth/logout` with the refresh token ends this installation's session (204). Discard
  both tokens afterwards whatever the answer. The access token itself stays valid until it expires,
  at most one access token lifetime; the client simply stops using it.

## The wipe directive

When an account is disabled or deleted, by an administrator or by the identity provider (removed from
the user file, disabled in the directory, taken out of the group that grants access), the next request
the client makes with that account's tokens is answered:

```http
HTTP/1.1 401 Unauthorized
X-Pilot-Directive: wipe
Content-Type: application/problem+json

{ "code": "account.revoked", "detail": "This account has been disabled or removed. ...", ... }
```

It can arrive on any call: an ordinary request, a refresh, or a sign in with a correct password for
an account that has been switched off. **The header decides, not the status code.** Whenever
`X-Pilot-Directive: wipe` is present:

1. Disconnect every tunnel of a profile that came from this server.
2. Delete every profile that came from this server, and its materialised runtime configuration.
3. Delete every vault entry that came from this server from the keystore: the secrets stored under
   `profile/{profileId:N}/{realm}` for those profiles.
4. Delete this server's refresh token, the sync cursor, and the copies of favourites, shortcuts and
   settings that were received from it.
5. Remove the server from the client's list of servers, or mark it as signed out, and tell the user
   in plain words that the account no longer has access.
6. Do not retry, and do not touch anything that did not come from this server: locally imported
   profiles and their stored sign ins stay.

The directive is only ever sent in answer to a token the server itself signed, or to a correct
password, so a stranger cannot trigger it. It only travels over HTTPS. A client that is offline when
the account is removed receives it the next time it reaches the server.

Two cases where a client is told to sign in instead, and so keeps its copy until it does:

- The administrator purged the user (`DELETE /api/v1/users/{id}?purge=true`). With the record gone the
  server no longer recognises the account's refresh tokens, and answers `auth.refresh_token_invalid`.
- An account disabled in Active Directory, when the client's refresh token has already expired. The
  directory refuses a disabled account's password, so it cannot be proven, and the answer is
  `auth.invalid_credentials`.

A client that is told to sign in and cannot, because the account is gone, should therefore offer the user
to remove this server and what came from it.

## Errors

Every refusal is RFC 9457 problem details:

```json
{
  "type": "urn:openvpnpilot:error:profile.duplicate",
  "title": "profile.duplicate",
  "status": 409,
  "detail": "The profile 'Example Site A' has exactly this configuration.",
  "instance": "/api/v1/profiles",
  "code": "profile.duplicate",
  "requestId": "557d1488-153d-4f0e-9d6a-2f4c8f0a1b2c",
  "errors": { "Username": ["The Username field is required."] }
}
```

Branch on `code`, never on `detail`: codes are stable, texts may change. `errors` is only present
for `request.validation_failed`.

| Code | Status | Meaning | What the client does |
| --- | --- | --- | --- |
| `request.validation_failed` | 400 | A field is missing, too long or malformed; `errors` names it | Fix the request; a defect in the client if it reaches a user |
| `request.not_found` | 404 | No such route | Defect |
| `request.precondition_required` | 428 | A profile update without `If-Match` | Send the ETag |
| `request.precondition_failed` | 412 | Someone changed it since it was read | Read again, reapply, retry |
| `request.conflict` | 409 | Collided with a change at the same moment, or referred to a profile deleted at that moment | Retry; the retry then answers what is wrong |
| `request.too_large` | 413 | The body is larger than the endpoint accepts | Send less at once, for example split a batch |
| `request.too_many` | 429 | Too many sign in attempts from this address, or refreshes from this installation | Wait `Retry-After` seconds |
| `transport.https_required` | 400 | Arrived without HTTPS | Configuration error; use `https://` |
| `pilot.header_missing` | 400 | A mandatory header is absent | Defect |
| `pilot.header_invalid` | 400 | A mandatory header is malformed | Defect |
| `pilot.api_version_unsupported` | 400 | Different API version | Tell the user client and server do not match |
| `pilot.client_outdated` | 426 | Client older than `minimumClientVersion` | Ask the user to update |
| `pilot.clock_skew` | 400 | The machine's clock is off | Tell the user to fix the clock |
| `auth.invalid_credentials` | 401 | Wrong name or password, or an Entra token that was not accepted | Ask again |
| `auth.mode_mismatch` | 400 | Password sign in on an Entra server or the reverse | Read `server/info` again |
| `auth.provider_unavailable` | 503 | Directory or Entra unreachable, or the directory not set up as the server expects | Stay offline, retry later; never wipe |
| `auth.forbidden` | 403 | Not allowed: not an administrator, or not in the allowed group | Hide the action; explain |
| `auth.identity_conflict` | 409 | Entra ID: this name belongs to another account on the server | Explain; an administrator has to act |
| `auth.token_missing` | 401 | No access token | Sign in |
| `auth.token_invalid` | 401 | Token malformed or not signed by this server | Refresh; if that fails, sign in |
| `auth.token_expired` | 401 | Access token expired | Refresh and repeat |
| `auth.token_revoked` | 401 | Account changed, token withdrawn | Refresh and repeat |
| `auth.client_mismatch` | 401 | Token belongs to another installation | Sign in again on this one |
| `auth.refresh_token_invalid` | 401 | Refresh token unknown, expired or signed out | Sign in |
| `auth.refresh_token_reused` | 401 | Refresh token used twice; session ended | Sign in; check the client never refreshes in parallel |
| `auth.reauthentication_required` | 401 | Prove identity again | Sign in |
| `account.revoked` | 401 | Account disabled or removed; comes with the wipe directive | [Wipe](#the-wipe-directive) |
| `profile.not_found` | 404 | No such profile | Drop it locally |
| `profile.duplicate` | 409 | Another profile has exactly this configuration | Show which one |
| `profile.invalid_configuration` | 400 | Empty, larger than 256 KiB, an unclosed block, a remote host over 255 characters, or credentials in an `<auth-user-pass>` block | Show the detail |
| `profile.not_self_contained` | 400 | A directive names a file instead of an inline block | Import locally first, which inlines files |
| `profile.no_server_verification` | 400 | No `ca`, `capath`, `pkcs12` or `peer-fingerprint` | Show the detail |
| `tag.not_found` | 404 | No such tag | Refresh the tag list |
| `tag.duplicate` | 409 | A tag of that name exists | Show it |
| `vault.entry_not_found` | 404 | No such vault entry | Drop it locally |
| `vault.entry_exists` | 409 | The vault already has this sign in | Fetch it instead; only an administrator replaces it |
| `sync.cursor_expired` | 410 | The cursor cannot be answered | Synchronise from 0 |
| `user.not_found` | 404 | No such user | Refresh the list |
| `user.self_modification` | 409 | An administrator tried to disable or delete themselves | Explain |
| `server.error` | 500 | A fault on the server | Show the request id |
| `server.data_key_mismatch` | 500 | Stored data could not be decrypted | Show the request id; the operator must act |

## Roles

| | `user` | `admin` |
| --- | --- | --- |
| List and read profiles, tags, configurations | yes | yes |
| Read vault entries | yes | yes |
| Add a vault entry that does not exist yet | yes | yes |
| Replace or delete a vault entry | | yes |
| Create, change and delete profiles and tags, batch import | | yes |
| Own favourites, shortcuts and settings | yes | yes |
| Manage users | | yes |

A `user` should not see buttons that would only earn 403 `auth.forbidden`. For server profiles that
means no edit, rename, delete or import, while connecting and everything personal works as today.

## Synchronisation

The client keeps a local copy of the server's profiles, tags and vault so it can connect without the
server, and brings it up to date with one call:

```http
GET /api/v1/sync/changes?since=0
```

```json
{
  "cursor": 42,
  "full": true,
  "profiles": [ { "id": "...", "name": "Example Site A", "contentHash": "49c2...", "changeSeq": 40, "...": "..." } ],
  "tags": [ { "id": "...", "name": "Office", "colour": null, "changeSeq": 1 } ],
  "vaultEntries": [ { "profileId": "...", "realm": "Auth", "username": "vpnuser", "password": "...", "changeSeq": 41, "...": "..." } ],
  "deletedProfiles": [],
  "deletedTags": [],
  "deletedVaultEntries": []
}
```

1. The first time, and whenever told to start over, ask with `since=0`. The answer has `full: true` and
   is the complete state: anything the client holds from this server that is not in it no longer
   exists there and is removed locally.
2. Store `cursor` and pass it as `since` next time. The answer then holds only what changed after it,
   deletions included, with `full: false`.
3. For every profile in `profiles`: create or update the local copy's metadata. When `contentHash`
   differs from the one held, fetch `GET /api/v1/profiles/{id}/configuration` and replace the local
   configuration. The hash is lower case hex SHA-256 of the configuration text, computed exactly as
   `ProfileImporter.ComputeHash` does.
4. For every entry in `vaultEntries`: write the secret to the keystore under
   `profile/{profileId:N}/{realm}`, replacing what is there. The same reference the credential provider
   already reads, so connecting needs no change.
5. `deletedProfiles`: delete the profile, its runtime file and every keystore entry under its id.
   `deletedVaultEntries`: delete that keystore entry only. `deletedTags`: drop the tag.
6. 410 `sync.cursor_expired`: forget the cursor and start again from 0. It happens when a client was
   away for longer than deletion records are kept (90 days), or when the server was restored from a
   backup older than the cursor.

When to synchronise: at start, after signing in, after every change this client made, and on a timer
of a few minutes while running. A change another person makes reaches everyone within that interval.
The cursor is only valid for the server that issued it; keep one per server.

The call is cheap when nothing changed: an empty delta and the same cursor.

Apply an answer in the order above, entries before deletions. An entry and a deletion of the same key
never arrive in one answer: a vault entry deleted and added again is reported as the entry alone.

Favourites, shortcuts and settings are not part of this feed. Read `/me/favourites`, `/me/hotkeys` and
`/me/settings` at start, after signing in, and whenever a synchronisation reported deleted profiles,
since deleting a profile removes it from everyone's favourites (see below).

## Profiles and tags

### Reading

- `GET /api/v1/profiles` lists every profile without configurations. `?tag=Office` filters by tag,
  `?search=text` matches name, remote host and tag regardless of case, as the client's search does.
- `GET /api/v1/profiles/{id}` is one profile, with its ETag also in the `ETag` header.
- `GET /api/v1/profiles/{id}/configuration` is what the client connects with:

  ```json
  { "profileId": "...", "contentHash": "49c2...", "configuration": "client\ndev tun\n...<ca>\n...\n</ca>\n" }
  ```

  Every read of a configuration is recorded in the server log with who read it.

### How the server reads a configuration

The server reads lines, quotes, inline blocks and directives exactly as the client's `OvpnConfigParser`
does, and derives the list fields as `OvpnConfiguration` and `ProfileConfigurationFacts` do:

- Lines end at `\r\n`, `\n` or a lone `\r`. Lines starting with `#` or `;` are comments.
- Arguments may be quoted with `"` or `'`; the quotes are not part of the value.
- Directive names are case sensitive and taken as written.
- `remoteHost` is the first argument of the first `remote`. `remotePort` is that remote's second
  argument, else the first `rport`, else the first `port`, else 1194. `protocol` is the remote's third
  argument, else the first `proto`; `tcp` when it starts with `tcp` regardless of case, else `udp`.
- Server verification counts as present with an inline `<ca>` or `<pkcs12>` block, or a `ca`, `capath`,
  `peer-fingerprint` or `pkcs12` directive. An inline `<peer-fingerprint>` block alone does not count,
  as it does not in the client.

A profile:

| Field | Meaning |
| --- | --- |
| `id` | Stable. Also the key of its vault entries. |
| `name`, `notes`, `colour` | As on the client. `colour` is `#RRGGBB` or `#RRGGBBAA`. |
| `remoteHost`, `remotePort`, `protocol` | From the first `remote`, `rport`/`port` and `proto`, as `ProfileConfigurationFacts` derives them. `protocol` is `udp` or `tcp`. |
| `requiresCredentials` | The configuration has `auth-user-pass`. |
| `hasUnsupportedOptions` | The configuration runs a program (`up`, `down`, `tls-verify` and the rest of the client's `ScriptDirectives`). |
| `protectRoutes` | `null` follows the client's setting. |
| `tags` | Tag names. |
| `contentHash` | See synchronisation. |
| `changeSeq` | Change number of the last change. |
| `eTag` | For `If-Match`. |
| `createdAt`, `createdBy`, `updatedAt`, `updatedBy` | Who and when. |

### Changing (administrators)

- `POST /api/v1/profiles` creates one:

  ```json
  {
    "name": "Example Site A",
    "configuration": "client\n...",
    "notes": null,
    "colour": "#3366ff",
    "protectRoutes": null,
    "tags": ["Office", "Europe"]
  }
  ```

  The configuration must be self contained, which is what the client's importer produces: every
  `ca`, `cert`, `key`, `dh`, `extra-certs`, `pkcs12`, `crl-verify`, `secret`, `tls-auth`, `tls-crypt`
  and `tls-crypt-v2` as an inline block (`dh none` is fine). So the upload path is: import locally with
  the existing wizard, then send each accepted configuration. Tags that do not exist are created. 201
  with the profile; 409 `profile.duplicate` when an identical configuration is stored.

  **The server stores the configuration in one respect differently from how it was sent:** a line
  `auth-user-pass <file>` becomes a bare `auth-user-pass`. The file holds a user name and password,
  which belong in the vault, and exists on no other machine; the bare directive makes OpenVPN ask, and
  the client answers from the vault as for any other profile. Every other byte is kept. The answer's
  `contentHash` is therefore the hash of what was stored, which differs from the local copy's hash
  when the line was rewritten. Take the configuration and hash from the server for the server copy;
  sending the same local profile again is still recognised as a duplicate.
- `POST /api/v1/profiles/batch` takes `{ "items": [ ...up to 500 of the above... ] }` and judges each
  item on its own:

  ```json
  { "created": 2, "duplicates": 1, "rejected": 1,
    "items": [ { "index": 0, "outcome": "created", "profile": { }, "code": null, "detail": null },
               { "index": 1, "outcome": "duplicate", "profile": null, "code": "profile.duplicate", "detail": "..." } ] }
  ```

  This is the server side of bulk import and maps onto the import wizard's review list. Every limit of
  a single profile is checked per item, so one item with a name that is too long, a malformed colour or
  even `null` is `rejected` with its code and detail, and the others are still created. An item
  identical to an earlier item of the same batch that was created is a `duplicate`. The whole call is
  refused only when `items` is missing, empty or longer than 500 (400), or the body is over 64 MiB
  (413). Split a large import into batches by count and by size.
- `PUT /api/v1/profiles/{id}` replaces name, notes, colour, route protection and tags, and the
  configuration when `configuration` is not null. It requires `If-Match: "<eTag>"`: without it 428,
  with a stale one 412, in which case read the profile again, reapply and retry. `If-Match` follows
  RFC 9110: `*` matches whatever is stored, a comma separated list matches when any of its tags does,
  and a malformed value matches nothing (412), it is never taken as absent.
- `DELETE /api/v1/profiles/{id}` deletes it together with its vault entries (204).

Tags: `GET /api/v1/tags`; administrators `POST`, `PUT /{id}` and `DELETE /{id}` with
`{ "name": "Office", "colour": "#ffaa00" }`. Names are unique regardless of case. Renaming or deleting a
tag counts as a change of every profile carrying it, so those arrive in the next synchronisation.

## The shared vault

One sign in per profile and realm, shared by everyone. The realm is OpenVPN's: `Auth` for the user
name and password of `auth-user-pass`, or the name of a private key for its passphrase, exactly the
`realm` of a `CredentialRequest`. It is part of the path and must be URL encoded, a slash as `%2F`
(`Uri.EscapeDataString` does both). The server takes it exactly as sent: 1 to 200 characters, no
control characters, and no space at either end, which is refused rather than trimmed, because a
trimmed realm would never match the keystore key the client looks up.

- `GET /api/v1/vault` returns every entry of every profile; `GET /api/v1/profiles/{id}/vault` those
  of one profile. Synchronisation delivers them too.
- `POST /api/v1/profiles/{id}/vault/{realm}` with `{ "username": "vpnuser", "password": "..." }`
  adds an entry that does not exist yet. **Every user may do this.** 201 with the entry, or 409
  `vault.entry_exists` when one is there.
- `PUT` on the same path stores or replaces it; `DELETE` removes it. Administrators only.

The intended flow on the client:

1. Before connecting a server profile, look in the keystore as today. Synchronisation has usually
   put the shared entry there already.
2. When OpenVPN asks and nothing is stored, prompt as today. When the connection succeeds with what
   was typed and the vault has no entry for that realm, `POST` it, so nobody else has to type it. Do
   not post a one time code, and do not post what failed.
3. On 409, the vault gained an entry meanwhile: fetch it and store it locally.
4. When the shared entry stops working, a user can overwrite their local keystore copy as today;
   replacing the shared one is for an administrator, through `PUT`.

`username` is `null` for a passphrase. Secrets are encrypted at rest on the server and are never
written to its logs, only that someone read or changed them.

## Favourites, shortcuts and settings

These belong to the signed in person, not to the team, and follow them to every machine. Each list is
replaced as a whole.

- `GET` / `PUT /api/v1/me/favourites`

  ```json
  { "items": [ { "profileId": "...", "slot": 1 }, { "profileId": "...", "slot": null } ] }
  ```

  `slot` is 1 to 10 (`HotkeyActions.MaximumFavouriteSlot`, 10 being the zero key) or `null`, each
  slot at most once, each profile at most once. Only server profiles can be favourites here; local
  profiles keep their favourite flags locally. When a profile is deleted, it disappears from every
  user's favourites.
- `GET` / `PUT /api/v1/me/hotkeys`

  ```json
  { "items": [ { "actionId": "ToggleQuickSwitcher", "gesture": "Control+Alt+V", "profileId": null, "isEnabled": true } ] }
  ```

  `actionId` is one of `HotkeyActions.All`, `gesture` is written as the client stores it. The server
  does not interpret either. When the profile a shortcut names is deleted, the shortcut stays and its
  `profileId` becomes `null`.
- `GET` / `PUT /api/v1/me/settings`

  ```json
  { "schemaVersion": 2, "document": { "general": { }, "appearance": { "theme": "Dark" }, "...": "..." } }
  ```

  `document` is the portable part of `PilotSettings`, exactly what `PilotSettingsTransfer.Export`
  produces, a JSON object of at most 64 KiB in UTF-8; apply it with `PilotSettingsTransfer.Import` so
  machine specific values stay. `schemaVersion` is `PilotSettings.SchemaVersion`. The answer carries an
  `eTag`; send it as `If-Match` to refuse overwriting a change another machine made (412), or leave it
  out to overwrite. `If-Match` before anything was stored, or a malformed one, is 412 as well.
  A `GET` before anything was stored answers `schemaVersion` 0, an empty document and `eTag` null.

Usage figures, the last connection and the number of connections (`Profile.LastConnectedAt`,
`ConnectCount`) are not kept on the server. They describe one machine's use, drive that machine's
"Recent" list and its "connect last used" shortcut, and recording them centrally would make the
server keep the connection history it deliberately does not keep.

## User administration

Administrators only, for an administration screen if the client offers one:

| Call | Effect |
| --- | --- |
| `GET /api/v1/users`, `GET /api/v1/users/{id}` | Everyone who has signed in, with state and last activity |
| `POST /api/v1/users/{id}/disable` | Disables; their clients get the wipe directive on their next request |
| `POST /api/v1/users/{id}/enable` | Enables a disabled or deleted user |
| `POST /api/v1/users/{id}/revoke-tokens` | Signs them out everywhere without erasing anything |
| `DELETE /api/v1/users/{id}` | Deletes; the wipe directive as with disable. The record stays so the name cannot simply sign in again |
| `DELETE /api/v1/users/{id}?purge=true` | Also removes the record and their favourites, shortcuts and settings |

A user's role comes from the identity provider (the user file, the directory group, the Entra role or
group, or the list of administrators in mode `none`), not from this API.

## Endpoint reference

Every endpoint with what it takes, what it answers and how it refuses. Field names are camelCase JSON;
`?` after a type means it may be `null`; times are ISO 8601 with offset; ids are GUIDs.

### Refusals every endpoint can give

Not repeated below. Every call under `/api/v1` except `server/info` can answer:

| Status | Code | When |
| --- | --- | --- |
| 400 | `transport.https_required` | Arrived over plain HTTP |
| 400 | `pilot.header_missing`, `pilot.header_invalid`, `pilot.api_version_unsupported`, `pilot.clock_skew` | A mandatory header is absent or wrong |
| 426 | `pilot.client_outdated` | Client older than `minimumClientVersion` |
| 400 | `request.validation_failed` | A field breaks its limit; `errors` names it |
| 413 | `request.too_large` | Body over the endpoint's limit |
| 500 | `server.error`, `server.data_key_mismatch` | A fault on the server; show the request id |

and every endpoint that needs a signed in user additionally:

| Status | Code | When |
| --- | --- | --- |
| 401 | `auth.token_missing`, `auth.token_invalid`, `auth.token_expired`, `auth.token_revoked`, `auth.client_mismatch` | See [Staying signed in](#staying-signed-in) |
| 401 + `X-Pilot-Directive: wipe` | `account.revoked` | See [The wipe directive](#the-wipe-directive) |
| 403 | `auth.forbidden` | An administrator endpoint called by a user |

### Types

**Token** (answer of sign in, Entra exchange and refresh)

```json
{
  "accessToken": "string",
  "accessTokenExpiresAt": "2026-09-18T09:38:09+00:00",
  "refreshToken": "string",
  "refreshTokenExpiresAt": "2026-10-18T09:23:09+00:00",
  "user": { "id": "guid", "username": "string", "displayName": "string?", "role": "admin|user", "provider": "none|file|ldap|entra" }
}
```

**Profile**

```json
{
  "id": "guid", "name": "string", "remoteHost": "string?", "remotePort": 1194, "protocol": "udp|tcp|null",
  "requiresCredentials": true, "hasUnsupportedOptions": false, "protectRoutes": null,
  "notes": "string?", "colour": "#RRGGBB|#RRGGBBAA|null", "tags": ["string"],
  "contentHash": "64 lower case hex", "changeSeq": 40, "eTag": "\"1234\"",
  "createdAt": "time", "createdBy": "string", "updatedAt": "time", "updatedBy": "string"
}
```

`remotePort` and `protocol` are `null` exactly when `remoteHost` is. `tags` are sorted regardless of case.

**Configuration**: `{ "profileId": "guid", "contentHash": "string", "configuration": "string" }`

**Batch result**

```json
{ "created": 1, "duplicates": 1, "rejected": 1,
  "items": [ { "index": 0, "outcome": "created|duplicate|rejected", "profile": "Profile?", "code": "string?", "detail": "string?" } ] }
```

**Tag**: `{ "id": "guid", "name": "string", "colour": "string?", "changeSeq": 1 }`

**Vault entry**

```json
{ "profileId": "guid", "realm": "Auth", "username": "string?", "password": "string", "changeSeq": 41,
  "createdAt": "time", "createdBy": "string", "updatedAt": "time", "updatedBy": "string" }
```

**Changes**

```json
{ "cursor": 42, "full": false, "profiles": ["Profile"], "tags": ["Tag"], "vaultEntries": ["Vault entry"],
  "deletedProfiles": ["guid"], "deletedTags": ["guid"], "deletedVaultEntries": [ { "profileId": "guid", "realm": "string" } ] }
```

**Favourites**: `{ "items": [ { "profileId": "guid", "slot": 1 } ] }`, slotted ones first in slot order.

**Hotkeys**: `{ "items": [ { "actionId": "string", "gesture": "string", "profileId": "guid?", "isEnabled": true } ] }`, by `actionId`.

**Settings**: `{ "schemaVersion": 2, "document": { }, "eTag": "string?", "updatedAt": "time?" }`

**User** (administration)

```json
{ "id": "guid", "username": "string", "displayName": "string?", "role": "admin|user", "provider": "none|file|ldap|entra",
  "state": "active|disabled|deleted", "stateSource": "administrator|provider|null", "stateChangedAt": "time?",
  "createdAt": "time", "lastLoginAt": "time?", "lastSeenAt": "time?" }
```

### Server and health

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/server/info` | anyone, no `X-Pilot-*` headers | nothing | 200, see [First contact](#first-contact) | nothing |
| `GET /health`, `/health/ready` | anyone, no headers | nothing | 200 `Healthy` when the database answers, else 503 | nothing |
| `GET /health/live` | anyone, no headers | nothing | 200 `Healthy` while the process runs | nothing |

### Signing in

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `POST /api/v1/auth/login` | anyone | `{ "username": "1..256", "password": "string? ..1024" }` | 200 Token | 400 `auth.mode_mismatch` (Entra server); 401 `auth.invalid_credentials`; 403 `auth.forbidden` (outside the allowed group); 401 `account.revoked` + wipe (correct password, account switched off); 429 `request.too_many`; 503 `auth.provider_unavailable` |
| `POST /api/v1/auth/entra/exchange` | anyone | `{ "accessToken": "Entra token ..16384" }` | 200 Token | 400 `auth.mode_mismatch`; 401 `auth.invalid_credentials` (token not accepted); 403 `auth.forbidden`; 401 `account.revoked` + wipe; 409 `auth.identity_conflict`; 429; 503 |
| `POST /api/v1/auth/refresh` | anyone | `{ "refreshToken": "..512" }` | 200 Token, a new pair | 401 `auth.refresh_token_invalid`, `auth.refresh_token_reused`, `auth.client_mismatch`, `auth.reauthentication_required`; 401 `account.revoked` + wipe; 429 (30 a minute per installation); 503 |
| `POST /api/v1/auth/logout` | anyone | `{ "refreshToken": "..512" }` | 204, also for an unknown token | nothing beyond the common ones |
| `GET /api/v1/auth/me` | signed in | nothing | 200 `user` object of the Token | the common ones |

### Profiles

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/profiles` | signed in | query `tag` (exact name, any case), `search` (name, remote host or tag contains, any case), both optional | 200 `[Profile]` by name | the common ones |
| `GET /api/v1/profiles/{id}` | signed in | nothing | 200 Profile, header `ETag` | 404 `profile.not_found` |
| `GET /api/v1/profiles/{id}/configuration` | signed in | nothing | 200 Configuration | 404 `profile.not_found` |
| `POST /api/v1/profiles` | admin | body below | 201 Profile, `Location` | 400 `profile.invalid_configuration`, `profile.not_self_contained`, `profile.no_server_verification`, `request.validation_failed`; 409 `profile.duplicate` |
| `POST /api/v1/profiles/batch` | admin | `{ "items": [ body below, 1..500 ] }`, at most 64 MiB | 200 Batch result | 400 when `items` is missing, empty or over 500; 413 `request.too_large`. Every other problem is an item's `rejected` or `duplicate` outcome |
| `PUT /api/v1/profiles/{id}` | admin | header `If-Match`; body below with `configuration` null to keep the current one | 200 Profile, header `ETag` | 428 `request.precondition_required`; 412 `request.precondition_failed`; 404 `profile.not_found`; 409 `profile.duplicate`; the 400 codes of `POST` |
| `DELETE /api/v1/profiles/{id}` | admin | nothing | 204; its vault entries go with it | 404 `profile.not_found` |

Profile body:

| Field | Type | Rule |
| --- | --- | --- |
| `name` | string | required, 1 to 200 characters, no control characters |
| `configuration` | string | required on create, at most 256 KiB, self contained (see [Profiles and tags](#profiles-and-tags)) |
| `notes` | string? | at most 4000 characters; `null` clears |
| `colour` | string? | `#RRGGBB` or `#RRGGBBAA`; `null` clears |
| `protectRoutes` | bool? | `null` follows the client's setting |
| `tags` | [string]? | at most 50, each 1 to 100 characters, no control characters, no `null`; created when unknown; on `PUT` the complete list |

### Tags

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/tags` | signed in | nothing | 200 `[Tag]` by name | the common ones |
| `POST /api/v1/tags` | admin | `{ "name": "1..100", "colour": "string?" }` | 201 Tag, `Location` | 409 `tag.duplicate` (any case) |
| `PUT /api/v1/tags/{id}` | admin | the same body | 200 Tag; a rename changes every profile carrying it | 404 `tag.not_found`; 409 `tag.duplicate` |
| `DELETE /api/v1/tags/{id}` | admin | nothing | 204; removed from every profile | 404 `tag.not_found` |

### Vault

`{realm}` is URL encoded, see [The shared vault](#the-shared-vault).

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/vault` | signed in | nothing | 200 `[Vault entry]` | the common ones |
| `GET /api/v1/profiles/{id}/vault` | signed in | nothing | 200 `[Vault entry]` | 404 `profile.not_found` |
| `POST /api/v1/profiles/{id}/vault/{realm}` | signed in | `{ "username": "string? ..512", "password": "1..4096" }` | 201 Vault entry, `Location` | 404 `profile.not_found`; 409 `vault.entry_exists`; 400 for the realm |
| `PUT /api/v1/profiles/{id}/vault/{realm}` | admin | the same body | 200 Vault entry, created or replaced | 404 `profile.not_found`; 400 for the realm |
| `DELETE /api/v1/profiles/{id}/vault/{realm}` | admin | nothing | 204 | 404 `vault.entry_not_found` |

An empty `username` is stored as `null`.

### Synchronisation

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/sync/changes` | signed in | query `since`: 0 or a cursor this server returned | 200 Changes | 400 for a negative `since`; 410 `sync.cursor_expired` |

### The caller's own data

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/me/favourites` | signed in | nothing | 200 Favourites | the common ones |
| `PUT /api/v1/me/favourites` | signed in | Favourites, up to 1000 items, `slot` 1 to 10 or `null` | 200 Favourites as stored | 400 for a slot or profile used twice or a `null` item; 404 `profile.not_found`; 409 `request.conflict` |
| `GET /api/v1/me/hotkeys` | signed in | nothing | 200 Hotkeys | the common ones |
| `PUT /api/v1/me/hotkeys` | signed in | Hotkeys, up to 200 items, `actionId` and `gesture` 1 to 100 characters | 200 Hotkeys as stored | 400 for an action used twice or a `null` item; 404 `profile.not_found`; 409 `request.conflict` |
| `GET /api/v1/me/settings` | signed in | nothing | 200 Settings | the common ones |
| `PUT /api/v1/me/settings` | signed in | `{ "schemaVersion": 0.., "document": { } }`, optional `If-Match` | 200 Settings | 400 when `document` is not an object or over 64 KiB; 412 `request.precondition_failed`; 409 `request.conflict` |

### User administration

| Call | Who | Takes | Answers | Refuses with |
| --- | --- | --- | --- | --- |
| `GET /api/v1/users` | admin | nothing | 200 `[User]` by name | the common ones |
| `GET /api/v1/users/{id}` | admin | nothing | 200 User | 404 `user.not_found` |
| `POST /api/v1/users/{id}/disable` | admin | nothing | 200 User | 404 `user.not_found`; 409 `user.self_modification` |
| `POST /api/v1/users/{id}/enable` | admin | nothing | 200 User | 404 `user.not_found` |
| `POST /api/v1/users/{id}/revoke-tokens` | admin | nothing | 200 User | 404 `user.not_found` |
| `DELETE /api/v1/users/{id}` | admin | query `purge`, default `false` | 204 | 404 `user.not_found`; 409 `user.self_modification` |

## Mapping onto the client's model


| Client | Server |
| --- | --- |
| `Profile.Id` | `profile.id`. Server profiles keep the server's id locally, so vault references and favourites line up. |
| `Profile.Source` | A new value such as `Server`, with `SourcePath` naming the server, so a wipe finds exactly what came from it. |
| `Profile.Configuration`, `ContentHash` | `configuration`, `contentHash` |
| `RemoteHost`, `RemotePort`, `Protocol`, `RequiresCredentials`, `HasUnsupportedOptions`, `ProtectRoutes`, `Notes`, `Colour` | same names |
| `Profile.Tags` | `tags` (names) |
| `Profile.IsFavourite`, `FavouriteSlot` | `/me/favourites`, per user |
| `HotkeyBinding` (`ActionId`, `Gesture`, `ProfileId`, `IsEnabled`) | `/me/hotkeys` items |
| `settings.json` | `/me/settings`, the portable part only |
| `ISecretStore` entry `profile/{id:N}/{realm}` | vault entry `(profileId, realm)` |
| `PackagedCredential` (`ProfileId`, `Realm`, `Username`, `Password`) | vault entry, the same four fields |
| `Session` history | stays local; the server keeps no history |
| `Profile.LastConnectedAt`, `ConnectCount` | stay local, per machine |

Where the client's code touches this: `IProfileStore`, `IHotkeyStore` and `ISettingsService` are the
seams a server backed implementation fits behind; `IProfileImportService` already has the prepare and
commit steps that map onto the batch call; `ISecretStore` stays the local store the vault fills; and
`ConnectionManager` keeps receiving the configuration text as it does now. `docs/usage.md` in the
client promises that nothing but the release check touches the network, and needs to say that a
configured server does too.

Things the client has to get right that are easy to miss:

- **Deleting a server profile deletes its keystore entries.** The client's own delete does not remove
  `profile/{id:N}/*` today; for a profile from the server, a deletion in the feed and the wipe directive
  both have to.
- **The local copy is identified by the server's id**, not by the hash: a server profile can carry the
  same configuration as a local import, and the two are different profiles.
- **Upload goes through the import wizard.** It inlines files; the server additionally rewrites
  `auth-user-pass <file>` and answers with the stored configuration, which the client keeps as the server
  copy.
- **One refresh at a time**, behind a lock, and the new refresh token stored before the new access token
  is used.
- **One client id per installation**, generated once and kept next to the settings; tokens do not work
  under another.
- **The request id** of every failed call goes into the client's log and into what the user sees.
- **Plain HTTP and certificate errors are never worked around**; the operator fixes the certificate.
