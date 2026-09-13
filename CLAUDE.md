# OpenVpnPilot

A desktop client for OpenVPN that is built for managing many profiles: search, folders, favourites,
bulk import, global hotkeys, live telemetry and session history. The application drives the `openvpn`
process through a privileged component and the management interface. It does not reimplement OpenVPN.

The target platforms are Windows and macOS. On Windows the privileged component is OpenVPN's own
interactive service; on macOS it is this project's helper, because macOS has no equivalent.

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

- `Core`, `OpenVpn`, `Data` and `App` stay platform neutral: no P/Invoke, no `Microsoft.Win32`, no
  path or separator assumptions outside `Platform.Windows` and `Platform.MacOS`.
- Every platform dependent capability gets an interface in `Core`. A platform is a project that
  implements those interfaces, never a change to the shared code.
- `Platform.MacOS.Helper` is the only part that runs as root, and `Platform.MacOS.Protocol` is the
  only thing the two sides share. The helper depends on nothing else of this repository, so what runs
  with privileges stays small enough to read in one sitting.

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

- A short README that says what this is and points at the rest, and one page per subject under
  `docs/`: `windows.md`, `macos.md`, `usage.md`, `cli.md`, `development.md`. A subject gets a page
  when it is a subject, not because there is more to say about one that already has one.
- Document behaviour there and in code, not in a growing pile of design notes. `docs/` is not a
  place for design documents, meeting notes or anything dated.

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
- **The far end of the tunnel is not always in the push reply, and the second value of `ifconfig`
  is only sometimes the peer.** Under `topology net30` the reply carries `ifconfig <local> <peer>`
  and the peer is the address a round trip should measure. Under `topology subnet` the same option
  carries `ifconfig <local> <netmask>`, and a server using it pushes `route-gateway` separately.
  Reading the second value unconditionally therefore yields a netmask as the gateway.

  A server may push neither. What must never be measured in that case is this machine's own tunnel
  address: the local stack answers it without a packet leaving the host, so it reports one or two
  milliseconds for any tunnel anywhere and reads as an unusually good connection. The order is the
  pushed gateway, then the gateway of the interface holding the tunnel address, then the server's
  public address measured over the ordinary route, and the interface says which of them it used.
- One time codes take two forms and both are implemented. A static challenge arrives appended to the
  password request as `SC:<echo>,<text>` and is answered in the same attempt with a password of
  `SCRV1:base64(password):base64(response)`. A dynamic challenge arrives as the reason for a
  verification failure, `CRV1:<flags>:<state>:<base64 user>:<text>`, and is answered in the next
  attempt with a password of `CRV1::<state>::<response>`. A dynamic challenge is a request for a
  code, not a wrong password, so it must not be reported to the user as a rejected credential.

---

## Verified macOS integration facts

Measured on macOS 26 on arm64 with .NET 10. These are test results, not assumptions. Do not
re-derive them, and correct this section if a measurement ever contradicts it.

### Why there is a helper, and why it is built this way

macOS has nothing like OpenVPN's interactive service. Only root can open a tun device, install
routes and change the name servers, so `openvpn` runs as root and something privileged has to start
it. Two requirements decided the shape of that something: a person must be able to use this like any
other application, and nothing may be left permanently changed when a tunnel ends badly.

- **A launchd daemon with socket activation, installed once by a package.** The application talks to
  it over a Unix socket in `/var/run`. launchd starts it on the first connection and it exits when it
  has been idle, so nothing of it runs while the application is closed. The alternative of asking for
  the password at every connection was rejected because that is not how a normal program behaves, and
  a setuid binary was rejected because it would inherit the whole environment of whoever ran it.
- **`SMAppService` cannot be used.** Measured: registering a daemon from inside the bundle is refused
  without a Developer ID signature, and this is distributed without one. The package therefore
  installs the job definition, and the helper package is the one part a person installs with a
  password.
- **The caller sends values, never options.** The protocol carries a configuration, a management port,
  a password and pull filters; the helper builds the command line itself. Nothing a caller sends can
  become an option, so no caller can turn a launch into `--up /tmp/mine`.
