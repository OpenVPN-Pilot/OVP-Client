# Using OpenVPN Pilot

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · Using it · [The `ovp` command](cli.md) · [Working on it](development.md)

## Organising a set

There are no folders. A profile carries as many tags as it needs, the sidebar lists them, and the
search box matches a tag along with the name and the remote host. One profile can belong to as many
groupings as make sense, which a tree cannot express, and nothing has to be maintained by hand.

## Working on more than one at a time

![The quick switcher filtering the list as letters are typed](../assets/screenshots/quick-switcher.png)

**Select several**, above the list, puts a checkbox on every row and turns into **Done selecting**
until it is pressed again. Tick some and connect, disconnect or delete them together; **Select all
shown** ticks what the current filter and search leave visible, which is how a whole tag is brought up
in one go. Deleting asks a second time, because it is the one action here that cannot be undone.
Profiles are connected one after another rather than all at once: each tunnel is a process, an
adapter and a port, and twenty starting in the same instant is how a machine runs out of all three.

A single profile is deleted from the detail panel, which asks first and names the profile it is
about. Its notes are shown there too, under when it was last used, and can be selected and copied.

There is no built in limit on how many tunnels run at once, and ten at a time is what the lab is for.
The real limits are outside the application: one OpenVPN process and one virtual adapter per tunnel,
and the adapter pool is what runs out first. A tunnel that cannot come up is given a minute and then
abandoned, so a saturated machine reports what happened instead of leaving processes behind.

## Changing a profile

**Edit** in the detail panel opens the profile. The form names what is changed by hand: the name, the
server with its port and protocol, the tags, the shortcut slot, whether pushed routes are accepted,
the notes, and the certificates and keys. Changing a field replaces the lines that field is about
and leaves the rest of the configuration exactly as it was, comments and ordering included. A
configuration that names several servers has the first one in the form.

**Edit as plain text** shows the whole configuration as OpenVPN reads it, for anything the form does
not name. Switching back reads what was typed into the form again, and whichever is showing is what
is saved. The general page of the settings decides which of the two the editor opens with.

Every change is checked as it is made, with the same checks for both views. What OpenVPN would refuse
is marked in red and stops a save: no server, a port or protocol it cannot read, nothing to verify the
server with, a block that is never closed, a certificate or key pasted into the wrong block, and a
configuration another profile already has. A reference to a file on disk and a directive that runs a
program are marked in amber and do not stop it. A connected profile keeps running with what it
started with, and uses the changed configuration from the next connection on.

Return saves from any single line field and escape closes without saving. The notes, the keys and the
plain text keep return for a new line.

## Moving a set to another machine

**Export** writes the ticked profiles, or every profile carrying the tags chosen there, either as plain
`.ovpn` files or as one encrypted `.ovppkg` package. A package can also carry the shortcuts, the
settings and the saved sign ins of those profiles, each of them chosen on its own. What belongs to
the machine is never in it: window positions, the OpenVPN path, autostart and a shared library.

Opening a package asks for its passphrase and then lists what it carries: every profile, marked as
new or as already stored under a name, and under them the sign ins, the shortcuts and the settings.
Nothing that is not ticked is taken. A shortcut is added only where this machine binds neither the
action nor the combination yet, and the settings replace this machine's only when that is ticked. A
package written by a newer version than the one opening it is refused rather than read in part.

## Sharing a library between machines

A library can live in one encrypted file that several machines use together, so that a profile
imported or changed on one of them arrives on the others. It is meant for a folder a sync client
keeps in step, such as one OneDrive synchronises: the application reads and writes a file, and
carrying that file between machines is the sync client's job.

Under **Settings, Profiles**, **Create a library...** writes this machine's profiles and their sign ins
into a new file and asks for the passphrase every other machine will need. Pass it on separately from
the file. **Use an existing library...** picks that file on another machine and asks for the
passphrase once. The passphrase is kept in the operating system's protected storage beside the
stored sign ins, and never in the database, the settings, a log or an export.

