# Settings

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · Settings · [Server](server.md) · [The `ovp` command](cli.md) · [Working on it](development.md) · [Testing](testing.md)

Everything a person can configure is one JSON file, and the settings screen is a form over it.

## The settings screen

| Tab | What it holds |
| --- | --- |
| General | The language, the theme, starting with the system, starting with the window hidden, whether closing the window keeps the tunnels running, whether the application is shown in the Dock while a window is open (macOS only), and whether the profile editor opens on the form or on the plain configuration |
| Connections | Ignoring pushed routes and DNS, reconnecting a tunnel that drops, the attempts and the delay, the connection timeout, and reconnecting the tunnels that were up when the application closed |
| Shortcuts | The global shortcuts, which are recorded by pressing them. A combination another shortcut or another application already owns is reported rather than failing silently |
| Notifications | Notifications as a whole, and each of connecting, connected, disconnected, connection lost, reconnecting and failed |
| Credentials | Using stored credentials, ticking remember by default, how many are stored, and forgetting them |
| Storage | Where the profiles live, and with a server its status, **Sync now**, **Sign out** and **Sign in**, see [server](server.md) |
| Profiles | Import and export, and reloading the list after the store was changed from somewhere else, such as `ovp` |
| Advanced | The OpenVPN verbosity, the log level, how long and how large the logs may grow, the check for a newer release, clearing the connection history, and writing a diagnostics bundle |

**Forget all stored credentials** removes everything the keystore holds, which on the library on this
computer includes the session of a server. With a server the button reads **Forget this server's stored
credentials**, signs out first, and removes this server's stored sign ins only, see
[synchronisation](server-sync.md#signing-out-and-changing-accounts).

The **diagnostics bundle** is one archive for a support request: the environment report, the settings, a
profile listing without configurations, `storage.txt`, and the logs. It contains no configurations,
private keys or credentials, and it is not anonymous: it names profiles, hosts and file paths that
include the user name. Its `settings.json` is the settings file as it stands, which with a server names
the server's address and carries the installation identity. Read a bundle before sending it on.

## The file

It is `settings.json` in the data directory, which [Windows](windows.md#where-things-are-kept) and
[macOS](macos.md#where-things-are-kept) name. It is created with the defaults at the first start, together with
the installation identity.

- Keys are camel case, names are matched without regard to case, enums are written as words, and
  comments and trailing commas are accepted when reading.
- The application reads the file once, at start, and does not watch it. A change made by hand while it
  runs is overwritten by the next save, so edit it while the application is closed. Saving rewrites the
  whole file, comments included, through a temporary file and a move, so an interrupted save cannot leave
  half a file.
- A key that is missing takes its default, so a file from an older version needs nothing. `schemaVersion`
  exists for the other case, where the default of an existing setting changed: without a stamp a value
  somebody chose cannot be told from one that was merely written down.
- A file that does not parse is moved aside as `settings.json.invalid` and the defaults take over.
- A file that cannot be opened is tried five times, a tenth of a second apart, which outlasts a scanner
  or a synchronisation client holding it. If it stays out of reach, the defaults apply for that run
  only: nothing is written to the file, so the defaults cannot replace what it holds, and no installation
  identity is made. With a server, the status bar and a banner say that the settings could not be read,
  and nothing is asked of the server until the application starts again.

## The keys

Defaults are given as they stand in a new file.

| Key | Default | Meaning |
| --- | --- | --- |
| `schemaVersion` | `2` | The layout the file was written by |
| `general.language` | `null` | A code such as `en` or `de`; `null` follows the system |
| `general.startMinimised` | `false` | Start with the window hidden |
| `general.startWithSystem` | `false` | Start with the system, see the platform pages |
| `general.closeToTray` | `true` | Closing the window keeps the tunnels running |
| `general.showInDock` | `true` | macOS only: whether a window puts the application in the Dock |
| `general.quickMenuScreen` | `null` | The display the quick menus open on; `null` uses the one the pointer is on |
| `general.mainWindow` | `x`, `y`, `width`, `height`, `maximised` | Where the main window was left |
| `general.profileEditor` | `"Form"` | `"Form"` or `"PlainText"` |
| `appearance.theme` | `"System"` | `"System"`, `"Light"` or `"Dark"` |
| `connections.protectRoutes` | `true` | Ignore pushed routes and DNS; a profile can override it |
| `connections.connectTimeoutSeconds` | `60` | `0` waits forever |
| `connections.autoReconnect` | `true` | |
| `connections.maxReconnectAttempts` | `5` | `0` never gives up |
| `connections.reconnectDelaySeconds` | `5` | Before the first retry; later ones back off |
| `connections.restoreOnStart` | `false` | Reconnect what was up when the application closed |
| `notifications.enabled` | `true` | The switch for all of them |
| `notifications.onConnecting`, `onConnected`, `onConnectionLost`, `onFailed` | `true` | |
| `notifications.onDisconnected`, `onReconnecting` | `false` | |
| `credentials.rememberByDefault` | `true` | Tick remember in the prompt |
| `credentials.useStoredSecrets` | `true` | Off makes every connection ask |
| `advanced.openVpnVerbosity` | `3` | Passed as `--verb`, from 0 to 11 |
| `advanced.logLevel` | `"Information"` | `"Verbose"`, `"Debug"`, `"Information"`, `"Warning"` or `"Error"` |
| `advanced.logRetentionDays` | `7` | `0` keeps log files until somebody removes them |
| `advanced.logMaximumMegabytes` | `1024` | `0` sets no limit; the oldest files go first |
| `advanced.checkForUpdates` | `true` | Contacts GitHub, see [using it](usage.md#looking-for-a-newer-release) |
| `advanced.updateRepository` | `"OpenVPN-Pilot/OVP-Client"` | `owner/name`; empty checks nothing |
| `advanced.openVpnPath` | `null` | Shown in the settings screen and saved, but nothing reads it: OpenVPN is found through the registry on Windows, and the macOS helper runs the OpenVPN it carries |
| `advanced.portableMode` | `false` | Not read by anything: the data directory is always the user's application data directory |
| `storage.mode` | `"Local"` | `"Local"` or `"Server"`, read before anything else starts |
| `storage.serverUrl` | `null` | The server's address, kept when switching back to this computer |
| `installation.id` | generated | A random identity made once, which a server binds its tokens to |

The favourites, their slots and the shortcuts are not in this file: they are in the database, and they
follow a person from a server separately from the settings.

## What a package and a server carry

A package can carry the settings, and a server hands the portable part to a person's every computer, in
the same form: this file without what describes a computer. These are cleared on the way out and kept
from the receiving computer on the way in:

- `general.mainWindow` and `general.quickMenuScreen`, which belong to the displays here;
- `general.startWithSystem`;
- `advanced.openVpnPath` and `advanced.portableMode`;
- `storage`, because taking another computer's mode from a server would switch this one to a store
  nobody chose here;
- `installation`, because taking another computer's identity would make two computers one to the
  server.

Everything else is portable, `general.showInDock` and the update repository included. A package that
cannot be read as settings applies nothing rather than half of it, and a package written by a newer
version than the one opening it is refused.
