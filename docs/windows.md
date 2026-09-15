# OpenVPN Pilot on Windows

[OpenVPN Pilot](../README.md) · Windows · [macOS](macos.md) · [Using it](usage.md) · [The `ovp` command](cli.md) · [Working on it](development.md)

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

The MSI is in the releases of this project. It is not code signed, so SmartScreen shows "Windows
protected your PC" the first time: **More info**, then **Run anyway**. Everything below builds the
same installer from the source instead, which is also how a release is made:

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

## Deploying it with group policy

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

## Where things are kept

Everything the application writes belongs to the user running it, so an installation for the whole
machine still keeps each person's profiles apart.

| | |
| --- | --- |
| Profiles, tags and history | `%LOCALAPPDATA%\OpenVpnPilot\pilot.db` |
| Settings | `%LOCALAPPDATA%\OpenVpnPilot\settings.json`, editable by hand |
| Credentials | `%LOCALAPPDATA%\OpenVpnPilot\secrets\`, one protected file each |
| Logs | `%LOCALAPPDATA%\OpenVpnPilot\logs\`, one `yyyy-MM-dd_HH.log` per hour, seven days and a gigabyte at most by default |
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
| `HKCU\Software\Classes\AppUserModelId\OpenVpnPilot` | the application | The name and icon Windows shows above a notification. Without it the notification centre labels every message with a generated identifier. |
| `HKLM\Software\Classes\OpenVpnPilot.ovpn` and `.ovppkg` | the installer | The program identifiers that put the application in the Open with menu for a configuration, and make it the handler for its own package format. The default for `.ovpn` is deliberately left alone. |
| `HKLM\Software\OpenVpnPilot` | the installer | Two markers so the Start menu entry and the PATH entry can be removed again. |
| `HKLM\...\Uninstall\<product code>` | Windows | The entry under Apps and features. |
| `HKLM\SOFTWARE\OpenVPN` | nobody, it is only read | Where OpenVPN Community says it is installed, and which group the interactive service authorises. |

Nothing else is written to the registry. Removing the product removes the two keys the installer
made; the profile store and the credentials are deliberately left alone, because uninstalling an
application is not the same as asking it to forget everything.
