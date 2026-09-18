# Lauf auf dem Linux-Server steuern

Zwei Skripte, eine Aufgabe: den Optimierungslauf von Windows aus auf dem
Hochschulrechner starten und beobachten.

| Datei | Läuft auf | Aufgabe |
|---|---|---|
| `sim.ps1` | Windows | Code packen, per SSH hochladen, Befehle absetzen, Ergebnisse holen |
| `sim-runner.sh` | Linux | tmux, xvfb-run, Pfade, Build, Start/Stop/Status |

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
.\scripts\sim.ps1 doctor    # Prüft SU2, mpirun, gmsh, dotnet, tmux, libpicogk.so
                            # (lädt beim allerersten Aufruf den Code hoch, baut aber nicht)
.\scripts\sim.ps1 run       # Hochladen, bauen, in tmux starten
.\scripts\sim.ps1 log       # Live zusehen (Strg+C beendet nur die Anzeige)
.\scripts\sim.ps1 status    # Läuft was? Seit wann? Wie viele Datensätze?
.\scripts\sim.ps1 fetch     # CSV + Log nach .\Serverergebnisse\ holen
.\scripts\sim.ps1 stop      # Lauf und hängende Rechenprozesse beenden
```

## Ergebnisse herunterladen

Alles landet unter `.\Serverergebnisse\<Server>_<Zeitstempel>\` — der Zeitstempel
verhindert, dass ein späterer Abruf einen früheren überschreibt. Der Ordner ist
gitignored.

### Nur das Ergebnis (Standard)

```powershell
.\scripts\sim.ps1 fetch
```

Holt `Simulation_Results.csv` und `simulation.log` — zusammen wenige KB.
Das ist der Normalfall: die CSV *ist* das Ergebnis der Optimierung.

### Der komplette Ergebnisse-Ordner

```powershell
.\scripts\sim.ps1 fetch -All
```

Kopiert alles: Geometrien, Netze, ParaView-Dateien, SU2-Arbeitsdateien.

> **Größe beachten.** Pro Variante fallen rund 15–20 MB an (STL 1–3 MB,
> SU2-Netz 4–8 MB, zwei `.vtu` 4–9 MB). Ein Probelauf mit 2 Varianten liegt bei
> ~54 MB, ein voller Lauf mit 10 × 10 Varianten bei **1,5–2 GB**.

### Einzelne Modelle gezielt holen

Dafür braucht es kein Skript — `scp` expandiert Platzhalter auf dem Server.
Der Pfad ist immer `Documents/AutomatisierungCleanVersion/Ergebnisse/…`:

```powershell
# Alle Dateien einer bestimmten Variante (Geometrie, Netz, Gmsh-Skript)
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/*Gen1_Var2* .\Serverergebnisse\

# Nur die Geometrie zum Anschauen im CAD/Slicer
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/Model_Gen1_Var2.stl .\Serverergebnisse\

# Nur die ParaView-Dateien einer Variante (Oberfläche + Strömungsfeld)
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/Analyseergebnisse/*Gen1_Var2* .\Serverergebnisse\

# Alle ParaView-Oberflächen aller Varianten, ohne die großen Volumendateien
scp BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/Analyseergebnisse/Surface_* .\Serverergebnisse\

# Fehlerprotokolle, wenn eine Simulation abgebrochen ist
scp -r BIC_12:Documents/AutomatisierungCleanVersion/Ergebnisse/FehlerLogs .\Serverergebnisse\
```

Welche Variante die interessante ist, steht in der CSV (höchste `Fitness`) oder
am Ende von `simulation.log` unter „ABSOLUTER CHAMPION".

Erst nachsehen, was überhaupt da ist:

```powershell
ssh BIC_12 'ls -lhS Documents/AutomatisierungCleanVersion/Ergebnisse'
ssh BIC_12 'du -sh Documents/AutomatisierungCleanVersion/Ergebnisse'
```

### Was im Ergebnisse-Ordner liegt

| Datei | Bedeutung |
|---|---|
| `Simulation_Results.csv` | **Das Ergebnis.** Eine Zeile je Variante, alle Parameter und Metriken |
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
100 %" in `ServerHochschuleEinrichten2.txt`).

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

Weil `*.local.json` nie übertragen wird, überlebt eine `config/simulation.local.json`
**auf dem Server** jedes Deploy. Genau der richtige Ort für Abweichungen, die nur
dort gelten — etwa ein kleiner Probelauf:

```json
{ "MaxIterations": 1, "VariantsPerIteration": 2 }
```

Entpackt wird über den vorhandenen Stand drüber. Gelöschte Dateien verschwinden
dadurch nicht automatisch vom Server; bei Bedarf dort einmal `rm -rf src` und neu
deployen.

## Warum ein eigenes Verzeichnis

Zielverzeichnis ist `~/Documents/AutomatisierungCleanVersion`, nicht das alte
`~/Documents/Automatisierung_v2`. Gründe:

- Die alte Ablage hat das flache Layout (`Automatisierung_v2.csproj` im Wurzelordner,
  `dotnet run` ohne Argumente). Das refaktorierte Projekt braucht `src/` und `config/`
  nebeneinander und den Aufruf `… MantaAuv <configdir>`.
- Der alte Ordner bleibt so als `main`-Referenz für den Vergleichslauf (TODO-21) liegen.

## Stolperfallen, die hier schon erledigt sind

- **Zeilenenden:** `.gitattributes` hält `*.sh` auf LF, und das Deploy zieht sie auf
  dem Server nochmal hart nach (`sed -i 's/\r$//'`). Sonst scheitert bash an `$'\r'`.
- **`~/.bashrc` greift nicht:** Bei `ssh host befehl` startet bash nicht-interaktiv,
  und Ubuntus `~/.bashrc` steigt dann sofort wieder aus. Nichts aus `conda init` oder
  den `MESA_*`-Zeilen ist gesetzt. `sim-runner.sh` baut sich seine Umgebung deshalb
  in jedem Aufruf selbst.
- **`pkill -9 -f dotnet`** aus der alten Anleitung träfe *jeden* .NET-Prozess des
  Nutzers. `stop` killt gezielt `Automatisierung_v2.dll` und dessen Kindprozesse.
- **Xvfb-Reste** werden bewusst nicht abgeschossen: `xvfb-run -a` sucht sich eine
  freie Display-Nummer. Wenn doch nötig: `pkill -9 -f Xvfb`.

## Wenn etwas klemmt

```powershell
.\scripts\sim.ps1 doctor              # Erste Anlaufstelle: Pfade und Werkzeuge
.\scripts\sim.ps1 log -NoFollow -Lines 100
```

Ein Lauf, der sofort wieder endet, wird von `start` erkannt — es werden dann direkt
die letzten Logzeilen ausgegeben. Ältere Logs liegen auf dem Server unter `logs/`.
