# Auftrag: OpenVpnPilot für macOS fertigstellen

> **Diese Datei ist nur die Übergabe und bleibt nicht im Repository.** Sie verstößt gegen die Neutralitäts- und Sprachregeln in CLAUDE.md. Kopier sie deshalb als Erstes nach `~/openvpnpilot-dev/PROMPT.md`. Mit deinem ersten Commit entfernst du sie aus dem Repository (`git rm MACOS-HANDOFF.md`). Danach arbeitest du mit der Kopie weiter.

Du arbeitest im Auto-Modus auf einem MacBook. Deine Aufgabe ist die macOS-Version von OpenVpnPilot, bis sie fertig, getestet und paketiert ist. Um Erlaubnis fragen musst du dabei nicht. Lies diesen Prompt ganz, bevor du anfängst.

## 1. Ausgangslage

- OpenVpnPilot ist ein Client für OpenVPN, gebaut mit .NET 10 und Avalonia. **Unter Windows funktioniert die Software zu 100 %.** Version 1.3.0 ist vollständig gegen ein Lab mit zehn Servern verifiziert. Windows ist für dich die Referenz, keine Baustelle.
- Die Architektur ist genau für diesen Schritt gebaut. Jede Fähigkeit, die vom Betriebssystem abhängt, ist ein Interface in `src/OpenVpnPilot.Core/Abstractions`:
  - `IOpenVpnLauncher`, `IProfileMaterializer`, `ISecretStore`, `IOpenVpnEnvironmentProbe`
  - `ISystemTrayIcon`, `INotificationPresenter`, `IGlobalHotkeyService`, `IAutoStartManager`

  Die Windows-Implementierungen liegen in `src/OpenVpnPilot.Platform.Windows`. `ICredentialProvider` ist schon plattformneutral umgesetzt. Für macOS legst du ein neues Projekt `src/OpenVpnPilot.Platform.MacOS` an, das diese Interfaces implementiert. Bestehenden Code baust du nicht um. Viel Arbeit sollte das nicht sein.
- An diesen Stellen musst du ansetzen:
  - **`src/OpenVpnPilot.App/AppHost.cs`**: wirft auf allem außer Windows absichtlich eine `PlatformNotSupportedException` und registriert die Windows-Dienste. Hier kommt die Auswahl nach Betriebssystem hin.
  - **Das CLI `ovp` (`src/OpenVpnPilot.Cli`)**: verwendet Windows-Typen noch direkt statt über die Interfaces, und zwar in `CommandRunner.cs`, `ConnectCommand.cs`, `PackageCommand.cs` und `EnvironmentReadiness.cs`. Das musst du auf die Interfaces umstellen, ohne das Verhalten unter Windows zu ändern.
  - **Single Instance und Übergabe an die laufende Instanz**: laufen über Named Pipes (`App/Services/SingleInstanceGuard.cs`, `Core/Ipc/PilotCommandClient.cs`). Auf macOS bildet .NET sie auf Unix-Domain-Sockets ab. Prüf, ob alles, was dort verwendet wird, auch auf macOS unterstützt ist.
  - **Pfade für Datenbank, Settings, Logs, Secrets, Sprachdateien und Runtime-Konfigurationen**: Finde heraus, wo sie aufgelöst werden, und leg sie auf macOS an sinnvolle Orte, zum Beispiel `~/Library/Application Support/OpenVpnPilot`.
- Laut meinen eigenen Notizen ist der teure Teil der Root-Helper, der OpenVPN startet (siehe Abschnitt 7). Dazu kommt, dass es keine Apple Developer ID gibt.
- Windows darf sich nicht verändern, und du kannst Windows hier nicht testen. Deshalb gilt für Änderungen an gemeinsamem Code (`Core`, `OpenVpn`, `Data`, `App`, `Cli`): so klein wie möglich, begründet und mit Tests abgedeckt. `Platform.Windows` muss weiterhin bauen.

## 2. Zuerst lesen

Verbindlich ist `CLAUDE.md`: Architektur, Stil, Neutralität, Git-Regeln und die gemessenen OpenVPN-Fakten. Diese Fakten sind aber **Messungen unter Windows**. Übernimm sie für macOS nicht ungeprüft (siehe Abschnitt 8). Lies danach `README.md`, `Core/Abstractions`, `Platform.Windows`, `AppHost.cs`, `installer/build.ps1`, `scripts/dev.ps1` und `lab/`.

