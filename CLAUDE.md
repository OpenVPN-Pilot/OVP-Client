# OpenVpnPilot

A desktop client for OpenVPN that is built for managing many profiles: search, folders, favourites,
bulk import, global hotkeys, live telemetry and session history. The application drives the `openvpn`
process through the interactive service and the management interface. It does not reimplement OpenVPN.

Target platform for the current version is Windows. macOS is prepared for but not implemented.

---

## Architecture rules

- Enterprise layering: dependency injection everywhere, middleware for request flows, managers for
  domain logic, helpers for utilities.
- No static mutable state. Everything behind an interface. Constructor injection only.
- `Nullable` enabled, warnings treated as errors.
- `async`/`await` throughout, every asynchronous method takes a `CancellationToken`.
- No `async void` except in event handlers.
- Every long running operation is cancellable and never blocks the UI thread.

## Language and style

- Everything in this repository is English: identifiers, comments, commit messages, README, and the
  default UI language.
- Comments explain why, not what. One style only: `// Validates the VPN configuration before launch.`
- No emoji anywhere in code or comments. No ASCII art. No banner or divider comments.
- Every user facing string must be localizable. Never hardcode display text in a view or view model.

## Platform rules

- The target platform is Windows, but `Core`, `OpenVpn`, `Data` and `App` stay platform neutral:
  no P/Invoke, no `Microsoft.Win32`, no path or separator assumptions outside `Platform.Windows`.
- Every platform dependent capability gets an interface in `Core`. Adding macOS later must mean adding
  a project that implements those interfaces, never restructuring existing code.

## Neutrality

This repository is public. Keep it free of context about who uses it or why it was written.

- No usage scenarios, origin stories, organisation names, machine names, user names or internal hosts
  in code, comments, commit messages, README, tests or sample data.
- One exception, and only one: the author is named in `LICENSE`, in the assembly metadata and as the
  installer's publisher. That is the name the work is published under and nothing else about the
  person belongs anywhere in the repository.
- Examples use invented names such as `example-site` and `vpn.example.com`, and addresses from the
  documentation ranges (`203.0.113.0/24`, `198.51.100.0/24`).

## Git

- Short, imperative, English commit subjects prefixed with a gitmoji code.
- Examples: `:sparkles: add profile import wizard`, `:bug: fix bytecount parser overflow`,
  `:recycle: extract connect pipeline`, `:white_check_mark: cover static challenge parsing`.
- Never commit profiles, keys, certificates or databases. `.gitignore` enforces this; do not override it.

## Documentation

- Exactly one good English README. No `docs/` directory full of markdown files.
- Document behaviour in the README and in code, not in a growing pile of design notes.

## Working agreement

- **No workarounds.** When something is blocked or behaves unexpectedly, stop and report it with the
  evidence and the options. Do not route around it.
- No silent `catch`. No `#pragma warning disable` without a comment stating why.
- A TODO is not a solution.
- Secrets never appear in the database, logs, exports or git. Credentials reach OpenVPN only through
  the management interface, never through an `auth-user-pass` file on disk.

---

## Verified OpenVPN integration facts

Measured against OpenVPN Community 2.7.6 on Windows 11. These are test results, not assumptions.
Do not re-derive them, and correct this section if a measurement ever contradicts it.

### Launching through the interactive service

The startup message written to `\\.\pipe\openvpn\service` is three UTF-16 strings, each NUL terminated,
sent in a single write:

```
workingdir \0 openvpnoptions \0 stdin \0
```

The reply is UTF-16, LF separated: `0x00000000` (status), `0x` + eight hex digits (process id),
`Process ID`. A non zero first field is a Windows error code and the remaining fields carry the message.

The management password is passed in the `stdin` field with a trailing LF, paired with
`--management <host> <port> stdin` on the command line. It never touches disk.

**The startup pipe is not the channel that outlives it.** Closing it as soon as the reply is read
does not end the tunnel: the service appends `--msg-channel <n>` to the options it passes, and that
is the channel the OpenVPN process keeps open to the service for the rest of its life. Measured by
reading the command line of a running process, which carries the option, and by the tunnels that
survive the client disconnecting. Ending a process is therefore the client's own job, and it works:
a process the service created was terminated from an ordinary, UAC filtered administrator token
without being refused.

Working option set:

```
--config <file> --management 127.0.0.1 <port> stdin --management-query-passwords
--management-hold --management-forget-disconnect --auth-retry interact --verb 3
```