- **The configuration is part of the attack surface and is rewritten, not forwarded.** It is parsed
  with a port of OpenVPN's own `parse_line`, checked against a list of what may appear, and written
  out again canonically. Anything that runs a program, reads a file the caller chose, or changes what
  the root process is, is refused with the reason. `--script-security 1` is placed after `--config`,
  so the configuration cannot raise it.
- **`setenv` is refused except for `UV_*` and `FORWARD_COMPATIBLE`.** The name server script runs as
  root, and `setenv` would put `PATH`, `BASH_ENV` or `dns_vars_file` into its environment.
- **The configuration travels as content, not as a path.** The helper writes its own copy into a
  root owned directory with mode 0700 and the file 0600, so between the check and the launch there is
  nothing left for anyone to swap.
- **A tunnel belongs to the session that started it.** When the connection ends, however it ends, its
  tunnels are ended too: an application that is gone can no longer answer credential prompts or the
  stop signal, and nobody else knows the management password.
- **The name server state is written down before it is changed.** What a tunnel changed is restored
  when it ends, and leftovers from a tunnel that was killed are restored when the helper starts,
  which is checked against the boot time so a stale record cannot undo a newer setting. This is the
  requirement that nothing stays broken, made explicit.
- **Who may start what mirrors the Windows rule.** Members of the administrators group, or of a group
  the package creates when the console user is not an administrator, may start their own
  configuration; everyone else may start only what an administrator installed.

### Variadic C functions cannot be reached through P/Invoke here

Apple's arm64 ABI passes the variadic arguments of a C function on the stack, while a declaration
with a fixed parameter list passes them in registers, so the callee reads something that was never
written. Measured with `fcntl(fd, F_DUPFD_CLOEXEC, 10)`: the same call succeeds in C and returns the
descriptor, and fails through `LibraryImport` with three `int` parameters. `fcntl(fd, F_GETFD)`, which
needs no variadic argument, succeeds, and so does the non variadic `dup`.

Nothing in this repository may declare a variadic libc function. `SpawnedProcess` therefore moves its
descriptors with `dup`, taking the lowest free number until one is high enough.

### What the child of a spawn inherits

`POSIX_SPAWN_CLOEXEC_DEFAULT` does what it says, so close on exec on the parent's own descriptors is
not needed. Measured by asking the child which descriptors it has, with a shell loop that opens
nothing: a spawned process sees exactly standard input, standard output, standard error and the
descriptor the management password arrives on, both for a single launch and for four at once.

That is why the password can be handed over on an inherited pipe. On Unix, OpenVPN reads a password
from standard input only when standard input is a terminal, so the pipe is named to it as
`/dev/fd/3` instead. It never touches a disk.

### A process can be gone while its last words are still in the pipe

Reading what a process said as soon as it has exited reads nothing at all: the thread draining the
pipe has not necessarily run yet. Measured through the helper's version probe, which reported OpenVPN
as missing although it had printed its version and exited cleanly. `SpawnedProcess` therefore
completes an `OutputDrained` task when the pipe ends, and anything that explains an exit by what was
said waits for that rather than for the exit.

### Serialisation compiled ahead of time does not run property initialisers

The helper is compiled ahead of time and therefore serialises through generated code. Measured: a
member a sender leaves out arrives as null or zero, whatever default the record declares, while the
reflection based serialiser keeps the declared default. A `LaunchSpecification` with no `verbosity`
arrives with zero and not with three, and one with no `pullFilters` arrives with null and not with an
empty list.

Nothing that comes off the socket may be assumed to be present, including the type of the message
itself. This is not a detail of style: the first version of the helper dereferenced a list it had
declared as empty, and the request died in an exception that ended the session without an answer.

### Every request is answered

A caller that hears nothing waits for a tunnel that was never started, and a service that stops
talking cannot be diagnosed from outside. The session loop therefore answers a request whose handling
threw, with a refusal that says the helper failed, and writes the exception to the helper's log.

### The keychain asks again after every build, and once per item

Credentials live in the login keychain, whose access control list hangs on the individual item and
names the asking program by its code signature. This build is signed ad-hoc, so its hash changes
every time it is built, and every build is therefore a program the keychain has never seen. Allowing
one covers one item, so with an item per profile it was one dialog per profile per build.