Einiges steht nur in meinen lokalen Notizen und nicht im Git:
- Auf `dev` wird gearbeitet, `master` enthält die Releases. **Du arbeitest direkt auf `dev` und pushst auch dorthin.** `master` rührst du nicht an.
- `CHANGELOG.md` wird auf Englisch geführt, ohne Gitmoji, im Keep-a-Changelog-Format. Neue Einträge kommen unter `Unreleased`.
- `ovp doctor` muss „ready“ melden, bevor du irgendetwas anderes testest.
- Früher ist die Windows-App beim Verbinden abgestürzt, wenn OpenVPN fehlte. Behoben hat das `EnvironmentGate`. Auf macOS gilt das von Anfang an: Fehlt openvpn oder der Helper, erklären das die Oberfläche und `ovp doctor`. Abstürzen darf die App deswegen nicht.

## 3. Das Gerät: ein Arbeits-MacBook

Das hier ist kein Spielplatz. Arbeite so sauber und nachvollziehbar wie auf einem fremden Produktivrechner.

- Du hast einen Admin-Account und darfst alles installieren, was du brauchst, aber nur aus offiziellen Quellen.
- Installiert sind bisher Claude, Visual Studio Code und git sowie alles, was ich dafür selbst eingerichtet habe. OpenVPN fehlt noch.
- Was schon auf dem Gerät war, gehört nicht dir. Es kommt in die Baseline, aber nie ins Inventar, und beim Aufräumen bleibt es stehen.
- **Das tust du nie:**
  - Systemdateien löschen oder ändern
  - SIP, Gatekeeper, Firewall, FileVault, MDM-Profile, Sicherheitssoftware der Firma oder ein Firmen-VPN anfassen oder abschalten
  - Kernel- oder System-Extensions installieren
  - `sudoers` ändern oder `NOPASSWD` einrichten
  - `rm -rf` mit Variablen oder Globs außerhalb deiner eigenen Verzeichnisse
  - force-push

  Wenn sich etwas nur auf einem dieser Wege lösen lässt, hör auf und melde dich im Chat.
- **Löschen darfst du nur, was im Inventar steht** (Abschnitt 4). Alles andere bleibt, wie es ist.
- **sudo:** Deine Shell hat kein TTY, du kannst also kein Passwort eingeben. Frag mich im Chat nie nach dem Passwort. Für Schritte mit Root-Rechten gibt es zwei Wege:
  - Du gibst mir die exakten Befehle für mein Terminal, gebündelt und erklärt.
  - Du nennst mir im Chat den exakten Befehl und öffnest danach den nativen Admin-Dialog mit `osascript -e 'do shell script "…" with administrator privileges'`. Das Passwort tippe ich dann selbst in den Dialog.

  Brauch Root-Rechte so selten wie möglich und fass solche Schritte zusammen.
- **Neutralität:** Das Repository ist öffentlich. Nichts, was dieses Gerät verrät, gehört hinein: weder dass es ein Arbeitsgerät ist, noch Firmenname, Rechner-, Benutzer- oder Hostname, Seriennummer oder MDM. Das gilt für Code, Commits, Tests, Beispieldaten, Logs und Screenshots.

## 4. Inventar – ab dem allerersten Befehl

Ich will später auf Zuruf alles wieder entfernen können. Leg dafür außerhalb des Repos das Verzeichnis `~/openvpnpilot-dev/` an. Es enthält:

- **`baseline/`**: ein Schnappschuss des Systems vor der ersten Änderung. Dazu gehören:
  - der Inhalt von `/Applications`, `/Library/LaunchDaemons`, `/Library/LaunchAgents`, `~/Library/LaunchAgents` und `/etc/paths.d`
  - Kopien von `~/.zprofile`, `~/.zshrc` und `~/.bash_profile`, soweit vorhanden
  - `$PATH`
  - welche Werkzeuge schon vorhanden sind, jeweils mit Pfad und Version: `git`, `xcode-select -p`, `brew`, `dotnet`
  - die Ausgaben von `pkgutil --pkgs`, `dscl . -list /Groups`, `netstat -rn` und `scutil --dns`
