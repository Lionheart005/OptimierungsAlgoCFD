# Automatisierte Formoptimierung (PicoGK + Gmsh + SU2)

Das Framework sucht selbstständig die beste Form für ein Bauteil. Es baut eine
Geometrie aus Parametern, vernetzt sie, lässt eine Strömungssimulation darüber
laufen, bewertet das Ergebnis mit einer Zielfunktion und leitet daraus die nächste
Variante ab — hunderte Male hintereinander, ohne Zutun.

```
Parameter  ->  Geometrie (PicoGK)  ->  Netz (Gmsh)  ->  Strömung (SU2)  ->  Fitness
     ^                                                                        |
     +------------------  Optimierungsalgorithmus  <-------------------------+
```

Die Geometrie entsteht aus **Voxeln**, nicht aus CAD-Flächen. Das ist der Grund,
warum die Kette nicht reißt: Bauteile dürfen sich beliebig durchdringen oder
verschmelzen, ohne dass eine Flächenverschneidung fehlschlägt.

Dieses Dokument hat zwei Teile:

* **[Teil A — Bedienen](#teil-a--bedienen)** für alle, die Läufe starten und
  Einstellungen ändern wollen. Kein C# nötig.
* **[Teil B — Entwickeln](#teil-b--entwickeln)** für alle, die den Code ändern
  oder ein neues Projekt anlegen wollen.

Wie ein Lauf auf dem Linux-Server gestartet wird, steht in
[`scripts/README.md`](../../scripts/README.md).

---

# Teil A — Bedienen

## Was bei einem Lauf passiert

Ein Lauf besteht aus **Iterationen**, jede Iteration aus mehreren **Varianten**.
Eine Variante ist ein kompletter Durchlauf durch die Kette:

1. **Parameter würfeln.** Der Optimierungsalgorithmus schlägt einen Parametersatz
   vor (z.B. Länge 54 mm, Breite 52 mm …).
2. **Schrumpfen.** Der *RubberBandScaler* rechnet die Maße auf eine einheitliche
   Arbeitsgröße herunter, damit die Voxel-Auflösung bei jeder Variante gleich fein
   wirkt. Am Ende werden die Messwerte wieder hochgerechnet.
3. **Geometrie bauen.** PicoGK erzeugt das Voxelmodell und exportiert eine STL.
   Optional wird sie geglättet.
4. **Vernetzen.** Gmsh legt einen Windkanal um das Modell und erzeugt das Rechennetz.
5. **Rechnen.** SU2 löst die Strömung und liefert den Widerstandsbeiwert `CD`.
6. **Bewerten.** Die Fitness-Funktion des Projekts verrechnet alle Messwerte zu
   einer einzigen Zahl. Groß = gut.

Nach allen Varianten einer Iteration:

7. **Champion küren.** Der beste Parametersatz der Iteration wird bestimmt.
8. **Türsteher (Bouncer).** Der Champion wird mit minimal verstellten Parametern
   **noch einmal komplett gerechnet**. Weicht seine Fitness zu stark ab, war er ein
   Zufallstreffer und wird verworfen — dann kommt der Nächstbeste dran. Das kostet
   eine zusätzliche Simulation je Iteration (sie läuft unter der Variantennummer 99).
9. **Nächste Runde vorbereiten.** Der Algorithmus zieht seine Schlüsse.

Am Ende wird der beste Lauf **des gesamten Durchgangs** gemeldet, nicht der
Gewinner der letzten Iteration.

## Welches Projekt gerechnet wird

Ein „Projekt" ist die Kombination aus Geometrie-Bauplan, Zielfunktion und
Parametervorgaben — zum Beispiel `MantaAuv`. Der Projektname ist **kein Eintrag in
einer Konfigurationsdatei**, sondern ein **Argument beim Start**:

| Wo du startest | So wählst du das Projekt |
|---|---|
| Server, über die Skripte | `.\scripts\sim.ps1 run -Project MantaAuv` |
| Direkt auf dem Server per SSH | `SIM_PROJECT=MantaAuv bash scripts/sim-runner.sh start` |
| Lokal auf deinem Rechner | `dotnet run --project src/Automatisierung_v2 -- MantaAuv` |

Ohne Angabe wird überall **`MantaAuv`** genommen. Die Vorgabe steht an drei
Stellen, je nachdem, welche du dauerhaft ändern willst:

* [`scripts/sim.ps1`](../../scripts/sim.ps1) — Parameter `$Project = 'MantaAuv'`
* [`scripts/sim-runner.sh`](../../scripts/sim-runner.sh) — `SIM_PROJECT:-MantaAuv`
* [`Program.cs`](Program.cs) — `args.Length > 0 ? args[0] : "MantaAuv"`

Der Name steuert zwei Dinge gleichzeitig:

1. **Welcher C#-Code läuft** — der `switch` in [`Program.cs`](Program.cs) verdrahtet
   Geometrie-Generator und Fitness-Funktion des Projekts.
2. **Welche Konfigurationsdatei gilt** — `config/projects/<Name>.json`.

Ein Name, für den es keinen `case` gibt, bricht sofort mit
`Unbekanntes Projekt: <Name>` ab. **Es reicht also nicht, eine neue JSON-Datei
anzulegen** — ein neues Projekt braucht auch Code (siehe
[Neues Projekt anlegen](#neues-projekt-anlegen)).

Der zweite Startparameter ist optional und gibt das Konfigurationsverzeichnis an:

```bash
dotnet run --project src/Automatisierung_v2 -- MantaAuv /pfad/zu/config
```

Ohne Angabe wird `config/` im Arbeitsverzeichnis gesucht und notfalls bis zu sechs
Ebenen darüber — deshalb funktioniert `dotnet run` auch aus dem Projektordner heraus.

## Die Konfigurationsdateien

Alle liegen unter `config/` im Wurzelverzeichnis des Repos:

```
config/
├── simulation.json            Framework: Pfade, Umfang, Physik, Algorithmuswahl
├── projects/MantaAuv.json     Projekt: Startwerte, Grenzen, Optimierungsziele
└── solvers/
    ├── su2.json               Strömungslöser: Fluid, Referenzwerte, CFL
    └── gmsh.json              Vernetzer: Windkanal, Netzfeinheit
```

**Es muss nichts davon vorhanden sein.** Jeder Wert hat eine im Code hinterlegte
Vorgabe; die Datei überschreibt nur, was sie selbst nennt. Eine fehlende Datei ist
kein Fehler, eine kaputte JSON bricht mit Dateinamen ab.

### Die `.local.json`-Regel

Neben jeder Datei darf eine `*.local.json` liegen, die sie überlagert:

```
simulation.json          <- gehört ins Repo, gilt für alle
simulation.local.json    <- nur auf diesem Rechner, gitignored
```

Die Reihenfolge ist: **Code-Vorgabe → Basisdatei → local-Datei.** Genutzt wird das
für alles, was nur auf einer Maschine gilt. Beispiel für einen kurzen Probelauf auf
dem Server:

```jsonc
// config/simulation.local.json
{
  "MaxIterations": 1,
  "VariantsPerIteration": 2
}
```

Die Deploy-Skripte übertragen `*.local.json` bewusst **nicht** — eine solche Datei
auf dem Server überlebt also jedes Deploy und wird nie von einer Windows-Fassung
überschrieben.

In den JSON-Dateien sind `//`-Kommentare und nachgestellte Kommata erlaubt.

## `config/simulation.json` — Framework

### Pfade zu externer Software

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `MpiRunPath` | `/home/lpleissner/miniconda/bin/mpirun` | Startet SU2 auf mehreren Kernen |
| `Su2Path` | `/home/lpleissner/software/bin/SU2_CFD` | Der Strömungslöser |
| `GmshPath` | `gmsh` | Der Vernetzer; ohne Pfad wird er im `PATH` gesucht |

Stimmt hier etwas nicht, meldet `.\scripts\sim.ps1 doctor` das, bevor ein Lauf startet.

### Umfang des Laufs

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `MaxIterations` | `10` | Anzahl Iterationen (Generationen) |
| `VariantsPerIteration` | `10` | Varianten je Iteration |
| `BaseVoxelResolution` | `0.5` | Voxelgröße in mm. Kleiner = feiner = deutlich langsamer |
| `TargetPicoGkSize` | `50.0` | Arbeitsgröße in mm, auf die der Scaler jede Variante bringt |
| `VoxelSmoothingIterations` | `40` | Glättungsdurchgänge der STL. `0` = keine Glättung |
| `VoxelSmoothingPremeltingSteps` | `2` | Vorgelagertes „Anschmelzen" gegen Voxel-Treppen |

Die Gesamtzahl der Simulationen ist `MaxIterations × VariantsPerIteration` **plus
eine Validierung je Iteration**. Vorgabe also 10 × 10 + 10 = 110 Läufe.

### Abschaltbare Bausteine

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `UseRubberBandScaler` | `true` | Aus: es wird mit den echten Maßen gerechnet, `ScaleFactor` bleibt 1 |
| `UseStlSmoothing` | `true` | Aus: die STL kommt unbearbeitet aus dem Voxelmodell |

### Strömung

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `MachNumber` | `0.1` | Anströmung; wird mit `SpeedOfSound` aus `su2.json` in m/s umgerechnet |
| `Su2MaxIterations` | `500` | Obergrenze der SU2-Iterationen je Simulation |
| `CauchyElements` | `150` | Über so viele Iterationen muss der Widerstand ruhig sein |
| `CauchyTolerance` | `"1e-4"` | Wie ruhig. Kleiner = genauer = langsamer |
| `MpiCores` | `12` | Kerne für SU2 |
| `GmshCores` | `12` | Kerne für die Vernetzung |

> **Zum Nachprüfen:** `MachNumber 0.1 × SpeedOfSound 343.2` ergibt 34,3 m/s — die
> Schallgeschwindigkeit ist die von **Luft**, während `su2.json` mit Dichte 1025
> und Viskosität 1,001e-3 **Wasser** beschreibt. Für ein Unterwasserfahrzeug ist
> das eine sehr hohe Geschwindigkeit. Wer die Zahlen physikalisch belastbar
> braucht, sollte hier einmal nachrechnen.

### Wahl des Optimierungsverfahrens

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `OptimizationAlgorithm` | `"Rsm"` | `"Rsm"` oder `"Evolution"` |

Groß-/Kleinschreibung und Leerzeichen sind egal. Ein unbekannter Name bricht
**nicht** ab, sondern warnt auf der Konsole und nutzt `"Rsm"`.

**`"Rsm"` — Antwortflächen-Verfahren.** Sammelt zuerst Stützstellen (DoE-Phase),
baut daraus ein mathematisches Ersatzmodell und simuliert darauf 50.000 Varianten
in Millisekunden. Nur die vielversprechendsten gehen in den echten Windkanal. Sehr
effizient bei 3–15 Parametern.

**`"Evolution"` — evolutionärer Algorithmus.** Mutiert den bisher besten
Parametersatz. Verteilt die Varianten auf vier Rollen (Feintuner bis Entdecker) und
zieht die Streuung nach der 1/5-Erfolgsregel enger oder weiter. Besser geeignet,
wenn es sehr viele Parameter gibt.

### Feineinstellung „Evolution"

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `MinimalDeviationExploration` | `1.0` | Mindestabstand zu bekannten Punkten (Entdecker-Rollen) |
| `MinimalDeviationExploitation` | `0.05` | Mindestabstand für Feintuner |
| `RoleFineTuner` | `0.2` | Bis zu diesem Anteil der Varianten: kleine Schritte |
| `RoleCautious` | `0.4` | … vorsichtige Schritte |
| `RoleNormal` | `0.6` | … normale Schritte; darüber: große Sprünge |

### Feineinstellung „Rsm"

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `RsmVirtualSimulations` | `50000` | Wie viele Varianten auf der Antwortfläche durchgerechnet werden |
| `RsmIdwPower` | `3.0` | Gewichtung naher Stützstellen. Höher = stärker lokal |
| `RsmExploitationRatio` | `0.7` | Anteil der Vorschläge nahe am bekannten Optimum |
| `RsmExploitationSigma` | `0.1` | Streuung dieser Vorschläge, als Anteil des Parameterbereichs |

Bevor das Ersatzmodell greift, braucht der RSM `((k+1)(k+2))/2 + 2` **gelungene**
Simulationen, bei `k` Parametern. Für die fünf Manta-Parameter also 23. Vorher
würfelt er gleichverteilt im erlaubten Bereich; im Log steht dann
`Benötige noch N Basis-Simulationen (DoE)`.

### Türsteher (Bouncer)

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `BouncerTolerance` | `0.2` | Erlaubte relative Abweichung der Fitness bei der Kontrollrechnung |
| `BouncerJitter` | `0.01` | Betrag, um den ein zufälliger Parameter dafür verstellt wird |

> **Wert prüfen.** Der Türsteher vergleicht seit dem Refactoring die **Fitness**
> statt nur den Widerstand. Die Fitness reagiert empfindlicher (sie geht mit
> `Drag^DragBalanceFactor` ein), dieselbe Toleranz ist dadurch **strenger** als
> früher. Meldet das Log häufig `Kein Modell stabil`, ist der Wert zu klein.

## `config/projects/MantaAuv.json` — Projekt

| Block | Bedeutung |
|---|---|
| `ProjectName` | Name, muss zum `case` in `Program.cs` passen |
| `BaseParameters` | Startwerte. Die Namen bestimmen, welche Parameter es überhaupt gibt |
| `MaxDeviations` | Anfängliche Mutationsstärke je Parameter (nur „Evolution") |
| `ParameterBounds` | Harte Unter- und Obergrenzen: `{ "Min": 30, "Max": 100 }` |
| `OptimizationTargets` | Zahlen, die in die Fitness-Formel eingehen |
| `DimensionalParameters` | Welche Parameter Längen sind und beim Schrumpfen mitskaliert werden |

Für `MantaAuv` sind die Parameter `Length`, `Width`, `MainRadius`, `WingRadius`
(alle in mm) und `TailTaper` (dimensionslos, Verjüngung des Hecks).

`TailTaper` steht bewusst **nicht** in `DimensionalParameters`: Es ist ein
Verhältnis. Stünde es dort, würde es beim Schrumpfen mitskaliert und die Form
verändern — genau das war ein Fehler in der Vorgängerversion.

Die Optimierungsziele:

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `MinimumAllowedVolume` | `2000` | Unter diesem Volumen (mm³) gibt es Strafe |
| `MaximumAllowedVolume` | `85000` | Darüber ebenfalls |
| `DragBalanceFactor` | `3.0` | Wie stark der Widerstand die Fitness bestimmt |

Alle drei sind **Pflicht**. Fehlt einer, bricht das Programm schon beim Start mit
einer Meldung ab, die den fehlenden Namen und die Datei nennt — nicht erst nach
Stunden Rechenzeit.

Die Fitness-Formel des Manta-Projekts:

```
Fitness = (Volume × SensorDistance) / Drag ^ DragBalanceFactor
```

… multipliziert mit `0.1`, wenn das Volumen außerhalb des erlaubten Bereichs
liegt. Eine fehlgeschlagene Simulation bekommt `0.0001`.

Die Formel selbst steht im Code, nicht in der JSON — siehe
[Zielfunktion ändern](#zielfunktion-ändern).

> **Vorsicht vor „Reward Hacking".** Optimierungsalgorithmen sind gnadenlos. Ohne
> die Volumengrenzen würde der Algorithmus das Bauteil auf Staubkorngröße
> schrumpfen, weil das den geringsten Widerstand hat. Harte Grenzen in
> `ParameterBounds` und `OptimizationTargets` sind kein Beiwerk.

## `config/solvers/su2.json` — Strömungslöser

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `Density` | `1025.0` | Dichte in kg/m³ (1025 = Meerwasser) |
| `DynamicViscosity` | `0.001001` | Dynamische Viskosität in Pa·s |
| `TurbulenceModel` | `"SA"` | Turbulenzmodell, z.B. `SA` oder `SST` |
| `SpeedOfSound` | `343.2` | Rechnet `MachNumber` in eine Geschwindigkeit um |
| `ReferenceLength` | `0.01` | SU2 `REF_LENGTH` in m |
| `ReferenceAreaMetric` | `"FrontalArea"` | **Name der Metrik**, aus der `REF_AREA` gebildet wird |
| `CflNumber` | `5.0` | Schrittweite des Lösers. Höher = schneller, aber instabiler |
| `CflAdapt` | `true` | Schrittweite automatisch anpassen |
| `CflAdaptFactorDown` / `…Up` | `0.5` / `1.2` | Faktoren beim Verkleinern/Vergrößern |
| `CflAdaptMin` / `CflAdaptMax` | `1.0` / `50.0` | Schranken dafür |

`ReferenceAreaMetric` verdient Aufmerksamkeit: Der Widerstandsbeiwert `CD` wird auf
diese Fläche bezogen. Passt der Name nicht zu einer Metrik, die das Projekt
liefert, rechnet SU2 mit einer Ersatzfläche weiter — und der CD-Wert ist um
Größenordnungen falsch. Das Programm warnt in dem Fall deutlich und listet die
tatsächlich vorhandenen Metriknamen auf.

Das Framework rechnet durchgehend in **Millimetern**; die Umrechnung mm² → m² für
SU2 passiert automatisch.

## `config/solvers/gmsh.json` — Vernetzer

### Windkanal (Simulationsdomäne)

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `TunnelShape` | `"Box"` | `"Box"` oder `"Cylinder"` |
| `TunnelSizeX` / `Y` / `Z` | `600` / `300` / `300` | Kantenlängen des Quaders in mm |
| `TunnelCenterX` / `Y` / `Z` | `0` / `0` / `0` | Mittelpunkt; das Modell sitzt im Ursprung |
| `TunnelDiameter` | `300` | Nur bei `"Cylinder"`: Durchmesser quer zur Strömung |
| `TunnelLength` | `600` | Nur bei `"Cylinder"`: Länge entlang X (Strömungsrichtung) |
| `TunnelSegments` | `64` | Nur bei `"Cylinder"`: Feinheit des Mantels |

Ein unbekannter `TunnelShape` warnt und fällt auf `"Box"` zurück. Faustregel: Der
Kanal sollte in alle Richtungen deutlich größer sein als das Modell, sonst
beeinflussen die Ränder das Ergebnis.

### Netzfeinheit

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `BoundaryLayerSizeMin` | `1.2` | Kleinste Zellgröße direkt an der Wand (mm) |
| `BoundaryLayerSizeMax` | `120.0` | Größte Zellgröße weit weg vom Modell |
| `BoundaryLayerDistMin` | `3.0` | Bis zu diesem Wandabstand gilt die feine Größe |
| `BoundaryLayerDistMax` | `40.0` | Ab diesem Abstand gilt die grobe |

Dazwischen wird linear vergröbert. Das ist der wirksamste Hebel für die Rechenzeit:
`BoundaryLayerSizeMin` zu halbieren vervielfacht die Zellenzahl.

## Ergebnisse lesen

`Ergebnisse/Simulation_Results.csv`, semikolongetrennt (öffnet direkt in Excel):

| Spalte | Inhalt |
|---|---|
| `Iteration`, `Variant` | Woher die Zeile stammt. `Variant 99` = Kontrollrechnung des Türstehers |
| `Length`, `Width`, … | Die **aktiven** Parameter — was der Algorithmus eingestellt hat |
| `Volume`, `FrontalArea`, `SensorDistance` | **Passive** Messwerte aus der Geometrie |
| `ScaleFactor` | Mit welchem Faktor geschrumpft wurde (1 = Scaler aus) |
| `Drag` | Widerstandsbeiwert CD aus SU2 |
| `Fitness` | Die Bewertung. Größer ist besser |
| `StlPath`, `MeshPath` | Wo Geometrie und Netz liegen |
| `SimulationFailed` | `1`, wenn Vernetzung oder Löser abgebrochen sind |

Die Spalten passen sich automatisch an: Neue Parameter oder Metriken erscheinen
ohne Codeänderung am Export.

> **Die Fitness-Spalte steht während einer laufenden Iteration auf `0`.** Die CSV
> wird nach jeder Variante geschrieben, die Bewertung erfolgt aber erst am Ende der
> Iteration. Sobald die Iteration samt Türsteher durch ist, wird die Datei
> vollständig neu geschrieben — dann stehen die Werte drin.

Die beste Variante steht auch am Ende von `simulation.log` unter
`ABSOLUTER CHAMPION`, mit Pfaden zu den passenden ParaView-Dateien.

## Typische Aufgaben

**Lauf kürzer machen** → `MaxIterations` und `VariantsPerIteration` in
`simulation.json` (oder in einer `simulation.local.json`).

**Anderes Optimierungsverfahren** → `OptimizationAlgorithm` auf `"Evolution"`.

**Anderes Bauteil rechnen** → beim Start `-Project <Name>` angeben, siehe
[Welches Projekt gerechnet wird](#welches-projekt-gerechnet-wird).

**Parameter fest einstellen** → in `ParameterBounds` `Min` und `Max` auf denselben
Wert setzen. Der Parameter wird dann immer auf diesen Wert geklemmt.

**Genauer rechnen** → `BaseVoxelResolution` kleiner, `BoundaryLayerSizeMin`
kleiner, `CauchyTolerance` kleiner. Jedes davon kostet spürbar Zeit.

**Schneller rechnen** → dieselben Werte größer; oder `Su2MaxIterations` senken.

**Mehr Kerne nutzen** → `MpiCores` und `GmshCores`. Nicht höher setzen als der
Rechner Kerne hat (`nproc` auf dem Server).

## Grenzen des Systems

**Fluch der Dimensionalität.** Das RSM-Ersatzmodell arbeitet gut bei 3–15
Parametern. Je mehr Parameter, desto mehr Stützstellen braucht es, bevor es
verlässlich vorhersagt. Bei sehr vielen Parametern besser `"Evolution"` nutzen.

**Auflösung.** Voxelgeometrie ist nie mikroskopisch glatt wie eine CAD-Fläche. Das
System ist für die frühe Konzeptphase gedacht — Makroformen vergleichen. Für die
Feinauslegung einer Grenzschicht im Submillimeterbereich ist es nicht gebaut.

**Physik.** Gerechnet wird inkompressibel und stationär (`SOLVER= INC_RANS` mit
Spalart-Allmaras). Freie Oberflächen, Kavitation, instationäre Ablösung und
bewegte Teile sind damit nicht abgedeckt.

---

# Teil B — Entwickeln

## Schichten

Der Kern kennt kein einziges Projekt und keinen einzigen Solver. Die Trennung wird
seit dem Refactoring **vom Compiler erzwungen** — die Namespaces entsprechen den
Ordnern, und `Core` hat kein `using` auf die anderen Schichten.

```
src/Automatisierung_v2/
├── Program.cs                 MyPicoGkProject              Zusammenbau (Composition Root)
├── Core/                      MyPicoGkProject.Core         Das Framework
│   ├── Interfaces/            die sieben Verträge
│   ├── Models/                Datenmodell
│   ├── Configuration/         JSON laden
│   ├── Pipeline/              Ablaufsteuerung + Türsteher
│   ├── Algorithms/            EA, RSM, Fabrik
│   └── Utilities/             Scaler, Smoother, STL-Writer
├── Kernels/PicoGk/            MyPicoGkProject.Kernels.PicoGk
├── Projects/MantaAuv/         MyPicoGkProject.Projects.MantaAuv
└── Solvers/Cfd/               MyPicoGkProject.Solvers.Cfd
```

Die Abhängigkeiten zeigen **nur nach innen**: `Projects` und `Solvers` kennen
`Core`, niemals umgekehrt. Nur `Program.cs` kennt alle.

`using PicoGK` steht in genau zwei Dateien: `Kernels/PicoGk/PicoGkKernel.cs` und
`Projects/MantaAuv/MantaGeometryGenerator.cs`. Ein Projekt ohne Voxelgeometrie
braucht PicoGK also überhaupt nicht.

## Die sieben Interfaces

| Interface | Aufgabe | Implementierung |
|---|---|---|
| `IGeometryGenerator` | Parameter → STL + Metriken | `MantaGeometryGenerator` |
| `IGeometryKernel` | Laufzeitumgebung hochfahren | `PicoGkKernel`, `DirectGeometryKernel` |
| `IMeshGenerator` | STL → Rechennetz | `GmshCfdMesher` |
| `ISimulationSolver` | Netz → Messwerte im Record | `Su2Solver` |
| `IFitnessCalculator` | Messwerte → eine Zahl | `MantaFitnessCalculator` |
| `IOptimizationAlgorithm` | nächste Parameter vorschlagen | `EvolutionaryAlgorithm`, `RsmOptimizationAlgorithm` |
| `IModelValidator` | Champion gegenprüfen | `ChampionValidator` |

`IGeometryKernel` ist bewusst von `IGeometryGenerator` getrennt: PicoGK muss über
`Library.Go(...)` gestartet werden und der ganze Programmablauf läuft *innerhalb*
dieses Aufrufs. Der Kernel ist projektunabhängig — mehrere Projekte teilen sich
einen. Für Tests und für Projekte ohne eigene Laufzeitumgebung gibt es
`DirectGeometryKernel`, der den Ablauf einfach direkt ausführt.

## Ablauf im Code

[`WorkflowController.RunOptimization()`](Core/Pipeline/WorkflowController.cs) ist
die Hauptschleife. Der Kern jeder Variante steckt in der privaten Methode
`RunPipeline(parameters, iteration, variant)`:

```
RubberBandScaler.Create(...)          Maße herunterrechnen
  -> _geometry.GenerateAndExport(...) GeometryResult mit Metriken
  -> ApplyGeometryMetrics(...)        Metriken zurückskalieren
  -> für jede SolverStage:
       Mesher.GenerateMesh(...)       (Cache: gleiche Instanz = ein Netz)
       Solver.Solve(...)              schreibt in record.PassiveParameters
```

Fehler in Vernetzung oder Löser setzen nur `record.SimulationFailed = true` — der
Lauf geht weiter. Der Kern kennt dabei **keine Metriknamen**; was ein Fehlschlag
für die Bewertung bedeutet, entscheidet der `IFitnessCalculator` des Projekts.

Dieselbe Methode benutzt der Türsteher für seine Kontrollrechnung, unter der
Variantennummer 99 und ohne Eintrag in der Historie.

## Datenmodell

**`ModelRecord`** — eine Variante. Trennt streng:
* `ActiveParameters` — was der Algorithmus eingestellt hat
* `PassiveParameters` — was gemessen wurde
* `Fitness`, `SimulationFailed`, `StlPath`, `MeshPath`

**`GeometryResult`** — Rückgabe des Generators. Metriken werden mit ihrer
**Dimension** gemeldet:

```csharp
result.AddMetric("Volume", volume, MetricScaling.Volume);       // kubisch zurück
result.AddMetric("FrontalArea", area, MetricScaling.Area);      // quadratisch
result.AddMetric("SensorDistance", score, MetricScaling.Area);  // halbes Kreuzprodukt!
result.AddMetric("Ratio", r, MetricScaling.None);               // unverändert
```

Der Kern rät nicht mehr anhand des Namens, wie zurückskaliert wird — das Projekt
sagt es. Ohne Angabe gilt `None`.

**`SimulationContext`** — die Laufzeitdatenbank. Wichtig ist die Trennung:

| | Herkunft | Wird verändert |
|---|---|---|
| `Config`, `Project` | aus JSON geladen | **nein** |
| `CurrentBaseParameters`, `CurrentDeviations` | Kopie beim Start | ja, vom Algorithmus |
| `History` | wächst | ja |

Der Algorithmus schreibt seinen Arbeitspunkt **niemals** in die `ProjectConfig`
zurück. Sonst würde ein zweiter Lauf im selben Prozess mit den Endwerten des ersten
starten. `ResetRuntimeState()` stellt die Vorgabe wieder her.

**`SolverStage`** — ein Paar aus Mesher und Solver. Der Controller bekommt ein
`SolverStage[]` und arbeitet es der Reihe nach ab. Stufen, die sich **dieselbe
Mesher-Instanz** teilen, vernetzen nur einmal (Cache über Referenzgleichheit). So
lässt sich eine FEM-Stufe mit eigenem Netz neben die CFD-Stufe hängen.

## Konfiguration laden

[`JsonConfigLoader`](Core/Configuration/JsonConfigLoader.cs) führt auf
`JsonNode`-Ebene zusammen: **Code-Vorgabe → Basisdatei → local-Datei**. Jede Stufe
überschreibt nur die Schlüssel, die sie nennt; verschachtelte Objekte werden
verschmolzen, alles andere ersetzt.

Eine Besonderheit: `ProjectConfig.ParameterBounds` ist ein
`Dictionary<string, (float Min, float Max)>`. ValueTuples kann `System.Text.Json`
nicht serialisieren, deshalb gibt es [`ProjectConfigDto`](Core/Configuration/ProjectConfigDto.cs)
als JSON-Abbild mit `{ "Min": 30, "Max": 100 }`.

## Zielfunktion ändern

Die Formel steht in
[`MantaFitnessCalculator.CalculateFitness`](Projects/MantaAuv/MantaFitnessCalculator.cs).
Die **Zahlen** darin kommen aus `OptimizationTargets` der Projekt-JSON, die
**Struktur** ist C#. Wer statt Widerstand den Auftrieb maximieren will, ändert hier
die Gleichung — und lässt den Solver die passende Metrik liefern.

Pflichtwerte werden im Konstruktor geprüft, nicht mitten im Lauf. Neue
Pflichtwerte gehören in das Array `RequiredTargets` derselben Klasse.

## Neuen Parameter hinzufügen

Am Beispiel eines Spoilerwinkels:

1. In `config/projects/MantaAuv.json` ergänzen:
   ```jsonc
   "BaseParameters":   { "SpoilerAngle": 15.0 },
   "MaxDeviations":    { "SpoilerAngle": 2.5 },
   "ParameterBounds":  { "SpoilerAngle": { "Min": 0.0, "Max": 45.0 } }
   ```
2. **Nur wenn es eine Länge ist**, zusätzlich in `DimensionalParameters` eintragen.
   Ein Winkel gehört dort **nicht** hin.
3. Den Parameter in `MantaGeometryGenerator` auslesen und verbauen:
   ```csharp
   float angle = parameters.TryGetValue("SpoilerAngle", out var a) ? a : 15f;
   ```

CSV-Export, Algorithmen und Skalierung ziehen automatisch nach — sie arbeiten über
die Namen im Dictionary, nicht über feste Felder.

## Neues Projekt anlegen

1. **Ordner** `Projects/MeinProjekt/` mit zwei Klassen:
   * `MeinProjektGeometryGenerator : IGeometryGenerator` — baut die Geometrie und
     meldet ihre Metriken samt `MetricScaling`
   * `MeinProjektFitnessCalculator : IFitnessCalculator` — die Zielfunktion
   * optional `MeinProjektConfig` mit den Code-Vorgaben
2. **Konfiguration** `config/projects/MeinProjekt.json`
3. **Verdrahtung** in [`Program.cs`](Program.cs):
   ```csharp
   case "MeinProjekt":
       projectConfig = JsonConfigLoader.LoadProjectConfig(
           projectName, MeinProjektConfig.Create(), configDirectory);
       geometry = new MeinProjektGeometryGenerator();
       kernel   = new PicoGkKernel();          // oder DirectGeometryKernel
       fitness  = new MeinProjektFitnessCalculator(projectConfig);
       break;
   ```
4. Falls die Referenzfläche anders heißt: `ReferenceAreaMetric` in
   `config/solvers/su2.json` anpassen.

Am Framework-Code ändert sich nichts.

## Neuen Solver oder Mesher anbinden

`ISimulationSolver` bzw. `IMeshGenerator` implementieren und in `Program.cs` als
weitere `SolverStage` eintragen:

```csharp
var stages = new[]
{
    new SolverStage(cfdMesher, su2Solver),
    new SolverStage(femMesher, femSolver)     // eigenes Netz
};
```

Ein Solver schreibt seine Ergebnisse als benannte Werte in
`record.PassiveParameters` — mehr weiß der Kern nicht über ihn. Zahlen und Pfade
gehören in eine eigene Options-Klasse und nach `config/solvers/<name>.json`, geladen
über `JsonConfigLoader.LoadSolverOptions<T>`.

## Tests

```bash
dotnet test Automatisierung.sln        # 112 Tests
```

Die Tests laufen ohne PicoGK, Gmsh und SU2: Der Controller wird mit gemockten
Interfaces gefahren, die Zufallsquellen von `EvolutionaryAlgorithm`,
`RsmOptimizationAlgorithm` und `ChampionValidator` sind über den Konstruktor
injizierbar (fester Seed = reproduzierbar).

Mit abgedeckt sind unter anderem: Reihenfolge der Solver-Kette, Mesh-Cache,
Rückskalierung der Metriken, Verhalten bei Fehlern, der Türsteher in allen
Verzweigungen, das IDW-Ersatzmodell gegen bekannte Stützstellen, der STL-Glätter
und — wichtig — dass die mitgelieferten JSON-Dateien dieselben Zahlen enthalten wie
die Code-Vorgaben. Ein Zahlendreher in `config/` fällt dadurch im Test auf.

## Bewusste Entscheidungen

Wer den Code liest, stolpert über ein paar Stellen, die Absicht sind:

* **Der Türsteher prüft die Fitness, nicht eine einzelne Metrik.** Damit ist er
  projektunabhängig — er kennt kein „Drag".
* **Fehlgeschlagene Simulationen sind keine RSM-Stützstellen.** Sie würden die
  Antwortfläche verzerren und zählen auch nicht für die DoE-Mindestanzahl.
* **Der Windkanal wird selbst als STL geschrieben** (`StlWriter` + `TunnelGeometry`),
  damit die CFD-Schicht nicht am Geometrie-Kernel hängt. Der Standardquader ist
  dreieckstreu derselbe wie zuvor aus PicoGK.
* **Alles rechnet in Millimetern.** Die Umrechnung nach SI passiert erst an der
  Schnittstelle zu SU2.
* **Der Kern kennt keine Metriknamen.** Jeder feste String wie `"Drag"` oder
  `"FrontalArea"` im Ordner `Core/` wäre ein Rückschritt.

## Offene Punkte

* **End-to-End-Vergleich gegen `main`** steht noch aus: ein kleiner Lauf
  (`MaxIterations=1`, `VariantsPerIteration=2`) gegen einen `main`-Lauf mit
  denselben Startwerten. Erwartete Abweichung ist genau eine: `TailTaper` wird
  jetzt korrekt *nicht* mehr mitskaliert.
* **`BouncerTolerance` nachjustieren** nach dem ersten echten Lauf.
* **`ModelRecord.MeshPath` führt nur einen Pfad** — den der ersten Solver-Stufe.
  Wer mehrere Netze protokollieren will, braucht dort eine Liste.
* **Parameter „festschrauben"** geht derzeit nur über gleiche `Min`/`Max`-Grenzen.
  Eine ausdrückliche Möglichkeit, einen Parameter aus der Optimierung zu nehmen,
  fehlt noch.
* **Der Physik-Widerspruch** zwischen Luft-Schallgeschwindigkeit und
  Wasser-Fluideigenschaften (siehe Teil A, Abschnitt „Strömung").

Ideen aus der Vorgängerfassung dieser Datei, die noch offen sind:

* **Kalibrierlauf zu Beginn** — prüfen, ob die Jitter-Ergebnisse stabil sind, und
  die Einstellungen (insbesondere `BouncerTolerance`) daraus ableiten, statt sie
  zu raten.
* **Weitere Optimierungsverfahren** — Gradientenabstieg als dritte Option neben
  `"Rsm"` und `"Evolution"`; dazu eine Auswertung der Parameterkorrelationen, um
  wirkungslose Parameter zu erkennen. Beides braucht nur eine weitere
  `IOptimizationAlgorithm`-Implementierung und einen Eintrag in
  `OptimizationAlgorithmFactory`.

Der vollständige Umbauplan mit Begründungen steht in
[`.plans/architektur_refactoring.md`](../../.plans/architektur_refactoring.md).
