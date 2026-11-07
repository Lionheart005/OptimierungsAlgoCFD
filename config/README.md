# `config/` — Notausgang für maschinenspezifische Ausnahmen (`*.local.json`)

> **Weiterführende Dokumente (ohne Doppelung):**
> - **Bedienung & Alle JSON-Einstellungen (Stufen 1–4):** [`src/projects/BEDIENHANDBUCH.md`](../src/projects/BEDIENHANDBUCH.md)
> - **Architektur des Konfigurations-Loaders:** [`src/Automatisierung_v2/README.md`](../src/Automatisierung_v2/README.md)
> - **Server-Steuerung & Deployment:** [`scripts/README.md`](../scripts/README.md)

**Im Normalfall bleibt dieser Ordner bis auf diese README komplett leer.** Alle regulären Einstellungen eines Projekts liegen direkt in dessen Projektordner unter `src/projects/<Projekt>/` (`project.json`, `simulation.json`, `solvers/su2.json`, `solvers/gmsh.json`). Ein Test (`ProjectLayoutTests.The_Config_Folder_Holds_No_Checked_In_Json`) stellt sicher, dass hier niemals versehentlich reguläre JSON-Dateien eingecheckt werden.

## Wozu dieser Ordner dient

Er ist der Ablageort für gitignorierte **`*.local.json`**-Ausnahmedateien, die ausschließlich auf **diesem einen Rechner** für alle Projekte gelten sollen (Stufe 3 der Konfigurationshierarchie; eine `src/projects/<Name>/….local.json` wäre Stufe 4 für genau ein Projekt auf diesem Rechner).

Die Deploy-Skripte (`scripts/sim.ps1`) übertragen `*.local.json`-Dateien bewusst **nicht** und löschen sie beim Aufräumen auf dem Server auch nicht.

## Wann man eine `*.local.json` (nicht) verwenden sollte

- **Sollstand ist, dass nirgends eine `*.local.json` existiert.** Jede vorhandene `*.local.json` erzeugt beim Programmstart einen unübersehbaren `[WARNUNG]`-Block im Log, der jeden überschriebenen Schlüssel samt Projekt- und Local-Wert auflistet, und wird in `effective-config.json` unter `MaschinenspezifischeUeberschreibungen` dokumentiert.
- **Nie für kurze Testläufe missbrauchen:** Wer `MaxIterations` oder Physik-Werte testen will, ändert sie direkt in `src/projects/<Projekt>/simulation.json`, damit das, was hochgeladen wird, auch das ist, was rechnet.
- **Einziger legitimer Anwendungsfall:** Ein Rechner mit ungewöhnlichen Installationspfaden, auf dem `mpirun`, `SU2_CFD` oder `gmsh` weder über den `PATH` noch über die Projekt-Pfade gefunden werden:
  ```jsonc
  // config/simulation.local.json (gitignoriert)
  {
    "MpiRunPath": "/pfad/zu/mpirun",
    "Su2Path": "/pfad/zu/SU2_CFD",
    "GmshPath": "/pfad/zu/gmsh"
  }
  ```

## Eine `*.local.json` auf dem Server prüfen oder löschen

Da `sim.ps1 deploy` lokale Ausnahmedateien auf dem Server niemals anfasst, lassen sie sich dort nur direkt per SSH einsehen und entfernen:

```powershell
ssh BIC_12 "cat ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"   # Inhalt prüfen
ssh BIC_12 "rm  ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"   # Datei löschen
```
