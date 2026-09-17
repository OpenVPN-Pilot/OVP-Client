# Changelog

All notable changes to this project are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Development happens on `dev`. `master` carries releases, and every entry under Unreleased moves into a
version heading when one is tagged. A release tag is `v<version>`, for example `v1.2.0`.

## [Unreleased]

### Fixed

- **The completion script the command writes is readable by the shell it is for.** It carried
  carriage returns, because the script is a literal in a source file and a source file carries
  whatever it was checked out with. A carriage return is not whitespace to a Unix shell: zsh read
  the one after `case "${words[2]}" in` as part of the word and refused the whole script, on every
  new shell, so completion silently never worked. Reinstall it with `ovp completion zsh --install`.

## [1.9.0] - 2026-09-17

### Added

- **Show in the Dock while a window is open**, in the general settings and in the menu bar entry's
  own menu, for macOS. It is on, which is the application as it was; off leaves it in the menu bar
  and nowhere else. The choice exists because being in the Dock costs something that cannot be taken
  back: macOS enters an application that has been in the Dock in its list of recent applications, and
  that entry outlives the window, the quitting and the process.

### Fixed

- **The application no longer puts itself in the Dock by launching, on macOS.** It was a regular
  application from the moment it started, so a copy started hidden or at login was entered in the
  Dock's list of recent applications although it never showed a window, and the tile that left behind
  outlived the process. It now starts as an accessory application, and a window is the only thing
  that puts it in the Dock.
- The line under the selected tab in the settings was drawn through the letters that reach below the
  baseline. It has room under the text now.

### Changed

- **About OpenVPN Pilot** is in the menu bar entry's menu as well as in the menu named after the
  application, because there is no such menu while the application is kept out of the Dock. The
  shortcuts are unaffected either way: macOS answers a menu's key equivalents whether the menu is
  shown or not.
- The profile editor setting reads as a choice rather than as a sentence broken in two. **View when
  opened** is followed by **Form** and **Plain configuration**, where **Opens with** was followed by
  **The form** and **The plain configuration**.

## [1.8.0] - 2026-09-16

### Changed

- **The project moved to another repository.** The check for a newer release follows it: a settings
  file naming the repository this was published from before is pointed at the new one, once, and a
  field naming anything else is left alone, because that is a fork somebody typed and it follows its
  own releases.
- The port in the profile editor has room for the number in it. Five digits were clipped by the box
  the buttons of a spinner share, and the field that gave the width up is the address, which is
  longer than its box whatever the box is.

### Added

- The mark as plain images, `logo-128` to `logo-1024` in `assets/artwork`, drawn by the same command
  as the icons. An icon container is what a system asks for and an image is what a page asks for,
  and until now the second had to be exported by hand.

## [1.7.0] - 2026-09-16

### Removed

- **The shared library** that 1.6.0 kept in a file in a synchronised folder, with everything that
  belonged to it: the section in the settings, the passphrase prompt, the banner and the state in the
  status bar, the sync log, the backups and the notes beside the file, the notices on deleting,
  editing and importing, and its documentation. A folder a sync client carries between machines
  cannot make the guarantees keeping one library for several people needs: the lock only works once
  the client has delivered it, two writes can cross, and every safeguard added on top was a way of
  living with that rather than removing it. Keeping profiles in step between machines will come back
  on a server the machines talk to instead.
- Packages no longer carry when each profile last changed or which profiles were deleted. Only the
  shared library read either, and a package written by 1.6.0 that has them still opens.
- What a machine that used the shared library kept is not read and not removed: the `library`
  folder in the data directory, the file and its `.backups` folder in the shared folder, and the
  passphrase in the keystore, which **Forget all stored credentials** clears along with the sign ins.

### Fixed

- Ending the application from Task Manager reported it as not responding. The request arrived as an
  ordinary close of the window, which with closing to the notification area turned on hid the window
  and kept the process running. A close that another program asks for, Task Manager or `taskkill`,
  now ends the application with the usual teardown; the close button, Alt+F4 and the taskbar still
  keep it in the notification area.

## [1.6.0] - 2026-09-15

### Added

- **A shared library.** The profiles and their saved sign ins can live in one encrypted file that
  several machines use together, meant for a folder a sync client such as OneDrive keeps in step.
  The settings create one, join one, change its passphrase and stop sharing it. The passphrase is
  asked for once per machine and kept in the operating system's protected storage; favourites,
  shortcut slots, shortcuts, settings and history stay personal.
