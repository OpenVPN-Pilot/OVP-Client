# Changelog

All notable changes to this project are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Development happens on `dev`. `master` carries releases, and every entry under Unreleased moves into a
version heading when one is tagged.

## [Unreleased]

### Added

- The quick switcher stops tunnels as well. One shortcut lists only what is running, space ticks
  several and return disconnects them, which is the same act as starting one done to a shorter list.
- The README says what reaches OpenVPN: the fixed options every connection carries, the ones added
  only when they were asked for, and how to watch a server take over the routing on purpose.

### Changed

- The author and copyright are recorded in the built executable, the package and the installer.

### Fixed

- A shortcut added in a later version never reached an installation that already existed. The
  default bindings were applied once and a flag remembered it, so the machines using the application
  longest were the ones a new shortcut was never bound on. They are applied every start now, which
  adds only what has no binding at all and leaves everything else alone.

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