### Authorisation model

The service authorises a caller that is a member of the local Administrators group or of the group named
by `HKLM\SOFTWARE\OpenVPN\ovpn_admin_group` (default `OpenVPN Administrators`, which the installer does
not create). Group resolution must use the well known SID `S-1-5-32-544` for Administrators, because the
group name is localized.

- **Authorised caller**: any config path, any options. Verified: a UAC filtered, non elevated token of an
  administrator is accepted, and options outside the whitelist such as `--up`, `--route` and `--cd` are
  accepted too.

  Determining this in code needs care. With UAC enabled, the process token of an administrator does
  **not** contain `S-1-5-32-544` at all, not even as a deny only entry, so
  `WindowsPrincipal.IsInRole` returns false for a user the service authorises. The unfiltered
  membership lives on the linked token, reached through `GetTokenInformation` with
  `TokenLinkedToken`. `WindowsAuthorisation.IsEffectivelyInGroup` implements this; use it rather than
  `IsInRole` for anything that has to predict the service's decision. Group names are localized, so
  always resolve the Administrators group by its well known SID.
- **Unauthorised caller**: the config must sit under `config_dir` and only whitelisted options are
  permitted (`auth-retry`, `config`, `log`, `log-append`, `management`, `management-forget-disconnect`,
  `management-hold`, `management-query-passwords`, `management-query-proxy`, `management-signal`,
  `management-up-down`, `mute`, `setenv`, `service`, `verb`, `pull-filter`, `script-security`).
  This branch is read from the OpenVPN sources and has not been measured, because the available account
  is privileged. Treat it as the constraint to design against, and verify it before relying on it.

### Where a materialised configuration may live

The service refuses to start a process whose working directory sits directly under the user's
`AppData`, failing with `CreateProcessAsUser` and `ERROR_DIRECTORY`. Measured on this machine:

| Working directory | Result |
| --- | --- |
| `%LOCALAPPDATA%\<anything>` | refused |
| `%APPDATA%\<anything>` | refused |
| `%LOCALAPPDATA%\Temp\<anything>` | works |
| `C:\Users\<user>\<anything>` | works |
| `%ProgramData%\<anything>` | works |
| any other drive | works |

The service source contains no explicit rejection of those paths, and the access control lists on the
working and passing directories are equivalent, so the cause is the process creation context: the
service impersonates the caller and creates the process **without** calling `LoadUserProfile`.

