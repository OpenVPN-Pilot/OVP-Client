# Changelog

All notable changes to this project are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows
[semantic versioning](https://semver.org/spec/v2.0.0.html).

Development happens on `dev`. `main` carries releases, and every entry under Unreleased moves into a
version heading when one is tagged.

## [Unreleased]

### Added

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

### Fixed

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
