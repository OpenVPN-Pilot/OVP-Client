# Changelog

All notable changes to this project are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Development happens on `dev`. `master` carries releases, and every entry under Unreleased moves into a
version heading when one is tagged. A release tag is `v<version>`, for example `v1.2.0`.

## [Unreleased]

### Fixed

- The solution builds with the .NET SDK 10.0.400, whose analysers refuse a log call that formats its
  arguments before knowing whether the line will be written. The source generated log methods now
  receive the values themselves and format them only when the line is written. The lines read as
  before, except that an update check which found no release reports its latest version as `(null)`
  rather than `-`.

## [1.3.0] - 2026-09-01

### Added

- A configuration opened from the shell is imported. The application registers itself as a handler
  for `.ovpn` and appears in the Open with menu; the default handler is deliberately not taken,
  because the OpenVPN GUI is usually it and Windows would ask the user in any case. Its own
  `.ovppkg` package format is claimed outright. A file arrives as a bare argument, which the option
  parser used to refuse: opening a configuration started the application and immediately ended it,
  reporting an unknown option nobody had typed.
- An optional check for a newer release, on by default and switchable under Settings, Advanced,
  with the repository it asks about beside it. It reads one GitHub release list without credentials,
  reports what it found, and downloads nothing. A newer release appears as a notice with a link to
  it. The checker and its tests already existed and had never been wired to anything.
- A settings file records which layout it was written by, so that the default of an existing setting
  can change without overruling a value somebody chose. The first step adopts the update check
  defaults, which is legitimate exactly once: until now neither setting had a switch, a field or a
  caller, so no stored value can have been an answer anybody gave.

### Changed

- The application is called OpenVPN Pilot. The compact form stays wherever a name has to be one
  word and wherever changing it would strand something: the executable, the installation directory,
  the data directory, the autostart entry and the application identity that labels the
  notifications are all still `OpenVpnPilot`.
- Release tags are `v<version>` rather than `version-<version>`. The update check reads both, so the
  tags published up to 1.2.0 are still understood.

## [1.2.0] - 2026-09-01

### Added

- A log window, opened from the header beside the history. It carries both streams in the order they
  happened: what the application recorded and what OpenVPN said, filterable by source, by level and
  by text, following the newest line unless told not to. OpenVPN's log stream was already being read
  for the push reply and thrown away afterwards, which is why a connection that failed used to leave
  nothing behind to look at.
- One log file per day, named `yyyy-MM-dd.log`, carrying both streams. How many days are kept is a
  setting, seven by default: a log names profiles, hosts and paths that include the account name, so
  keeping them for ever is not a decision to make on the user's behalf.
- The environment is checked at startup and again before every connection, and what is missing is
  said in a banner with a link to the OpenVPN download. The check existed and backed `ovp doctor`;
  nothing in the window had ever asked it.
- The connection history can be cleared from the history window, either the sessions the current
  filter selects or the whole thing, after a confirmation.
- The quick menus remember which display they were left on and move between displays with alt and an
  arrow key. The main window remembers where it was, and refuses to restore onto a monitor that is no
  longer there.
- The round trip says which address it was measured against, and whether that address is the far end
  of the tunnel or the server itself.

### Fixed

- The round trip was measured against this machine's own tunnel address whenever the server pushed no
  gateway. The local stack answers that without a packet leaving, so every such tunnel reported one
  or two milliseconds and looked excellent while measuring nothing. The address is now the pushed
  gateway, or the gateway of the tunnel interface, or the server itself, and never this machine.
- A server using `topology subnet` had its netmask read as the tunnel gateway, because the second
  value of `ifconfig` is the peer only under `topology net30`.
- A missing dependency ended the process. Anything escaping an asynchronous command is rethrown on
  the user interface thread, and a machine without the interactive service produced exactly that from
  the pipe. Connecting now reports what went wrong and keeps the other tunnels running.
- Windows labelled every notification with a generated identifier such as
  `Microsoft.Explorer.Notification{...}`, because the process declared no application identity. It
  now declares one, registers a name and icon for it, and the installer stamps the same identity on
  the start menu shortcut.
- Long text ran past the edge of the detail panel instead of wrapping. A scroll viewer that allows
  horizontal scrolling measures its content without a width limit, and text measured without one
  neither wraps nor trims; the same mistake stopped the profile list from ever showing an ellipsis,
  because a stack panel does the same thing to what it stacks. The detail column can also be dragged
  wider now.
