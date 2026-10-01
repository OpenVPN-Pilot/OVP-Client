# Implementation brief: server mode (sync, online, offline) for OpenVPN Pilot

You are implementing the client side of OpenVPN Pilot Server in this repository (OVP-Client, C#/.NET 10,
Avalonia). The server is finished, released as **v1.0.0**, and documented. Your job is to let a machine
run either **fully local, as today**, or **against a server**, never both at once, with an offline mode
that keeps working from a local copy and pushes its own changes later without asking.

This file is a working brief, not part of the product. It lives on the branch `feature/server-mode`
together with `SERVER-CONTRACT.md`, a snapshot of the server's contract, so the work can continue on any
machine. **Remove both files in the last commit before the pull request into `dev`.** Read this brief
completely before you write code, then work through section 12 in order.

---

## 0. Ground rules

- `CLAUDE.md` in this repository is the standard the code is held to. Read all of it first. Everything in
  this brief is subordinate to it. Where this brief seems to contradict it, stop and ask.
- Rules that matter most here: DI everywhere, everything behind an interface, constructor injection, no
  static mutable state; `Nullable` on and warnings as errors; async with a `CancellationToken` on every
  async method, nothing long on the UI thread; `Core`, `OpenVpn`, `Data` and `App` stay platform neutral;
  every user facing string localizable (English **and** German); comments explain why; no emoji; no
  silent `catch`; no TODO as a solution; **no workarounds**: when something is blocked or behaves
  unexpectedly, stop and report with evidence and options.
- **Secrets never appear in logs, exports, git or the database.** Refresh tokens, passwords and vault
  entries live in `ISecretStore` only. Configurations carry private keys: never log their content.
- **TLS is never weakened** in product code. No `ServerCertificateCustomValidationCallback` returning
  true, no "ignore certificate" setting. An operator with a private authority installs it in the
  operating system's trust store. Tests may pin one specific test certificate (section 10).
- Neutrality: examples use `vpn.example.com`, `example-site`, `203.0.113.0/24`, `198.51.100.0/24`,
  `https://pilot.example.com`. No real names, hosts or organisations anywhere.
- Ask the user when a decision is genuinely theirs. Section 3 already settles the big ones; do not
  re-open them.

## 1. The contract

The server's contract is **`docs/client-integration.md` in OVP-Server**. Every endpoint, header, body,
answer, error code and behaviour you rely on is there, including a full endpoint reference.

- In this branch: `SERVER-CONTRACT.md`, a snapshot of v1.0.0 (its relative links point into the server
  repository).
- Released version: https://github.com/OpenVPN-Pilot/OVP-Server/blob/v1.0.0/docs/client-integration.md
- With the server repository checked out next to this one: `..\OVP-Server\docs\client-integration.md`
- Sign in modes and the Entra app registration:
  https://github.com/OpenVPN-Pilot/OVP-Server/blob/v1.0.0/docs/authentication.md
- A running server also serves its OpenAPI document at `/swagger/v1/swagger.json` (when the operator
  enabled Swagger). The markdown contract wins where they disagree.

Read the contract completely before section 5. Do not change the server. If the contract lacks
something you need, stop and report it; do not work around it in the client.

Facts from the contract you will need constantly:

- HTTPS only, JSON camelCase, `Cache-Control: no-store`, all paths under `/api/v1`.
- Mandatory headers on every call except `GET /api/v1/server/info` and `/health*`:
  `X-Pilot-Client-Version` (assembly version, 3 parts), `X-Pilot-Api-Version: 1`,
  `X-Pilot-Client-Id` (installation GUID), `X-Pilot-Platform` (`windows`/`macos`/`linux`),
  `X-Pilot-Timestamp` (UTC ISO 8601), optional `X-Pilot-Request-Id`. Every answer echoes
  `X-Pilot-Request-Id`; log it with every failure.
- Errors are RFC 9457 problem details. **Branch on `code`, never on text or status alone.**
- Access token about 15 minutes, in memory only. Refresh token single use, bound to the client id, stored
  in `ISecretStore`. **Refresh behind one lock**; two parallel refreshes with one token end the session.
- `X-Pilot-Directive: wipe` on any answer decides a wipe, whatever the status.
- Sync: `GET /api/v1/sync/changes?since=<cursor>`; `since=0` gives `full: true`; apply entries before
  deletions; `410 sync.cursor_expired` means start again from 0; fetch
  `GET /api/v1/profiles/{id}/configuration` only when `contentHash` changed.
- Personal data is not in the feed: `/api/v1/me/favourites`, `/me/hotkeys`, `/me/settings`.
- Vault entries map 1:1 to `ISecretStore` references `profile/{profileId:N}/{realm}`.
- Two roles. `user`: read, connect, add a missing vault entry, own favourites/shortcuts/settings.
  `admin`: additionally create, change, delete profiles and tags, replace/delete vault entries, manage users.
- The server rewrites `auth-user-pass <file>` to a bare `auth-user-pass` on upload and answers with the
  stored configuration and its hash. A server profile's local copy always takes the server's text and hash.
- Batch import: up to 500 items and 64 MiB per call, each item judged on its own.
- `If-Match: *` overwrites whatever the server holds. That is what the offline push uses (section 6.6).