- **`INVENTORY.md`**: Hier wird nur angehängt, und zwar **in dem Moment, in dem du etwas änderst**. Jeder Eintrag nennt Zeitpunkt, was, warum, wo und den exakten Befehl zum Entfernen. Aufgenommen wird wirklich alles:
  - Homebrew selbst sowie Formeln, Casks und Taps
  - das .NET SDK und globale dotnet-Tools
  - die Container-Laufzeit samt VM, Images, Volumes, Netzen und Kontexten
  - openvpn
  - Caches (`~/.nuget`, der Homebrew-Cache)
  - Änderungen an Shell-Profilen und `/etc/paths.d`
  - LaunchDaemons und LaunchAgents, Helper-Binaries, Sockets
  - Keychain-Einträge
  - Datenschutz-Freigaben (Bedienungshilfen, Bildschirmaufnahme, Mitteilungen)
  - Anmelde- und Hintergrundobjekte
  - Apps in `/Applications` und angelegte Gruppen
  - der Repo-Klon selbst, sofern du ihn angelegt hast
  - vorübergehende Änderungen am Netzwerk
- **`teardown.sh`**: Das Skript hältst du laufend aktuell, die Schritte stehen in umgekehrter Reihenfolge. Ausgeführt wird es **nur, wenn ich ausdrücklich darum bitte**.
- **`PROGRESS.md`**: Dein Arbeitsstand. Die Arbeit dauert lange, und dein Kontext wird dabei komprimiert. Halte deshalb hier fest, was erledigt ist, was offen ist, welche Fakten du gemessen hast und was als Nächstes kommt.

Wenn ich dich bitte aufzuräumen, gehst du so vor:
1. Stell sicher, dass keine Arbeit verloren geht: Alles muss auf `origin/dev` gepusht sein, bevor der Klon verschwindet.
2. Zeig mir die Liste der Dinge, die du entfernen willst.
3. Entferne sie in umgekehrter Reihenfolge.
4. Vergleiche das Ergebnis mit der Baseline.
5. Sag mir, was ich selbst tun muss, zum Beispiel Freigaben zurücknehmen, Anmeldeobjekte entfernen oder Keychain-Abfragen bestätigen.

## 5. Einrichtung

Arbeite die Schritte in dieser Reihenfolge ab. Bei **[ICH]** muss ich etwas tun. Sag mir dann genau, was.

1. Leg die Baseline an (Abschnitt 4).
2. Prüf, was schon da ist. git habe ich bereits installiert, vielleicht noch mehr. Installier nur, was fehlt.
3. Falls die Xcode Command Line Tools fehlen, installier sie mit `xcode-select --install`. Dabei öffnet sich ein Dialog. **[ICH]**
4. Falls Homebrew fehlt: Die Installation braucht einmal sudo. **[ICH]** Gib mir dafür den offiziellen Installationsbefehl für mein Terminal. Der Installer ergänzt `~/.zprofile`, das gehört ins Inventar.
5. Installiere das .NET 10 SDK, möglichst ohne sudo und so, dass es leicht wieder zu entfernen ist, zum Beispiel mit dem offiziellen `dotnet-install.sh` nach `~/.dotnet`. Falls du Migrationen brauchst, kommt `dotnet-ef` 10.0.11 als globales Tool dazu.
6. Container-Laufzeit: Docker Desktop kostet für Firmen ab einer bestimmten Größe Lizenzgebühren. Nimm auf diesem Gerät deshalb **Colima** mit `docker` und `docker-compose` über Homebrew. Willst du es anders lösen, frag mich vorher.

   **Achtung:** Neun der zehn Lab-Server laufen über **UDP**. Sie liegen auf den Host-Ports 1201 bis 1210, nur 1205 ist TCP. Prüf früh, ob die UDP-Portweiterleitung unter Colima zuverlässig funktioniert. Falls nicht, meld dich mit den Optionen, statt das Lab umzubauen.