A machine uses a shared library or a library of its own, never a mixture. Joining therefore
**replaces** the profiles on that machine, with their sign ins, and adds none of them to the shared
file, where everybody else would find them. When the machine has profiles, the settings say how many,
offer **Export them first...**, and go on to choosing the file only once the box saying this was
understood is ticked. A profile the library holds with the same configuration is taken as the same
profile, and keeps its favourite mark, its shortcut slot and its history. Joining is refused while a
tunnel is connected, because its profile might be one that is replaced.

| Shared | Stays on each machine |
| --- | --- |
| Every profile, with its configuration, name, tags, notes, colour and route protection | Favourites and their shortcut slots |
| The saved sign ins of those profiles | Shortcuts, settings and the session history |
| Which profiles were deleted, for a year | When a profile was last used |

### How it stays in step

- **At start, what this machine changed comes first.** Anything changed while the application was
  closed, with `ovp` for example, is reconciled with the file before anything is taken from it.
- **While it runs**, a change made here is noticed within seconds and written out. The file is
  looked at every minute and read only when its contents changed: it is compared by its hash, so a
  sync client touching the file does not make every machine read it again.
- **Writing takes a lock**, a `<name>.lock` file beside the shared one naming the machine that holds
  it. The new version is written beside the file and replaces it in one step, so nobody reads half of
  it. Before that step the file is read again, and if another machine wrote it in the meantime the
  merge is done again with what that machine wrote. A lock older than two minutes belongs to a
  machine that went away and is taken over.
- **Quitting** writes out whatever is still waiting.

The status bar says where the library stands at every moment: **Synchronising...** while a
synchronisation runs, shown for at least a moment so a change can be seen going out, then
**Synchronised** with the time, or what stands in the way with when the next attempt is made and
whether changes are waiting. Its tooltip has the whole sentence. **Sync log** at the far end of the bar
opens the steps taken since the application started, newest first: a change noticed here or in the
file, the file read and written with its size, what was taken in, conflicts, failures and the retries
they scheduled.

### When the file cannot be reached

The list keeps working with this machine's own copy whatever happens to the file. A banner in the
main window says what stands in the way and since when changes made here are waiting, and another
attempt follows after half a minute, then after longer and longer pauses up to five minutes. **Try
again** does not wait.

- A folder that cannot be reached, without a network for example, is simply tried again.
- A file that is missing from a folder that is there is looked for again and not written again.
  The sync client may not have delivered it yet, or somebody removed it on purpose. When it is gone
  for good, stop sharing on one machine and create the library again at the same place; the others
  carry on with it as long as the passphrase is the same.
- A passphrase that no longer opens the file was changed on another machine, and the banner asks for
  the new one.
- A file written by a newer version is not written back until this machine is updated, because what
  it cannot fully read it cannot faithfully write.

### Conflicts

Every machine remembers what the file held when it last synchronised, so it can tell who changed
what. That decides nearly everything without a conflict at all:

- **Changes are merged field by field.** A profile renamed on one machine and given another port on
  a second keeps both changes.
- **The same field changed on two machines** before either synchronised is the one real conflict.
  The later change wins, and the banner names the profile and says which side was kept.
- **A deletion never wins over a later change.** A profile deleted on one machine and changed
  afterwards on another comes back, and the banner says so. A connected profile that another machine
  deleted stays until its tunnel is disconnected.
- **The same configuration imported on two machines** before either synchronised is one profile
  held twice. Every machine keeps the same one of the two, and what this machine kept about the
  other, its favourite mark, its history and its sign ins, moves over to it.
- **A sign in changed on two machines** keeps the one typed on the machine that synchronises last,
  since there is no telling which of them the server accepted more recently. A sign in removed on
  one machine is removed on all of them, whether the server refused it or **Forget all stored
  credentials** was pressed, which says so on the credentials page and leaves the passphrase alone.
