# Working with a server

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [Settings](settings.md) · Server · [The `ovp` command](cli.md) · [Working on it](development.md) · [Testing](testing.md)

[Signing in](server-signing-in.md) · [Synchronisation](server-sync.md)

OpenVPN Pilot can keep its profiles on an [OpenVPN Pilot Server](https://github.com/OpenVPN-Pilot/OVP-Server)
instead of only on the computer it runs on. The server holds the profiles, their tags and the sign ins
people share for them, and each person's favourites, shortcuts and portable settings. How to set a
server up and run it is in that repository.

Nothing about the tunnels changes. They are made on this computer by the same `openvpn` as before, so
no traffic passes through the server. What changes is where the list of profiles comes from.

Three pages cover it:

- This page: what server mode is, how to start using it, and how to switch.
- [Signing in](server-signing-in.md): the ways a server lets people in, what the messages mean, and
  what a role allows.
- [Synchronisation](server-sync.md): what is kept in step, what happens without the network, how
  conflicts are settled, and how signing out and withdrawn accounts work.

## Where the profiles live

They live in one of two places and never in both: on this computer, as they always have, or on one
server. The choice is the `storage` section of the [settings file](settings.md#the-file): `mode` is
`Local` or `Server`, and `serverUrl` names the server. It is read once, before anything else starts,
so changing it means starting the application again, which is what switching does.

With a server, the application works on a copy kept on this computer. Listing, searching, connecting
and the history all use the copy, which is why they keep working when the server cannot be reached.
The copy is an ordinary profile library in a SQLite file of its own, `servers/<key>/pilot.db` under
the data directory, one folder per server. It holds the configurations as the local library does, so
it is as sensitive as the local library is.

## Starting to use a server

**On the first start.** The very first start of a copy that shows a window asks where the profiles
should live: **This computer**, **A server**, or **Decide later**, which is the same as the first and
leaves the choice to the settings. Only a true first start asks, and the settings file not existing yet
is the signal. An installation that updates has a file and is never asked. A copy started with
`--headless` is not asked either, and since starting it creates the file, none of the later starts is
a first start.

**From the settings.** **Settings, Storage, Switch to a server** runs the same steps. Every step can be
left again, and nothing is written before the last one has worked.

1. **The address.** `https://pilot.example.com`, or with a port such as `https://pilot.example.com:8443`.
   A bare host name is read as `https://`, and plain `http://` is refused with the reason, because
   passwords, keys and sign ins travel over this connection. An address carries no user name, no
   password, no path and no query. The host is lower cased, so every spelling of one address is one
   server. A server published under a path cannot be used, because the client reaches the API from
   the root of the address.
2. **The check.** The application asks the server about itself and shows its name, its version and
   how it signs people in, or the precise problem. [Signing in](server-signing-in.md) lists them.
3. **The sign in.** The form is the one the server asks for.
4. **The restart.** Once the server accepted the sign in, the mode and the address are written to the
   settings and the application starts again. A sign in that fails leaves everything as it was.
5. **The first synchronisation.** The new copy confirms the session and fetches the profiles with
   its progress on screen, and the main window appears when they are there. If that fails, the window
   offers to try again, to go back to the sign in (which signs out first, so somebody else can sign in),
   or to use this computer instead.

## Switching

**Settings, Storage** offers **Switch to a server**, **Switch to another server** and **Switch to this
computer**.

- Switching is refused while a tunnel is up or coming up. The tunnels shown belong to the store being
  left, and ending a connection as a side effect of a setting is not something to do quietly.
- It asks for a confirmation, restarts the application and deletes nothing. The profiles on this
  computer and the copy of every server stay where they are, and switching back finds them as they
  were left. The restart passes `--after-restart` to the new copy, which waits for the old one to end
  before it claims the single instance, see [the `ovp` command](cli.md#the-application-itself).
- Another address is another server, with a copy and a session of its own. The address that was in
  use stays in the settings when switching to this computer, so switching back offers it again.
- A settings file edited by hand to name a server whose address cannot be used opens the library on
  this computer instead, and the log says why.

## What is shared and what stays here

| Comes from the server and is the same for everybody | Follows the person to every computer they sign in on | Stays on this computer |
| --- | --- | --- |
| Profiles: name, configuration, notes, colour, route protection and tags | Favourites and their slots | The connection history |
| Tags | Shortcuts | When a profile was last connected, and how often |
| The sign ins shared for profiles | The portable settings | Window positions, the OpenVPN path, autostart and the quick menu display |
| | | The settings that say where the profiles live, and the installation identity |
| | | The session with the server, in the keystore |

What follows a person is replaced as one piece: the list of favourites, the list of shortcuts and the
settings document. The [settings page](settings.md#what-a-package-and-a-server-carry) says which settings are portable.

## Who may change what

The server gives every person a role. Everybody connects, keeps their own favourites, shortcuts and
settings, and can add a sign in the server does not have yet. An administrator can also import, edit
and delete profiles and tags and replace a shared sign in. The window offers a person only what their
role allows, with the role as it was last known, so it is the same without the network. The details are
under [roles](server-signing-in.md#roles).

## What the window shows

- **The status bar** shows the server, how fast it answers, how long ago the copy was brought up to date
  and how many changes are waiting, such as `pilot.example.com · 23 ms · synced 2 min ago · 3 changes
  waiting`. A grey **Local** stands there instead when the profiles live on this computer.
  The dot is green when synchronised, blue while synchronising, amber when the server cannot be
  reached or is not ready, or when changes are waiting, and red for what only the person or the server's
  operator can put right: a sign in is needed, the client is too old, the clock is refused, the
  certificate is not trusted, or the settings could not be read.
- **Pointing at it** shows the address, the server's version, who is signed in, the cursor, the last
  pull and push and the last error with its request id. **Clicking it** offers **Sync now**, **Sign in
  again** when that is needed, **Show server log** and **Open storage settings**.
- **A banner** appears over the list for what blocks the synchronisation: a sign in that is needed, a
  client too old for the server, a clock the server refuses, a certificate that is not trusted, or
  settings that could not be read. The banner for a client that is too old offers the page of the
  release repository named under **Settings, Advanced**. Being offline is not one of them: the
  application keeps working from the copy.
- **The notification area menu**, and the application menu on macOS, carry the same line and **Sync now**.
- **Settings, Storage** shows the address, who is signed in with which role and provider, the server's
  version, the last synchronisation and the number of changes waiting, with **Sync now**, **Sign out**
  or **Sign in**, and **Show server log**. Closing the page stops a synchronisation it started and the
  schedule carries on.
- **The log window** has **Server only** among its source filters, which shows what the application
  did with the server: calls, signing in, the synchronisation, waiting changes and switching. Every
  failure there carries the request id the server's operator can find in the server's own log.
  **This application only** includes those lines as well.
- **The diagnostics bundle** has a `storage.txt` that says where the profiles live and, for a server,
  its host, its version and API version as it answers then, the last pull and push, the cursor, the
  last error code with its request id, the role and provider, and how many changes wait with the codes
  they met. The bundle's `settings.json` is the settings file as it stands, so it names the server
  address and the installation identity too; read a bundle before sending it on.

## The command line

`ovp` reads the same copy the window shows and never talks to the server. What would change the copy
is refused with exit code 7, see [the `ovp` command](cli.md#with-a-server).

## What contacts the server

Only the server the person chose, over HTTPS, with the operating system deciding which certificates
are trusted and no setting that weakens that. Redirects are not followed.

- `GET /api/v1/server/info` and `GET /health/ready`, anonymously, every 30 seconds while the server
  answers. While it does not, the wait grows to 60 and then 120 seconds. They carry nothing about the
  person and give the round trip shown in the status bar.
- Everything else carries the client's version, the API version, the installation identity, the
  platform (`windows` or `macos`), the time in UTC and a fresh request id in `X-Pilot-*` headers. The
  installation identity is a random value made once and kept in the settings. The server binds its
  tokens to it, so it is never exported and never taken from an import, and a settings file should not
  be copied from one computer to another.
- The profiles, tags, shared sign ins, favourites, shortcuts and settings described under
  [synchronisation](server-sync.md#what-is-synchronised).

The only other thing the application contacts is GitHub, for the [check for a newer
release](usage.md#looking-for-a-newer-release), which can be switched off.

## Known limits

- One server at a time. Another one is a switch, and the copy of the first stays on disk.
- The Microsoft sign in through the system browser has never been run on a Mac, see
  [signing in](server-signing-in.md#microsoft).
- The hand over to the new copy during a restart, which depends on the single instance claim being
  released in time, has not been measured end to end.