- The log level and the OpenVPN verbosity were stored, shown in the settings and never applied. The
  logger fixed its minimum before the settings file was read, and the verbosity was written into the
  command line as a constant.
- The application log was nine tenths Entity Framework reporting each SQL statement it ran at
  information level, which buried everything worth reading. It is raised to warning.

## [1.1.0] - 2026-08-30

### Added

- A profile dragged onto a tag in the sidebar is given that tag. Dragging a row that is ticked takes
  everything ticked with it, so a set can be tagged in one gesture. The tag is added rather than
  replacing the ones the profile already has.
- `LICENSE` at the root, MIT, naming the author.
- Tests for the main view model: what the list shows, how the two sidebar lists hand one selection
  between them, what is ticked, what an action applies to when nothing is, and what dragging onto a
  tag does. None of it was covered, and the filter handover is the kind that comes back.
- A test that every option the command line carries is one the interactive service accepts from a
  caller it has not authorised. That branch still needs a standard user account to verify; this
  stops it being broken by an option an administrator would never notice.

- The quick switcher stops tunnels as well. One shortcut lists only what is running, space ticks
  several and return disconnects them, which is the same act as starting one done to a shorter list.
- The README says what reaches OpenVPN: the fixed options every connection carries, the ones added
  only when they were asked for, and how to watch a server take over the routing on purpose.

### Changed

- The author and copyright are recorded in the built executable, the package and the installer.
- The README has a table of contents and reads in three parts: using it, how it works, working on it.
  It now says that issues and merge requests are welcome, what makes one easy to accept, and that
  every line here was written by a language model in conversation with an author who reads C#.
- The GitHub Actions workflow is gone. Every release is built by hand, which takes one command, and a
  workflow that has never run is a claim rather than a check.
- Code signing and a kill switch are off the roadmap rather than pending. A certificate costs money
  every year to say what the source already says, and a kill switch belongs to a client used as an
  exit node, which this is not.

### Fixed

- A shortcut added in a later version never reached an installation that already existed. The
  default bindings were applied once and a flag remembered it, so the machines using the application
  longest were the ones a new shortcut was never bound on. They are applied every start now, which
  adds only what has no binding at all and leaves everything else alone.
- Shutting Windows down while the application was open ended it with a fault dialog on the
  shutdown screen, reporting an unknown software error at 0xE0434352. Ending the session raises
  a shutdown request and then closes every window, so the teardown that answered the request had
  already disposed the container the window's own handler was about to ask for a setting. The
  windows are closed first now and the teardown runs after them.
- The same handler refused that close when the window was set to close to the notification area,
  which answers Windows with a veto and reports the application as the reason the machine will
  not shut down. Only a person closing the window is a preference now; a close that comes from
  the session ending or the application shutting down proceeds.
- Closing the main window with close to the notification area turned off ended the process with a
  stack overflow. The handler shut the application down, which closed the same window, which
  entered the handler again. The request is posted now rather than made from inside the close.
- Quitting from the notification area, from the window, or with `--quit` left the tunnels running
  and the sessions open in the history. Only ending the Windows session raised the shutdown request
  the teardown was attached to, and that was the path that crashed. Measured against a lab server:
  the tunnel outlived the application every time it was asked to quit. The teardown now runs on the
  event both paths raise, and every step of it is bounded and reported, so a step that hangs or
  fails no longer costs the ones after it.
- An exception that reaches nobody is written to `logs\failure.log` instead of only appearing as
  a Windows error dialog that names an address and nothing else.

## [1.0.0] - 2026-08-29

The first version worth installing. An installer, a command line other software can drive, and ten
servers to prove it against.

### Added

- Windows installer. An MSI puts the application under Program Files, adds a Start menu entry,
  removes itself from the usual place, and puts the installation directory on the machine PATH so
  that `ovp` works in any terminal without anyone editing an environment variable.
- `ovp` is the front door. `ovp connect` starts the application when none is running and hands the
  request to it, so anything driving the client calls one command and never has to know whether a
  window happened to be open. `ovp start [--headless]` and `ovp stop` open and end it.
- Command line options on the application itself, for a shortcut or a scheduled task that starts it:
  `--headless` for no window and no notification area entry, `--background`, `--connect`,
  `--disconnect`, `--disconnect-all` and `--quit`. A second launch hands its options to the copy
  that already runs and exits.
