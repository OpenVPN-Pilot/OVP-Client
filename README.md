# OpenVpnPilot

A desktop client for OpenVPN built for people who manage a lot of profiles.

The stock OpenVPN GUI on Windows is a tray icon with a flat, unsearchable list. That works for two or
three connections. It stops working somewhere around twenty. OpenVpnPilot keeps the proven OpenVPN
process doing the tunnelling and replaces the interface around it.

> **Status: early development.** The OpenVPN integration layer has been proven end to end against
> OpenVPN Community 2.7.6, but the application itself is not usable yet. See [Roadmap](#roadmap).

## Planned features

- Instant search across profile name, folder, tag and remote host
- Folders, tags and favourites with numbered slots
- A quick switcher: one global hotkey, type a few letters, connect
- Bulk import of `.ovpn` files, plus watched folders that keep profiles in sync
- Freely assignable global hotkeys for connect, reconnect, disconnect and favourite slots
- Several tunnels connected at once, each with its own live telemetry
- Live dashboard with throughput, ping, assigned addresses and pushed routes
- Session history with durations and transfer volumes, exportable as CSV
- Credentials kept in the operating system keystore, never in a file on disk
- Import and export packages for sharing a profile set between machines
- Dark, light and system themes, autostart, auto reconnect and multiple languages

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

## Architecture

| Project | Responsibility |
| --- | --- |
| `OpenVpnPilot.Core` | Domain model, abstractions, connect middleware pipeline |
| `OpenVpnPilot.OpenVpn` | Management interface protocol, `.ovpn` parsing, connection supervision |
| `OpenVpnPilot.Data` | SQLite persistence, repositories, import and export |
| `OpenVpnPilot.Platform.Windows` | Interactive service client, secret storage, hotkeys, autostart |
| `OpenVpnPilot.App` | Avalonia user interface and tray integration |
| `OpenVpnPilot.Cli` | `ovp`, a headless companion command |

Each tunnel runs as its own `openvpn` process with its own management interface on a loopback port.
Credentials are supplied over that interface and are never written to disk. `Core`, `OpenVpn`, `Data`
and `App` contain no platform specific code, so support for another operating system means adding an
implementation of the existing interfaces rather than restructuring the application.

## Roadmap

- [x] Prove the interactive service and management interface integration end to end
- [x] Solution structure and coding standards
- [x] Configuration parsing and inlining, management client, interactive service launcher
- [x] `ovp doctor` and `ovp connect`
- [x] Connection supervisor with credential handling
- [x] SQLite store, schema and profile import with duplicate detection
- [x] User interface with profile list, search, live status and tray icon
- [ ] Folders, favourites, search, quick switcher and import
- [ ] Hotkeys, notifications, dashboard and session history
- [ ] Localization, autostart, packaging and releases
