# Testing

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [Settings](settings.md) · [Server](server.md) · [The `ovp` command](cli.md) · [Working on it](development.md) · Testing

```bash
dotnet test
```

runs everything, and needs no network, no OpenVPN and no server. What does need one is skipped, and a
skipped test says why, so a run reports what it did not cover rather than passing quietly.

## The test projects

| Project | What it covers |
| --- | --- |
| `OpenVpnPilot.Core.Tests` | The client of a server (headers, results, the session, signing in, the wipe directive, address keys), the settings file and its migration, storage resolution, the single instance claim, localization, the update check |
| `OpenVpnPilot.OpenVpn.Tests` | Parsing, editing, inlining and validating configurations, the management protocol and one time code challenges, the push reply, connection supervision and the manager that owns the tunnels |
| `OpenVpnPilot.Data.Tests` | Import and duplicate detection, the content hash a server compares, portable packages |
| `OpenVpnPilot.App.Tests` | View models, services, the synchronisation (outbox, push, pull, schedule, favourites and settings, roles, shared sign ins, the wipe, the status shown), switching between this computer and a server, startup options, the diagnostics bundle, the language files, and the composition of the whole container |
| `OpenVpnPilot.Platform.MacOS.Tests` | The client side of the helper, the helper's configuration policy and security checks, the Dock presence |
| `OpenVpnPilot.Cli.Tests` | Which commands `ovp` refuses while the profiles are a server's |
| `OpenVpnPilot.Server.IntegrationTests` | The client's own synchronisation against a real server, see [below](#against-a-real-server) |

How they are written:

- The server client and the synchronisation run against a scripted HTTP handler. Databases are real
  SQLite files in a temporary folder, not an in-memory stand in, so the migrations and the queries that
  SQLite refuses to translate are exercised.
- `CompositionTests` builds the application's whole container in both storage modes with every
  registration validated and created, so a service that cannot be composed fails a test rather than the
  first start against a server.
- The language files are compared with each other and with the keys the sources ask for, so a missing
  translation fails the build instead of quietly falling back to English.
- A test that only means something on one system says so and is skipped elsewhere: those of
  `Platform.MacOS.Tests` that exercise macOS itself, and the one in `Core.Tests` that depends on how
  Windows shares an open file.
- `Platform.Windows` has no test project of its own. What it does for the application is covered where
  it is used, and what depends on the interactive service was measured against OpenVPN itself.
- The Microsoft sign in is tested with a stand in for Microsoft, never against Microsoft.

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

## Against a real server

`tests/OpenVpnPilot.Server.IntegrationTests` runs the client's own synchronisation against an
[OpenVPN Pilot Server](https://github.com/OpenVPN-Pilot/OVP-Server). Each client in it is one
installation as the application composes it: an identity, a copy in a SQLite file, a keystore and the
synchronisation. The keystore and the settings are kept in memory; everything that talks to the server
is the application's own code. Two clients are two computers to the server. Without a server every
test is reported as skipped, so `dotnet test` stays the same everywhere.

What they do:

- sign in as an administrator and as a user, and as a stranger with a wrong password;
- upload profiles created offline in one batch, which come back under the server's ids;
- run a complete and then a partial synchronisation as seen from a second computer, through a create, a
  rename and a deletion;
- have two computers share a sign in, where the first one given is the one both end up with;
- edit and mark favourites while the server is stopped, and send both once it is back, with the outboxes
  ending empty;
- see an account disabled by an administrator reach its client as the wipe directive on its next call, and
  nothing more being sent afterwards;
- age the server's cursor past what the client holds and see the client start over with a complete
  synchronisation.

### Setting it up

The server is a checkout of its own repository, at `v1.0.0` or later, beside this one as `../OVP-Server`.
`OVP_SERVER_CHECKOUT` names another place. `tests/OpenVpnPilot.Server.IntegrationTests/Stack` runs it
apart from any other copy on the machine, with a project name, port, database, image tag, certificate
and users of its own: the server checkout's own `.env`, certificates and user file are never read.
First, once:

```bash
dotnet run tests/OpenVpnPilot.Server.IntegrationTests/Stack/prepare.cs
```

That writes a certificate for `localhost` and an `.env` with a database password and keys generated on
the spot, all of which git ignores. The certificate is valid for 90 days. Running it again keeps what is
there; to start over delete `certs/` and `.env` in that folder and remove the database volume, which
keeps the password it was created with. Then, from that folder:

```bash
docker compose up -d --build --wait
```

The users are in `Stack/config/users.yaml`: an administrator, a user, and a user the tests disable and
enable again to see the wipe directive. The server accepts their plain passwords with a warning at every
start; the file belongs to a throwaway server on this machine and nothing else.

### Running it

The address, the certificate the server presents, and the two containers:

```bash
OVP_TEST_SERVER_URL=https://localhost:18443/ \
OVP_TEST_SERVER_CERT=tests/OpenVpnPilot.Server.IntegrationTests/Stack/certs/server.crt \
OVP_TEST_API_CONTAINER=ovp-client-tests-api-1 \
OVP_TEST_DATABASE_CONTAINER=ovp-client-tests-postgres-1 \
dotnet test tests/OpenVpnPilot.Server.IntegrationTests
```

```powershell
$env:OVP_TEST_SERVER_URL = 'https://localhost:18443/'
$env:OVP_TEST_SERVER_CERT = 'tests/OpenVpnPilot.Server.IntegrationTests/Stack/certs/server.crt'
$env:OVP_TEST_API_CONTAINER = 'ovp-client-tests-api-1'
$env:OVP_TEST_DATABASE_CONTAINER = 'ovp-client-tests-postgres-1'
dotnet test tests/OpenVpnPilot.Server.IntegrationTests
```

| Variable | What it does |
| --- | --- |
| `OVP_TEST_SERVER_URL` | Turns the tests on, and names the server |
| `OVP_TEST_SERVER_CERT` | The certificate the server presents. It is the only one the tests accept, and it doubles as the certificate authority of the profiles they upload, because the server wants a configuration that carries one |
| `OVP_TEST_API_CONTAINER` | The server's container. Without it the test that stops the server is skipped |
| `OVP_TEST_DATABASE_CONTAINER` | The database's container. Without it the test that ages the cursor is skipped |

The certificate is accepted through a check that lives in the test project and nowhere else; the
application itself has no way to trust a certificate the system does not. The tests remove what they
create and enable again the account they disable, so they can run any number of times against the same
stack. `docker compose down -v` in that folder removes it.

### What they have shown

Measured against server 1.0.0 with this client's own pipeline, outbox and synchronisation:

- A stopped server is offline, not an error. Every cycle ends offline, the copy keeps working, and a
  rename and a favourite made meanwhile are in the database. Once the server is started again the next
  cycle pushes both, a second computer sees the rename, and both outboxes end empty.
- A disabled account reaches its client on the very next call: it is answered 401 with the wipe directive
  although its access token has not expired, and from then on the client sends nothing at all.
- The vault keeps the first sign in. A second computer adding an entry for the same profile and realm is
  answered 409, and reading the vault gives it the first computer's entry, which it stores instead of its
  own.
- An expired cursor is answered 410 and `since=0` still works. Provoked by raising the server's pruned
  position above the client's cursor, as its maintenance does once tombstones are 90 days old, the client
  asks again from zero and completes.

What has not been measured: the Microsoft sign in through the system browser on macOS, the single
instance hand over across the restart that `--after-restart` bridges, and the re-key of a profile created
offline anywhere but in these tests, on a SQLite file.
