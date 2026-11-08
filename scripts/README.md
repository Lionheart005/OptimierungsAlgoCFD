# Server-, Deployment- & Einrichtungs-Handbuch (`scripts/`)

> **Weiterführende Dokumente (ohne Doppelung):**
> - **Bedienung & Alle JSON-Einstellungen (ohne Programmierkenntnisse):** [`src/projects/BEDIENHANDBUCH.md`](../src/projects/BEDIENHANDBUCH.md)
> - **Neues Bauteil / Projekt anlegen (3D & 2D):** [`src/projects/README.md`](../src/projects/README.md)
> - **Framework-Architektur & C#-Entwicklung:** [`src/OptimierungsAlgoCFD/README.md`](../src/OptimierungsAlgoCFD/README.md)
> - **Lokale Ausnahmekonfiguration (`*.local.json`):** [`config/README.md`](../config/README.md)

Zwei Skripte, eine Aufgabe: den Optimierungslauf von Windows aus auf dem
Hochschulrechner starten und beobachten.

| Datei | Läuft auf | Aufgabe |
|---|---|---|
| `sim.ps1` | Windows | Code packen, per SSH hochladen, Befehle absetzen, Ergebnisse holen |
| `sim-runner.sh` | Linux | tmux, xvfb-run, Pfade, Build, Start/Stop/Status |
| `new-project.ps1` | Windows | Ein neues Projekt anlegen (kopiert `src/projects/_Vorlage3D`) |
| `plot_history.py` | Windows / Linux | Konvergenzverläufe (history.csv) prüfen, auswerten und plotten |

`new-project.ps1` hat mit dem Server nichts zu tun — es steht hier, weil es dasselbe
Werkzeugkästchen ist. Was danach zu tun ist, steht in
[`src/projects/README.md`](../src/projects/README.md).

`sim-runner.sh` wird mit hochgeladen und liegt auf dem Server im selben Repo.
Wer ohnehin per SSH eingeloggt ist, kann es dort direkt benutzen:

```bash
cd ~/Documents/AutomatisierungCleanVersion
bash scripts/sim-runner.sh status
```

## Voraussetzungen

- **Auf dem Server:** einmalig `setup_server.sh` (Miniconda, SU2, Gmsh, PicoGK, .NET).
  Das erledigen diese Skripte *nicht* — sie setzen die fertige Umgebung voraus.
- **Auf Windows:** `ssh`, `scp` und `tar` (in Windows 11 enthalten), ein Eintrag in
  `~/.ssh/config` und ein dort hinterlegter SSH-Schlüssel — siehe nächster Abschnitt.
  `BIC_12` ist die Vorgabe.

### SSH-Schlüssel

Ein einzelnes `sim.ps1 run` setzt fünf bis sechs ssh/scp-Aufrufe ab. Ohne Schlüssel
müsstest du jedes Mal das Passwort eingeben, und die stillen Abfragen (`running`,
`hash`) unterdrücken stderr — eine Passwortabfrage sähe dort aus wie ein Hänger.
Anmeldung per Schlüssel ist deshalb praktisch Voraussetzung.

**Was gebraucht wird:** ein Schlüsselpaar unter `%USERPROFILE%\.ssh\` (Standardname,
z.B. `id_ed25519` + `id_ed25519.pub`). Weil es ein Standardname ist, findet ssh ihn
von allein — eine `IdentityFile`-Zeile in der `config` ist nicht nötig. Der
**öffentliche** Teil muss auf dem Server in `~/.ssh/authorized_keys` stehen.

**Prüfen, ob das schon der Fall ist:**

```powershell
ssh -o BatchMode=yes BIC_12 "echo ok"
```

`BatchMode=yes` schaltet jede Passwortabfrage ab. `ok` = Schlüssel funktioniert.
`Permission denied (publickey,password)` = er liegt noch nicht auf dem Server.

**Schlüssel erzeugen, falls noch keiner existiert:**

```powershell
ssh-keygen -t ed25519
```

**Öffentlichen Teil auf den Server bringen** (einmalig, fragt nach dem Passwort).
`ssh-copy-id` gibt es unter Windows nicht:

```powershell
$key = (Get-Content "$env:USERPROFILE\.ssh\id_ed25519.pub" -Raw).Trim()
ssh BIC_12 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && grep -qxF '$key' ~/.ssh/authorized_keys 2>/dev/null || echo '$key' >> ~/.ssh/authorized_keys; chmod 600 ~/.ssh/authorized_keys"
```

Bewusst nicht `type …pub | ssh …`: PowerShell macht aus dem durchgereichten Text
CRLF-Zeilenenden, und ein `\r` in `authorized_keys` macht den Eintrag unbrauchbar.
Das `grep -qxF` verhindert doppelte Einträge bei mehrfachem Aufruf.

## Alltag

```powershell
.\scripts\sim.ps1 projects  # Welche Projekte kennt der Server, wo liegen Ergebnisse?
.\scripts\sim.ps1 doctor    # Prüft SU2, mpirun, gmsh, dotnet, tmux, libpicogk.so
                            # (lädt beim allerersten Aufruf den Code hoch, baut aber nicht)
