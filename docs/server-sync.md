# Synchronisation with a server

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [Settings](settings.md) · [Server](server.md) · [The `ovp` command](cli.md) · [Working on it](development.md) · [Testing](testing.md)

[Server](server.md) · [Signing in](server-signing-in.md) · Synchronisation

With a server, the application works on a copy of the server's profiles and keeps it in step. This page
says what is kept in step, when, what happens when the server cannot be reached, and how the two sides
settle a difference.

## What is synchronised

| | Direction | Notes |
| --- | --- | --- |
| Profiles | both | Name, configuration, notes, colour, route protection and tags. Created and changed here by an administrator, changed there by anybody who may. |
| Tags | both | A tag that loses its last profile here is deleted on the server as well. |
| Shared sign ins | both | One per profile and realm, kept in the keystore here. See [shared sign ins](#shared-sign-ins). |
| Favourites and their slots | both | Replaced as one list. |
| Shortcuts | both | Replaced as one list. |
| Portable settings | both | Replaced as one document. [Which settings are portable](settings.md#what-a-package-and-a-server-carry). |

Never sent: the connection history, when a profile was last connected and how often, window positions,
the OpenVPN path, autostart, the quick menu display, which store is in use and the installation
identity as a setting. The sign ins in the keystore are not sent either, with one exception: a sign in
that was typed and worked is shared, see [shared sign ins](#shared-sign-ins). Only the shared columns of
a profile are ever written from the server, so a full synchronisation leaves the history and the last
connection alone.

## When it runs

One synchronisation is a cycle. A cycle runs:

- at once when the application starts with a stored session;
- every two minutes;
- two seconds after a change made here, so that a burst of edits becomes one cycle;
- when the network comes back, and then from the start of the retry schedule;
- when asked: **Sync now** in the status bar, in **Settings, Storage**, in the notification area
  menu and, on macOS, in the application menu.

Cycles never overlap. A request that arrives during a cycle becomes exactly one more cycle after it, and
requests close together become one cycle. **Sync now** under **Settings, Storage** waits for the cycle that
is running, runs its own so that the answer covers everything changed before the request, and says how it
ended; the other ways of asking only ask.

A cycle pushes first, then pulls: the waiting changes are sent, then the change feed is applied, then the
favourites, the shortcuts and the settings are taken. A failure that means nothing more can be sent now
ends the cycle where it happened. Nothing is pulled after a push that could not finish, because the
pull would put back what is still waiting to be sent.

## The change feed

The first synchronisation of a copy asks for everything, and the answer is complete: whatever the
server does not list is gone from the copy, its profiles, tags and shared sign ins alike. Every later
one asks for what changed since a cursor the server gave last time, and the answer says what was
deleted. The whole answer is applied in one transaction with the cursor stored last, so a pull that fails
anywhere leaves the old cursor behind and is simply asked again.

When the server no longer knows the cursor, because it has pruned what that cursor would need, it answers
that and the client starts again from the beginning. The configuration of a profile is fetched only when
its hash differs from the one held, four fetches at a time, before anything is written.

## Without the network

The copy keeps working: listing, searching, connecting, the history and, for an administrator, editing,
importing and deleting. A change made meanwhile is recorded as waiting. The status bar turns amber and
counts them, the dot is amber, and the cursor, the last pull and the last push stay where they were.
When the server is back the changes are sent without asking.

**What is recorded.** A marker names what changed, never a copy of it: when it is sent, the current state
is read. That is what lets markers collapse. Several edits of a profile become one update, a delete
replaces the edits before it, a profile created and deleted again before the server could be reached
leaves nothing to send, and the favourites, the shortcuts and the settings are each one marker however
often they changed. A secret is never written to the database for this.

**How it retries.** After a cycle that could not reach the server, or found it failing, the next one
waits 5, then 15, then 30, then 60 seconds, and 60 seconds for as long as that lasts. A longer
`Retry-After` from the server is honoured instead. A network that comes back is tried at once. Every
other outcome waits the ordinary two minutes: a session that needs a new sign in, a clock the server
refuses, a client that is too old and a certificate that is not trusted do not improve by asking
sooner. How the server answers is asked apart from the synchronisation, every 30 seconds, so the status
bar notices the server going away or coming back before the next cycle would.

**What counts as offline.** No connection, a name that does not resolve, a connection that breaks and a
call without an answer in time (ten seconds to connect, fifteen for a call, five minutes for a batch
upload). A server that answers but whose readiness check fails is shown as degraded, and when that check
cannot be asked either, as offline. A certificate that is not trusted is its own state and never offline.

## How differences are settled

The push is deliberately simple. It does not compare with what the server holds and does not ask who
changed what last: a profile goes up with `If-Match: *`, the lists as a whole, the settings without a
tag. A change someone else made to the same profile meanwhile is overwritten.

- **A rename does not undo an edit.** The configuration is sent only when it was edited here, which the
  marker remembers whatever collapses into it later. That the server's configuration differs may just as
  well be somebody else's edit.
- **What is waiting wins over a pull.** A profile, tag or shared sign in with a change waiting is left as
  this computer has it until the change is sent. The favourites, the shortcuts and the settings are
  treated the same way.
- **A deletion is the exception.** When the server no longer has a profile, the changes waiting for it
  could only be refused, and they go.
- **The server may rewrite a configuration.** After storing a profile, the application takes the
  configuration as the server stored it when its hash differs, unless the profile was changed here while
  the upload was under way. That change is not lost: it is sent again as an update.
- **A profile deleted here while its upload was under way** is deleted on the server too, after the
  upload.
- **Deleting what is already gone is done.** A deletion the server answers with "not found" counts as sent.
- **The same configuration twice.** An upload the server refuses as a duplicate becomes the server's
  profile with that configuration, once the pull has brought it. When there is none, the temporary profile
  is removed.
- **A server that holds nothing yet for a person** is told this computer's shortcuts and settings
  instead of handing over empty ones, so a first sign in does not erase the shortcuts every installation
  starts with. The favourites always follow the server.

**What the server refuses.** A refusal about the change itself, such as a configuration it finds
invalid, is final. The change is dropped, counted, written to the log, and shown as `not synchronised:
N` in the status bar until a cycle completes without any. Anything that says not now keeps the change and
ends the cycle: a server error, too many requests, a failed precondition, a conflict, or a session, clock,
protocol or client problem. A change refused as forbidden because the role no longer allows it drops all
the waiting administrator changes, see [roles](server-signing-in.md#roles).

A profile created here that the server refuses is kept, because it is somebody's work. It is marked in the
list, in the details and in the editor as existing on this computer only, with the server's code, and it
is offered to the server again when it is changed.

**Importing into a server.** An administrator's import stores the profiles and uploads them at once, in
batches of up to 500 and about 32 MiB. The review list then says for each profile whether the server
created it, already had it, or refused it with the server's reason. A profile the server refused is not
kept. While the server cannot be reached the profiles are kept and sent when it can. Applying a package
works the same way, and the sign ins it carries are offered to the vault.

## Shared sign ins

A server keeps one sign in per profile and realm, so that nobody else has to type it.

- **Sharing.** When a sign in that was typed works, which is when the tunnel comes up, it is offered to
  the server's vault. Nothing is shared for a sign in read from the keystore, for one that failed, or for
  an attempt that asked for a one time code, because a code is valid once and sharing the password that
  came with it would hand everybody half a sign in.
- **The first one wins.** The vault keeps the first sign in it is given. When somebody was quicker, the
  answer is a conflict, and the application takes the one in the vault and stores it instead of its own.
- **Not stored here.** When the person did not tick remember, the sign in is held in memory only until it
  has been sent, never written to this computer, and lost if the application ends first.
- **Receiving.** Shared sign ins arrive with the change feed into the keystore under the profile, so
  connecting needs no typing. One that differs from what is stored replaces it, and one the server no
  longer has is removed. A sign in waiting to be shared is left alone.
- **Replacing.** An administrator can replace the shared sign in for a profile from the profile editor.
  That is an act to wait for, not a change to queue, so it only works while the server can be reached, and
  the new one is stored here too.

## A profile deleted on the server while it is connected

Its tunnel is ended first, then the profile is removed together with its history here, and the status bar
says which profile it was.

## Signing out and changing accounts

**Signing out.** **Sign out** under **Settings, Storage** removes the server from this computer. There is
no further question. The synchronisation stops, every tunnel ends, the server is told, and the tokens are
discarded whatever it answers. Then the copy is emptied: every profile with its tags, history and stored
sign ins, the shortcuts, the changes waiting and the memory of who was signed in. The database file stays,
because the running application holds it open. Nothing of the server can be connected without signing in
again, and the next sign in, by anybody, starts with a complete synchronisation. Changes still waiting
at that moment are lost.

**Forget this server's stored credentials** under **Settings, Credentials** signs out in the same way, and
then removes the stored sign ins of this server's profiles only: those of the library on this computer and
of other servers stay. On the library on this computer the same button forgets every stored sign in the
keystore holds, a server's session included.

**A session that ends without signing out**, for example because it expired or was revoked, keeps the copy
and everything waiting, and works offline until somebody signs in.

**Changing accounts.** The copy remembers who was signed in last. Signing in as the same person continues.
Signing in as somebody else first discards what belonged to the previous person: the changes waiting, the
profiles they created that never reached the server with their stored sign ins, their favourites and their
shortcuts, and it forgets the cursor so the next synchronisation is a complete one. The server's profiles,
the history of this computer and their stored sign ins stay, because they belong to everybody and to this
machine.

## When the server withdraws the account

When an administrator disables an account, or the server otherwise withdraws it, the next call of that
account's client, even a call with a valid token, is answered with the header `X-Pilot-Directive: wipe`.
The header decides, whatever the status. From then on the application sends nothing to the server at all,
and carries out the directive:

1. The synchronisation stops and every tunnel ends.
2. The stored sign ins of every profile in the copy are removed, and so is the stored session.
3. The copy's folder is deleted.
4. The portable settings return to their defaults, keeping what describes this computer.
5. The mode becomes `Local` and the server's address is removed from the settings.
6. The application says in plain words that the account no longer has access and that everything from
   the server is gone, and starts again on the library on this computer.

A step that fails is logged with the request id and the next one still runs, because leaving the rest of
the server's data behind would be worse than a partial report. Nothing that did not come from the server is
touched: the library on this computer and the copies of other servers stay. The directive is final and is
never retried.

When the directive arrives while signing in to a server from the first start or from the storage settings,
the application is not working on that server's copy. What this computer still kept of that server, its
copy, the stored sign ins of its profiles and its session, is removed, the person is told, and the
application stays where it was.

## Finding out what happened

Choose **Server only** in the log window. It shows the calls with their request ids, each cycle with what
it pushed, dropped and pulled, the cursor and how long it took, every change deferred or refused with the
server's code, and the moments the server went away and came back. The status bar's tooltip and the
diagnostics bundle's `storage.txt` carry the last error code with its request id.
