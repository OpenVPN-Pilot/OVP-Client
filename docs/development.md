# Working on OpenVPN Pilot

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [The `ovp` command](cli.md) · Working on it

## Architecture

| Project | Responsibility |
| --- | --- |
| `OpenVpnPilot.Core` | Domain model, abstractions, localization, settings, update check |
| `OpenVpnPilot.OpenVpn` | Management interface protocol, `.ovpn` parsing, connection supervision |
| `OpenVpnPilot.Data` | SQLite persistence, import, portable packages |
| `OpenVpnPilot.Platform.Windows` | Interactive service client, secret storage, notification area, shortcuts |
| `OpenVpnPilot.Platform.MacOS` | Helper client, keychain, menu bar item, notifications, hotkeys, login item |
| `OpenVpnPilot.Platform.MacOS.Protocol` | The messages and the installed paths the two sides of the helper agree on |
| `OpenVpnPilot.Platform.MacOS.Helper` | The privileged helper: the only part that runs as root |
| `OpenVpnPilot.App` | Avalonia user interface and the services that drive it |
| `OpenVpnPilot.Cli` | `ovp`, a headless companion command |

One connection, end to end: the profile is written out of SQLite to a private file under
`%ProgramData%`, the interactive service is asked over its named pipe to start `openvpn` with that
file and a management port, the client attaches to that port and holds the tunnel until it has
answered whatever the server asks for, and everything after that (state, throughput, pushed routes,
credentials, the stop signal) travels over the same management connection. When the tunnel ends the
file is deleted and the process is confirmed gone.

On macOS the same connection goes through the helper instead: the application opens a session on the
helper's socket, sends the configuration as text with the management port and password, and the helper
validates it, writes its own root owned copy, builds the command line and starts `openvpn` as root.
Everything after that is again the management connection, which the application holds directly. A
tunnel belongs to the session that started it and is ended when that session closes, because an
application that is gone can no longer answer a credential prompt or a stop signal.

The helper is deliberately small and depends on nothing else in this repository except the protocol
it shares with the application. What runs as root should be readable in one sitting.

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

Requires the .NET 10 SDK. The user interface is built with Avalonia. Building the macOS installers
needs Xcode's command line tools as well, which `installer/build-macos.sh` checks for before it
starts and which [docs/macos.md](macos.md) explains.

To build and run what you just changed, in one step:

```powershell
pwsh scripts/dev.ps1
```

```bash
bash scripts/dev.sh
```

One per platform, doing the same thing. Both stop whatever copy is open first, which is the part that
is easy to forget: only one copy runs per user, so starting a new one while an old one is open hands
the request to the old one and nothing on screen changes. `-Headless` and `--headless` start it
without a window, `-Connect <name>` and `--connect <name>` connect a profile once it is up, and
`-NoBuild` and `--no-build` skip straight to starting what is already built.

The macOS one also names where .NET is, which a build from the source tree needs and an installed
build does not. An SDK unpacked into a home directory is found through `DOTNET_ROOT` and nowhere
else, and without it the application prints that .NET is not installed and exits, which reads like
the application failing rather than the shell missing a variable.

The build output is where `dotnet` puts it:

| | Windows | macOS |
| --- | --- | --- |
| Application | `src/OpenVpnPilot.App/bin/Debug/net10.0/OpenVpnPilot.exe` | `.../net10.0/OpenVpnPilot` |
| Command | `src/OpenVpnPilot.Cli/bin/Debug/net10.0/ovp.exe` | `.../net10.0/ovp` |
| Installer payload | `artifacts/install`, written by `installer/build.ps1` | `artifacts/install`, written by `installer/build-macos.sh` |
| Installer | `artifacts/release/OpenVpnPilot-<version>-win-x64.msi` | `artifacts/release/OpenVpnPilot-<version>-osx-arm64.dmg` |

### Building on macOS

```bash
bash installer/build-macos.sh
```

That produces both installers, and on the way it builds the OpenVPN the helper package carries:

```bash
bash installer/build-openvpn-macos.sh
```

