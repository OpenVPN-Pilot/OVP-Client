# Signing in to a server

[OpenVPN Pilot](../README.md) · [Windows](windows.md) · [macOS](macos.md) · [Using it](usage.md) · [Settings](settings.md) · [Server](server.md) · [The `ovp` command](cli.md) · [Working on it](development.md) · [Testing](testing.md)

[Server](server.md) · Signing in · [Synchronisation](server-sync.md)

## What happens first

The application asks the server about itself before it asks anyone for a password: when an address is
entered, and every time the sign in form is opened again. The answer is judged before the form
appears.

| Verdict | Shown as |
| --- | --- |
| The server is an OpenVPN Pilot Server this client can work with | The form |
| Something answers, but not as a server does, or its answer is not what the protocol describes | "Something answers at this address, but it is not an OpenVPN Pilot Server." |
| It speaks another version of the protocol | "This server speaks a different version of the protocol than this application. One of the two needs an update." |
| It requires a newer client | "This server needs OpenVPN Pilot *minimum* or newer, and this is *own version*. Update it first." |
| It could not be asked | The message for what went wrong, such as offline or an untrusted certificate |

## The ways of signing in

The server says how it lets people in, and the form follows it. The wording under the form tells the
person which kind they are looking at.

| The server's mode | What the form asks | Wording |
| --- | --- | --- |
| `none` | A user name | "This server asks only for your user name." |
| `file` | A user name and a password | "Sign in with the user name and password the server's administrator gave you." |
| `ldap` | A user name and a password | "Sign in with your directory account, as on your work computer." |
| `entra` | Nothing: a **Sign in with Microsoft** button | "A browser window opens for the Microsoft sign in. Come back here once it says you are done." |

A mode this version does not know, or `entra` without the details the Microsoft sign in needs, is
reported as "This server signs people in a way this version of OpenVPN Pilot does not know. Update the
application." and offers no form.

The password field is emptied after a wrong password and after a success. **Cancel** ends an attempt that
is waiting, which in practice is the browser.

### Microsoft

The server publishes what the sign in needs: the tenant, the client id, the scope and the authority. The
application uses them exactly as given. It is a public client using the authorisation code flow with
PKCE through the Microsoft Authentication Library, in the **system browser**, never an embedded one, so
the person sees the real Microsoft page with whatever single sign on and second factor the browser
already holds. The redirect goes to `http://localhost` on a port the library picks, which is what the
server's operator registers.

The access token Microsoft issues is handed to the server for its own tokens and is then discarded. The
browser tab says when the sign in is done and can be closed; a failure is reported as "The Microsoft sign
in did not complete" with the library's error code, and a closed browser as "The sign in was cancelled."

**Staying signed in.** A server in Entra mode ends every session a fixed time after the Microsoft sign
in, eight hours unless its operator says otherwise (`OVP_ENTRA_REAUTH_HOURS`), and refreshing in between
does not move that point. It is how the server notices an account that was disabled in the directory,
because it has no other way to ask. Answering it with a browser window every morning is not how an
application that stays signed in behaves, so the application keeps what the Microsoft sign in left
behind, the library's token cache, in the keystore beside the session (`server/<key>/entra`, on a Mac in
the same keychain item as everything else, so there is no further question from the keychain). When the
server asks for proof, the application asks Microsoft again without the person and trades the new token
for a new session. Microsoft still decides: a disabled account, a changed password or a policy that wants
the person makes that attempt fail, and only then does the session end and the person sign in in the
browser. When Microsoft or the server cannot be reached at that moment, nothing ends: the session is kept
and the attempt is repeated with the next refresh. What is kept goes with the session, at signing out,
when the server withdraws the account, and when another person signs in.

**Known limit.** The sign in and its renewal are covered by tests only with a stand in for Microsoft.
The interactive sign in has been used against Microsoft on a Mac; the renewal without the
person has not yet been seen against Microsoft itself. The log says which way it went: `The session with
server ... was renewed through Microsoft` when it worked, and `... was refused` followed by the session
ending when Microsoft wanted the person.

## The session

A successful sign in gives the application two tokens. The access token lives in memory only. The
refresh token is a credential and is kept in the operating system's keystore under the server's key
(`server/<key>/refresh`), which is how a session survives a restart. It is picked up at the next start
without contacting the server, with the person as they were last seen, so the role is known offline.

- The access token is renewed a minute before it expires, or once after the server refuses it as expired,
  revoked or invalid, and the refused call is then repeated once. A second refusal is the answer.
- A refresh token is used once. Every renewal runs behind one lock, so several calls that need a new
  token share one renewal rather than spending the token twice, which the server would answer by ending
  the session. The new token is stored before it is used.
- When the server refuses a refresh for good, because the token is invalid or reused, the person has to
  prove who they are again and Microsoft would not do it without them, or the token belongs to another
  installation, the session ends. Nothing is
  erased: the copy keeps working offline, a banner says **Sign in required**, and **Sign in** on the
  banner, **Sign in again** in the status bar menu and **Sign in** under **Settings, Storage** open
  the form again.
- Offline, throttled and a directory that cannot be reached do not end the session.
- Once the server has been asked for a new token, the answer is taken over even when the application
  is stopping or the synchronisation was cancelled meanwhile, bounded only by the network's own time
  limits. The answer is the only copy of the new token, and the server ends a session whose old token is
  presented again as stolen.