## 2. Read first in this repository

The investigation that preceded this brief found the following. Verify against the code before relying
on a line number, because `origin/dev` is ahead of `master`.

- `CLAUDE.md`, `CHANGELOG.md` (format and branching note at the top), `docs/usage.md`, `docs/cli.md`,
  `docs/development.md`, `README.md`.
- Startup and composition: `src/OpenVpnPilot.App/Program.cs`, `App.axaml.cs`
  (`OnFrameworkInitializationCompleted`, `StartServices`, `LoadAndAnnounceAsync`,
  `StartBackgroundWorkAsync`, `Teardown` with its 4 s budget), `AppHost.cs` (the only composition root).
  **Services are composed before settings are loaded.** That drives decision 3.2.
- Settings: `src/OpenVpnPilot.Core/Settings/PilotSettings.cs` (sections, `CurrentSchemaVersion`,
  `Migrate()`: adding a setting needs no migration), `JsonSettingsService.cs` (a missing file is written
  with defaults; that is the only first-run signal today), `PilotSettingsTransfer.cs` (what is portable
  and what is machine specific).
- Storage: `src/OpenVpnPilot.Data/PilotDbContext.cs`, `Entities/Profile.cs` (`ProfileSource`),
  `src/OpenVpnPilot.App/Services/ProfileStore.cs` (`IProfileStore`), `HotkeyStore.cs`, `SessionStore.cs`,
  `ProfileImportService.cs`, `ProfilePackageWriter.cs`, `src/OpenVpnPilot.Core/Abstractions/ISecretStore.cs`
  (`SecretReference`), `src/OpenVpnPilot.Core/Storage/ApplicationPaths.cs`.
- Connection and credentials: `MainWindowViewModel.ConnectAsync`,
  `src/OpenVpnPilot.OpenVpn/Runtime/ConnectionManager.cs`, `ConnectionSupervisor.cs`
  (`AnswerCredentialRequestAsync`, `CONNECTED`), `src/OpenVpnPilot.App/Services/StoredCredentialProvider.cs`,
  `SessionRecorder.cs` (the pattern for listening to `ConnectionManager.StateChanged`).
- UI: `Views/MainWindow.axaml` (row 3 is the status bar), `MainWindowViewModel.cs` (`StatusMessage`,
  totals), `Views/SettingsWindow.axaml` + `SettingsViewModel.cs` (tabs, draft, save, the "check now"
  precedent), `TrayIconController.cs`, `ApplicationMenuController.cs` (macOS), `WindowCoordinator.cs`,
  `ImportViewModel.cs`, `ProfileEditorViewModel.cs`.
- Logging: `AppHost.cs` (Serilog under MEL, only sink `LogHubSink`), `Services/LogHub.cs` (`LogSource`
  is `Pilot` or `OpenVpn` today), `LogViewModel.cs` (source filter), `AppLog.cs`, `SessionRecorderLog.cs`.
- Network precedent: `src/OpenVpnPilot.Core/Updates/GitHubReleaseChecker.cs`,
  `src/OpenVpnPilot.App/Services/UpdateCoordinator.cs`, `PingMonitor.cs`, `EnvironmentGate.cs`.
- CLI: `src/OpenVpnPilot.Cli/` (`ListCommand.StoreFactory`, `ConnectCommand` with its own database path,
  `ImportCommand`, `PackageCommand`, `ProfileCommands`).
- Localization: `src/OpenVpnPilot.App/lang/en.json` and `de.json`; tests
  `tests/OpenVpnPilot.App.Tests/Localization/CatalogueCoverageTests.cs` and `ShippedCatalogueTests.cs`
  (every key in both languages, same placeholders, no empty values).
- Tests: `tests/*` (xUnit, hand written fakes in `tests/OpenVpnPilot.App.Tests/Fakes.cs`, no mocking
  library, `Method_Scenario_Expectation` names, real SQLite in a temp folder, `StubHandler` for HTTP in
  `GitHubReleaseCheckerTests`).
- **Precedent worth reading:** 1.6.0 had a "shared library" with a status bar segment, a sync log
  flyout, a banner and a "never both, refuse while connected" switch. It was removed in commit
  `c023159`. Read it for UI and switching patterns, not for its merge logic:
  `git show c023159 -- src/OpenVpnPilot.App/Views/MainWindow.axaml`,
  `git show c023159^:src/OpenVpnPilot.App/Services/Library/SharedLibraryStatus.cs`,
  `git show c023159^:src/OpenVpnPilot.App/Services/Library/SharedLibraryText.cs`.
  Do not reuse the settings key `library`; old settings files may still carry it.

## 3. Decisions already made

Do not re-open these. They follow from what the user asked for and from the code as it is.

### 3.1 Two modes, never both

`Local` is today's behaviour, unchanged. `Server` means one configured server, and this machine shows
only that server's profiles. Switching is an explicit action in the settings, refused while any tunnel
is up (as 1.6.0 did), and restarts the application into the other mode. Switching never deletes the
other mode's data: going back to `Local` shows the local library exactly as it was.

### 3.2 The server copy is an ordinary local database