7. Installiere OpenVPN mit `brew install openvpn` und notiere die Version. Die Fakten in CLAUDE.md wurden mit 2.7.6 gemessen.
8. Repo: Falls ich schon einen Ordner bereitgelegt habe, nimm den. Sonst klonst du mit `git clone https://github.com/Schecher1/OpenVpnPilot.git ~/Developer/OpenVpnPilot`. Dann wechselst du auf `dev`.
   - **Git-Identität:** Prüf vor dem ersten Commit `git config user.name` und `git config user.email`. Beide müssen zu den bisherigen Commits passen (`git log -1 --format='%an <%ae>'`). Ein Firmenname oder eine Firmenadresse darf nicht in die öffentliche Historie. Passt es nicht, frag mich.
   - **Push:** Prüf mit `git push --dry-run origin dev`, ob Pushen funktioniert. Falls nicht, sag mir Bescheid.
9. Lass `dotnet build` und `dotnet test` auf dem unveränderten Stand laufen und melde mir das Ergebnis als Ausgangsbefund.

## 6. Umfang

- Implementiere alle Interfaces aus Abschnitt 1 für macOS und jede weitere Stelle, die du dabei findest und die vom Betriebssystem abhängt.
- Der Funktionsumfang entspricht Windows vollständig:
  - Suche, Tags, Favoriten, Import-Assistent mit Drag & Drop, `.ovppkg`-Pakete
  - überwachte Ordner, mehrere Tunnel gleichzeitig, Telemetrie, Verlauf mit CSV-Export
  - Quick Switcher und Disconnect-Palette
  - globale Hotkeys, Menüleisten-Eintrag, Mitteilungen
  - Autostart, Update-Hinweis, Diagnosepaket
  - `.ovpn` und `.ovppkg` lassen sich aus dem Finder öffnen
  - `ovp` im Terminal, inklusive `doctor`, `connect`, `disconnect` und `status`
- Runtime-Konfigurationen enthalten den privaten Schlüssel direkt in der Datei. Das Verzeichnis bekommt deshalb die Rechte `0700`, die Datei `0600`, und beide gehören nur dem Benutzer.
- Pfade können Leerzeichen enthalten, zum Beispiel `Application Support`. Übergib sie immer als eigenes argv-Element, nie als Shell-String.
- Secrets speicherst du über die Security-Framework-API in der Keychain. Das `security`-CLI nimmst du **niemals** dafür, sonst stünde das Secret in der Prozessliste. Zugangsdaten erreichen OpenVPN weiterhin nur über das Management-Interface.
- Wähle die Bundle-ID einmal und mit Bedacht, und halte sie neutral. Mitteilungseinstellungen, Keychain-Zugriff und Datenschutz-Freigaben hängen an ihr. Nach einem Release darf sie sich deshalb nicht mehr ändern, genau wie die AppUserModelID unter Windows.

## 7. Sicherheit geht vor: der privilegierte Start

openvpn braucht auf macOS root-Rechte, um das utun-Gerät anzulegen und Routen zu setzen. Unter Windows übernimmt das der Interactive Service. Auf macOS musst du das Gegenstück selbst bauen. Das ist die sicherheitskritische Stelle des ganzen Projekts. Maßstab ist das Autorisierungsmodell des Windows-Dienstes, das in CLAUDE.md beschrieben ist.

Mindestanforderungen:
- **Aufrufer prüfen:** Authentifiziere den Aufrufer über die Peer-Credentials des Sockets, zum Beispiel mit `getpeereid`. Autorisiert sind Mitglieder der Gruppe `admin` oder einer eigenen Gruppe. Wer nicht autorisiert ist, bekommt wie beim Windows-Dienst nur die Optionen aus der Whitelist und nur Konfigurationen aus einem festen Verzeichnis.
- **Sauber starten:** Starte openvpn mit einem argv-Array, nie über eine Shell. Leg Socket und Dateien mit engen Rechten an.
- **Gefährliche Optionen:** Manche Optionen erlauben es, als root Code auszuführen oder beliebige Dateien zu schreiben. Für nicht autorisierte Aufrufer lehnst du sie ab. Dazu gehören `up`, `down`, `route-up`, `plugin`, `script-security`, `log`, `writepid`, `status`, `cd` und Includes über `config`. Gleich die Liste vollständig mit der OpenVPN-Dokumentation ab.
- **Eigene Skripte:** Skripte, die der Helper selbst braucht, etwa für DNS, gehören root und liegen an einem geschützten Ort.
- **Ausgeschlossen:** kein setuid-Binary, keine sudoers-Regel und kein Passwortdialog bei jeder Verbindung.
- **Klein halten:** Der Teil mit Root-Rechten soll klein und stabil sein. Jede Neuinstallation braucht mich für sudo, er sollte sich also selten ändern.
- **Offene Entscheidungen:**
  - Kommt openvpn von Homebrew, oder liefert die App es mit? Beim Mitliefern ist die GPL zu beachten.
  - Wie wird der Helper installiert und wieder entfernt, zum Beispiel per `.pkg` mit einem LaunchDaemon?
  - Funktioniert `SMAppService` ohne Developer ID überhaupt? Das musst du prüfen.