The sources are fetched from the projects that publish them and pinned by checksum: OpenVPN 2.7.7,
OpenSSL 3.6.4, lzo 2.10 and lz4 1.10.0. Everything is linked statically and the result is checked to
use nothing but the system libraries, which is the point of building it at all. The name server hook
is compiled in as `/Library/PrivilegedHelperTools/openvpnpilot/dns-updown`, which is the helper, and
a test holds that path and the one in the code together.

Why it has to be compiled in rather than passed as an option: OpenVPN runs a command named by
`--dns-updown` as a user script, which requires `--script-security 2`, and runs the one compiled in as
the default at level 1. The helper forces level 1 and puts that option after the configuration, so no
configuration can raise it. Pushed name servers therefore work, and no configuration can make root run
anything of its own.

OpenVPN is GPLv2. The package carries its licence, the build script and a record of every source that
went into it, so what is distributed can be built again from what is named.

| | |
| --- | --- |
| Application | `artifacts/release/OpenVpnPilot-<version>-osx-arm64.dmg` |
| Helper | `artifacts/release/OpenVpnPilot-Helper-<version>-osx-arm64.pkg` |
| The OpenVPN build | `artifacts/openvpn/stage/`, reused until `--force` |
| The .NET used | the official SDK, at `~/.dotnet/dotnet` when it is there |

The last row matters. A .NET from a package manager can be a source build that links that manager's
libraries, and one of the two things shipped here runs as root.

### The artwork

Everything visual comes out of one folder, `assets/artwork`, and one command draws all of it from
geometry rather than from images somebody once exported, so each is sharp at every size the system
asks for:

```bash
dotnet run --project tools/artwork
```

| | |
| --- | --- |
| `OpenVpnPilot.icns` | The macOS application icon: the mark on a light tile, in the ten representations an icns is expected to carry |
| `OpenVpnPilot.ico` | The same icon for Windows, in eight sizes, which the executable and the windows both use |
| `status-item.png` | The menu bar entry on macOS: black shapes on transparency, which the menu bar tints itself |
| `dmg-background.png` | The background of the disk image window |

All four are committed, and the projects link them from there rather than keeping copies. Below 32
points the tile is dropped and the mark is drawn on its own: a tile and a mark inside it at 16 points
leave the mark ten points across and the dot in its middle two, and what survives at that size is the
mark filling the square.

It runs on Windows as well as macOS, which is why it is a .NET program drawing with Skia rather than
anything of either system: `System.Drawing` is Windows only and AppKit is macOS only, and the same
four files have to come out of either. Avalonia already draws the interface with Skia, so the same
renderer draws what the interface is labelled with. Both container formats are written by hand, the
`icns` because `iconutil` exists only on macOS and the `ico` because nothing in the toolchain draws
one at all.

The project is deliberately not in the solution, for the same reason the installers are not: an
ordinary build has no business producing artwork, and what it writes is committed.

One thing about the `icns` is worth not rediscovering. The one point entries for 16 and 32 points,
`icp4` and `icp5`, are left out. They predate PNG in that format and are read as raw pixels by tools
that expect the older meaning, `iconutil` among them, which turns them into noise; macOS itself reads
them correctly, so the file looks right and the tooling looks broken. The system scales the two point
entries down for a display that does not double, and that is the same drawing.

The positions the disk image window places its two icons at are in `installer/build-macos.sh` and the
background is drawn for those positions, so changing one means changing the other. Arranging that
window is the Finder's job, which is why the build mounts a writable image, tells the Finder what the
window should look like, and only then compresses it.

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

Issues and pull requests are welcome, including the small ones: a wrong translation, a confusing
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

This repository was written with the help of Claude Opus 5, at maximum reasoning effort, which is
also where the `CLAUDE.md` at the root comes from. It is not a prompt; it is the standard the code
is held to.

None of this is vibe code. I was writing C# long before a model could write any of it, and I know
what belongs in a codebase and what does not. The model is the faster typist; it does not decide
what is right.

Judge it by the code.