.\scripts\sim.ps1 run       # Hochladen, bauen, in tmux starten
.\scripts\sim.ps1 resume    # Abgebrochenen Lauf fortsetzen (ohne Upload, ohne Build)
.\scripts\sim.ps1 calibrate # Netzkalibrierung starten (ermittelt optimalen MeshRefinementFactor)
.\scripts\sim.ps1 recheck -Variant Gen3_Var2 -Finer 1 # Einzelne Variante auf feinerem Netz nachrechnen
.\scripts\sim.ps1 log       # Live zusehen (Strg+C beendet nur die Anzeige)
.\scripts\sim.ps1 status    # Läuft was? Seit wann? Wie viele Datensätze?
.\scripts\sim.ps1 fetch     # CSV + Log nach .\Serverergebnisse\ holen
.\scripts\sim.ps1 stop      # Lauf und hängende Rechenprozesse beenden
.\scripts\sim.ps1 delete    # Sauberer Neuanfang: Ergebnisse aller Projekte + Logs löschen
```

`delete` ist für den Fall „ab jetzt ein ganz neuer Lauf“: Es löscht auf dem Server
`Ergebnisse/` (alle Projekte), `simulation.log`, das Archiv `logs/` und `.last_exit`.
So liegen nach einem Lauf mit 5 Modellen nicht noch die Modelle 6–10 vom Lauf davor
im Ordner. Code, `bin/`/`obj/` (also der Build und `libpicogk.so`), `.deploy_hash`
und alle `*.local.json` bleiben stehen, damit der nächste Lauf ohne Neubau startet.
Der Befehl listet erst auf und fragt dann nach (`-Force` überspringt die Frage). Bei
einem laufenden Lauf verweigert er das Löschen. Was du behalten willst, holst du
vorher mit `fetch -All`.

Welches Projekt gerechnet wird, entscheidet `-Project <Name>` — der Name ist der
Ordnername unter `src/projects/`. Bei `deploy`, `run` und `doctor` gilt ohne Angabe
die Vorgabe `MantaAuv` (mit Hinweis). `fetch` und `status` fragen dagegen automatisch
das zuletzt gerechnete Projekt (`lastproject`) auf dem Server ab, sodass nach einem Lauf
kein manuelles `-Project` nötig ist.

### Abgebrochenen Lauf fortsetzen (`resume`)

tmux schützt nur dagegen, dass die Konsole zugeht oder die SSH-Verbindung abreißt —
nicht gegen einen Neustart des Servers, einen Stromausfall oder einen Absturz. Dafür
schreibt jeder Lauf ein Journal (`Ergebnisse/<Projekt>/Wiederaufnahme/journal.jsonl`):
eine Zeile je fertiger Rechnung, sofort auf die Platte gebracht.

```powershell
.\scripts\sim.ps1 status -Project Naca2D   # meldet "Abgebrochener Lauf gefunden: … Rechnungen gesichert"
.\scripts\sim.ps1 resume -Project Naca2D   # setzt dort fort
```

`resume` startet das Programm mit `--resume`. Es läuft mit demselben Zufalls-Seed von
vorn, nimmt die gesicherten Ergebnisse aber aus dem Journal, statt sie neu zu rechnen
(Log: `[WIEDERAUFNAHME] … übernommen`). Das dauert Sekunden; danach wird normal
weitergerechnet. Verloren geht höchstens die Variante, die beim Abbruch gerade lief.

* `resume` lädt **nichts hoch und baut nicht**. Weicht der lokale Code vom Server ab,
  gibt es nur eine Warnung: neuer Code braucht einen neuen Lauf (`run`).
* Hat sich die Konfiguration des Projekts geändert, bricht `resume` ab und nennt die
  Datei. Ein geänderter Seed in `simulation.json` wird gemeldet, es gilt der aus dem Journal.
* Ein neues `run` (auch ein Neustart durch `deploy`) archiviert das alte Journal als
  `journal_<Zeitstempel>.jsonl` und beginnt frisch.

`sim-runner.sh` auf dem Server hat **keinen** stillen Standard mehr — von Hand aufgerufen
verlangt es `SIM_PROJECT=<Name>` (außer bei `lastproject`, `projects`, `delete`, `stop`, `running`, `hash`),
weil ein Vertipper dort sonst klaglos ein anderes Projekt gerechnet und dessen Ergebnisse überschrieben hätte.

## Ergebnisse herunterladen

Alles landet unter `.\Serverergebnisse\<Server>_<Projekt>_<Zeitstempel>\` — Projektname
und Zeitstempel verhindern, dass sich verschiedene Projekte oder Abrufe überschreiben. Der Ordner ist
gitignored.

Das Herunterladen packt die gewünschten Dateien auf dem Server mit `sim-runner.sh pack <Projekt> <Umfang>`
zu einem einzigen `tar.gz`, lädt dieses mit **einem** scp-Aufruf herunter und entpackt es lokal mit `tar.exe`.
Über das VPN spart das Zeit und verhindert Hänger bei vielen Einzelverbindungen.

### Drei Umfänge: Standard, -Champion, -All

#### 1. Nur das Ergebnis (Standard)

```powershell
.\scripts\sim.ps1 fetch
```

Holt `Simulation_Results.csv`, `effective-config.json`, `run-info.json`, `Quellstand/`, das Projekt-Log, `Analyseergebnisse/Verlaeufe/`,
`Kalibrierung/` und `Verifikation.csv` (jede Datei nur wenn vorhanden).
Das ist der Normalfall: wenige KB bis MB, schnell über VPN. Fehlt die CSV nach dem Entpacken, bricht
das Skript mit einer roten Fehlermeldung und der Liste aller auf dem Server vorhandenen Projektergebnisse ab.

#### 2. Mit Champion-Dateien (-Champion)

```powershell
.\scripts\sim.ps1 fetch -Champion
```

Holt den Standard-Umfang plus die ParaView-VTU-Dateien des Champions und Top-N (`Surface_GenX_VarY.vtu`
und `Volume_GenX_VarY.vtu` laut `simulation.log` sowie alle `*Champion*.vtu`).

#### 3. Der komplette Ergebnisse-Ordner (-All)

```powershell
.\scripts\sim.ps1 fetch -All
```

Kopiert alles: alle Geometrien (STL), Netze (.su2), ParaView-Dateien (.vtu) und Arbeitsdateien.

> **Größe beachten.** Pro Variante fallen rund 15–20 MB an (STL 1–3 MB,
> SU2-Netz 4–8 MB, zwei `.vtu` 4–9 MB). Ein Probelauf mit 2 Varianten liegt bei
> ~54 MB, ein voller Lauf mit 10 × 10 Varianten bei **1,5–2 GB**.

### Einzelne Modelle gezielt holen

Dafür braucht es kein Skript — `scp` expandiert Platzhalter auf dem Server.
Der Pfad ist immer `Documents/AutomatisierungCleanVersion/Ergebnisse/<Projekt>/…`;
**jedes Projekt hat seinen eigenen Ordner**, sonst überschriebe ein zweites Projekt
die Ergebnisse des ersten:

```powershell
# Alle Dateien einer bestimmten Variante (Geometrie, Netz, Gmsh-Skript)
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv/*Gen1_Var2* .\Serverergebnisse\

# Nur die Geometrie zum Anschauen im CAD/Slicer
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv/Model_Gen1_Var2.stl .\Serverergebnisse\

# Nur die ParaView-Dateien einer Variante (Oberfläche + Strömungsfeld)
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv/Analyseergebnisse/*Gen1_Var2* .\Serverergebnisse\

# Alle ParaView-Oberflächen aller Varianten, ohne die großen Volumendateien
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv/Analyseergebnisse/Surface_* .\Serverergebnisse\

# Fehlerprotokolle, wenn eine Simulation abgebrochen ist
scp -r BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv/FehlerLogs .\Serverergebnisse\
```

Welche Variante die interessante ist, steht in der CSV (höchste `Fitness`) oder
am Ende von `simulation.log` unter „ABSOLUTER CHAMPION".

Erst nachsehen, was überhaupt da ist:

```powershell
.\scripts\sim.ps1 projects          # welche Projekte, wie viele Datensaetze
ssh BIC_12 'ls -lhS Documents/AutomatisierungCleanVersion/Ergebnisse/MantaAuv'
ssh BIC_12 'du -sh  Documents/AutomatisierungCleanVersion/Ergebnisse/*'
```

### Was im Ergebnisse-Ordner liegt

Alles unterhalb von `Ergebnisse/<Projekt>/`:

| Datei | Bedeutung |
|---|---|
| `Simulation_Results.csv` | **Das Ergebnis.** Eine Zeile je Variante, alle Parameter und Metriken |
| `effective-config.json` | Womit gerechnet wurde, samt Herkunft je Schlüssel. Gehört zur CSV dazu |
| `run-info.json` | Fahrtenschreiber: Code-Stand (Commit, Branch, Deploy-Hash), Programmversionen, Zufalls-Seed, Rechner, Status, Wiederaufnahmen |
| `Quellstand/` | Kopie von `src/projects/<Projekt>/` (`*.cs`, `*.json`) zum Start des Laufs |
| `Wiederaufnahme/journal.jsonl` | Lauf-Journal für `resume`; ältere als `journal_<Zeitstempel>.jsonl` |
| `Analyseergebnisse/Verlaeufe/` | Gespeicherte Konvergenzverläufe (`History_*.csv`) und Konfigurationen (`Config_*.cfg`) |
| `Analyseergebnisse/Surface_GenX_VarY.vtu` | Oberfläche für ParaView (Druckverteilung) |
| `Analyseergebnisse/Volume_GenX_VarY.vtu` | Strömungsfeld für ParaView |
| `Model_GenX_VarY.stl` | erzeugte Geometrie |
| `Model_GenX_VarY.su2` | Rechennetz |
| `Meshing_GenX_VarY.geo` | Gmsh-Skript, mit dem das Netz entstanden ist |
| `FehlerLogs/` | SU2-Konsolenausgabe abgebrochener Simulationen |
| `restart.dat`, `surface.vtu`, `vol_solution.vtu`, `current_config.cfg`, `history.csv` | SU2-Arbeitsdateien, werden bei **jeder** Variante überschrieben |

Herunterladen während eines laufenden Laufs ist unbedenklich (es wird nur
gelesen), liefert aber eine Momentaufnahme: eine Datei, die gerade geschrieben
wird, kann unvollständig ankommen. Für das Endergebnis also erst abholen, wenn
`status` „gestoppt" meldet.

Anderer Rechner oder anderes Projekt:

```powershell
.\scripts\sim.ps1 run -Server BIC_8 -Project MantaAuv
```

Nach `run` kann die Konsole zu — der Lauf hängt in einer tmux-Sitzung und
überlebt das Ende der SSH-Verbindung.

## Eine Rechnung von Hand prüfen

Nach einem Lauf (oder währenddessen) kann die Konvergenz der SU2-Strömungssimulationen
überprüft und visualisiert werden:

### 1. Verläufe und Ergebnisse abholen

```powershell
.\scripts\sim.ps1 fetch
# bzw. für ein bestimmtes Projekt:
.\scripts\sim.ps1 fetch -Project MantaAuv
```

Das holt `Simulation_Results.csv`, `effective-config.json` und standardmäßig den Ordner
`Analyseergebnisse/Verlaeufe/` (mit allen `History_GenX_VarY.csv` und `Config_GenX_VarY.cfg`)
nach `.\Serverergebnisse\<Projekt>\`.

### 2. Konvergenz prüfen und plotten mit `plot_history.py`

Das Skript `scripts/plot_history.py` basiert auf `pandas` und `matplotlib`
(`pip install pandas matplotlib`):

**a) Übersichtstabelle aller Varianten anzeigen:**
```powershell
python scripts/plot_history.py Serverergebnisse/MantaAuv
```
Gibt eine kompakte Konsole-Tabelle aus:
- Anzahl gerechneter Iterationen im Vergleich zu `ITER`
- Konvergiert (`ja`/`nein`, d.h. Abbruchkriterium vor Erreichen von `ITER` erfüllt)
- Residuenabfall in Dekaden (erstes `rms[...]`-Residuum)
- Endwerte und Schwingung `(max-min)/|mean|` in % über das Cauchy-Fenster für jede Metrik (`CD`, `CL`, …)

**b) Einzelne Variante im Detail plotten:**
```powershell
python scripts/plot_history.py Serverergebnisse/MantaAuv --var Gen1_Var1
# oder direkt die CSV-Datei angeben:
python scripts/plot_history.py Serverergebnisse/MantaAuv/Analyseergebnisse/Verlaeufe/History_Gen1_Var1.csv
```
Erzeugt ein PNG mit drei übereinanderliegenden Diagrammen (gespeichert als `History_Gen1_Var1.png`
neben der CSV-Datei):
1. **Kennwerte über Iteration**: Verlauf der aerodynamischen Beiwerte (`CD`, `CL`, …).
2. **Residuen (logarithmisch)**: Alle `rms[...]`-Spalten über die Iterationen.
3. **Konvergenz-Fenster (Cauchy)**: Gezoomte Darstellung der Hauptmetrik mit Endwertlinie,
   Toleranzband (`±cauchy_eps`) und schraffiertem Cauchy-Fenster.

Mit `--show` öffnet sich zusätzlich ein interaktives Matplotlib-Fenster.

**c) Mehrere Varianten vergleichen:**
```powershell
python scripts/plot_history.py Serverergebnisse/MantaAuv --compare
python scripts/plot_history.py Serverergebnisse/MantaAuv --compare --var Gen1_Var1 Gen2_Var3
```
Überlagert die Kennwert- und Residuenkurven der Varianten in einer gemeinsamen Grafik
und speichert `History_Compare.png` im Verzeichnis.

### 3. Strömungsfeld und Druckverteilung in ParaView prüfen

Wenn die Konvergenz rechnerisch geprüft ist und das 3D-Strömungsfeld im Detail interessiert:

```powershell
.\scripts\sim.ps1 fetch -Champion -Project MantaAuv
```
Holt die VTU-Dateien des Champions (`Surface_*.vtu` und `Volume_*.vtu`). Diese können
direkt in ParaView geöffnet werden, um Druckverteilung, Grenzschicht und eventuelle
Wirbelablösungen visuell zu analysieren.

## Auslastung auf dem Server ansehen

Das läuft bewusst außerhalb der Skripte, direkt per SSH. Wichtig ist `-t`:
`top` und `htop` sind interaktiv und brauchen ein Terminal, das ssh ohne dieses
Flag gar nicht erst anfordert.

```powershell
ssh -t BIC_12 'top -u $USER'      # beenden mit  q
ssh -t BIC_12 'htop -u $USER'     # bunter, beenden mit  q  oder  F10
```

Die **einfachen** Anführungszeichen sind nötig: in doppelten würde PowerShell
`$USER` schon lokal ersetzen (zu einem leeren Wert), statt es dem Server zu überlassen.

Nur ein kurzer Blick, ohne interaktives Fenster — geht auch ohne `-t`:

```powershell
ssh BIC_12 'top -b -n 1 -u $USER | head -n 12'
ssh BIC_12 'echo "Kerne: $(nproc)"; uptime'
```

So sieht ein gesunder Lauf aus:

```
    PID USER      ...  %CPU  COMMAND
 733193 lpleiss+  ... 554.5  dotnet      <- das Framework, treibt die Solver
 733310 lpleiss+  ... 100.0  SU2_CFD     <- ein Prozess je MPI-Rang,
 733311 lpleiss+  ... 100.0  SU2_CFD        Anzahl = "MpiCores" in simulation.json
```

Steht `dotnet` bei ~100 % und es ist **kein** `SU2_CFD` zu sehen, rechnet gerade
die Geometrie oder Gmsh — oder MPI wird nicht genutzt (siehe „CPU-Auslastung nur
100 %" weiter unten unter [Troubleshooting & Fehlerbehebung](#troubleshooting--fehlerbehebung-auf-dem-server)).

## Was beim Deploy passiert

1. `src/`, `config/` und `scripts/` werden gepackt — **ohne** `bin/`, `obj/`,
   `*.local.json` sowie `*.md` und `*.ps1`. Auf dem Server landet aus `scripts/`
   also nur `sim-runner.sh`; alles andere braucht er nicht. Das ist kein Detail:
   der Fingerabdruck wird genau über diese Liste gebildet, sonst würde schon eine
   Korrektur in dieser README einen laufenden Lauf neu starten.
2. Über Pfade und Dateiinhalte wird ein Fingerabdruck gebildet und mit dem
   verglichen, der auf dem Server liegt (`.deploy_hash`).
3. **Unverändert** → kein Upload, ein laufender Lauf bleibt unangetastet.
4. **Verändert und es läuft gerade etwas** → der Lauf wird gestoppt, der neue
   Code hochgeladen und gebaut, danach wird neu gestartet.
   Mit `-NoRestart` wird nur hochgeladen und *nicht* gebaut — ein Build würde dem
   laufenden Prozess die DLL unter den Füßen wegziehen.
5. `dotnet build` und danach `libpicogk.so` neben die DLL kopieren (der Build
   räumt den Ordner gelegentlich auf).
6. Erst nach erfolgreichem Build: `.deploy_hash` und `deploy-info.json` schreiben.
   `deploy-info.json` enthält Commit, Branch, Deploy-Zeit und — wenn ein git gefunden
   wird (PATH oder GitHub Desktop) — ob es nicht-committete Änderungen gab. Das Programm
   übernimmt das in `run-info.json`, denn auf dem Server gibt es kein `.git`.

**Verwaiste Dateien werden vor dem Entpacken entfernt.** Ein Deploy überschreibt nur,
es löscht nicht — eine umbenannte oder verschobene Datei lag danach doppelt auf dem
Server, und bei C#-Dateien heißt das: derselbe Typ zweimal, `CS0101`, Build kaputt.
Genau das ist beim Umzug nach `src/projects/` passiert. Deshalb räumt `deploy` unter
`src/`, `scripts/` und `config/` erst auf und packt dann aus. Ausgenommen sind
`*.local.json` (die sollen ein Deploy überleben, das ist ihr ganzer Zweck) sowie
`bin/` und `obj/`, damit der Build inkrementell bleibt. Unter `Ergebnisse/` wird
**nichts** angefasst.

Weil `*.local.json` nie übertragen wird, überlebt eine solche Datei auf dem Server
jedes Deploy — sie lässt sich nur per SSH dort entfernen, wo sie liegt.
**Sollstand ist, dass es keine gibt**, und der Start meldet jede vorhandene mit jedem
Schlüssel, den sie aushebelt. Für einen kurzen Probelauf ist sie der falsche Weg:
`MaxIterations` und `VariantsPerIteration` gehören in
`src/projects/<Projekt>/simulation.json` — dann ist das, was du hochlädst, auch das,
was läuft. Näheres in [`config/README.md`](../config/README.md).

## Warum ein eigenes Verzeichnis

Zielverzeichnis ist `~/Documents/AutomatisierungCleanVersion`, nicht das alte
`~/Documents/Automatisierung_v2`. Gründe:

- Die alte Ablage hat das flache Layout (`Automatisierung_v2.csproj` im Wurzelordner,
  `dotnet run` ohne Argumente). Das refaktorierte Projekt braucht `src/projects/` neben
  dem Framework und den Aufruf `… <Projektname>`.
- Der alte Ordner bleibt so als `main`-Referenz für den Vergleichslauf (TODO-21) liegen.

## Stolperfallen, die hier schon erledigt sind

- **Zeilenenden:** `.gitattributes` hält `*.sh` auf LF, und das Deploy zieht sie auf
  dem Server nochmal hart nach (`sed -i 's/\r$//'`). Sonst scheitert bash an `$'\r'`.
- **`~/.bashrc` greift nicht:** Bei `ssh host befehl` startet bash nicht-interaktiv,
  und Ubuntus `~/.bashrc` steigt dann sofort wieder aus. Nichts aus `conda init` oder
  den `MESA_*`-Zeilen ist gesetzt. `sim-runner.sh` baut sich seine Umgebung deshalb
  in jedem Aufruf selbst.
- **`pkill -9 -f dotnet`** aus der alten Anleitung träfe *jeden* .NET-Prozess des
  Nutzers. `stop` killt gezielt `OptimierungsAlgoCFD.dll` und dessen Kindprozesse.
- **Xvfb-Reste** werden bewusst nicht abgeschossen: `xvfb-run -a` sucht sich eine
  freie Display-Nummer. Wenn doch nötig: `pkill -9 -f Xvfb`.
- **Tote VPN-Verbindung hängt ewig:** Ohne Zeitgrenze wartet ssh/scp bei einem
  abgerissenen AnyConnect-Tunnel unbegrenzt und ohne Meldung. Seit dem 04.11.2026 gibt
  `sim.ps1` jedem Aufruf großzügige Grenzen mit (`$SshOptions` oben im Skript): eine
  Minute für den Verbindungsaufbau, Abbruch erst nach **3 Minuten** ohne jedes
  Lebenszeichen. Das ist bewusst weit gefasst: Unter Volllast antwortet der Server
  träge, und knappe Werte haben früher gesunde Verbindungen abgebrochen. Ein stiller,
  aber laufender Befehl (Build, `stop`) wird dadurch nicht abgebrochen, weil sshd die
  Lebenszeichen selbst beantwortet. **Bricht die Verbindung ab, rechnet ein Lauf in
  tmux auf dem Server trotzdem weiter.** Der Test
  `ScriptContractTests.Every_Ssh_And_Scp_Call_Carries_The_Timeouts` passt auf, dass
  kein neuer Aufruf ohne Grenzen dazukommt.

## Wenn etwas klemmt

```powershell
.\scripts\sim.ps1 doctor              # Erste Anlaufstelle: Pfade und Werkzeuge
.\scripts\sim.ps1 log -NoFollow -Lines 100
```

Ein Lauf, der sofort wieder endet, wird von `start` erkannt — es werden dann direkt
die letzten Logzeilen ausgegeben. Ältere Logs liegen auf dem Server unter `logs/`.

---

## Einmalige Server-Einrichtung (Linux ohne Sudo / Admin-Rechte)

Wenn das System auf einem neuen Linux-Hochschulserver (Ubuntu, ohne `sudo`-Rechte) von Grund auf eingerichtet werden soll, stehen zwei Wege zur Verfügung: vollautomatisch per Skript oder Schritt für Schritt von Hand.

### Weg 1: Automatische Einrichtung via `setup_server.sh` (Empfohlen)

1. Kopiere [`src/OptimierungsAlgoCFD/setup_server.sh`](../src/OptimierungsAlgoCFD/setup_server.sh) auf den Server und führe es aus:
   ```bash
   chmod +x setup_server.sh
   ./setup_server.sh
   ```
2. **Hinweis zu Anaconda Terms of Service:** Fragt Conda nach der Zustimmung zu den Nutzungsbedingungen, führe einmalig Folgendes aus und starte das Skript danach erneut:
   ```bash
   export PATH=$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH
   export LD_LIBRARY_PATH=$HOME/miniconda/lib:$LD_LIBRARY_PATH
   conda tos accept --override-channels --channel https://repo.anaconda.com/pkgs/main
   conda tos accept --override-channels --channel https://repo.anaconda.com/pkgs/r
   ```
3. **Nach dem Setup:** Einmal aus der SSH-Sitzung ausloggen und neu einloggen, damit die Einträge in `~/.bashrc` aktiv sind.

### Weg 2: Manuelle Installation Schritt für Schritt (ohne Sudo)

Falls einzelne Komponenten von Hand nachinstalliert oder überprüft werden sollen:

#### 1. Ordnerstruktur & Miniconda
```bash
mkdir -p ~/software ~/Documents/AutomatisierungCleanVersion
wget https://repo.anaconda.com/miniconda/Miniconda3-latest-Linux-x86_64.sh -O ~/miniconda.sh
bash ~/miniconda.sh -b -u -p ~/miniconda
~/miniconda/bin/conda init bash
source ~/.bashrc
rm ~/miniconda.sh
```

#### 2. System-Bibliotheken, Xvfb & tmux über Conda
```bash
conda install -y -c conda-forge git boost-cpp tbb-devel blosc pkg-config \
  xorg-libxrandr xorg-libxinerama xorg-libxcursor xorg-libxi xorg-xvfb-server tmux \
  xorg-libxext xorg-randrproto xorg-renderproto xorg-libxrender xorg-xorgproto \
  gcc_linux-64 gxx_linux-64 mesalib libglu freeglut xorg-libx11 openmpi
```

**Wichtig: SU2 braucht MPICH, nicht OpenMPI.** Das offizielle SU2-Binary (Schritt 3) ist statisch mit MPICH gebaut. Unter dem OpenMPI aus dem Befehl oben startet `-n 12` zwölf unabhängige *serielle* Kopien, die sich gegenseitig die Dateien überschreiben, ohne jede Fehlermeldung; die Ergebnisse sind unbrauchbar. Deshalb zusätzlich ein eigenes MPICH-Environment anlegen:
```bash
conda create -y -n mpich -c conda-forge mpich
```
In der `simulation.json` des Projekts steht dann `"MpiRunPath": "/home/<benutzer>/miniconda/envs/mpich/bin/mpiexec"`. Das Programm prüft die Variante beim Start (`[MPI]`-Warnung im Log, Eintrag `MPI-Variante` in `run-info.json`), und `sim.ps1 doctor` meldet OpenMPI als Problem. `setup_server.sh` legt das Environment automatisch an.

#### 3. Externe Werkzeuge herunterladen (CMake, SU2, Gmsh)
```bash
cd ~/software

# CMake (3.29.3)
wget https://github.com/Kitware/CMake/releases/download/v3.29.3/cmake-3.29.3-linux-x86_64.tar.gz
tar -zxvf cmake-3.29.3-linux-x86_64.tar.gz && rm cmake-3.29.3-linux-x86_64.tar.gz

# SU2 CFD (v7.5.1 mit MPI-Support)
wget https://github.com/su2code/SU2/releases/download/v7.5.1/SU2-v7.5.1-linux64-mpi.zip -O su2.zip
unzip su2.zip
mv SU2-v7.5.1-linux64-mpi SU2-7.5.1 || mv SU2-v7.5.1-linux64 SU2-7.5.1 || mv bin SU2-7.5.1
rm su2.zip
mkdir -p $HOME/software/bin
if [ -d "$HOME/software/SU2-7.5.1/bin" ]; then
    cp -r $HOME/software/SU2-7.5.1/bin/* $HOME/software/bin/
else
    cp -r $HOME/software/SU2-7.5.1/* $HOME/software/bin/
fi
chmod +x $HOME/software/bin/SU2_CFD

# Gmsh (4.11.1)
wget https://gmsh.info/bin/Linux/gmsh-4.11.1-Linux64.tgz
tar -zxvf gmsh-4.11.1-Linux64.tgz && rm gmsh-4.11.1-Linux64.tgz
```

#### 4. OpenGL-Header für Mesa bereitstellen (Sudo-Bypass)
Da ohne `sudo` kein Schreibzugriff auf `/usr/include` besteht, werden die für PicoGK nötigen OpenGL-Header direkt in Condas `include`-Ordner geladen:
```bash
mkdir -p $HOME/miniconda/include/GL $HOME/miniconda/include/KHR
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/gl.h -O $HOME/miniconda/include/GL/gl.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glext.h -O $HOME/miniconda/include/GL/glext.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/GL/glcorearb.h -O $HOME/miniconda/include/GL/glcorearb.h
wget --no-check-certificate https://gitlab.freedesktop.org/mesa/mesa/-/raw/main/include/KHR/khrplatform.h -O $HOME/miniconda/include/KHR/khrplatform.h
```

#### 5. PicoGK Runtime kompilieren (Headless-Modus mit X11)
```bash
cd ~/software
git clone --recursive https://github.com/leap71/PicoGKRuntime.git ~/software/PicoGK

# C++ Typ-Fix in PicoGKTrace.h (oMS ist std::chrono::duration, .count() liefert den Skalar):
sed -i 's/std::cerr << oMS << " elapsed\\n";/std::cerr << oMS.count() << " elapsed\\n";/g' ~/software/PicoGK/Source/PicoGKTrace.h

export PKG_CONFIG_PATH=$HOME/miniconda/lib/pkgconfig:$PKG_CONFIG_PATH
export CFLAGS="-I$HOME/miniconda/include"
export CXXFLAGS="-I$HOME/miniconda/include"
export LDFLAGS="-L$HOME/miniconda/lib"

rm -rf ~/software/PicoGK/build/* || true
mkdir -p ~/software/PicoGK/build && cd ~/software/PicoGK/build

# WICHTIG: -DGLFW_BUILD_X11=ON ist Pflicht, damit xvfb-run das virtuelle Display anbinden kann!
~/software/cmake-3.29.3-linux-x86_64/bin/cmake \
  -DCMAKE_PREFIX_PATH=$HOME/miniconda \
  -DBoost_ROOT=$HOME/miniconda \
  -DBoost_NO_SYSTEM_PATHS=ON \
  -DPICOGK_NO_VIEWER=ON \
  -DGLFW_BUILD_WAYLAND=OFF \
  -DGLFW_BUILD_X11=ON \
  -DCMAKE_POLICY_DEFAULT_CMP0144=NEW \
  -DPKG_CONFIG_EXECUTABLE=$HOME/miniconda/bin/pkg-config ..

make -j $(nproc)

# Bibliothek unter beiden Namen (libpicogk.so und libpicogk.26.2.so) bereitstellen:
TARGET_BIN="$HOME/Documents/AutomatisierungCleanVersion/src/OptimierungsAlgoCFD/bin/Debug/net9.0"
mkdir -p "$TARGET_BIN"
PICOGK_LIB=$(find ~/software/PicoGK/build -iname "*picogk.so" | head -n 1)
cp "$PICOGK_LIB" "$TARGET_BIN/libpicogk.so"
cp "$PICOGK_LIB" "$TARGET_BIN/libpicogk.26.2.so"
```

#### 6. .NET 9 SDK & Umgebungsvariablen in `~/.bashrc`
```bash
cd ~/software
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x ./dotnet-install.sh
env LD_LIBRARY_PATH="" ./dotnet-install.sh --channel 9.0
rm dotnet-install.sh

cat << 'EOF' >> ~/.bashrc

# --- PicoGK Automated CFD Framework ---
export PATH=$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH
export LD_LIBRARY_PATH=$HOME/miniconda/lib:${LD_LIBRARY_PATH:-}
export HWLOC_HIDE_ERRORS=1
export LIBGL_ALWAYS_SOFTWARE=1
export MESA_GL_VERSION_OVERRIDE=4.1
export MESA_GLSL_VERSION_OVERRIDE=410
EOF

source ~/.bashrc
```

### Manueller Start auf dem Server (falls ohne `sim-runner.sh` gearbeitet wird)

Im Normalbetrieb erledigt `sim.ps1` bzw. `sim-runner.sh` alle diese Schritte automatisch. Zur Referenz die manuellen Befehle:

```bash
# 1. Tmux-Sitzung öffnen
~/miniconda/bin/tmux new -s sim

# 2. Umgebung laden & Lauf mit xvfb-run starten
cd ~/Documents/AutomatisierungCleanVersion
export PATH=$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH
export LD_LIBRARY_PATH=$HOME/miniconda/lib:${LD_LIBRARY_PATH:-}
export HWLOC_HIDE_ERRORS=1
export LIBGL_ALWAYS_SOFTWARE=1
xvfb-run -a dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv > simulation.log 2>&1

# 3. Aus tmux abkoppeln (Detach): Strg+B drücken, loslassen, dann D drücken
# Wieder einhängen: ~/miniconda/bin/tmux attach -t sim

# 4. Manueller Not-Aus (nur eigene Prozesse, niemals globales 'pkill dotnet'!):
~/miniconda/bin/tmux kill-session -t sim
pkill -9 -f OptimierungsAlgoCFD.dll || true
pkill -9 -f SU2_CFD || true
pkill -9 -f mpirun || true
pkill -9 -f Xvfb || true
```

### Troubleshooting & Fehlerbehebung auf dem Server

- **Rechnung wirkt falsch, SU2 läuft zwölfmal einzeln, `[MPI]`-Warnung im Log:**  
  `MpiRunPath` zeigt auf OpenMPI (z. B. `miniconda/bin/mpirun`). Das offizielle SU2-Binary ist mit MPICH gebaut. Auf `$HOME/miniconda/envs/mpich/bin/mpiexec` umstellen (Einrichtung siehe Schritt 2). Der Standardwert `"mpirun"` reicht auf dem Server **nicht**: Er findet über den PATH das OpenMPI.
- **`symbol lookup error` bei `mpirun` / OpenMPI:**  
  Das System greift auf ein unpassendes globales OpenMPI zu. Das MPICH-`mpiexec` aus dem Environment `mpich` verwenden (siehe oben) und in `simulation.json` unter `MpiRunPath` mit vollem Pfad eintragen.
- **`prterun was unable to launch` / `Executable not found`:**  
  `SU2_CFD` liegt nicht unter dem konfigurierten Pfad oder ist nicht ausführbar. Prüfen mit `ls -l ~/software/bin/SU2_CFD` und ggf. `chmod +x ~/software/bin/SU2_CFD`.
- **`DllNotFoundException: Unable to load shared library 'libpicogk'`:**  
  `dotnet build` hat das Ausgabeverzeichnis bereinigt. `sim-runner.sh` kopiert die Bibliothek automatisch nach; bei manuellem Build beide Dateien (`libpicogk.so` und `libpicogk.26.2.so`) wie in Schritt 5 wieder nach `src/OptimierungsAlgoCFD/bin/Debug/net9.0/` kopieren.
- **CPU-Auslastung nur 100 % (Single-Core statt Multi-Core):**  
  SU2 läuft nicht über `mpirun` oder `MpiCores` steht auf `1`. In `src/projects/<Projekt>/simulation.json` prüfen, ob `MpiCores` (z. B. `12`) und `MpiRunPath` gesetzt sind, und mit `top -u $USER` kontrollieren, ob mehrere `SU2_CFD`-Prozesse starten.
- **`X connection broken` / `Display already in use`:**  
  Ein alter `Xvfb`-Prozess blockiert das Display. Immer `xvfb-run -a` (automatische Display-Wahl) verwenden und hängende Instanzen bei Bedarf mit `pkill -9 -f Xvfb` beenden.