In `Server` mode the application uses a **second SQLite file with the same schema**, a cache owned by
the sync engine, at `<DataDirectory>/servers/<serverKey>/pilot.db` (`serverKey`: lower case hex of the
first 16 bytes of SHA-256 over the normalised base URL). Every existing store, view model, the history,
the quick switcher, the hotkeys and the connection path then work unchanged on that file. The sync engine
writes into it; local changes are recorded in an outbox table in the same file.

Consequence: the database path must be known **before** `AppHost.Build`. Add a small reader in `Core`
(for example `StorageModeReader`) that reads only the mode and server URL from `settings.json`,
tolerantly, with no side effects, and use it in the app before composing and in the CLI. Resolve the
active database path through `IApplicationPaths` (or a new `IActiveStorage`), never by building paths in
callers. `ConnectCommand`'s hard-coded path must go through the same resolution.

### 3.3 Switching restarts the application

Persist the new mode, then start a new instance and exit. The new instance must wait for the old one
to release the single instance guard: pass a startup option such as `--after-restart <pid>` that waits,
bounded, for that process to exit before claiming the guard. Put the restart behind an interface in
`Core` with whatever platform specifics macOS needs. Tunnels are already down (3.1); the outbox is in
the database, so nothing needs flushing within the 4 s teardown budget.

### 3.4 First run asks

When there is no `settings.json` (a true first start), the application asks before the main window:
**"Keep profiles on this computer"** or **"Use a server"**, plus "decide later" (= local). An existing
installation that updates to this version is never asked and stays `Local`. `JsonSettingsService` must
expose whether the file existed when it was loaded (capture it before writing defaults); do not infer it
from anything else.

### 3.5 Offline mode with dumb sync

The cache database is the working copy. When the server is unreachable, everything that works from the
cache keeps working: listing, searching, connecting, history, favourites, shortcuts, settings, and for
administrators editing, importing and deleting. Every local change in `Server` mode is recorded as a
**dirty marker** in the outbox. When the server is reachable again the client **pushes first, then
pulls**:

- **Dumb push, no three-way merge, no "which is newer".** The client says "here are my changes" and the
  server takes them. Profile updates go with `If-Match: *`, settings without `If-Match`, favourites and
  shortcuts as whole lists. Someone else's change to the same profile in the meantime is overwritten.
  That is the intended behaviour; do not add conflict prompts.
- The outbox holds **which entity is dirty, not a copy of it**. At push time the client sends the
  entity's **current** local state. Several edits of one profile become one `PUT`. A delete supersedes
  pending edits. An offline create followed by edits becomes one `POST` with the latest state.
- Vault entries are never written into the outbox. The marker names `(profileId, realm)`; the secret is
  read from `ISecretStore` at push time.
- After a successful push the pull applies the server's state, which now includes the pushed changes.
  While an entity still has a pending marker, the pull does not overwrite it locally (local pending wins
  until it has been pushed).

Section 6.6 has the exact rules per entity and per answer.

### 3.6 What stays on the machine

Session history, `LastConnectedAt` and `ConnectCount` stay in the cache database and never go to the
server. The server deliberately keeps no connection history.

### 3.7 Settings

The machine specific values `PilotSettingsTransfer` already strips stay local. In `Server` mode the
portable part follows the server (`/me/settings`); switching back to `Local` keeps whatever is current
(no separate copy per mode). The new mode and server fields are machine specific: add them to what
`PilotSettingsTransfer` strips on export and restores on import.

## 4. Branch and workflow

- Work on **`feature/server-mode`**. It already exists on `origin`, branched from `dev` at 1.9.0, and
  holds this brief: `git fetch origin && git switch feature/server-mode`. Merge `origin/dev` into it when
  dev moves on.
- Commit on `feature/server-mode` in small, buildable steps with gitmoji subjects and a body that says
  why (see `git log` for the style), ending with the co-author trailer your environment gives you.
  `CHANGELOG.md` gets an `## [Unreleased]` section (there is none yet) with entries in the same commit as
  the change they describe.
- `dotnet build` with zero warnings and `dotnet test` green after every commit.
- Do not merge into `dev` or `master`. When everything in section 11 is done, ask the user before pushing
  the branch and opening a pull request into `dev`.
- Do not bump the version; a release is the user's decision.

## 5. The server connection (Core and App)

Put protocol code that has no UI in `src/OpenVpnPilot.Core/Server/` (platform neutral), UI and
orchestration in `src/OpenVpnPilot.App/Services/Server/`. Suggested types, adjust names to the code
base's conventions:

### 5.1 Contracts

Hand written records for every request and answer you use, matching the contract field by field
(camelCase via `JsonSerializerOptions`, `DateTimeOffset`, `Guid`). A `ServerProblem` record for problem
details with `Code`, `Status`, `Detail`, `RequestId`, `Errors`. A `ServerErrorCodes` static class with
the codes as constants, so nothing branches on string literals scattered around.

### 5.2 `IServerApi` and the HTTP pipeline

- One `HttpClient` per configured server, created by a small factory you own (no
  `IHttpClientFactory`; the project does not reference Microsoft.Extensions.Http). Timeout 15 s for
  ordinary calls, longer for batch upload. No `Expect: 100-continue`.