- A package can carry the saved sign ins. Handing over fifty profiles is most of the work left
  undone if the recipient still has to be told fifty passwords, so the export screen offers it and
  `ovp pack --with-credentials` does the same. It is only possible for a package, because only a
  package is encrypted, and the screen says plainly what it means.
- Packages can be imported from the interface. Dropping or picking a `.ovppkg` file turns the import
  screen into one that asks for the passphrase. Until now a package could only be opened from a
  terminal.
- Configurations dropped on the main window open the import wizard with them.
- `ovp completion powershell --install` writes the completion script and references it from the
  shell profile, rather than leaving the user to paste it somewhere themselves.
- `ovp connect --challenge <value>` answers a one time code of either kind, which is what lets the
  challenge paths be exercised without a window.
- A notice when a server pushes a compression setting. A current client refuses any of them and then
  abandons the whole set of pushed options, so the tunnel reconnects forever reporting a reason that
  names neither compression nor the server.
- A test that every localization key the sources ask for is actually translated. A missing key is
  not an exception: the localizer falls back to the key itself, so the defect ships as a
  notification reading `notify.connectingTitle`.
- Ten OpenVPN servers as a Docker Compose project, each with a site behind it. Certificates, key
  passphrases, user names and passwords, both kinds of one time code, UDP and TCP, and the two ways
  a server can take over a client's routing, all running at once.
- `scripts/dev.ps1`, which stops whatever copy is open, builds, and starts what it just built. Doing
  those by hand in the wrong order is how an old copy ends up being the one that is running.
- Checkboxes on demand. Select in the header puts one on every row, and what is ticked can be
  connected, disconnected or deleted together. Deleting asks a second time, because it is the one
  action here that cannot be undone and the one most likely to be aimed at twenty rows.
- Connect everything shown, which acts on what the current filter and search leave visible. Choosing
  a tag first is how a whole set is brought up at once.
- Control and return in the quick switcher connects and brings the window up. Return alone still
  connects and leaves you where you were, which is what a palette is for.
- The connection timeout is finally used. It was declared, defaulted to a minute and read by nothing,
  so a tunnel that could not come up reported that it was connecting until the application closed.
- A page for the lab, `lab/index.html`, listing the ten servers with their credentials and a check
  that says which of their sites answer. It loads one pixel from each site, because a page opened
  from a file is not allowed to ask any other way.
- Export screen. Profiles can be written out either as one encrypted `.ovppkg` package or as one
  `.ovpn` file each. The package always requires a passphrase; plain configurations cannot be
  protected at all, because OpenVPN has to read them, and the screen says so.
- Library entry for profiles a watched directory brought in, so an automatic import no longer drops
  them unannounced into the middle of an alphabetical list. They stay marked until they are marked
  as seen.
- Notification for a connection that is starting, which is the feedback the quick switcher and the
  shortcuts previously lacked.
- Action to forget the credentials stored for one profile, so the next connection asks again. Until
  now the only way to reach the prompt was to let a connection fail.
- Configurable interval for re-reading watched directories, for shares where the file system change
  notifications cannot be relied on.
- Favourite slot ten.
- `ovp pack` and `ovp unpack` select by tag.

### Changed

- Folders are gone. Tags and the search box already covered organising a set, and a tree the user
  has to maintain was a second way to do the same thing. Existing folders are removed by the
  migration; the profiles that were filed under them are untouched.
- Tags are called tags in the German interface too, and carry an explanation of what they do.
- A package is always encrypted. It exists to be moved between machines, which means it will sit in
  a download folder at some point, and a single file carrying every private key in the set is not
  something to leave readable.
- The import screen picks archives through the file picker rather than a button of their own, and
  offers a subfolder option for directories.
- The search box is much wider and stretches with the window.
- The mark in the title bar matches the application icon: a ring with a dot in its hole.
- The diagnostics bundle states plainly that it is not anonymous and lists what it contains.
- Importing and exporting are reached from the profiles page of the settings screen rather than from
  the header, which now carries only what is used while tunnels are running.
- Tab headers are the size of the rest of the interface. The stock size is a twenty four point
  heading, which made the settings screen read as a different product.
- The paths the application and the companion command use are defined once rather than in each.

### Fixed

- Every OpenVPN process a retried connection started was left running. Sixty three of them
  accumulated in eight minutes, which exhausted the virtual adapters and left the machine unable to
  connect anything at all. Ending the process is now the last thing a teardown does whatever else
  went wrong, and retiring a connection cannot leave one behind however it fails.