Runtime configurations therefore live under `%ProgramData%\OpenVpnPilot\runtime\<user SID>\`. That
location works, survives temporary file cleanup, and can be given a per user access control list.
A materialised configuration carries the private key inline, so both the directory and the file
disable inheritance and grant only the owning user and the local system account.

### Management interface protocol

The interface reports itself as version 6. Sequencing that works:

1. Connect, then wait for the `ENTER PASSWORD:` prompt, which arrives **without** a trailing newline.
2. Send the management password, wait for `SUCCESS: password is correct`.
3. Send `version 6`, `state on`, `bytecount 1`, `log on`.
4. Wait for `>HOLD:` before sending `hold release`. Releasing earlier is silently ignored.
5. Tear down with `signal SIGTERM`, which exits the process cleanly and leaves nothing behind.

**Commands must be issued one at a time.** Pipelining several commands drops all but the first few
without any error. Keep a request queue and send the next command only after the current one produced a
terminal response (`SUCCESS:`, `ERROR:` or `END`).

`log on all` replays the log history and therefore produces two terminators, a `SUCCESS:` line and a
later `END`. Counting both as terminal desynchronises the queue. Use `log on` for realtime only, or track
the expected terminator per command.

OpenVPN's own log echoes received commands unreliably and may repeat a previous command's text. Trust
the responses on the socket, not the `MANAGEMENT: CMD` lines in the log file.

**Only the read loop completes a command.** A command registered after that loop has ended waits for
an answer nobody is left to send, and the wait has no timeout of its own. That is what made a
disconnect hang: the process was already gone, the stop signal was never answered, the transition
lock was never released and the profile reported that it was disconnecting for good. `ManagementClient`
records the end of the read loop and refuses a command issued afterwards, and `ConnectionSupervisor`
bounds the signal and tears down in a `finally` either way.

**A connection that ends belongs to nobody.** `ConnectionManager` retires an entry as soon as the
supervisor reports a state that means the tunnel is over and nobody asked for it. Holding it made the
profile look busy, made connecting again fail as a duplicate, and made the automatic reconnect decide
there was nothing to reconnect.

Observed notification formats:

```
>STATE:<time>,<name>,<description>,<localip>,<remoteip>,<port>,<localport>,<ipv6>
>BYTECOUNT:<in>,<out>
>LOG:<time>,<flags>,<message>
>HOLD:Waiting for hold release:<timeout>
>INFO:<text>
>PASSWORD:Need '<realm>' username/password
```

State sequence of a successful connection:
`WAIT`, `AUTH`, `GET_CONFIG`, `ASSIGN_IP`, `ADD_ROUTES`, `CONNECTED`. The `CONNECTED` line carries the
assigned local address, the server address and the port, which is what the dashboard displays.

### Verifying against servers

`lab/` is a Docker Compose project with ten OpenVPN servers, each with a site behind it, covering
what a single server cannot: certificate only, a private key with a passphrase, user name and
password with and without a client certificate, the same over TCP, both kinds of one time code, a
server pushing name servers and routes, one asking to carry all traffic, and one pushing compression.
Every server has its own tunnel network, so ten tunnels can be up at once.

Two facts came out of building it and are worth not re-deriving:

- **OpenVPN builds a fresh environment for a script.** An `auth-user-pass-verify` script does not
  inherit the server process's environment, so anything it needs has to be put there with `setenv` in
  the configuration. Without that every server in the lab behaved like the plainest one, quietly.
- **A script can raise a dynamic challenge.** Writing `CRV1:<flags>:<state>:<base64 user>:<text>` to
  the file named by `auth_failed_reason_file` and exiting non zero sends it to the client as the
  reason for the refusal. Confirmed end to end against lab server seven: the client answers in the
  next attempt with a password of `CRV1::<state>::<response>` and connects. This is the only way to
  exercise that path without a real server that issues codes.

### Storing timestamps

SQLite refuses to order or compare a value whose CLR type is `DateTimeOffset`, and reports it as a
query that cannot be translated rather than as a runtime failure. A history sorted by time or
filtered by period is therefore impossible while the default text storage is used. Every timestamp in
the model is stored as ticks through a value converter applied in `PilotDbContext`, which sorts as an
integer and indexes well. Adding a new timestamp needs nothing: the converter is applied by walking
the model.

### Client side gotchas

- With data channel offload active, a 2.7 client rejects any pushed compression setting, including
  `comp-lzo no`, and fails with `Failed to apply push options`.

  Measured against lab server ten: the client reports `RECONNECTING` with the reason
  `process-push-msg-failed` and loops there, restarting as fast as it can. Neither the state nor the
  reason names compression, so `PushReplyParser` recognises `comp-lzo` and `compress` in the push
  reply, the supervisor ends the attempt rather than letting it restart forever, and the interface
  says which option it was.

  **`pull-filter ignore` is not a way around this one.** It is whitelisted and it does suppress the
  option, and the result is worse than the refusal it replaces: measured against the same server, the
  tunnel comes up, the route is installed, and nothing passes. The server logs
  `Bad LZO decompression header byte` for every packet, because it is still compressing what the
  client has been told to stop expecting. Only the server can resolve it, by not pushing compression.
  The filters are therefore not offered and not applied automatically.
- A configuration without `ca`, `capath` or `peer-fingerprint` fails during option parsing, before the
  management interface starts listening. Validate this at import time so the failure is explained rather
  than observed as a process that dies immediately.
- The pushed options are not available through any management command. The only place they appear is
  the log stream, as `PUSH: Received control message: 'PUSH_REPLY,...'`, which is what
  `PushReplyParser` reads.
- The status that reports the end of a connection carries no byte counters, because nothing is
  flowing any more. Anything that records what a session transferred has to keep the last values it
  saw while the tunnel was up.
- One time codes take two forms and both are implemented. A static challenge arrives appended to the
  password request as `SC:<echo>,<text>` and is answered in the same attempt with a password of
  `SCRV1:base64(password):base64(response)`. A dynamic challenge arrives as the reason for a
  verification failure, `CRV1:<flags>:<state>:<base64 user>:<text>`, and is answered in the next
  attempt with a password of `CRV1::<state>::<response>`. A dynamic challenge is a request for a
  code, not a wrong password, so it must not be reported to the user as a rejected credential.