- **Two machines writing at once**, most often because one of them was offline, is something the
  lock cannot prevent: the lock only reaches the other machines once the sync client has carried it
  there. The sync client then keeps one of the two files and puts the other beside it under the
  file's name with a machine's name appended. Every version names the versions it descends from, so
  the machine whose file was not kept notices, merges against the version both started from, and
  writes its changes again. The copy is mentioned in the banner and can be deleted once you have
  looked.
- **Deleting unusually many profiles at once**, ten or more, or two or more that are most of the
  library, is held back instead of written. The usual cause is not a decision at all but a machine
  whose database was started again empty. The banner asks whether to restore them on this machine
  from the file or to delete them for everyone, and nothing is written until one of the two is chosen.

While a library is shared, deleting a profile, editing one and importing say beside the button that
the change reaches every machine.

### Backups

A shared library has no server behind it to keep its history, so every machine keeps some:

- **On the machine itself**, every version of the file it saw or wrote, the last twenty and one a day
  for thirty days, under `library\backups` in the data directory. Nothing that happens to the shared
  folder can reach them.
- **Beside the file**, in a folder named after it with `.backups`, every machine keeps a copy of the
  version it last synchronised with, named after the account and the machine, and a small note with
  the same names, the version of the application and when it last synchronised. That is a copy for
  everybody that survives the file being lost, and **Settings, Profiles** lists from those notes which
  machines use the library and which stopped. The names come from the operating system; anything a
  file system could object to is replaced in the file name.

Every copy is the file as it was, encrypted as it was, and useless without the passphrase of its
time. **Backups...** lists them all with when they were taken and how many profiles they hold.
Restoring one asks first, then makes it the library again for every machine: profiles added since
are deleted, profiles deleted since come back, and what the file held before is kept as a backup
first, so a restore can be undone the same way. It is also the way back from a file that is missing
or can no longer be opened.

### When someone should no longer have it

**Change the passphrase** writes the file again with a new one, and every other machine asks for it
the next time it synchronises. The old passphrase still opens what was written before: copies somebody
already has and older versions a sync client keeps in its history. Take away access to the folder as
well.

**Stop sharing** ends it for this machine only, and asks what becomes of the profiles, with neither
answer chosen in advance: keep them and go on with them as this machine's own library, or remove them
with their sign ins and start again with an empty one, which offers an export first. Either way the
passphrase and what the machine remembered about the file are removed, and the file is left as it is
for everybody else.

## Adding a language

Language files are JSON. The ones that ship live in `lang` beside the executable, and anything placed
in `lang` under the application data directory is layered on top of them, key by key. Copy `en.json`,
translate the values, drop it in and reload from the settings screen: no rebuild, and a key you have
not translated falls back to English rather than disappearing.

## What reaches OpenVPN

The application never edits a profile to make it work. It writes the configuration out exactly as it
was imported and puts everything it needs on the command line, so what a server sees is the
configuration you gave it plus a fixed set of options:

```
--config <file> --management 127.0.0.1 <port> stdin --management-query-passwords
--management-hold --management-forget-disconnect --auth-retry interact --verb 3
```

The verbosity is the one part of that line you can change, under **Settings, Advanced**. Three
carries the state changes, the push reply and the reason a handshake failed, which is what the
client reads and what the log window shows; raise it only while looking into something.

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

![The connection settings: route protection, reconnect attempts and timeouts](../assets/screenshots/settings.png)

## Looking for a newer release

The application can ask GitHub whether a newer release exists. It is the only thing here that
contacts a network nobody asked it to, so it is worth saying exactly what it does:

- It reads `https://api.github.com/repos/<owner>/<name>/releases/latest`, without credentials.
- It compares the release tag with the running version and reports the result.
- It downloads nothing and installs nothing. A newer release is a notice with a link.
- On macOS the notice says so: a release carries no macOS build, and a newer version there means
  building that release from the source, which is one command.

The repository defaults to the one this project is published from, and the check is on. Both are
under **Settings, Advanced**: clearing the repository, or turning the check off, stops every request.

Release tags are `v<version>`, for example `v1.2.0`. Tags written as `version-<version>`, which is
what this project published up to 1.2.0, are read as well.
