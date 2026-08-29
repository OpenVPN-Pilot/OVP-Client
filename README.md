# OpenVpnPilot

A desktop client for OpenVPN built for people who manage a lot of profiles.

The stock OpenVPN GUI on Windows is a tray icon with a flat, unsearchable list. That works for two or
three connections. It stops working somewhere around twenty. OpenVpnPilot keeps the proven OpenVPN
process doing the tunnelling and replaces the interface around it.

> **Status: version 1.0.0.** The integration layer and the interface are proven end to end against
> OpenVPN Community 2.7.6 and against the ten server lab in this repository. Builds are unsigned by
> choice. See [Roadmap](#roadmap).

## Contents

**Using it**
[Features](#features) ·
[Requirements](#requirements) ·
[Installing](#installing) ·
[Organising a set](#organising-a-set) ·
[Working on more than one at a time](#working-on-more-than-one-at-a-time) ·
[Adding a language](#adding-a-language) ·
[The `ovp` command](#the-ovp-command) ·
[Driving it from other software](#driving-it-from-other-software)

**How it works**
[What reaches OpenVPN](#what-reaches-openvpn) ·
[Where things are kept](#where-things-are-kept) ·
[Architecture](#architecture)

**Working on it**
[Building](#building) ·
[The test lab](#the-test-lab) ·
[Contributing](#contributing) ·
[How this was built](#how-this-was-built) ·
[Roadmap](#roadmap) ·
[Licence](#licence)
## Features

- Instant search across profile name, tag and remote host
- A quick switcher: one global shortcut, type a few letters, press return. Return connects and
  leaves you where you were; control and return brings the window up as well
- The same palette for stopping: one shortcut lists what is running, space ticks several, return
  disconnects them
- Checkboxes on demand, to connect, disconnect or delete a set of profiles together
- Tags and favourites with numbered slots bound to shortcuts
- Global shortcuts for connect, reconnect, disconnect and the favourite slots
- Several tunnels connected at once, each with its own live telemetry
- Live figures per tunnel: throughput, uptime, round trip, assigned address, pushed routes and DNS
- Credentials kept in the operating system keystore, never in a file on disk
- One time codes, both the kind presented up front and the kind raised after a refusal
- Session history with durations and transfer volumes, exportable as CSV
- Bulk import from files, folders and ZIP archives, plus watched folders that keep profiles in sync
- Export as plain configurations, or as an encrypted `.ovppkg` package for another machine, which
  can carry the saved sign ins so a whole set arrives ready to connect
- A command line on both the application and `ovp`, so other software can bring a tunnel up before
  it needs one
- Notifications for connected, lost, reconnecting and failed, suppressible per event
- Dark, light and system themes, autostart, bounded auto reconnect
- English and German, and a new language is a JSON file rather than a new build

## Requirements

- Windows 10 or 11
- [OpenVPN Community](https://openvpn.net/community-downloads/) 2.6 or newer, including the
  **OpenVPN Interactive Service** that the official installer sets up

OpenVPN Connect is a different product built on a different core and cannot be used as the backend.
The application detects this case at startup and says so explicitly.

Connecting without an elevation prompt requires the calling account to be authorised by the interactive
service. That means membership in the local Administrators group, or in the group named by the
`ovpn_admin_group` registry value (`OpenVPN Administrators` by default, which the OpenVPN installer does
not create). Without that, configurations must live under the OpenVPN `config_dir`. The application
reports which of these applies rather than failing with a generic error.

## Installing

```powershell
pwsh installer/build.ps1
```

The script is `installer/build.ps1`; the package it describes is `installer/OpenVpnPilot.wxs`.
It publishes the application and the `ovp` command into one directory and packages them as an MSI
under `artifacts/release`. Installing it puts the application under Program Files, adds a Start menu
entry, and puts the installation directory on the machine PATH so that `ovp` works in any terminal.
It is removed from Apps and features like anything else, and removing it takes the PATH entry with
it. The package carries the .NET runtime, so OpenVPN itself is the only prerequisite.

Building the installer needs the [WiX toolset](https://wixtoolset.org):

```bash
dotnet tool install --global wix
```

The installer is deliberately not part of the solution. Adding it would put WiX between a developer
and an ordinary build, and building an installer is not something an ordinary build should do.

### Deploying it with group policy

The package is built for it. Assign or publish it under **Computer Configuration, Software Settings,
Software installation** from a UNC path every machine can read. Read out of the built package:

| | |
| --- | --- |
| Scope | per machine, `ALLUSERS=1` |
| Custom actions | none, so nothing runs outside the installer's own sequence |
| Reboot | never scheduled |
| Platform | x64, one language |

Nothing has to be answered during the installation, so no transform is needed. The directory, the
Start menu entry and the PATH entry are the same for everyone on the machine; the profile store, the
settings and the credentials belong to whoever runs it.

Uninstall by removing the assignment, or with `msiexec /x` and the product code.

## Organising a set

There are no folders. A profile carries as many tags as it needs, the sidebar lists them, and the
search box matches a tag along with the name and the remote host. One profile can belong to as many
groupings as make sense, which a tree cannot express, and nothing has to be maintained by hand.

Profiles a watched directory brings in appear under **New** until they are marked as seen, so an
automatic import never drops them unannounced into the middle of the list.

## Working on more than one at a time

**Select** in the header puts a checkbox on every row. Tick some and connect, disconnect or delete
them together; deleting asks a second time, because it is the one action here that cannot be undone.

**Connect everything shown** in the sidebar acts on what the current filter and search leave visible,
which is how a whole tag is brought up in one go. Profiles are connected one after another rather
than all at once: each tunnel is a process, an adapter and a port, and twenty starting in the same
instant is how a machine runs out of all three.

There is no built in limit on how many tunnels run at once, and ten at a time is what the lab is for.
The real limits are outside the application: one OpenVPN process and one virtual adapter per tunnel,
and the adapter pool is what runs out first. A tunnel that cannot come up is given a minute and then
abandoned, so a saturated machine reports what happened instead of leaving processes behind.

## Adding a language

Language files are JSON. The ones that ship live in `lang` beside the executable, and anything placed
in `lang` under the application data directory is layered on top of them, key by key. Copy `en.json`,
translate the values, drop it in and reload from the settings screen: no rebuild, and a key you have
not translated falls back to English rather than disappearing.

## The `ovp` command

`ovp` is a companion for the terminal. When the application is running, `connect`, `disconnect` and
`status` are handed to it over the instance channel, so they act on the tunnels its window shows
rather than starting a second, invisible set beside them.

```bash
ovp doctor
```

```bash
ovp add C:\profiles --commit --tag production
```

```bash
ovp con site-alpha
```

```bash
ovp st
```

`ovp help <command>` explains one command.

Completion offers the commands and, wherever one is expected, the stored profile names. It reads them
at completion time, so a profile imported a minute ago completes without reloading the shell:

```bash
ovp completion powershell --install
```

The installer puts `ovp` on PATH. Without it, the command can also be installed as a .NET tool:

```bash
dotnet pack src/OpenVpnPilot.Cli -c Release
```

```bash
dotnet tool install --global --add-source artifacts/packages OpenVpnPilot.Cli
```

## Driving it from other software

`ovp` is the only thing anything else has to call. A session manager that brings a tunnel up before
opening a remote desktop needs one command, and it must not matter whether anyone had the
application open:

```bash
ovp connect "site-alpha"
```

The request is handed to the application, which owns the tunnels and the store. When none is
running, it is started first. Add `--headless` to start it with no window and no notification area
entry, which is what something running unattended wants:

```bash
ovp connect "site-alpha" --headless
```

```bash
ovp disconnect "site-alpha"
```

```bash
ovp stop
```

`ovp start [--headless]` opens it without connecting anything, `ovp stop` ends it and its tunnels,
and `ovp status` reports what is connected, one line per tunnel. Exit codes are 0 for done, 1 for a
command that was not understood, 4 when nothing was listening, and 6 when the store could not be
read.

The application understands the same options directly, for a shortcut or a scheduled task that
starts it: `--headless`, `--background`, `--connect`, `--disconnect`, `--disconnect-all` and
`--quit`. Only one copy runs per user, so a second launch hands its options to the copy that already
runs and exits.

## What reaches OpenVPN

The application never edits a profile to make it work. It writes the configuration out exactly as it
was imported and puts everything it needs on the command line, so what a server sees is the
configuration you gave it plus a fixed set of options:

```
--config <file> --management 127.0.0.1 <port> stdin --management-query-passwords
--management-hold --management-forget-disconnect --auth-retry interact --verb 3
```

The management interface is how the client drives the tunnel and the only way a credential ever
reaches OpenVPN; its password is generated per connection and passed on standard input, so it never
touches disk. Nothing else is added unless it was asked for:

| Added when | Options |
| --- | --- |
| Route protection is on, per profile or for the application | `--pull-filter ignore "redirect-gateway"`, `--pull-filter ignore "dhcp-option"`, `--pull-filter ignore "block-outside-dns"` |
| A log file was asked for | `--log <file>` |

Route protection is what stops a server from taking the host's routing and DNS with it. To watch a
server that asks for all traffic do it anyway, turn it off for that one profile in the profile
editor, or for everything under **Settings, Connections**. From a terminal it is simply the option
left out:

```bash
ovp connect lab/clients/lab-09-cert.ovpn --detached --seconds 20
```

That server pushes `redirect-gateway def1`, so with protection off the host sends everything into the
tunnel for as long as it is up. Nothing else about the machine changes and it is over when the
command is, which is why a short run from a terminal is the way to look at it.

## Where things are kept

Everything the application writes belongs to the user running it, so an installation for the whole
machine still keeps each person's profiles apart.

| | |
| --- | --- |
| Profiles, tags and history | `%LOCALAPPDATA%\OpenVpnPilot\pilot.db` |
| Settings | `%LOCALAPPDATA%\OpenVpnPilot\settings.json`, editable by hand |
| Credentials | `%LOCALAPPDATA%\OpenVpnPilot\secrets\`, one protected file each |
| Logs | `%LOCALAPPDATA%\OpenVpnPilot\logs\` |
| Added languages | `%LOCALAPPDATA%\OpenVpnPilot\lang\` |
| Configurations while connected | `%ProgramData%\OpenVpnPilot\runtime\<user SID>\` |

A materialised configuration carries its private key inline, which is why it lives under
`%ProgramData%` with an access control list for one user rather than in a temporary directory. It
exists only for the lifetime of a connection, and anything a crash leaves behind is removed at the
next start.

The registry holds settings that have nowhere else to go:

| Key | Written by | What for |
| --- | --- | --- |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | the application | The autostart entry, only while "start with Windows" is on. Removing it turns autostart off. |
| `HKLM\Software\OpenVpnPilot` | the installer | Two markers so the Start menu entry and the PATH entry can be removed again. |
| `HKLM\...\Uninstall\<product code>` | Windows | The entry under Apps and features. |
| `HKLM\SOFTWARE\OpenVPN` | nobody, it is only read | Where OpenVPN Community says it is installed, and which group the interactive service authorises. |

Nothing else is written to the registry. Removing the product removes the two keys the installer
made; the profile store and the credentials are deliberately left alone, because uninstalling an
application is not the same as asking it to forget everything.

## Architecture

| Project | Responsibility |
| --- | --- |
| `OpenVpnPilot.Core` | Domain model, abstractions, localization, settings, update check |
| `OpenVpnPilot.OpenVpn` | Management interface protocol, `.ovpn` parsing, connection supervision |
| `OpenVpnPilot.Data` | SQLite persistence, import, portable packages |
| `OpenVpnPilot.Platform.Windows` | Interactive service client, secret storage, notification area, shortcuts |
| `OpenVpnPilot.App` | Avalonia user interface and the services that drive it |
| `OpenVpnPilot.Cli` | `ovp`, a headless companion command |

One connection, end to end: the profile is written out of SQLite to a private file under
`%ProgramData%`, the interactive service is asked over its named pipe to start `openvpn` with that
file and a management port, the client attaches to that port and holds the tunnel until it has
answered whatever the server asks for, and everything after that (state, throughput, pushed routes,
credentials, the stop signal) travels over the same management connection. When the tunnel ends the
file is deleted and the process is confirmed gone.

Each tunnel runs as its own `openvpn` process with its own management interface on a loopback port.
Credentials are supplied over that interface and are never written to disk. `Core`, `OpenVpn`, `Data`
and `App` contain no platform specific code, so support for another operating system means adding an
implementation of the existing interfaces rather than restructuring the application.

## Building

```bash
dotnet build
```

```bash
dotnet test
```

Requires the .NET 10 SDK. The user interface is built with Avalonia.

To build and run what you just changed, in one step:

```powershell
pwsh scripts/dev.ps1
```

It stops whatever copy is open first, which is the part that is easy to forget: only one copy runs
per user, so starting a new one while an old one is open hands the request to the old one and nothing
on screen changes. `-Headless` starts it without a window, `-Connect <name>` connects a profile once
it is up, and `-NoBuild` skips straight to starting what is already built.

The build output is where `dotnet` puts it:

| | |
| --- | --- |
| Application | `src/OpenVpnPilot.App/bin/Debug/net10.0/OpenVpnPilot.exe` |
| Command | `src/OpenVpnPilot.Cli/bin/Debug/net10.0/ovp.exe` |
| Installer payload | `artifacts/install`, written by `installer/build.ps1` |
| Installer | `artifacts/release/OpenVpnPilot-<version>-win-x64.msi` |

## The test lab

`lab/` is a Docker Compose project with ten OpenVPN servers and a site behind each of them. It exists
because one server proves one path, and the parts of a VPN client that are hardest to get right are
the ones a single server never exercises.

```bash
docker compose -f lab/docker-compose.yml up -d --build
```

Open `lab/index.html` for a page listing the ten servers, what each one is for, the credentials they
want, and a check that says which of their sites answer right now. A site that answers is proof the
tunnel is carrying traffic, which is more than a client reporting that it is connected.

The ten client configurations appear in `lab/clients` once the certificate material has been built.
Import them and the whole set is covered: a certificate on its own, a private key with a passphrase,
a user name and password with and without a client certificate, the same over TCP, a one time code
presented up front and one raised as the reason for a refusal, a server pushing name servers and
routes, a server asking to carry all traffic, and a server pushing a compression setting a current
client refuses. Every server has its own tunnel network, so all ten can be connected at once.

Each site answers only through the tunnel in front of it and serves three things: something small to
look at, something large to pull as fast as the tunnel allows, and something large served at a fixed
rate, which is what a video looks like to a network. Credentials, addresses and names are invented
and written into the generated configurations.

```bash
docker compose -f lab/docker-compose.yml down -v
```

That stops it and removes the certificate authority with it.

## Contributing

Issues and merge requests are welcome, including the small ones: a wrong translation, a confusing
label, a server that behaves in a way the client does not expect. A bug report that names the server
and what it pushed is worth more than a stack trace.

Two things make a change easy to accept.

**Say why in the code.** Comments here explain the reason, never the mechanism: what was tried, what
the alternative was, what breaks if it is changed back. `CLAUDE.md` holds the house rules and, more
importantly, the OpenVPN facts that were established by measurement rather than assumption. Read it
before changing anything that talks to OpenVPN, and correct it if a measurement ever contradicts it.

**Prove it against a server.** `lab/` runs ten of them, covering the paths a single server never
does. A change to the connection lifecycle without a test is a change nobody can check.

`dotnet build` treats warnings as errors and `dotnet test` has to stay green. Everything in the
repository is English, including commit messages, which are short, imperative and prefixed with a
gitmoji code.

## How this was built

Every line of this repository was written by Claude Opus 5, run at maximum reasoning effort, in a
conversation with the author. No part of it was typed by hand.

That is worth stating plainly rather than leaving to be discovered, and it is worth qualifying. The
author writes C# and read what was produced: the architecture, the layering, the platform boundary
and the decisions recorded in `CLAUDE.md` were reviewed and pushed back on, and the model was
corrected where it was wrong. Several of the hardest findings in this repository came from measuring
against real servers and disagreeing with what had been assumed, including one case where the
documented workaround for a pushed compression setting turned out to produce a tunnel that connects
and silently carries nothing.

So: generated, but not unexamined. Judge it by the code, the comments and the tests rather than by
how it was produced.

## Roadmap

- [x] Prove the interactive service and management interface integration end to end
- [x] Solution structure and coding standards
- [x] Configuration parsing and inlining, management client, interactive service launcher
- [x] Connection supervisor, credential handling and one time codes
- [x] SQLite store, schema and profile import with duplicate detection
- [x] User interface with profile list, search, live status and notification area icon
- [x] Tags, favourites, quick switcher, import and export
- [x] Global shortcuts, notifications, telemetry and session history
- [x] Localization, settings, autostart and auto reconnect
- [x] Portable packages, watched folders and a diagnostics bundle
- [x] Installer, `ovp` on PATH, and a command line other software can drive
- [ ] An update feed
- [ ] macOS

Releases are not code signed. A certificate costs money every year to tell people what the source
already tells them, so builds are unsigned: build it yourself with the two commands above, or accept
the warning Windows shows for anything unsigned.

There is no kill switch and none is planned. This is a client for reaching another network, not for
being an exit node, so a tunnel that drops leaks nothing that was not already going out the same way.

## Licence

MIT. See [LICENSE](LICENSE).
