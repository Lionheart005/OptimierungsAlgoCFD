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
.\scripts\sim.ps1 run       # Hochladen, bauen, in tmux starten
.\scripts\sim.ps1 log       # Live zusehen (Strg+C beendet nur die Anzeige)
.\scripts\sim.ps1 status    # Läuft was? Seit wann? Wie viele Datensätze?
.\scripts\sim.ps1 fetch     # CSV + Log nach .\Serverergebnisse\ holen
.\scripts\sim.ps1 stop      # Lauf und hängende Rechenprozesse beenden
```

Anderer Rechner oder anderes Projekt:

```powershell
.\scripts\sim.ps1 run -Server BIC_8 -Project MantaAuv
```

Nach `run` kann die Konsole zu — der Lauf hängt in einer tmux-Sitzung und
überlebt das Ende der SSH-Verbindung.

## Was beim Deploy passiert

1. `src/`, `config/` und `scripts/` werden gepackt — **ohne** `bin/`, `obj/` und
   **ohne** `*.local.json`.
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
