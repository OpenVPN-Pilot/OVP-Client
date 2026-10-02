# The `ovp` command

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · The `ovp` command · [Working on it](development.md)

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
command that was not understood, 4 when nothing was listening, 6 when the store could not be read,
and 7 when a command that changes the store was refused because the profiles are a server's.

The application understands the same options directly, for a shortcut or a scheduled task that
starts it: `--headless`, `--background`, `--connect`, `--disconnect`, `--disconnect-all` and
`--quit`. Only one copy runs per user, so a second launch hands its options to the copy that already
runs and exits.

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