- A machine uses a shared library or its own, never a mixture. Joining replaces the profiles on the
  machine and adds none of them to the file; the settings say how many there are, offer an export
  first and ask for a tick before the file is chosen. Stopping asks whether to keep the profiles as
  the machine's own library or to start again with an empty one. Both are refused while a tunnel is
  connected where profiles would be replaced or removed.
- It is reconciled at start, with what this machine changed while the application was closed going
  first, again within seconds of a change made here, and whenever the file's contents changed, which
  is checked every minute by its hash rather than by its date. Writing takes a lock file beside the
  shared one, replaces the file in one step, and merges again if another machine wrote it in the
  meantime. What cannot be written is recorded as waiting, retried with a growing pause and written
  on the way out.
- Every machine remembers what the file held when it last synchronised, so a change is merged field
  by field and only the same field changed on two machines is a conflict, which the later change
  wins. A deletion never wins over a later change, is remembered for a year so a machine that was
  away does not bring the profile back, and waits while the profile is connected. The same
  configuration imported on two machines ends up as the same single profile everywhere.
- A banner in the main window for what stands in the way of the shared library, with the passphrase
  prompt or another attempt, and for conflicts that were resolved and copies of the file a sync
  client left behind. The status bar says what a synchronisation changed, and the list reloads.
- Every version of the shared file names the versions it descends from, so a machine whose write the
  sync client replaced with another machine's, as happens when one of them was offline, merges
  against where both started and keeps its changes instead of losing them.
- Deleting ten or more profiles for everyone at once, or two or more that are most of the library, is
  held back and asked about, restore on this machine or delete for everyone. A machine whose
  database was started again empty no longer empties the library for every machine.
- **Backups of the shared library.** Every machine keeps the versions it saw, the last twenty and one
  a day for thirty days, and leaves a copy of the version it last synchronised with and a note
  beside the file, in a `.backups` folder. The settings list the machines using the library and every
  backup, and restore one for every machine, which is also the way back from a missing or damaged
  file.
- Deleting, editing and importing say that the change reaches every machine while a library is shared.
- The status bar shows where the shared library stands at all times, synchronising, synchronised
  with the time, or what stands in the way with the next attempt and whether changes are waiting.
  **Sync log** beside it lists the steps taken: changes noticed, the file read and written, what was
  taken in, conflicts, failures and retries.
- **Choosing what a package carries.** An export picks its profiles by ticking them or by tag, and
  includes the shortcuts, the settings and the saved sign ins each on its own. Opening a package lists
  every profile it holds, marked as new or as already stored, and what else it carries, and takes
  only what is ticked. Shortcuts are added only where they clash with nothing, and the settings
  replace this machine's only when asked to. What belongs to one machine, its window positions,
  OpenVPN path, autostart and shared library, never travels.

### Changed

- The package format is version 2. It records when each profile last changed, carries the settings
  and which profiles were deleted, and a version that finds a newer format refuses the package with
  the version that wrote it rather than reading part of it. Packages written by earlier versions
  still open.
- A profile's change time moves only when something shared about it changes. Marking a favourite,
  connecting and an edit that changes nothing leave it alone, which is what lets two machines tell a
  real change from a visit.

## [1.5.0] - 2026-09-15

### Added

- **The configuration can be edited.** It was not editable at all, on the reasoning that a slip would
  break a working profile, and what that left was re-importing a whole file to change a port. The
  profile editor now has a form for what is changed by hand, the server, its port and protocol and
  the certificates and keys, and a changed field replaces only the lines it is about, leaving
  comments, ordering and line endings as they were. The whole configuration is one button away as
  plain text for everything else, the edit carries across in both directions, and the general
  settings decide which of the two the editor opens with.
- Every edit is checked as it is made against what OpenVPN is known to refuse, in a sentence that
  names the value and, in the plain text, the line: no server, a port or protocol it cannot read,
  nothing to verify the server with, a block that is never closed, a closing tag with no opening, and
  a certificate or key pasted into the wrong block. Those stop a save. A reference to a file on disk
  and a directive that runs a program are reported and do not. A save refreshes what the list reads
  from the configuration, and a configuration another profile already holds is refused the way an
  import would refuse it.
- A profile can be deleted from the detail panel, which asks first and names the profile it is about.
- The detail panel shows a profile's notes under when it was last used, selectable so an address or a
  contact in them can be copied.
- A limit on how large the log directory may grow, a gigabyte by default and changeable in the
  advanced settings. The oldest files go first when it is reached.

### Changed

- The log is written one file per hour rather than one per day. A tunnel that logged the same failure
  for every packet wrote daily files of more than a gigabyte, and the retention kept a week of them.
  An hour that writes more than a twentieth of the limit continues in a second file, so the limit
  holds even while the file being written is the large one. Daily files an earlier version wrote
  expire by the same rules.