**Das ist der einzige geplante Checkpoint.** Bevor du den Helper implementierst, schickst du mir im Chat ein kurzes Design: Optionen, deine Empfehlung, Bedrohungsmodell, Installation und Deinstallation sowie die Herkunft von openvpn. Dann wartest du auf mein OK und arbeitest so lange an allem anderen weiter.

## 8. Auf macOS messen statt annehmen

Miss mindestens die folgenden Punkte und dokumentiere die Ergebnisse:
- **Management-Protokoll:** Kommt der Passwort-Prompt ohne Zeilenumbruch? Wird `hold release` erst nach `>HOLD:` angenommen? Verarbeitet openvpn Befehle nur einzeln? Vermutlich ist das alles wie unter Windows. Bestätige es trotzdem.
- **DNS:** Setzt openvpn 2.7 gepushte Nameserver auf macOS selbst, oder braucht es dafür ein Skript? Wird beim Trennen alles sauber zurückgesetzt?
- **Kompression:** Auf macOS gibt es keinen Data Channel Offload, deshalb verhält sich Server zehn dort womöglich anders als unter Windows. Miss das, bevor du die Logik rund um `PushReplyParser` übernimmst oder änderst.
- **Round-Trip-Messung:** Welches Gateway und welches Messziel ergeben sich bei `topology subnet` und bei `net30`?
- **Systemverhalten:**
  - Brauchen Mitteilungen ein signiertes Bundle?
  - Welche Berechtigung brauchen globale Hotkeys?
  - Wie verhält sich die Keychain nach einem Rebuild, wenn sich die ad-hoc-Signatur ändert?
  - Was passiert beim Abmelden und Herunterfahren mit offenen Tunneln, und welche Avalonia-Ereignisse kommen dabei auf macOS an?

Die Ergebnisse trägst du in CLAUDE.md als neuen Abschnitt „Verified macOS integration facts“ ein, im Stil der bestehenden Abschnitte. Nenne dort die Versionen von macOS, OpenVPN, Avalonia und der Container-Laufzeit, aber keine Gerätenamen. Widerspricht eine Messung einem bestehenden Eintrag, korrigierst du den Eintrag.

## 9. Oberfläche

- Die Oberfläche soll aussehen und sich verhalten wie unter Windows: dieselben Views, dasselbe Layout, dieselben Styles, Texte, Icons und Abläufe. Als Referenz dienen die Bilder in `assets/screenshots` und die README.
- Abweichen darf sie nur dort, wo macOS es verlangt:
  - App-Menü mit „Über“ und „Beenden“
  - Cmd statt Strg
  - die Fensterknöpfe oben links (Ampel)
  - ein Menüleisten-Icon als Template-Bild, damit es in hellem und dunklem Modus passt
- Prüf das Ergebnis mit Screenshots. `screencapture` braucht dafür die Freigabe „Bildschirmaufnahme“. **[ICH]** Falls Computer-Use verfügbar ist, kannst du das stattdessen nehmen. Vergleiche deine Screenshots mit den Windows-Bildern. Screenshots mit persönlichen Daten oder Firmendaten committest du nie.
- Jeden neuen Text gibt es auf Englisch und auf Deutsch.

## 10. Testen, einschließlich Edge Cases

**Automatische Tests**
- `dotnet build` (Warnungen gelten als Fehler) und `dotnet test` laufen auf macOS komplett grün.
- Schreib neue Tests für alles, was sich ohne das Betriebssystem testen lässt: den Optionsfilter und die Autorisierung des Helpers, die Pfade und die Parser.
- Für den Helper gibt es einen Guard nach dem Vorbild von `LaunchOptionWhitelistTests`.

