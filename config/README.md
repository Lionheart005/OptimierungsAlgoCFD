# `config/` — der Notausgang

**Hier gibt es im Normalfall nichts einzutragen.** Dieser Ordner ist seit TODO-24 leer
bis auf diese Datei — sie hält ihn nur in git, damit er auf einem frischen Rechner
existiert.

## Wo die Einstellungen jetzt stehen

Alles, was für ein Projekt eingestellt werden muss, liegt in **einem** Ordner:

```
src/projects/MantaAuv/
  project.json          Parameter, Grenzen, Optimierungsziele
  simulation.json       Framework-Werte (Laufumfang, Pfade, Verfahren)
  solvers/su2.json      SU2: Physik, Referenzwerte, Ergebnisgrößen
  solvers/gmsh.json     Gmsh: Windkanal, Grenzschicht
  *.cs                  Geometrie und Fitness
```

Der Ordnername ist der Projektname. Umgeschaltet wird nur über

```powershell
.\scripts\sim.ps1 run -Project MantaAuv
```

Ein zweites Projekt ist ein zweiter Ordner daneben und kommt dem ersten nicht in die
Quere — weder bei den Zahlen noch bei den Ergebnissen.

## Wozu dieser Ordner dann noch da ist

Für **eine** Sorte Datei: `*.local.json`. Das ist eine gitignorierte Ausnahmedatei für
genau einen Rechner. Sie überlagert die Projektdatei und wird von `sim.ps1` bewusst
**nicht** mit hochgeladen — eine Datei, die hier auf dem Server liegt, überlebt jedes
Deploy und wird von keinem überschrieben.

| Datei | Gilt für |
|---|---|
| `config/simulation.local.json` | alle Projekte auf diesem Rechner |
| `config/solvers/su2.local.json` | alle Projekte auf diesem Rechner |
| `src/projects/<Name>/simulation.local.json` | nur dieses Projekt, nur dieser Rechner |

Je spezifischer, desto später — und gitignoriert schlägt eingecheckt.

## Warum es diese Dateien trotzdem nicht geben soll

Der einzige Grund für sie war, dass der Server andere Programmpfade braucht als der
Windows-Entwicklungsrechner. Seit TODO-24 steht im **Code-Standard** aber `mpirun`,
`SU2_CFD` und `gmsh` ohne Verzeichnis, und `sim-runner.sh load_env` legt die passenden
Verzeichnisse auf den `PATH`.

Und wenn ein Rechner sie dort trotzdem nicht findet, ist der richtige Ort dafür die
`simulation.json` **des Projekts** — eingecheckt, sichtbar, mitdeployt. MantaAuv macht
genau das: sie trägt die drei absoluten Pfade des Hochschulservers, weil bei
`ssh host befehl` keine interaktive Shell startet und dort nichts aus `conda init`
greift. Auch das ist also kein Grund für eine Datei in diesem Ordner.

Alles andere — Laufumfang, Physik, Netzfeinheit — ist Projektsache und gehört nach
`src/projects/<Name>/`. Eine `*.local.json` mit solchen Werten ist ein verstecktes
zweites Stellrad: was du hochlädst, ist dann nicht, was läuft.

> **Sollstand: es existiert nirgends eine `*.local.json`.**
> Findet der Start doch eine, meldet er sie mit einem auffälligen Block — samt jedem
> einzelnen Schlüssel, den sie dem Projekt aushebelt, und beiden Werten.

Wenn `.\scripts\sim.ps1 doctor` auf einem Rechner meldet, dass `mpirun` oder `SU2_CFD`
nicht auf dem `PATH` liegen, ist eine `config/simulation.local.json` mit **ausschließlich**
den drei Pfad-Schlüsseln der legitime Notausgang:

```jsonc
{
  "MpiRunPath": "/pfad/zu/mpirun",
  "Su2Path": "/pfad/zu/SU2_CFD",
  "GmshPath": "/pfad/zu/gmsh"
}
```

Der Warnblock erinnert dich dann bei jedem Start daran, dass dieser Rechner eine
Sonderlocke hat.

## Löschen geht nur vor Ort

Ein Deploy kann eine `*.local.json` weder anlegen noch ändern noch löschen — sie ist vom
Upload ausgeschlossen. Auf dem Server entfernt man sie per SSH:

```powershell
ssh BIC_12 "cat ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"   # erst anschauen
ssh BIC_12 "rm  ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"
```
