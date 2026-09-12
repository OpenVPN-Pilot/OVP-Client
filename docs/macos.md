# OpenVPN Pilot on macOS

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · macOS · [Using it](usage.md) · [The `ovp` command](cli.md) · [Working on it](development.md)

## Requirements

- macOS 13 or newer, on Apple silicon or Intel
- the helper package, built from this repository, which brings its own OpenVPN
- Xcode's command line tools and the .NET 10 SDK, to build the two of them once

Nothing else is installed and nothing is needed from a package manager. Only root can open a tun
device, install routes or change the name servers, and macOS has nothing like the interactive
service, so the helper package is the privileged part: a launchd daemon that starts on the first
connection and ends itself when it has been idle. Installing it asks for a password once; using the
application afterwards does not.

The helper carries the OpenVPN it runs rather than looking for one. An OpenVPN from a package manager
lives under a directory owned by the account that installed it, together with the libraries it loads,
and running that as root would hand root to anything running as that account.

Members of the administrators group may start any configuration. An account that is not an
administrator is added to the group `openvpnpilot` by the installer, which grants the same thing;
without either, only the configurations an administrator installed under
`/Library/Application Support/OpenVpnPilot/Configurations` can be started. The application reports
which of these applies, and so does `ovp doctor`.

## Installing

**There is no macOS download, and this is the one thing to know before starting.** A build that
another Mac will open without an argument has to be signed and notarised by Apple, which needs a paid
Developer ID, and making one needs a Mac to make it on. This project has neither, so publishing a
disk image would be publishing something Gatekeeper refuses and that nobody could check the origin
of. Windows is different: an unsigned installer there is a warning to click through, and the MSI is
in the releases.

So macOS is built from the source. That is one command, it needs no decisions, and it produces
exactly what a release would have:

```bash
bash installer/build-macos.sh
```

It takes a while the first time, because it also builds the OpenVPN the helper carries, from pinned
sources. Afterwards `--skip-openvpn` reuses that. A build made on the machine it then runs on carries
no quarantine flag, so it opens without Gatekeeper saying anything at all, which is the second reason
this is not as bad as it sounds.

It writes two things under `artifacts/release`, because they are installed by different people at
different moments:

| | |
| --- | --- |
| `OpenVpnPilot-<version>-<rid>.dmg` | the application. Open it and drag OpenVPN Pilot to Applications. No password. |
| `OpenVpnPilot-Helper-<version>-<rid>.pkg` | the privileged helper and the OpenVPN it runs. Asks for a password. |

Install the application first, then the helper. Open the disk image, drag **OpenVPN Pilot** onto the
Applications folder, then:

```bash
sudo installer -pkg artifacts/release/OpenVpnPilot-Helper-1.3.0-osx-arm64.pkg -target /
```

The helper package also links `ovp` into `/usr/local/bin`, which is what puts the command on PATH,
and adds a standard account to the `openvpnpilot` group so it can start its own configurations. Until
the helper is installed the application starts and works, says the helper is missing, and offers the
link; it does not fail at the first connection.

To remove the helper and everything it installed:

```bash
sudo "/Library/Application Support/OpenVpnPilot/uninstall.sh"
```

That stops the daemon, removes the helper, its OpenVPN, the job definition, the group and the `ovp`
link, and forgets the package receipt. Nothing of the helper is left and nothing else on the machine
is touched.

The application and the profile store are deliberately left alone, because uninstalling the
privileged part is not the same as asking the application to forget everything. To remove those as
well, as the account that used it:

```bash
rm -rf /Applications/OpenVPN\ Pilot.app
rm -rf ~/Library/Application\ Support/OpenVpnPilot
rm -f ~/Library/LaunchAgents/org.openvpnpilot.app.login.plist
```

The bundle was called `OpenVpnPilot.app` up to 1.3.0. Both names are looked for, so an installation
made before the rename keeps working, but only one of them should be in Applications: drag the older
one out after installing the newer.

The last of those exists only while "start with the system" was on. The saved sign ins are in the
login keychain under the service `OpenVpnPilot`, which Keychain Access deletes.

## Gatekeeper, and what unsigned means here

There is no Apple Developer ID behind this, so nothing here is notarised. The application bundle
carries an ad-hoc signature, which is enough for macOS to run the code and not enough for Gatekeeper
to let it through unasked. The package is not signed at all: signing one needs a Developer ID, and
ad-hoc signatures do not apply to packages.

**Built and used on the same Mac, none of this happens.** The quarantine flag is what triggers
Gatekeeper, and it is set when a file arrives from somewhere else. A disk image written by
`installer/build-macos.sh` on the machine it is then installed on carries no such flag and opens
without a word. That is the ordinary case here and the reason building it yourself is the
instruction rather than a fallback.

It matters when the image is carried to another Mac, over a network, a share or a memory stick. Then:

| | |
| --- | --- |
| The application, opened from the Finder | "OpenVPN Pilot cannot be opened because it is from an unidentified developer." Right click it and choose **Open**, then confirm once. macOS remembers the decision. |
| The application, on a recent macOS | The first attempt may only offer **Done**. Open **System Settings, Privacy & Security**, scroll to the message naming OpenVPN Pilot, and choose **Open Anyway**. |
| The package | The same, through right click, **Open**, which hands it to the Installer. |

Nothing here asks you to turn Gatekeeper off, and nothing here should. Allowing one application you
have the source for is a decision about that application; turning the check off is a decision about
every application you will ever download.

## Where things are kept

Everything the application writes belongs to the user running it, so an installation for the whole
machine still keeps each person's profiles apart.

| | |
| --- | --- |
| Profiles, tags and history | `~/Library/Application Support/OpenVpnPilot/pilot.db` |
| Settings | `~/Library/Application Support/OpenVpnPilot/settings.json`, editable by hand |
| Credentials | the login keychain, one item per profile under the service `OpenVpnPilot` |
| Logs | `~/Library/Application Support/OpenVpnPilot/logs/`, one `yyyy-MM-dd.log` per day |
| Added languages | `~/Library/Application Support/OpenVpnPilot/lang/` |
| Autostart | `~/Library/LaunchAgents/org.openvpnpilot.app.login.plist`, only while "start with the system" is on |

Credentials go to the keychain through the Security framework, never through the `security` command,
which would put the secret in the process list for anyone to read.

What the helper package owns is separate, and root owns all of it:

| | |
| --- | --- |
| The helper | `/Library/PrivilegedHelperTools/org.openvpnpilot.helper` |
| The name server hook | `/Library/PrivilegedHelperTools/openvpnpilot/dns-updown`, the helper under another name |
| Its job definition | `/Library/LaunchDaemons/org.openvpnpilot.helper.plist` |
| Its socket | `/var/run/org.openvpnpilot.helper.sock`, held by launchd |
| The OpenVPN it runs | `/Library/Application Support/OpenVpnPilot/openvpn/`, with the sources it was built from named beside it |
| Configurations for everyone | `/Library/Application Support/OpenVpnPilot/Configurations/`, writable by root only |
| Configurations while connected | `/Library/Application Support/OpenVpnPilot/runtime/<uid>/`, mode 0700, the file 0600 |
| Its log | `/Library/Logs/OpenVpnPilot/helper.log` |

The configuration a tunnel runs from is not the file the application wrote. The application sends the
text over the socket, the helper checks it, rewrites it canonically, and writes its own copy that only
root can read. There is no path for anyone to swap between the check and the launch, and nothing the
caller sends can become an option: the helper builds the command line itself.