- **Select** is called **Select several**, sits above the list it acts on rather than in the header,
  and reads **Done selecting** while it is on. It gave no sign of having done anything, or of how to
  leave the mode it had entered.
- The bar under a selection is two lines, what is ticked and then what to do with it, with delete set
  apart at the far end. What cannot be undone shares one red style, outlined where it asks and filled
  where it confirms, in the list, the detail panel and the profile editor.
- Return presses the primary button of the profile editor, the settings, the import and the export,
  and escape closes each of them and the history and log windows without saving. A field that holds
  several lines keeps return for a new line.
- The profile editor window can be resized.
- Tag names are matched without regard to case wherever a tag is looked up, as the sidebar already
  counted them, so importing `Office` into a store that has `office` no longer makes a second entry.

### Removed

- **Watched folders**, with their settings, the section on the profiles page and the **New** entry in
  the library that listed what they brought in. Keeping the store in step with a directory caused
  more trouble than it saved, and importing or opening a package does the same job when somebody asks
  for it. A migration drops the table and the column; profiles a directory brought in stay, recorded
  as ordinary imports.
- **Connect everything shown** in the sidebar. Ticking what is shown and connecting the selection does
  the same on purpose rather than by accident.

### Fixed

- Opening a package ended the application when two of its profiles shared a tag the store did not
  have yet. Each profile looked its tags up in the database, where the tag the previous profile had
  just added was not saved, so the same tag was added twice and the unique index refused the save;
  the exception escaped the import. Tags are now resolved once per import and remembered, and
  anything else a store refuses during an import is reported on the import screen.
- In the disconnect palette on macOS, space was typed into the search box instead of ticking a row,
  and the arrow keys could be taken by the box. The palette handled its keys after the box, and on
  macOS the system's input method serves the box first and delivers a space as text. Keys are now
  taken before the box sees them, and a space is taken from the text.
- On macOS the application stayed in the Dock with its window closed, although it lives in the menu
  bar and on Windows leaves the taskbar with the window. It now leaves the Dock while no window of its
  own is open and comes back when one is shown.
- On macOS the application menu kept the framework's entry about itself. The platform part that fills
  the menu was never registered, and the menu was set after the platform had already read it, which
  it does once. Both are fixed, and the application's own about entry and settings sit in front of
  the entries macOS adds for hiding and quitting.
- A configuration that sets its port with `port` rather than on the remote line was listed with port
  1194.

## [1.4.0] - 2026-09-13

### Added

- **macOS.** The application, the companion command and the installers all run there, driving the
  same `openvpn` through the same management interface as on Windows. What differs is the privileged
  part that starts it: Windows has OpenVPN's own interactive service and macOS has nothing like it,
  so this project brings its own helper. `Core`, `OpenVpn`, `Data` and `App` did not change for any
  of it; macOS is an implementation of the platform interfaces that already existed.
- A privileged helper, `Platform.MacOS.Helper`, which is the only part that runs as root and depends
  on nothing else in the repository except the protocol it shares with the application. It is a
  launchd daemon started by the first connection through socket activation and ending itself when it
  has been idle, so nothing of it runs while the application is closed, and installing it asks for a
  password once rather than every time a tunnel comes up. The caller sends values and never options:
  the helper parses the configuration with a port of OpenVPN's own `parse_line`, refuses anything
  that would run a program or read a file the caller chose, writes its own root owned copy, and
  builds the command line itself. What a tunnel changed to the name servers is written down before it
  is changed and restored when it ends, including after a crash, checked against the boot time so a
  stale record cannot undo a newer setting.
- The helper package carries the OpenVPN it runs, built from pinned sources by
  `installer/build-openvpn-macos.sh` and linked statically against nothing but the system. An OpenVPN
  from a package manager lives under a directory owned by the account that installed it, together
  with the libraries it loads, and running that as root would hand root to anything running as that
  account.
- Two macOS installers, built by `installer/build-macos.sh`: the application in a disk image, dragged
  to Applications with no password, and the helper as a package, which asks for one. The disk image
  opens the window a Mac installer is expected to open, with a background, fixed icon positions and
  its own volume icon, and the package installs, registers and links `ovp` onto PATH and can be
  removed again with one script that leaves nothing behind.
- `scripts/dev.sh`, the macOS counterpart of `scripts/dev.ps1`: stop what is open, build, start what
  was built. It also names where .NET is, which a build from the source tree needs and an installed
  build does not.
- `assets/artwork`, where everything visual now comes from, and `assets/make-artwork.swift`, which
  draws all of it from geometry: the macOS icon, the Windows icon, the menu bar template and the disk
  image background. The projects link those files rather than keeping copies.