- A keystore that does not hand over the stored session at the start, because the person denied or
  dismissed the keychain's question, does not end it either. Nothing is sent, the banner says **The sign
  in was not handed over**, and the next start asks the keychain again. Signing in replaces it.
- When the keystore refuses the new refresh token, the session goes on from memory for this run and the
  stored token is removed, so the next start asks for a sign in rather than presenting a token the server
  has already seen.
- Signing in again as the person the copy last knew simply continues. Signing in as somebody else first
  discards the previous person's waiting changes, favourites and shortcuts, see
  [synchronisation](server-sync.md#signing-out-and-changing-accounts).

Signing out is under **Settings, Storage**, and it removes the server from this computer. It is
described under [synchronisation](server-sync.md#signing-out-and-changing-accounts).

## What the messages mean

Every message that comes from the server has a line beneath it, `Request id: ...`, which the server's
operator can find in the server's own log. It is left out when nothing reached the server, which is the
case for the first two rows, and when the server sent none.

| Message | What happened and what to do |
| --- | --- |
| The server could not be reached. Check the address and the network connection. | No connection, the name did not resolve, the connection broke, or there was no answer in time (ten seconds to connect, fifteen for a call). Not an error of the account. |
| This computer does not trust the server's certificate. | See [certificates](#certificates). |
| The user name or the password is wrong. | `auth.invalid_credentials`. |
| This account may not use this server. Ask the server's administrator for access. | `auth.forbidden`: the account is known and not allowed, for example outside the group the server requires. |
| The server cannot reach its directory or Microsoft right now. Try again later. | `auth.provider_unavailable`: the server's side of the sign in is down. |
| Too many attempts. Try again in N seconds. | `request.too_many`. N is the server's `Retry-After`, rounded up, so waiting that long is never refused again. Without one, "Wait a moment and try again." |
| The server refuses this computer's clock. Set the correct date and time... | `pilot.clock_skew`. The server's own words follow, naming both times. Every call carries the computer's time. |
| This Microsoft account matches another account on the server. | `auth.identity_conflict`: the server's administrator has to resolve it. |
| The server has changed how people sign in. Go back and continue again. | `auth.mode_mismatch`: the mode asked for is not the one the server now uses. |
| This version of OpenVPN Pilot is too old for this server. Update it first. | `pilot.client_outdated`. |
| The server was reached without HTTPS. | `transport.https_required`. Cannot normally happen, because the client only uses `https://`. |
| This account no longer has access to the server. | The server withdrew the account, see [synchronisation](server-sync.md#when-the-server-withdraws-the-account). |
| There is no sign in to continue with. Sign in again. | No session is stored. |
| The system's credential store, the keychain on a Mac, did not hand over the stored sign in... | The person denied or dismissed the keychain's question at the start. Start the application again and allow access, or sign in again. |
| The settings could not be read when the application started... | There is no installation identity to send, so nothing is asked of the server until the application starts again, see [the settings file](settings.md#the-file). |
| The server refused the sign in (`code`) or (status N) | Anything else. The code is the server's stable problem code. |

The application decides what to do by the code and the status of an answer, never by the server's text,
which may change. The clock is the one place where the server's text is shown, because it names exactly
what the person needs to see.

## Certificates

The server's certificate has to be one the operating system trusts. The application uses the system's
decision unchanged: there is no setting, no button and no option on the command line that accepts a
certificate the system refuses, and the code is written so that there is none. A refusal is shown as a problem to be fixed
on the computer, red in the status bar with a banner, never as being offline and never with a way past
it.

The fix belongs to the server's administrator or to this computer's: the server needs a certificate
that this computer's system accepts, or the authority that issued it has to be installed in the system's
trust store here. A certificate that is expired, issued to another name, or issued by an authority this
computer does not know all end up in the same place.

## Roles

The server names a role for every person: `admin` or `user`, shown as "administrator" and "user". The
application takes the role from the server at sign in and with every renewal of the session, remembers
it with the copy, and uses it while offline.

| | User | Administrator |
| --- | --- | --- |
| Connect, search, see the history | yes | yes |
| Favourites, shortcut slots, shortcuts and portable settings | yes | yes |
| Export profiles | yes | yes |
| Add a sign in the server does not have yet, by connecting | yes | yes |
| Import profiles, and apply a package | no | yes |
| Edit a profile beyond its favourite and its slot | no | yes |
| Delete profiles, and tag them | no | yes |
| Replace the sign in everybody uses for a profile | no | yes, while the server can be reached |

For a user the window simply does not offer what they cannot do: the import button and the delete
buttons are gone, dragging profiles onto a tag does nothing, and the profile editor shows the profile as
it is with only the favourite and the shortcut slot to change. Until the role has been read, nothing is
offered that a moment later turns out to be forbidden. A role the application does not know is shown as
the server wrote it and is treated as a user.

A role can change while the application runs. When the server says `403` to a change that only an
administrator may make, the application knows the role has been taken away: the waiting changes to
profiles and tags are dropped, counted as not synchronised, and the role is read again. A profile that
was created here is kept and marked as existing on this computer only, and the other edits are replaced
by the server's version at the next synchronisation.