**Lab**
- `docker compose -f lab/docker-compose.yml up -d --build` startet die zehn Server und schreibt die Client-Konfigurationen nach `lab/clients`.
- Hinter den Servern liegen Sites mit den Adressen 10.9.0.11 bis 10.9.0.20.
- Zugangsdaten: Benutzer `pilot`, Passwort `pilot-secret`, Passphrase für den Key `key-secret`, Einmalcode `123456`.
- `docker compose down -v` löscht die CA mit.

**Ende-zu-Ende über die echte App**
- Verbinde jeden Server einzeln, prüf die Telemetrie, ruf die Site hinter dem Tunnel auf, trenne die Verbindung und kontrollier den Verlauf.
- Danach verbindest du alle zehn gleichzeitig.

**Edge Cases, mindestens diese:**
- falsches Passwort
- Key mit Passphrase
- statischer und dynamischer Einmalcode; ein dynamischer Code darf nicht als falsches Passwort gemeldet werden
- Verbindung über TCP
- gepushte DNS-Server werden gesetzt und nach dem Trennen wieder entfernt
- der Server mit Kompression
- Trennen, während die Verbindung noch aufgebaut wird
- `kill -9` auf openvpn
- Helper gestoppt oder nicht installiert
- openvpn fehlt
- App-Absturz bei laufendem Tunnel; beim nächsten Start wird der verwaiste Prozess erkannt
- Beenden mit offenen Tunneln (Cmd+Q, über das Dock, beim Abmelden)
- Ruhezustand und Aufwachen
- Netzwechsel (WLAN aus und wieder an)
- zweite Instanz
- `.ovpn` aus dem Finder öffnen
- Pfade und Profilnamen mit Leerzeichen, Umlauten und Unicode
- Zugriff auf die Keychain verweigert
- Import einer Konfiguration ohne `ca`
- Hotkey-Konflikt
- Wechsel zwischen hellem und dunklem Modus
- beide Sprachen

**Nach jedem Netzwerktest**
- Vergleich Routen (`netstat -rn`) und DNS (`scutil --dns`) mit der Baseline. Alles, was danach übrig bleibt, ist ein Bug.

**Vorsicht bei Server neun**
- Server neun will den gesamten Verkehr über den Tunnel leiten. Teste ihn nur mit aktivem Routenschutz. Ohne Routenschutz testest du ihn nur nach Rücksprache, denn dann übernimmt der Tunnel das Routing dieses Laptops.
- Falls ein Firmen-VPN läuft, sprich dich vorher mit mir ab.

## 11. Build-Skript

Leg das Gegenstück zu `installer/build.ps1` ebenfalls in `installer/` ab, zum Beispiel als `installer/build-macos.sh`.

**Was das Skript übernimmt**
- die Version aus `Directory.Build.props`
- sinngemäß dieselben Parameter: Configuration, Version, SelfContained, Runtime
- App und `ovp` werden in ein gemeinsames Verzeichnis gepublisht
- die `.pdb`-Dateien werden entfernt
- ein alter Publish-Ordner wird vorher geleert; vorher prüft das Skript, ob er noch benutzt wird
- die Ausgabe landet unter `artifacts/` und heißt `OpenVpnPilot-<version>-<rid>.<ext>`

**Was dabei herauskommt**
- ein `.app`-Bundle mit Info.plist, `.icns`-Icon und den Dateitypen `.ovpn` und `.ovppkg`, verpackt in einer `.dmg`
- zusätzlich ein `.pkg`, falls der Helper eine Installation mit Root-Rechten braucht
- Builds für `osx-arm64` und `osx-x64`; getestet wird die Architektur dieses Macs; ein Universal-Bundle, wenn es machbar ist
- eine ad-hoc-Signatur
- `ovp` ist nach der Installation im Terminal erreichbar

**Außerdem**
- Ohne Developer ID ist keine Notarisierung möglich. Was das für Gatekeeper bedeutet, dokumentierst du in der README.
- Test: Bau aus einem frischen Klon, installiere das Ergebnis, starte die App, verbinde dich und deinstalliere wieder. Prüf danach, dass nichts zurückgeblieben ist.
- Optional kannst du `scripts/dev.sh` als Gegenstück zu `scripts/dev.ps1` anlegen.
- Achte auf `.gitattributes`: `*.sh` wird mit LF eingecheckt, alles andere standardmäßig mit CRLF.