Measured with a probe that turns the dialog off, so a prompt shows up as a status instead of
blocking: an item written by one build and read by the next answers -25293, errSecAuthFailed. Three
ways out were tried and none of them works.

| Attempt | Result |
| --- | --- |
| Ad-hoc, as built today | the next build is refused |
| A stable self-signed certificate | the next build is refused |
| An access control list naming every application | the next build is refused |
| The data protection keychain | -34018 without an entitlement, and with one the process is killed at launch |

The certificate is the interesting failure, because it half works. The access control list becomes
`identifier "..." and certificate leaf = H"..."`, which matches every build signed with it. What does
not move is the partition, which stays `cdhash:<the build that wrote the item>`: a partition reads
`teamid:<id>` only for a certificate Apple issued, and a self-signed one has no team. The partition
alone is enough to refuse.

Nothing in this repository can therefore stop the dialog. What it can decide is how often it appears,
which is why every sign in lives in one item rather than one per profile: once per build instead of
once per profile per build. An Apple Developer ID would fix it properly and there is none.

### A notification that is refused says so

The notification centre answers `requestAuthorizationWithOptions:` with a granted flag and an
`NSError`, and answers `addNotificationRequest:` with another. Discarding both is what made a silent
application indistinguishable from a working one: nothing appeared, nothing was written, and there
was nowhere to look. Both completion blocks are therefore real and write what they were told, once.

Measured: `UNErrorDomain` code 1, `Notifications are not allowed for this application`, is the switch
for this application standing off under System Settings, Notifications. The application is listed
there once it has asked for permission once, and turning the switch on is the whole of the remedy.
The refusal says nothing about the bundle, the signature or the identifier, and it is not a state of
the Mac: reading it as one cost an afternoon.

`~/Library/Preferences/com.apple.ncprefs.plist` is not where that switch is kept on macOS 26. It does
not exist even once notifications are working, so its absence means nothing and it is not worth
reading.

### A Unix socket path is short

`sockaddr_un` holds 104 characters on macOS. The per user temporary directory alone is longer than
that with a name after it, so anything that binds a socket under a temporary directory has to keep
the path short. The helper's own socket lives at `/var/run/org.openvpnpilot.helper.sock`.

### How the disk image window is arranged

What a disk image window looks like is not data anyone can write into the image. The Finder keeps it
in a `.DS_Store` that only the Finder writes, so `installer/build-macos.sh` builds a writable image,
mounts it, tells the Finder what the window should be, and only then compresses it. Measured on
macOS 26, and each of these cost a build to find:

- **An AppleEvent gets two minutes by default, and the Finder does not always answer inside it.**
  What expires is one command, not the script, so the window ends up half arranged: sized, with no
  background and the icons where they fell. Every command is therefore inside `with timeout of 600
  seconds`, and every `delay` is outside the block that talks to the Finder, because `delay` inside
  one is a command the Finder is asked to carry out and counts against the same timeout.
- **The bounds the Finder is given include the title bar.** A window asked for 400 shows 372 of the
  background and cuts the rest off the bottom, so the title bar is added to what is asked for.
- **The Finder deletes `.VolumeIcon.icns` and clears the custom icon attribute when it opens the
  volume**, every time. The volume icon is therefore set after the window has been arranged and
  closed, not before, and the build checks that both the file and the attribute are still there
  before it compresses. `hdiutil` also does something of its own with that file when it is in the
  folder an image is created from, and it was not in the result, so it is copied onto the mounted
  volume instead.
- **The mount point is read back from `hdiutil attach -plist` rather than assumed.** A volume of the
  same name already mounted pushes the new one aside to a name with a number after it, and the rest
  of the build then arranges, decorates and checks the wrong disk without saying so.

The artwork both halves of this need is drawn by `tools/artwork`, which is run by hand and whose
output is committed. Everything visual comes out of `assets/artwork`, and nothing else in the
repository holds a copy of it. It draws with Skia and is a .NET program rather than a Swift one, so
that the Windows icon can be rebuilt from Windows; it writes the `icns` and the `ico` containers
itself, because `iconutil` is macOS only and nothing draws an `ico` at all.