- A `DelegatingHandler` that adds the mandatory headers to every call except `server/info` and `/health*`:
  client version (3 parts, as `UpdateCoordinator.CurrentVersion` computes it), API version `1`, the
  installation id, platform, timestamp at send time, a fresh request id.
- A handler or wrapper that inspects **every** answer for `X-Pilot-Directive: wipe` and raises one event
  (section 5.5), before anything else looks at the answer.
- Authorisation: `Authorization: Bearer <access token>` from an `IServerSession`. On 401
  `auth.token_expired` or `auth.token_revoked`: refresh once and repeat the call once.
- **Refresh lock:** one `SemaphoreSlim` per session. A caller that finds a refresh in progress waits for
  its result instead of starting another. Store the new refresh token in `ISecretStore` **before** the
  new access token is used. Refresh proactively about a minute before `accessTokenExpiresAt`.
- Map failures to a small result type the callers switch on: success; problem (with code); offline
  (`HttpRequestException`, timeout, DNS, connection refused); TLS refused (certificate not trusted:
  show it as a configuration problem, never offer to bypass). Never throw raw `HttpRequestException`
  into view models.
- `server/info` and `/health/ready` are anonymous and carry no `X-Pilot-*` headers.

### 5.3 Installation id and tokens

- Installation id: a GUID generated once per installation, stored as a machine specific setting (stripped
  by `PilotSettingsTransfer`), never regenerated on update. Tokens are bound to it.
- Refresh token: `ISecretStore` under a new reference kind, for example `server/{serverKey}/refresh`.
  Extend `SecretReference` with a builder and a parser for it, and make sure every code path that
  enumerates `profile/...` references (export, "sign in again", "forget all") treats the new kind
  correctly. "Forget all stored credentials" in the settings must also sign out of the server.
- Access token: memory only.
- Signed in user (`id`, `username`, `displayName`, `role`, `provider`): keep the last known value in the
  cache database or a machine setting, so the UI can show the role while offline.

### 5.4 Sign in, per `authMode` from `GET /api/v1/server/info`

- Always first: `GET /api/v1/server/info`. Check `name == "OpenVPN Pilot Server"`, `apiVersion == "1"`,
  and `minimumClientVersion` against the own version (refuse with a clear "update the client" message).
- `none`: user name only. `file` and `ldap`: user name and password. `POST /api/v1/auth/login`.
- `entra`: sign in with Microsoft using **MSAL (`Microsoft.Identity.Client`)** as a public client,
  authorisation code with PKCE in the **system browser** (`WithUseEmbeddedWebView(false)`), redirect URI
  `http://localhost`, authority, client id and scope exactly as `server/info` gives them. Exchange the
  Entra access token at `POST /api/v1/auth/entra/exchange`. Keep MSAL's own token cache in memory only:
  the server's refresh token is what lasts. `auth.reauthentication_required` later means: run the
  Microsoft sign in again. Check that MSAL's system browser flow works on macOS as well; if it does not,
  stop and report.
- Show `auth.invalid_credentials`, `auth.forbidden`, `auth.provider_unavailable`, `request.too_many`
  (with `Retry-After`), `pilot.clock_skew` (with the detail: it names both times) as distinct, plain
  messages.
- Sign out: `POST /api/v1/auth/logout` with the refresh token, then discard both tokens whatever the
  answer. Signing out keeps the cache and the outbox; signing in again as **the same user** continues.
  Signing in as **a different user** first discards the outbox and the personal data, then does a full
  synchronisation.

### 5.5 The wipe directive

