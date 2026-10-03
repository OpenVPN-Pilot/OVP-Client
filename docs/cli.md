# The `ovp` command

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [Settings](settings.md) · [Server](server.md) · The `ovp` command · [Working on it](development.md) · [Testing](testing.md)

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
ovp add ~/profiles --commit --tag production
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

```bash
ovp completion zsh --install
```

`bash` is offered as well. On macOS `zsh` is the default shell, and the script is installed into
zsh's own completion system rather than through bash's.

The installer puts `ovp` on PATH. Without it, the command can also be installed as a .NET tool:

```bash
dotnet pack src/OpenVpnPilot.Cli -c Release
```

```bash
dotnet tool install --global --add-source artifacts/packages OpenVpnPilot.Cli
```

## The commands

| Command | Aliases | What it does |
| --- | --- | --- |
| `doctor` | `dr` | Checks whether OpenVPN, or on macOS the helper, is installed and usable, one requirement at a time |
| `start [--headless]` | `open`, `up` | Starts the application, with or without a window |
| `stop` | `quit`, `exit` | Ends the application and its tunnels |
| `list [--json] [--names] [--tag <name>]` | `ls` | Lists the stored profiles |
| `status` | `st` | Shows what the running application has connected |
| `connect <name or file> [options]` | `con`, `conn` | Connects a profile, see below |
| `disconnect <name>` or `--all` | `dis` | Disconnects a tunnel the running application owns |
| `import <path> [--commit] [--tag <name>]` | `add` | Examines `.ovpn` files, folders and ZIP archives, and with `--commit` stores them |
| `export <directory> [--profile <name>] [--tag <name>]` | | Writes profiles out as `.ovpn` files, in a directory only the current user can read |
| `pack <file> --passphrase <value> [--profile <name>] [--tag <name>] [--with-credentials]` | | Writes an encrypted `.ovppkg` package |
| `unpack <file> [--commit] [--passphrase <value>]` | | Reads a package, and with `--commit` stores it |
| `favourite <name> [--slot <1-9>] [--clear]` | `fav`, `favorite` | Sets or clears a favourite and its slot |
| `remove <name> [--yes]` | `rm`, `delete` | Deletes a profile and its history |
| `completion <powershell\|bash\|zsh> [--install]` | | Prints or installs a completion script |
| `help [command]`, `version` | `--help`, `-h`, `--version`, `-v` | |

`connect` also takes `--headless` (when the application has to be started, start it with no window),
`--seconds <n>` (how long to stay connected when the command itself holds the tunnel, 30 by default),
`--protect-routes`, `--username`, `--password`, `--challenge` (the answer for a one time code of either
kind) and `--detached` (do not hand the request to the running application). Naming a configuration file
instead of a stored profile connects from the command alone, printing each state change until the time is
up, which is for trying a file that has not been imported.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Done. `doctor` is ready, `status` has something connected, a connection came up |
| 1 | The command was not understood, nothing matched the name, there is no such package, or `status` has nothing connected |
| 2 | `doctor` found something that blocks connecting, or a launch was refused |
| 3 | A connection attempt failed, or a package could not be opened |
| 4 | Nothing was listening, or the application could not be started |
| 5 | `connect` from a file ended without ever connecting |
| 6 | The profile store could not be read or written, most often because the application is writing to it at that moment |
| 7 | A command that changes the store was refused because the profiles are a server's, see [With a server](#with-a-server) |

## Driving it from other software

`ovp` is the only thing anything else has to call. A program that needs a tunnel up before it starts
working needs one command, and it must not matter whether anyone had the
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
and `ovp status` reports what is connected, one line per tunnel.

## The application itself

The application understands the same verbs directly, for a shortcut or a scheduled task that starts it:
`OpenVpnPilot.exe` on Windows, or the executable inside the bundle on macOS. It is a windowed program, so
it writes to a terminal only when it was started from one.

| Option | What it does |
| --- | --- |
| `<file>` | A bare argument is imported, which is what a double click on a `.ovpn`, a ZIP archive or a `.ovppkg` package arrives as |
| `--headless` | No window and no notification area entry; the tunnels are driven by whatever launched it, or by `ovp` |
| `--background`, `--minimised`, `--minimized` | The window starts hidden and the notification area entry brings it back |
| `--connect <profile>` | Connects a profile; may be repeated |
| `--disconnect <profile>` | Disconnects a profile; may be repeated |
| `--disconnect-all` | Disconnects everything |
| `--quit` | Ends the copy that is running; starts nothing when none is |
| `--after-restart <pid>` | Waits for that process to end, up to thirty seconds, before claiming the single instance |
| `--first-sync` | Continues setting up a server by running the first synchronisation before the window appears |
| `-h`, `--help` | Prints the options |

Only one copy runs per user. A second launch hands its options to the copy that already runs and exits,
so the same command line means the same thing whether or not the application was open. Without any
action, that brings the window forward. Disconnecting is carried out before connecting, so
`--disconnect-all --connect x` reads as switching to `x`. An option the application does not know is an
error rather than a file name.

Exit codes are 0 for done, 1 when the options were not understood and 4 when actions were asked for and no
copy was listening to carry them out.

`--after-restart` and `--first-sync` are not for people. The application passes them to itself when it
restarts to switch between this computer's profiles and a server, see [server](server.md#switching). The
new copy is started while the old one still holds the single instance claim, so without waiting it would
find the old copy running, hand over to it and exit, and the restart would end with no application at
all. If the old copy outlasts the thirty seconds, the new one hands over to it like any second start.

## With a server

When the application works with an OpenVPN Pilot Server, the profiles on the machine are a copy of
that server's, kept in step by the application. `ovp` reads the same copy the window shows, so
`list`, `connect`, `export`, `pack`, `status` and completion behave exactly as they do with the
profiles kept on the computer.

What would change the copy is refused, with exit code 7 and a sentence saying why: `import --commit`,
`unpack --commit`, `favourite` and `remove`. The application records every change it makes so the
change reaches the server; a change written by `ovp` would be recorded nowhere, would never reach the
server and would be overwritten by the next synchronisation. Make the change in the application
instead. A dry run, `import` or `unpack` without `--commit`, still reports what it would do.

`ovp` never talks to the server itself.