---

## Windows notification identity

A notification area balloon is not shown as a balloon on Windows 10 and later. The shell converts it
into a toast, files it in the notification centre and labels it with the calling process's
application user model identity. A process that never declares one is given a generated identity,
which is what put `Microsoft.Explorer.Notification{<guid>}` above every message this client sent.

`WindowsAppIdentity.Apply` therefore does two things before the framework starts: it calls
`SetCurrentProcessExplicitAppUserModelID`, and it writes `DisplayName` and `IconUri` under
`HKCU\Software\Classes\AppUserModelId\OpenVpnPilot` so the shell can resolve that identity to a
name and an icon. The installer stamps the same identity onto the start menu shortcut through
`System.AppUserModel.ID`. All three must say `OpenVpnPilot` and the identifier must not change once
released, because notification settings the user makes are stored against it.

The registration is confirmed present after a run, and the label has now been observed end to end: the
notification centre does show `DisplayName` above the message. The small icon beside it is a
different matter.

**That small icon is not read from `IconUri` on every run; Windows resolves it once per AUMID and
keeps what it first resolved.** Measured on a machine that had run this application, under this
identifier, since before the current artwork existed: the group header kept showing an icon from
months earlier, regardless of how many times `IconUri` was rewritten afterwards or how many times the
process restarted. A brand new identifier that had never appeared on the machine before, with the same
registration code, came up showing the raw executable name and a generic placeholder instead of
`DisplayName` and `IconUri` at all, on its first run and its second, which is what a shortcut only
installed application supplies and a loose executable does not have.

The cache is `%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db`, and it is not
process-local: stopping `WpnUserService_<hash>`, deleting `wpndatabase.db` together with its
`-wal`/`-shm` files, and starting the service again is what made the stale icon disappear and the
current one appear, confirmed end to end on this machine. It is a whole-account cache, not one this
application can reach into or reset for the user, and every other application's notification history
on the account is erased along with it, so this is a one-off unstick for a development machine, not
something the product does or should do. A fresh installation on a machine that has never seen
`OpenVpnPilot` before starts with no entry to be stale, and is not expected to show this.

The large image a notification carries is a different mechanism, `dwInfoFlags` on the balloon itself,
and behaves exactly as documented: `NIIF_INFO`/`WARNING`/`ERROR` draw one of Windows' own stock icons
regardless of the tray's own; `NIIF_USER` draws the tray's `hIcon` instead, at whatever pixel size that
icon carries, stretched to the size the toast wants and showing every pixel of the stretch. Neither
reads well next to a brand mark this simple, so `WindowsTrayIcon.ShowAsync` sends `NIIF_NONE` and shows
no large image at all.

---

## Verified shutdown behaviour

Measured against Avalonia 12.1 on Windows 11, by delivering `WM_QUERYENDSESSION` and `WM_ENDSESSION`
to the running process. These are test results, not assumptions.

- **The two ways out are not the same event.** Ending the Windows session raises
  `ShutdownRequested` and then closes every window. `desktop.Shutdown()`, which the notification
  area, the window and the companion command all call, raises no such request at all. `Exit` is
  raised by both, once, after the last window has closed, and is therefore where the teardown
  belongs. Attaching it to `ShutdownRequested` means it never runs on the ordinary quit, and means
  disposing the container while the windows that read from it are still to be closed.
- **A window that refuses to close vetoes the shutdown.** Cancelling a close whose
  `WindowCloseReason` is `OSShutdown` answers `WM_QUERYENDSESSION` with zero, and Windows reports
  the application as the reason the machine will not shut down. Only `WindowClosing` is a person
  expressing a preference; the other reasons must be allowed to proceed.
- **Shutting down from inside a `Closing` handler recurses.** The shutdown closes the same window,
  which enters the handler again, until the stack runs out. Post the request instead.
- **An exception in these handlers is an exception inside `WndProc`.** Nothing catches it, the
  process dies with `0xE0434352`, and on the shutdown screen the Windows fault dialog is the only
  trace left. Every step of the teardown is therefore bounded and reported, and unhandled
  exceptions are written to `logs\failure.log`.
