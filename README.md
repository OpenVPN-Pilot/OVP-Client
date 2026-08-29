# OpenVpnPilot

A desktop client for OpenVPN built for people who manage a lot of profiles.

The stock OpenVPN GUI on Windows is a tray icon with a flat, unsearchable list. That works for two or
three connections. It stops working somewhere around twenty. OpenVpnPilot keeps the proven OpenVPN
process doing the tunnelling and replaces the interface around it.

> **Status: early development.** The integration layer and the interface are proven end to end
> against OpenVPN Community 2.7.6, but there is no installer yet and nothing has been released.
> See [Roadmap](#roadmap).

## Features

- Instant search across profile name, tag and remote host
- A quick switcher: one global shortcut, type a few letters, press return
- Tags and favourites with numbered slots bound to shortcuts
- Global shortcuts for connect, reconnect, disconnect and the favourite slots
- Several tunnels connected at once, each with its own live telemetry
- Live figures per tunnel: throughput, uptime, round trip, assigned address, pushed routes and DNS
- Credentials kept in the operating system keystore, never in a file on disk
- One time codes, both the kind presented up front and the kind raised after a refusal
- Session history with durations and transfer volumes, exportable as CSV
- Bulk import from files, folders and ZIP archives, plus watched folders that keep profiles in sync
- Export as plain configurations, or as an encrypted `.ovppkg` package for another machine
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

## Building

```bash
dotnet build
```

```bash
dotnet test
```

Requires the .NET 10 SDK. The user interface is built with Avalonia.

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

`ovp help <command>` explains one command. `ovp completion powershell` prints a completion script that
offers the stored profile names.

The command can be installed so it lands on PATH:

```bash
dotnet pack src/OpenVpnPilot.Cli -c Release
```

```bash
dotnet tool install --global --add-source artifacts/packages OpenVpnPilot.Cli
```

## Organising a set

There are no folders. A profile carries as many tags as it needs, the sidebar lists them, and the
search box matches a tag along with the name and the remote host. One profile can belong to as many
groupings as make sense, which a tree cannot express, and nothing has to be maintained by hand.

Profiles a watched directory brings in appear under **New** until they are marked as seen, so an
automatic import never drops them unannounced into the middle of the list.

## Adding a language

Language files are JSON. The ones that ship live in `lang` beside the executable, and anything placed
in `lang` under the application data directory is layered on top of them, key by key. Copy `en.json`,
translate the values, drop it in and reload from the settings screen: no rebuild, and a key you have
not translated falls back to English rather than disappearing.

## Architecture

| Project | Responsibility |
| --- | --- |
| `OpenVpnPilot.Core` | Domain model, abstractions, localization, settings, update check |
| `OpenVpnPilot.OpenVpn` | Management interface protocol, `.ovpn` parsing, connection supervision |
| `OpenVpnPilot.Data` | SQLite persistence, import, portable packages |
| `OpenVpnPilot.Platform.Windows` | Interactive service client, secret storage, notification area, shortcuts |
| `OpenVpnPilot.App` | Avalonia user interface and the services that drive it |
| `OpenVpnPilot.Cli` | `ovp`, a headless companion command |

Each tunnel runs as its own `openvpn` process with its own management interface on a loopback port.
Credentials are supplied over that interface and are never written to disk. `Core`, `OpenVpn`, `Data`
and `App` contain no platform specific code, so support for another operating system means adding an
implementation of the existing interfaces rather than restructuring the application.

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
- [ ] Installer, signed releases and an update feed
- [ ] Kill switch
- [ ] macOS