- A refusal the client can never recover from was retried like a dropped connection. The server asks
  for the same thing on the next attempt and the client refuses it again, so retrying produced
  nothing but another process, several times a minute.
- Choosing a tag left the built in filters holding a selected item they did not contain, and they
  answered by writing their own selection back. Both entries looked chosen and the list showed
  everything.
- The quick switcher answered a search for something that does not exist with the entire set. Every
  letter of "lab-11" can be found somewhere in "lab-10-cert 127.0.0.1:1210 udp", so looking for a
  subsequence across the endpoint and the tags matched almost anything.
- A tunnel the server made impossible reported it several times a second forever. A client that
  refuses the pushed options refuses them again on every attempt, as fast as it can reconnect, and
  nothing stopped the process. The attempt is now ended once, with the reason kept rather than
  replaced by the channel closing that follows it.
- The explanation for a refused compression setting was only shown while a tunnel was up, which is
  never the case for the tunnel it explains, and it advised a pull filter that does not work.
  Measured against a server that pushes one: the filter does let the tunnel come up, and nothing
  passes through it, because the server keeps compressing what the client has been told to stop
  expecting. Only the server can resolve it.
- Stopping a tunnel that could not be stopped ended the application. Anything escaping a command is
  rethrown on the user interface thread and takes the process with it; a failure to stop one tunnel
  is now reported and the others are left alone.
- The action buttons in the detail panel ran off the edge of it. They wrap now, which a translated
  label needs whatever its length.
- A connection with no credentials reported that none were available, which reads as a fault in the
  client rather than a prompt nobody answered.
- A copy asked to start with no window got one anyway. The lifetime shows whatever window it is
  handed once startup returns, so deciding afterwards not to show it was not a decision that got
  respected. This affected the autostart entry as much as `--headless`.
- The companion command reported a failure to read the profile store as an unhandled exception with
  a stack trace. It now says what happened in a sentence, names the running application as the
  likely reason, and returns a code.
- Building the installer while the application was running from the directory it publishes into
  failed with a permission error naming a single file. It now says which process holds the
  directory and what to do about it.
- The option to carry saved sign ins in a package was hidden entirely when nothing was stored, which
  is indistinguishable from the option not existing. It is shown and disabled, with a line saying
  why.
- Stopping a tunnel whose process had already gone left it reporting that it was disconnecting, for
  good. The stop signal is answered by the management channel, so a channel that had closed answered
  nothing and the wait was unbounded; every later attempt then queued behind that one. A command
  issued after the channel has closed now fails immediately, and a disconnect always ends with the
  connection reported as stopped whatever the far end does.
- A tunnel that ended by itself was still counted as running. The profile looked busy, connecting it
  again was refused as a duplicate, and the automatic reconnect decided there was nothing to
  reconnect. Connections are now held only while they are running.
- The notification for a connection that is starting showed `notify.connectingTitle`, because
  nothing translated it.
- The search box painted over the wordmark and the buttons beside it at the smallest window size. A
  column that shares out the space left over is still measured at its content's minimum width, and
  the minimum was wider than the space there was.
- The status bar kept its previous language after a language change.

## [0.1.0] - 2026-08-29

The first version with a usable interface. Not released.

### Added

- Management interface client, `.ovpn` parsing and inlining, and launching through the OpenVPN
  interactive service.
- One time codes, both the form presented up front and the form raised after a refusal.
- SQLite store, profile import with duplicate detection, and a connection supervisor that runs
  several tunnels at once.
- Avalonia interface with search, live status, a notification area entry implemented directly on the
  shell interface, and balloon notifications.
- Quick switcher on a global shortcut, and shortcuts for connect, reconnect, disconnect and the
  favourite slots.
- Settings screen, localization in English and German, and a language that can be added as a file
  rather than a build.
- Credentials protected by the operating system, never in the profile database.
- Session history with a comma separated export, telemetry with pushed routes and round trip, and
  bounded auto reconnect.
- Watched directories, portable packages, a diagnostics bundle and autostart.
- `ovp`, a companion command that hands connect, disconnect and status to the running application.

### Fixed

- Opening the history threw and took the application down: SQLite cannot order or compare a
  `DateTimeOffset`. Timestamps are stored as ticks.
- Every session was recorded as having carried nothing, because the status that reports the end of a
  connection has no counters.
- The recorder handled statuses concurrently, so the one closing a session could overtake the last
  counter update.
- Startup deadlocked on the user interface thread.