Implement exactly the steps in the contract, in this order, from one place (`IServerWipe` or similar):
disconnect every tunnel (all profiles shown are this server's), delete every keystore entry of every
profile in the cache database plus the refresh token, delete the cache folder
`<DataDirectory>/servers/<serverKey>/`, clear the server's personal state, switch the mode to `Local`,
tell the user in plain words that the account no longer has access, then restart into `Local`. Never
retry, never touch the local library or local keystore entries. Log it at warning level with the
request id. Collect the profile ids **before** deleting the database.

## 6. Synchronisation and offline

### 6.1 Schema additions (one EF migration in `OpenVpnPilot.Data`)

- `ProfileSource.Server` for profiles that came from the server.
- A `SyncState` row: cursor, last successful pull, last successful push, last error code, last request id.
- An outbox table `PendingChange`: `Id` (auto increment, gives the order), `Kind` (`ProfileCreate`,
  `ProfileUpdate`, `ProfileDelete`, `TagUpdate`, `TagDelete`, `VaultAdd`, `Favourites`, `Hotkeys`,
  `Settings`), `EntityId` (Guid?), `Realm` (string?), `CreatedAt`, `Attempts`, `LastErrorCode`. No
  payload column: the payload is the current state (3.5), and secrets never go into the database.
- The migration also runs on the local `pilot.db`; the tables simply stay empty there. Timestamps follow
  the existing ticks converter automatically (see `CLAUDE.md` verified facts).

### 6.2 Recording changes

Record markers where the change happens, in the same unit of work, and only in `Server` mode. Prefer a
decorator or a mode aware implementation of `IProfileStore`, `IHotkeyStore` and the settings path over
sprinkling mode checks through view models. Markers collapse: an existing `ProfileUpdate` for the same id
is not added twice; a `ProfileDelete` removes pending updates of that id; a delete of a profile that has
a pending `ProfileCreate` removes both and sends nothing. After recording, ask the engine for a push soon
(debounced about 2 seconds).

What a `user` can record: favourites, shortcuts, settings, vault adds. Everything else is administrator
only and hidden for users (section 7), so it cannot be recorded.

### 6.3 Pull (`ISyncEngine`)

One engine per session, one cycle at a time (a lock; a request during a cycle schedules one more cycle,
not a parallel one). A cycle: push (6.6), then pull:

1. `GET /api/v1/sync/changes?since=<cursor>` (0 when there is none).
2. Upsert profiles: shared fields only (name, notes, colour, protect routes, tags, remote host/port,
   protocol, requires credentials, has unsupported options, content hash, `Source = Server`), never
   `LastConnectedAt`, `ConnectCount`, favourites (they come from `/me/favourites`). When the hash differs
   from the stored one, fetch the configuration and store text and hash as given. Skip entities with a
   pending marker.
3. Tags: upsert and delete by id. Vault entries: `ISecretStore.WriteAsync` under
   `SecretReference.ForProfile(profileId, realm)`.
4. Deletions after entries: delete profiles (cascades sessions and tags locally, as a local delete
   does) and **their keystore entries** (the local delete path does not remove them today; for server
   profiles it must); delete single vault entries; drop tags.
5. `full: true`: remove every server profile, tag and vault entry that is not in the answer.
6. `410 sync.cursor_expired`: forget the cursor, run again from 0 in the same cycle.
7. Store the cursor only after everything of the answer was applied (one transaction for the database
   part; keystore writes before the transaction commits the cursor).
8. Read `/me/favourites`, `/me/hotkeys` and `/me/settings` and apply them unless a marker of that kind
   is pending. Favourites set `IsFavourite`/`FavouriteSlot` on the cache rows; hotkeys replace the cache's
   bindings; settings go through `PilotSettingsTransfer.Import`. Remember the settings `eTag` only for
   display; the push does not send `If-Match` (3.5).
9. Reload the main list on the UI thread when anything changed (`MainWindowViewModel.LoadAsync` or a
   lighter refresh), and refresh the quick switcher and hotkey slots the same way local changes do.

When: at start (from the cache first, network second, never blocking the window), after sign in,
after a local change (debounced), every 2 minutes while signed in, when the network comes back
(`NetworkChange.NetworkAvailabilityChanged`), and on "Sync now". Back off while offline (for example
5 s, 15 s, 30 s, 60 s, then every 60 s).

### 6.4 Connecting while offline

Connecting never waits for the server. The configuration and the vault entries are already local.

### 6.5 Vault: share what worked

Implement the contract's intended flow:

- `StoredCredentialProvider` records, per `(profileId, realm)`, whether the answer of this attempt was
  **typed** (prompt) rather than read from the keystore, and never for a one time code or a dynamic
  challenge.
- A separate listener on `ConnectionManager.StateChanged` (the `SessionRecorder` pattern; the provider
  must not depend on `ConnectionManager`, that is a DI cycle) records a `VaultAdd` marker on
  `Connected` for typed answers, and forgets them on `Failed` or a retry.
- Push: `POST /api/v1/profiles/{id}/vault/{realm}` with the secret read from `ISecretStore` (realm with
  `Uri.EscapeDataString`). `201`: done. `409 vault.entry_exists`: fetch the profile's entries and store the
  shared one locally. If the user ticked "remember" the secret is already stored; if not, the provider
  must keep it in memory until the push and never write it to the database.
- When the shared entry stops working, the existing retry path overwrites the local copy as today.
  Replacing the shared entry (`PUT`) is an administrator action in the profile editor.

### 6.6 Push (outbox), per kind

Process markers in `Id` order. Before each call read the current local state. Then:

| Kind | Call | Answer handling |
| --- | --- | --- |
| `ProfileCreate` | `POST /api/v1/profiles` (or a batch, see 6.7) with name, configuration, notes, colour, protect routes, tags | `201`: **re-key** the local profile from its temporary id to the server's id (below), store the returned configuration hash and fetch the configuration (it may have been rewritten). `409 profile.duplicate`: the server already has this configuration. After the next pull, re-key the temporary profile onto the server profile with the same content hash (when the local text has `auth-user-pass <file>`, hash it with that line reduced to a bare `auth-user-pass`, as the contract describes the server doing), so its keystore entries and favourite follow; if none matches, delete the temporary profile and its keystore entries. Log it either way. `400`: drop the marker, keep the local profile marked as not uploaded, report in the footer. |
| `ProfileUpdate` | `PUT /api/v1/profiles/{id}` with `If-Match: *` and the full current state (configuration only when it changed locally, else `null`) | `200`: done, take the returned `contentHash`. `404 profile.not_found`: someone deleted it; drop the marker, the pull removes it. `409 profile.duplicate`, `400`: drop, report. |
| `ProfileDelete` | `DELETE /api/v1/profiles/{id}` | `204` or `404`: done. |
| `TagUpdate` / `TagDelete` | `PUT` / `DELETE /api/v1/tags/{id}` | `404`: done. `409 tag.duplicate`: drop, report. |
| `VaultAdd` | 6.5 | 6.5 |
| `Favourites` | `PUT /api/v1/me/favourites` with the cache's current favourites | `404 profile.not_found`: drop favourites of unknown profiles, retry once. |
| `Hotkeys` | `PUT /api/v1/me/hotkeys` with the cache's current bindings | as above |
| `Settings` | `PUT /api/v1/me/settings` with `PilotSettingsTransfer.Export`, no `If-Match` | `400`: drop, report. |

Rules for every kind:

- **Offline or 5xx:** stop the push, keep the marker, increase `Attempts`, retry with the back off.
  Do not pull in this cycle.
- **401 needing sign in** (`auth.refresh_token_invalid`, `auth.refresh_token_reused`,
  `auth.reauthentication_required`): stop, keep everything, show "sign in again".
- **403 `auth.forbidden`:** the role changed to `user`; drop administrator markers, report, refresh the
  role.
- **Wipe directive:** 5.5, nothing else.
- **Permanent refusals** (400, 404, 409 as listed): drop the marker, log at warning with code and request
  id, and count it in the footer as "not synchronised" with the detail in the tooltip and the log.
- `request.too_many` / `request.too_large`: respect `Retry-After`; split batches.

**Re-keying an offline created profile** (temporary id → server id) happens in one database transaction:
insert the profile under the new id with all its columns, move its tag links and session rows, update
hotkey bindings and other references that carry the id, update pending markers that name the old id,
delete the old row. Then rename its keystore entries (`profile/{old:N}/*` → `profile/{new:N}/*`), then
mark `Favourites` dirty. If any step fails, the old profile and its marker stay as they were and the
create is retried; never leave half a re-key behind. Test this path thoroughly (section 10).

### 6.7 Import, packages, editor and delete in `Server` mode

- **Import wizard (administrators):** prepare as today, with duplicate detection against the cache
  database. Commit uploads with `POST /api/v1/profiles/batch` in chunks of at most 500 items and well
  under 64 MiB, and shows the per item outcome (`created`, `duplicate`, `rejected` with detail) in the
  review list. Created profiles are inserted into the cache from the answer and their configurations
  fetched. Offline, the commit creates local profiles with temporary ids and `ProfileCreate` markers
  instead. `IProfileImportService.CommitAsync` currently returns a count; change what it returns where
  the server path needs more.
- **Packages (`.ovppkg`):** exporting works in both modes (it reads). Applying a package in `Server` mode
  is the import path above for administrators, hidden for users. Packaged credentials become `VaultAdd`
  markers.
- **Profile editor:** administrators edit as today and every save records `ProfileUpdate`. Users see the
  editor read only except for their personal fields (favourite, slot). Hide rename, notes, configuration,
  tags, route protection, delete for users. Administrators additionally get "replace shared sign in"
  per realm (`PUT .../vault/{realm}`, online only).
- **Delete:** administrators only; records `ProfileDelete`; locally removes the profile and its keystore
  entries.
- **CLI:** reads (`list`, `connect`, `export`, `status`, completion) work on the active database in both
  modes. Commands that write the database (`import --commit`, `remove`, `favourite`, `unpack`) refuse in
  `Server` mode with a clear message and a dedicated exit code documented in `docs/cli.md`. Do not try to
  make the CLI talk to the server.

## 7. User interface

All text in `en.json` and `de.json`, live language switching as the rest of the app does it.

### 7.1 First run

A dialog before the main window, only on a true first start (3.4):

1. "Where should your profiles live?" Two large choices: **This computer** ("Profiles, sign ins and
   settings stay on this machine. Nothing is sent anywhere.") and **A server** ("Your team's profiles and
   shared sign ins come from an OpenVPN Pilot Server. Works offline from a local copy."). A small
   "Decide later" link equals "This computer".
2. Server path: address field (`https://` only; refuse `http://` with an explanation), "Continue" calls
   `server/info` and shows the server name, version and sign in mode, or the precise problem (not
   reachable, certificate not trusted, not an OpenVPN Pilot Server, API version, client too old).
3. Sign in according to `authMode` (5.4).
4. First full synchronisation with progress; then the main window. A failure in step 3 or 4 offers
   "Back" and "Use this computer instead".

### 7.2 Settings: a new tab "Storage" (German "Speicherort")

- Current mode, and for `Server`: address, signed in user, role, provider, server version, last sync,
  pending changes, buttons "Sync now", "Sign out" / "Sign in", "Show server log".
- "Switch to server…" / "Switch to this computer…": refuses while tunnels are up ("Disconnect first"),
  confirms what happens ("The application restarts. Your local profiles stay on this computer and come
  back when you switch again."), then for `Server` runs the same address and sign in steps as the first
  run inside the dialog, and restarts on success.
- Changing the server address is "switch to server" with a new address: a different `serverKey`, a
  different cache.
- The update section's hint that the update check is the only network access becomes conditional (see
  7.5).

### 7.3 Status bar (row 3 of `MainWindow.axaml`)

Keep `StatusMessage` and the traffic totals. Add a server segment, visible in `Server` mode only, in the
spirit of 1.6.0's sync segment:

- A state dot: green **online and synchronised**, blue **synchronising**, amber **offline, working from
  the local copy** or **changes waiting**, red **sign in required**, **client too old**, **clock wrong** or
  **certificate not trusted**.
- Text, compact: `pilot.example.com · 23 ms · synced 2 min ago · 3 changes waiting`. Latency is the round
  trip of `GET /api/v1/server/info` (anonymous, every 30 s while online, backed off while offline).
  "Healthy" comes from `GET /health/ready`: a reachable server that answers 503 there is shown as
  "server degraded". "synced …" is relative and updates every 30 s. "changes waiting" counts outbox
  markers; "not synchronised: n" counts dropped ones since the last success.
- Tooltip: server address and version, API version, signed in user and role, cursor, last pull and push
  times, last error code with its request id.
- Click: a flyout with "Sync now", "Show server log", "Sign in again" when needed, "Open storage
  settings".
- In `Local` mode show a small grey "Local" label with a tooltip that points to the storage settings.

### 7.4 Banners and menus

- Banner in row 1 (the existing banner stack) for states that block: sign in required, client too old
  (link to the update), clock wrong (show the detail), certificate not trusted. Offline is **not** a
  banner; it is the footer's amber state.
- Tray menu: in `Server` mode add "Sync now" and a disabled line with the state text; rebuild on state
  changes like the other entries. macOS application menu: the same through `ApplicationMenuController`.
- Header buttons: hide import for users in `Server` mode.

### 7.5 Wording that becomes untrue

`docs/usage.md`, `README.md`, `settings.checkForUpdatesHint` (en and de), the comments in
`PilotSettings.cs`, `IUpdateChecker.cs` and `SettingsWindow.axaml` all say the update check is the only
network access. Make each say that a configured server is contacted too, and nothing else.

## 8. Logging

- Use the existing pipeline: MEL `ILogger<T>` over Serilog into `LogHub`. Add `LogSource.Server` and a
  "Server" choice in the log window's source filter, so server traffic can be shown on its own; "Show
  server log" opens the log window with that filter.
- Source generated `[LoggerMessage]` methods in `*Log.cs` files next to the code that writes them
  (`AppLog.cs` and `SessionRecorderLog.cs` are the models). Pass values, not formatted strings (enums,
  ids, codes as they are).
- Event ids: the app uses blocks of 100 per component in the 3000s; 3600 to 3999 are free. Suggested:
  3600 connection and HTTP, 3700 sign in and tokens, 3800 sync engine, 3900 outbox and mode switch. Put a
  comment listing the blocks at the top of the first new log file.
- What to log:
  - Every server call at `Debug`: method, path, status, duration, request id.
  - Every failure at `Warning`: method, path, status, `code`, request id.
  - Sign in, sign out, refresh failure, role change, mode switch, first run choice: `Information`.
  - Wipe directive: `Warning`, with what was removed (counts, never values).
  - Every sync cycle: `Information` summary (pushed n, dropped n, pulled profiles/tags/vault/deletions,
    cursor from → to, duration); offline transitions once per transition, not per retry.
- **Never** log tokens, passwords, vault entries, configuration text or `Authorization` headers. Log that
  a secret was read or written and for which profile and realm, never its value.
- `DiagnosticsBundle`: add mode, server host, server and API version, last sync times, outbox count,
  last error codes and request ids. Nothing secret.

## 9. Documentation

- `docs/usage.md`: a section on server mode: first run, switching, what is shared and what stays
  personal, offline behaviour and dumb sync ("your changes are sent as they are when the server is back;
  a change someone else made to the same profile meanwhile is overwritten"), roles, sign out, the wipe.
- `docs/cli.md`: behaviour of every command in `Server` mode and the new exit code.
- `docs/development.md`: how to run the tests, including the integration tests against a local server
  (section 10.2).
- `README.md`: one feature line and the network statement (7.5).
- `CHANGELOG.md`: an `## [Unreleased]` section with `### Added` etc., in the style of the existing
  entries (bold lead-in where they use one).

## 10. Tests

### 10.1 Unit tests (xUnit, hand written fakes, no network)

Use a `StubHandler : HttpMessageHandler` (see `GitHubReleaseCheckerTests`) that scripts answers and
records requests, real SQLite in a temp folder for database paths, and the existing fakes. Cover at least:

- Headers: all mandatory headers present on API calls, absent on `server/info` and `/health*`, client
  version three parts, a fresh request id per call.
- Refresh: 401 `auth.token_expired` refreshes once and repeats; **ten parallel calls with an expired token
  cause exactly one refresh**; the new refresh token is stored before the access token is used; a failed
  refresh signs out without touching the cache.
- Wipe directive on an ordinary call, on refresh and on sign in: tunnels disconnected, cache folder and
  server keystore entries removed, local library and local keystore entries untouched, mode back to
  `Local`, no retry.
- Pull: full sync into an empty cache; delta with changes and deletions; entries before deletions;
  configuration fetched only on hash change; personal columns survive a full sync; `full: true` removes
  what is missing; 410 restarts from 0; cursor stored only after a complete apply; pending markers
  protect local state.
- Outbox: collapse rules (update+update, update+delete, create+edit, create+delete); push order; `PUT`
  with `If-Match: *`; every answer row of the table in 6.6; offline keeps markers and skips the pull;
  permanent refusals are dropped and counted.
- Re-key of an offline created profile: sessions, tags, hotkeys, markers and keystore entries follow the
  new id; a failure in the middle leaves everything as before.
- Vault: typed and connected posts once; read from keystore never posts; one time codes never post;
  409 stores the server's entry; failed connection posts nothing.
- First run detection: no settings file → asked; existing file → not asked, mode `Local`.
- Mode switch refused while a tunnel is up; storage resolution picks the right database path in both
  modes; the CLI's write commands refuse in `Server` mode.
- `PilotSettingsTransfer` strips and restores the new machine specific fields.
- View models: footer state texts for each state, role based visibility in the editor and the header.
- The localization tests stay green: every new key in English and German with the same placeholders.

### 10.2 Integration tests against a real server

Add them to a test project (or a new `tests/OpenVpnPilot.Server.IntegrationTests`), skipped unless
`OVP_TEST_SERVER_URL` is set. The test may pin the server's test certificate given in
`OVP_TEST_SERVER_CERT` (path to the PEM) with a validation callback that accepts **only that certificate**,
in test code only. Run the server from a checkout of the server repository next to this one
(`git clone git@github.com:OpenVPN-Pilot/OVP-Server.git ..\OVP-Server`, tag `v1.0.0` or later):

```powershell
cd ..\OVP-Server
# A test certificate for localhost
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost" -keyout certs/server.key -out certs/server.crt
Copy-Item .env.example .env       # fill OVP_DB_PASSWORD, OVP_DATA_KEY, OVP_JWT_SIGNING_KEY; OVP_AUTH_MODE=file; OVP_PUBLISH_PORT=19443 if 8443 is reserved
Copy-Item config/users.example.yaml config/users.yaml   # an admin and a user; plain passwords work for tests and are warned about
docker compose up -d --build
```

If the user already runs a stack there, use a separate Compose project name (`-p`) and port rather than
touching theirs, and ask before changing their `.env`. Cover end to end: sign in as admin and user,
upload a batch, full and delta sync, an offline period (stop the API container) with edits as admin and
favourite changes as user, push after restart of the container, a second client id seeing the changes, a
vault add and a 409 from a second client, the wipe directive (disable the user through
`POST /api/v1/users/{id}/disable` as admin), and `sync.cursor_expired` if you can provoke it.

### 10.3 Manual end to end with the application

Use the VPN lab in `lab/` for real tunnels and the server above for profiles:

1. First run with an empty data directory (move `%LOCALAPPDATA%\OpenVpnPilot` aside, and restore it after,
   with the user's consent): choose server, sign in, see the empty list, import the lab's client
   configurations as admin, see per item outcomes.
2. Connect a profile that needs credentials, type them with "remember" off, check the vault on the server
   got the entry, connect from a second machine or a second Windows user without typing.
3. Stop the API container: footer turns amber, connect still works, edit a profile and change favourites,
   footer shows waiting changes; start the container: the push happens, the footer turns green, the
   server shows the changes.
4. Switch to local in the settings while connected (refused), disconnect, switch (restart), the local
   library is back unchanged; switch back to server.
5. Disable the user on the server: the next request wipes, the app tells the user and restarts local.
6. Check the log window's "Server" filter and the log files for completeness, and grep the log folder for
   the test passwords and refresh tokens: nothing may be found.

If the trust of the test certificate in the operating system is needed for the app itself, ask the user
to install it; never add a bypass.

## 11. Definition of done

- Everything in sections 5 to 9 implemented; `dotnet build` with zero warnings; `dotnet test` green;
  integration tests green against a local server; the manual checklist 10.3 done and its results written
  down for the user (what was checked, what failed, what was not possible).
- `CLAUDE.md` gets new verified facts for anything you measured that the next person would otherwise
  rediscover (for example MSAL on macOS, single instance behaviour across the restart, SQLite re-key).
- Docs and changelog updated in the same commits as the behaviour.
- No secret in logs, exports, database or git; no TLS bypass; no workaround; no TODO.
- A short report to the user: what was built, decisions you had to take beyond this brief and why, open
  points.

## 12. Order of work

Each step ends with a green build and tests and at least one commit.

1. Check out `feature/server-mode`. Read `CLAUDE.md`, the contract, and the files in section 2.
2. Storage resolution and mode setting (3.2, 3.7), first run detection (3.4), restart with guard hand
   over (3.3). App still behaves exactly as before in `Local` mode.
3. Core server client: contracts, HTTP pipeline, headers, problem mapping, sessions, refresh lock, token
   storage, wipe event (5.1 to 5.3, 5.5) with unit tests.
4. Schema migration (6.1), pull engine (6.3) with tests.
5. Outbox, recording, push, re-key (6.2, 6.6) with tests.
6. Sign in UI and first run dialog (5.4, 7.1), Entra via MSAL.
7. Storage settings tab, mode switch flow, footer segment, banners, tray and macOS menu (7.2 to 7.4).
8. Vault sharing (6.5), import upload and package path, editor roles, delete (6.7), CLI behaviour.
9. Logging pass (8), diagnostics bundle, wording (7.5), docs (9).
10. Integration tests (10.2), manual end to end (10.3), verified facts, report to the user.