### Changed

- The README is one page that says what this is and points at `docs/`, which holds one page per
  subject: Windows, macOS, using it, the `ovp` command, and working on it. It had grown to everything
  anyone might want to know about two operating systems in one scroll.
- **macOS is built from the source and is not published as a download.** A build another Mac opens
  without an argument has to be signed and notarised by Apple, which needs a paid Developer ID, and
  making one needs a Mac to make it on. The documentation, the banner the application shows when the
  helper is missing, the note the update check raises and what `ovp doctor` prints all say so, and
  the link they offer is the page with the one command on it rather than a releases page with nothing
  on it for the reader. A build made on the machine it then runs on carries no quarantine flag and
  Gatekeeper says nothing at all, which is what makes this reasonable rather than a chore.
- The macOS bundle is called `OpenVPN Pilot.app`. The Finder labels an application with its file name
  and with nothing else: `CFBundleDisplayName`, a localized `InfoPlist.strings` and
  `LSHasLocalizedDisplayName` were each tried and each ignored, so the disk image, Applications and
  the Dock all showed the compact form while the window and every notification said the spaced one.
  The compact form stays where a name has to be one word. Both names are looked for, by `ovp` and by
  the helper package, so an installation made before this keeps working.
- The application icon carries a tile of its own rather than leaving one to the system. macOS 26 puts
  a grey container under an icon that has none, so the same application looked one way there and
  another on Windows, and neither was chosen. Below 32 points the tile is dropped again, because a
  tile with a mark inside it at that size leaves the mark ten points across.
- The companion command asks for each capability through its interface and decides its platform once,
  the way the application does. It reached for Windows types directly and could only ever run there.
  Windows keeps the same implementations and the same behaviour; macOS adds zsh completion, which is
  the default shell there, in zsh's own completion system rather than through bash's.
- A language file can word a key for one platform, so wording that names a part of one system reads
  correctly on both without a second catalogue.

### Fixed

- The language setting set to follow the system came up in English on a German machine, on Windows as
  well as macOS, however the machine was set up. The repository built with `InvariantGlobalization`,
  which leaves `CultureInfo.CurrentUICulture` as the invariant culture whose name is the empty
  string: there was never a language to follow, and every date and number was formatted the invariant
  way rather than the reader's. Both Windows 10 and macOS carry ICU, so nothing is bundled for this.
  The helper keeps globalization switched off for itself, being a root daemon that formats nothing
  for anyone.
- Press and drag on a profile ended the application on macOS. The drag carried its identifiers in an
  in process format, which Avalonia documents as never being serialized to a platform drag, and the
  macOS backend builds the dragging session out of exactly what was serialized: nothing. AppKit
  refuses a session with no items by raising, and an Objective-C exception raised under the run loop
  is an abort. Windows keeps its data object inside the process and never noticed.
- Only one copy of the application runs per user on Unix as well, rather than one per login session.
- The solution builds with the .NET SDK 10.0.400, whose analysers refuse a log call that formats its
  arguments before knowing whether the line will be written. The source generated log methods now
  receive the values themselves and format them only when the line is written. The lines read as
  before, except that an update check which found no release reports its latest version as `null`
  rather than `-`.
- Tearing a connection down no longer looks up a process identifier of zero or below. On Unix such
  an identifier addresses a whole process group, or every process the user may signal, and the
  lookup succeeds; a launcher that reported zero had the test suite end itself, its runner and the
  shell that started them. A test fake that reported an arbitrary identifier, which on a system
  that hands them out in sequence can belong to anything, reports zero as well.
- The Windows taskbar and notification area showed the macOS icon: the two platforms share the mark
  but the `.ico` was drawn with the same rounded-square tile the macOS grid needs, and Windows'
  32 point and larger icons picked the tiled drawing up. The Windows icon is now the mark on its own
  at every size.
- A Windows notification carried a large image nobody asked for: Windows' own stock icon for the
  severity, a blue circle, an orange triangle or a red circle, in place of the application's. Drawing
  the application's own icon there instead turned out no better, since the tray's `hIcon` is far
  smaller than the image wants and every pixel of stretching it up showed. Notifications now carry no
  large image at all.
- The small icon the notification centre shows beside the application's name kept showing one from
  months earlier on a development machine, regardless of how often the registration that names an
  icon for it was rewritten afterwards or how often the process restarted: Windows resolves it once
  per application identity, caches it in `wpndatabase.db`, and never rereads it. Confirmed by clearing
  that cache, which is a whole account's notification history and not something this application
  reaches into; a fresh installation has no stale entry to begin with and is not expected to need it.

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