## 12. Robustheit

Es gelten die Regeln aus CLAUDE.md: ein CancellationToken für jede asynchrone Methode, kein stilles `catch`, keine Workarounds. Zusätzlich gilt:
- Alles, was auf den Helper oder auf openvpn wartet, hat einen Timeout.
- Das Abbauen einer Verbindung ist immer zeitlich begrenzt.
- Stürzt der Helper oder openvpn ab, endet das in einem klaren Zustand, nie in einem Profil, das hängen bleibt.
- In Logs stehen keine Secrets.
- Jeder Fehler, den der Nutzer bemerkt, wird in der Oberfläche verständlich erklärt. Das Programm beendet sich deswegen nie.

## 13. Git und Dokumentation

- Commits schreibst du auf Englisch mit Gitmoji-Präfix, wie in CLAUDE.md beschrieben. Halte sie klein und inhaltlich zusammenhängend.
- **Pushen:** Nach jedem abgeschlossenen Schritt pushst du auf `origin/dev`, wenn der Stand baut und alle Tests grün sind. Einen roten Stand pushst du nie, force-push gibt es nicht, und auf `master` pushst du nicht. Vor jedem Push prüfst du den Diff auf Neutralität: keine Gerätedaten, keine Namen, kein Bezug zur Firma.
- Profile, Keys, Zertifikate und Datenbanken werden nie committet. `.gitignore` weichst du nicht auf; `.DS_Store` darfst du ergänzen.
- In `CHANGELOG.md` kommen deine Einträge unter `Unreleased`.
- `README.md` und `CLAUDE.md` darfst du ergänzen, wo es nötig ist:
  - In die README kommt der macOS-Teil mit Voraussetzungen, Installation, Build und dem Lab auf macOS. Es bleibt bei einer README, ein `docs/`-Verzeichnis gibt es nicht.
  - In CLAUDE.md passt du die Aussage zu den Plattformen an und ergänzt die gemessenen macOS-Fakten.
- Releases, Tags und Versionsänderungen gibt es nur nach Rückfrage.

## 14. Kommunikation

- Im Chat schreibst du Deutsch. Im Repository bleibt alles auf Englisch.
- An diesen Meilensteinen schickst du eine kurze Statusmeldung:
  - Die Umgebung steht.
  - Das Design für den Checkpoint ist fertig.
  - Der erste Tunnel steht.
  - Die Lab-Matrix ist bestanden.
  - Das Build-Skript ist getestet.
  - Der Abschlussbericht ist fertig.
- Wenn ich etwas tun muss, sag mir genau, was (Befehl, Dialog oder Einstellung) und warum. Arbeite in der Zwischenzeit an etwas anderem weiter.
- Wenn du nicht weiterkommst oder sich etwas unerwartet verhält, hör auf. Meld dich mit Belegen, Optionen und deiner Empfehlung.
- Fragen stellst du jederzeit im Chat.

## 15. Fertig, wenn

- [ ] App und `ovp` laufen auf macOS mit vollem Funktionsumfang, und die Oberfläche entspricht der unter Windows.
- [ ] Der Helper ist nach dem freigegebenen Design umgesetzt und durch Tests abgesichert.
- [ ] `dotnet build` und `dotnet test` sind grün, und das Verhalten des Windows-Codes ist unverändert.
- [ ] Alle zehn Lab-Server funktionieren einzeln und gleichzeitig. Die Edge Cases sind geprüft und dokumentiert.
- [ ] Nach den Tests stimmen Routen und DNS wieder mit der Baseline überein.
- [ ] Das Build-Skript liegt in `installer/` und ist aus einem frischen Klon getestet, inklusive Installation und Deinstallation.
- [ ] README, CLAUDE.md und CHANGELOG sind ergänzt.
- [ ] Inventar und `teardown.sh` sind vollständig.
- [ ] Alles ist auf `origin/dev` gepusht.

Zum Schluss schickst du mir einen Bericht. Er enthält:
- was du gebaut hast
- die Testmatrix mit Ergebnissen
- die gemessenen macOS-Fakten
- offene Punkte
- was installiert ist, mit Verweis auf das Inventar
- wie das Aufräumen funktioniert
