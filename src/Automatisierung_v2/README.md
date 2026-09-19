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
5. **Rechnen.** SU2 löst die Strömung und liefert den Widerstandsbeiwert `CD` —
   und auf Wunsch weitere Beiwerte wie Auftrieb oder Momente, eine Zeile in der
   `su2.json` des Projekts.
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
Parametervorgaben — zum Beispiel `MantaAuv`. **Ein Projekt ist ein Ordner unter
`src/projects/`, und der Ordnername ist der Projektname.** Es gibt keine zweite
Stelle, an der er stünde: kein Feld in einer JSON, kein Eintrag in einer Liste.

| Wo du startest | So wählst du das Projekt |
|---|---|
| Server, über die Skripte | `.\scripts\sim.ps1 run -Project MantaAuv` |
| Direkt auf dem Server per SSH | `SIM_PROJECT=MantaAuv bash scripts/sim-runner.sh start` |
| Lokal auf deinem Rechner | `dotnet run --project src/Automatisierung_v2 -- MantaAuv` |

**Es gibt keinen stillen Standard.** Ohne Angabe bricht das Programm ab und nennt
die vorhandenen Projekte; `sim-runner.sh` genauso. Einzige Ausnahme ist der
Parameter `-Project` in [`scripts/sim.ps1`](../../scripts/sim.ps1), der weiter auf
`MantaAuv` vorbelegt ist — der steht aber im Aufruf und wird zusätzlich gemeldet,
sobald er benutzt wird. Der Grund für die Strenge: ein Vertipper würde sonst
stundenlang das falsche Projekt rechnen, mit plausibel aussehenden Zahlen.

Welche Projekte es gibt:

```powershell
dotnet run --project src/Automatisierung_v2 -- --list-projects   # lokal
.\scripts\sim.ps1 projects                                       # auf dem Server
```

Die Liste kommt nicht aus einer Datei: die
[`ProjectRegistry`](Core/Configuration/ProjectRegistry.cs) sucht beim Start alle
`IProjectDefinition`-Klassen per Reflection und gleicht sie **in beide Richtungen**
gegen die Ordner ab. Eine Definition ohne Ordner findet ihre Zahlen nicht, ein
Ordner ohne Definition ist nicht startbar — beides bricht mit Ansage ab, statt
still weiterzurechnen.

Der zweite Startparameter ist optional und gibt das Projektverzeichnis an, wenn es
nicht gefunden wird:

```bash
dotnet run --project src/Automatisierung_v2 -- MantaAuv /pfad/zu/src/projects
```

Ohne Angabe wird `src/projects/` im Arbeitsverzeichnis gesucht und notfalls bis zu
sechs Ebenen darüber — deshalb funktioniert `dotnet run` auch aus dem Projektordner
heraus. Alternativ die Umgebungsvariable `SIM_PROJECTS_DIR`.

## Die Konfigurationsdateien

**Alles, was ein Projekt einstellt, liegt in seinem Ordner.** Vier Dateien, und
zwei Projekte kommen sich nicht in die Quere:

```
src/projects/
├── _Vorlage/                  Kopiervorlage für ein neues Projekt
└── MantaAuv/
    ├── project.json           Startwerte, Grenzen, Optimierungsziele
    ├── simulation.json        Framework: Pfade, Umfang, Physik, Algorithmuswahl
    ├── solvers/su2.json       Strömungslöser: Fluid, Referenzwerte, Ergebnisgrößen
    ├── solvers/gmsh.json      Vernetzer: Windkanal, Netzfeinheit
    ├── MantaProject.cs        verdrahtet das Projekt (IProjectDefinition)
    ├── MantaGeometryGenerator.cs
    └── MantaFitnessCalculator.cs
```

Die Manifestdatei heißt in **jedem** Projekt `project.json`. Ein Dateiname, der
überall derselbe ist, kann nicht mit dem Projektnamen verwechselt werden —
`config/projects/MantaAuv.json` konnte das.

**Es muss nichts davon vorhanden sein.** Jeder Wert hat eine im Code hinterlegte
Vorgabe; die Datei überschreibt nur, was sie selbst nennt. Eine fehlende Datei ist
kein Fehler, eine kaputte JSON bricht mit Dateinamen ab.

**Ein unbekannter Schlüssel ist dagegen ein Abbruch.** Früher verschluckte der
Merge einen Tippfehler: `"MachNumer"` fiel beim Lesen weg, und der Lauf rechnete
stundenlang mit dem Standardwert weiter. Heute nennt die Meldung die Datei, den
Schlüssel und die nächstähnlichen gültigen Namen.

In den JSON-Dateien sind `//`-Kommentare und nachgestellte Kommata erlaubt. Die
mitgelieferten Dateien sind durchkommentiert — wer sie mit einem Werkzeug neu
schreibt, verliert genau die Erklärungen.

### Die vier Schichten

| # | Datei | Im Repo? | Gilt für |
|---|---|---|---|
| 1 | Code-Standards (`SimulationConfig`, `Su2SolverOptions`, `GmshMesherOptions`, `CreateDefaults()`) | ja | alles |
| 2 | `src/projects/<Name>/…json` | ja | dieses Projekt, alle Rechner |
| 3 | `config/…local.json` *(Notausgang)* | **nein** | alle Projekte, dieser Rechner |
| 4 | `src/projects/<Name>/…local.json` *(Notausgang)* | **nein** | dieses Projekt, dieser Rechner |

**Regel in einem Satz: je spezifischer, desto später — und gitignoriert schlägt
eingecheckt.** Schicht 3 gewinnt über Schicht 2, damit ein Projekt dem Rechner
nicht seine Programmpfade umbiegt; Schicht 4 gewinnt über alles.

Eine **globale** eingecheckte Basisdatei gibt es nicht mehr. Sie hat früher nur die
Code-Standards verdoppelt und wäre die schlimmste Sorte Konfiguration: eine, die
aussieht, als würde sie gelten.

### Die `.local.json`-Regel

Eine `*.local.json` ist eine gitignorierte Ausnahmedatei für **genau einen
Rechner**. Die Deploy-Skripte übertragen sie bewusst nicht — eine solche Datei auf
dem Server überlebt jedes Deploy und lässt sich nur dort löschen, wo sie liegt.

**Sollstand ist, dass es nirgends eine gibt.** Der Mechanismus bleibt als
Notausgang für Rechner mit exotischer Installation, aber jede vorhandene Datei
meldet das Programm beim Start — mit Dateiname und **jedem einzelnen Schlüssel**,
den sie dem Projekt aushebelt, samt beider Werte:

```
==================================================================
[WARNUNG] Eine maschinenspezifische Datei ueberschreibt dieses Projekt:
          config/simulation.local.json
          MaxIterations        Projekt: 10   ->   local: 2
          Normalerweise sollte es diese Datei nicht geben. Gehoeren die
          Werte zum Projekt, dann nach src/projects/MantaAuv/ uebernehmen
          und die Datei loeschen.
==================================================================
```

Für einen kurzen Probelauf braucht es sie nicht: `MaxIterations` in
`src/projects/<Name>/simulation.json` setzen und deployen. Es gibt dann kein
verstecktes zweites Stellrad.

### Was wirklich gilt

Jeder Lauf schreibt `Ergebnisse/<Projekt>/effective-config.json` mit der
tatsächlich verwendeten Konfiguration **samt Herkunft je Schlüssel**. Damit bleibt
ein alter Lauf nachvollziehbar, egal was danach an den Dateien passiert. Vorab
nachsehen, ohne etwas zu starten:

```powershell
dotnet run --project src/Automatisierung_v2 --no-build -- MantaAuv --print-config
```

Das läuft auch auf Windows ohne PicoGK, Gmsh und SU2 — es ist der schnellste Weg,
eine Änderung an den JSONs gegenzuprüfen. `sim.ps1 doctor` benutzt denselben Aufruf
auf dem Server, um die Programmpfade zu prüfen.

## `simulation.json` — Framework

### Pfade zu externer Software

| Schlüssel | Code-Standard | Bedeutung |
|---|---|---|
| `MpiRunPath` | `mpirun` | Startet SU2 auf mehreren Kernen |
| `Su2Path` | `SU2_CFD` | Der Strömungslöser |
| `GmshPath` | `gmsh` | Der Vernetzer |

**Der Code-Standard ist PATH-relativ.** .NET löst einen bloßen Programmnamen über
den `PATH` auf, und `sim-runner.sh load_env` legt die üblichen
Installationsverzeichnisse dorthin. Früher standen hier die absoluten Pfade eines
bestimmten Servers — ein fremder Nutzer erbte damit eine Installation, die es auf
seinem Rechner nicht gibt, und merkte es erst im Lauf.

**MantaAuv weicht davon ab** und trägt in seiner `simulation.json` absolute Pfade
ein: der Hochschulserver findet SU2, Gmsh und `mpirun` nicht über den `PATH`, weil
bei `ssh host befehl` keine interaktive Shell startet. Das ist genau die
Aufgabenteilung der Schichten — die Ausnahme steht beim Projekt, nicht im Framework.
Auf einem anderen Server: die drei Zeilen dort löschen, dann gilt wieder der
PATH-relative Standard.

Welche Variante greift, sagt `.\scripts\sim.ps1 doctor` **vor** dem Lauf: er nennt
zu jedem Programm den tatsächlich gefundenen Ort.

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

## `project.json` — Projekt

| Block | Bedeutung |
|---|---|
| `BaseParameters` | Startwerte. Die Namen bestimmen, welche Parameter es überhaupt gibt |
| `MaxDeviations` | Anfängliche Mutationsstärke je Parameter (nur „Evolution") |
| `ParameterBounds` | Harte Unter- und Obergrenzen: `{ "Min": 30, "Max": 100 }` |
| `OptimizationTargets` | Zahlen, die in die Fitness-Formel eingehen |
| `DimensionalParameters` | Welche Parameter Längen sind und beim Schrumpfen mitskaliert werden |

Ein Feld `ProjectName` gibt es nicht mehr — der Ordner sagt, wie das Projekt heißt.
Dieselben Zahlen stehen zusätzlich als Code-Standard in der Projektdefinition
(`MantaProjectConfig.Create()`); die JSON überlagert sie, und ein Test hält beide
zusammen.

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

## `solvers/su2.json` — Strömungslöser

| Schlüssel | Vorgabe | Bedeutung |
|---|---|---|
| `Density` | `1025.0` | Dichte in kg/m³ (1025 = Meerwasser) |
| `DynamicViscosity` | `0.001001` | Dynamische Viskosität in Pa·s |
| `TurbulenceModel` | `"SA"` | Turbulenzmodell, z.B. `SA` oder `SST` |
| `SpeedOfSound` | `343.2` | Rechnet `MachNumber` in eine Geschwindigkeit um |
| `ReferenceLength` | `0.01` | SU2 `REF_LENGTH` in m |
| `ReferenceAreaMetric` | `"FrontalArea"` | **Name der Metrik**, aus der `REF_AREA` gebildet wird |
| `ResultMetrics` | `{ "Drag": "CD" }` | **Welche Ergebnisgrößen ankommen**, siehe unten |
| `ConvergenceField` | `"DRAG"` | SU2 `CONV_FIELD` — woran SU2 seine Konvergenz misst |
| `HistoryOutput` | `["ITER","RMS_RES","AERO_COEFF"]` | Welche Spaltengruppen SU2 schreibt |
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

### Mehr als nur Widerstand auslesen

SU2 schreibt über `AERO_COEFF` ohnehin **alle** Beiwerte in seine `history.csv` —
verworfen wurden sie bisher nur beim Lesen. `ResultMetrics` ordnet zu:

```jsonc
// Links: unter welchem Namen die Fitness-Formel den Wert sieht.
// Rechts: wie die Spalte in der SU2-history.csv heisst.
"ResultMetrics": {
  "Drag": "CD",
  "Lift": "CL",
  "Nickmoment": "CMy"
}
```

Verfügbar sind `CD` (Widerstand), `CL` (Auftrieb), `CSF` (Seitenkraft) sowie `CMx`,
`CMy`, `CMz` (Roll-, Nick-, Giermoment). „Auftrieb dazunehmen" ist damit **eine
Zeile in der JSON** und kein Eingriff in `Solvers/`.

Drei Dinge dazu:

* **Objekte werden additiv gemerged.** Ein Projekt, das nur `{"Lift":"CL"}`
  schreibt, bekommt `Drag` aus dem Code-Standard dazu. Eine Größe wieder
  *los*zuwerden geht nur über `Su2SolverOptions.ResultMetrics` im Code.
* **Eine konfigurierte, aber fehlende Spalte ist ein Fehler.** Der Solver nennt die
  tatsächlich vorhandenen Spalten und markiert das Modell als fehlgeschlagen.
  Verglichen wird **exakt** (nach Anführungszeichen und Leerzeichen), nur notfalls
  über den Präfix und dann mit Warnung: ein `"CM"` hätte sonst `CMx`, `CMy` und
  `CMz` getroffen, und welche Spalte gewinnt, hinge an der Reihenfolge.
* **`REF_AREA` normiert alle Beiwerte, auch `CL`.** Die Frontalfläche ist für den
  Widerstand richtig; wer auf Auftrieb optimiert, sollte prüfen, ob nicht eine
  Flügelfläche die passendere Bezugsgröße wäre. Und `CL` hängt an der
  Anströmrichtung — beim ersten Auftriebslauf einmal gegen eine bekannte Geometrie
  prüfen.

Wer eine Größe konfiguriert, sollte sie auch in `RequiredMetrics` seines
Fitness-Rechners nennen: der Controller prüft diese Liste **nach der ersten
gerechneten Variante** und bricht dort ab, statt stundenlang mit einer
Ersatz-Fitness weiterzurechnen.

## `solvers/gmsh.json` — Vernetzer

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

Jedes Projekt hat seinen eigenen Ergebnisordner — `Ergebnisse/<Projekt>/`. Vorher
war es ein gemeinsamer Topf, und ein zweites Projekt überschrieb die CSV des ersten.

| Datei | Inhalt |
|---|---|
| `Simulation_Results.csv` | **Das Ergebnis.** Eine Zeile je Variante |
| `effective-config.json` | Womit gerechnet wurde, samt Herkunft je Schlüssel |

`Simulation_Results.csv`, semikolongetrennt (öffnet direkt in Excel):

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
`src/projects/<Name>/simulation.json`, dann deployen. Das ist das einzige Stellrad.

**Anderes Optimierungsverfahren** → `OptimizationAlgorithm` auf `"Evolution"`.

**Anderes Bauteil rechnen** → beim Start `-Project <Name>` angeben, siehe
[Welches Projekt gerechnet wird](#welches-projekt-gerechnet-wird).

**Neues Bauteil anlegen** → `.\scripts\new-project.ps1 <Name>`, dann Geometrie und
Fitness im neuen Ordner ausfüllen. Die sechs Schritte stehen in
[`src/projects/README.md`](../projects/README.md).

**Eine weitere Ergebnisgröße auswerten** (Auftrieb, Momente) → `ResultMetrics` in
`solvers/su2.json`, siehe
[Mehr als nur Widerstand auslesen](#mehr-als-nur-widerstand-auslesen).

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
src/
├── Automatisierung_v2/            Das Framework -- für ein Projekt NIE angefasst
│   ├── Program.cs                 MyPicoGkProject                  Composition Root
│   ├── Composition/               MyPicoGkProject.Composition      CfdProjectDefinition
│   ├── Core/                      MyPicoGkProject.Core             Der Kern
│   │   ├── Interfaces/            die acht Verträge
│   │   ├── Models/                Datenmodell
│   │   ├── Configuration/         JSON laden, Projekte finden
│   │   ├── Pipeline/              Ablaufsteuerung + Türsteher
│   │   ├── Algorithms/            EA, RSM, Fabrik
│   │   └── Utilities/             Scaler, Smoother, STL-Writer, MeshMetrics
│   ├── Kernels/PicoGk/            MyPicoGkProject.Kernels.PicoGk
│   └── Solvers/Cfd/               MyPicoGkProject.Solvers.Cfd
└── projects/                      Alle Projekte, je ein Ordner
    ├── _Vorlage/                  MyPicoGkProject.Projects.MeinProjekt
    └── MantaAuv/                  MyPicoGkProject.Projects.MantaAuv
```

`src/projects/` liegt bewusst **neben** dem Framework, nicht darin: „hier das
Gerüst, dort meine Projekte" ist sofort lesbar. Preis dafür ist eine Zeile in der
csproj, die `..\projects\**\*.cs` mit hereinzieht.

Die Abhängigkeiten zeigen **nur nach innen**: `Projects` und `Solvers` kennen
`Core`, niemals umgekehrt. Das ist vom Compiler erzwungen — `Core/` hat kein
einziges `using` auf `Projects`, `Solvers` oder `Kernels`.

`Composition/` ist die Schicht, die alle anderen kennen darf, dieselbe Rolle wie
`Program.cs`. Dort liegt `CfdProjectDefinition`: die fertig verdrahtete Kette
PicoGK → Gmsh → SU2, von der ein CFD-Projekt erbt. Sie muss diese Klassen beim
Namen nennen und kann deshalb nicht in `Core/` liegen.

`using PicoGK` steht in vier Dateien: `Kernels/PicoGk/PicoGkKernel.cs`,
`Kernels/PicoGk/PicoGkMeshMetrics.cs` und den beiden Geometrie-Generatoren unter
`src/projects/`. Ein Projekt ohne Voxelgeometrie braucht PicoGK überhaupt nicht — es
überschreibt `CreateKernel()` mit `DirectGeometryKernel`.

**`_Vorlage` ist aus dem Build des Programms ausgeschlossen** und wird vom
Testprojekt compiliert. Sonst gäbe es eine Projektdefinition mit dem Platzhalternamen
`MeinProjekt`, zu der kein Ordner gehört. Gar nicht compiliert wäre die Vorlage aber
totes Textmaterial: so bricht eine geänderte Schnittstelle sofort `dotnet test` und
nicht Monate später beim Anlegen eines Projekts.

## Die acht Interfaces

| Interface | Aufgabe | Implementierung |
|---|---|---|
| `IProjectDefinition` | Projekt verdrahten: Name, Vorgaben, Geometrie, Fitness, Kette | `MantaProject` (über `CfdProjectDefinition`) |
| `IGeometryGenerator` | Parameter → STL + Metriken | `MantaGeometryGenerator` |
| `IGeometryKernel` | Laufzeitumgebung hochfahren | `PicoGkKernel`, `DirectGeometryKernel` |
| `IMeshGenerator` | STL → Rechennetz | `GmshCfdMesher` |
| `ISimulationSolver` | Netz → Messwerte im Record | `Su2Solver` |
| `IFitnessCalculator` | Messwerte → eine Zahl | `MantaFitnessCalculator` |
| `IOptimizationAlgorithm` | nächste Parameter vorschlagen | `EvolutionaryAlgorithm`, `RsmOptimizationAlgorithm` |
| `IModelValidator` | Champion gegenprüfen | `ChampionValidator` |

`IProjectDefinition` ist der Vertrag, der `Program.cs` von den Projekten befreit hat:

```csharp
string Name { get; }                                  // = Ordnername
ProjectConfig      CreateDefaults();                  // project.json überlagert das
IGeometryKernel    CreateKernel();
IGeometryGenerator CreateGeometry(ProjectContext c);
IFitnessCalculator CreateFitness(ProjectContext c);
SolverStage[]      CreateStages(ProjectContext c);
```

`ProjectContext` reicht durch, was ein Projekt zum Verdrahten braucht: Name, Ordner,
die fertig geladene `SimulationConfig` und `ProjectConfig` sowie
`LoadSolverOptions<T>("su2")`, das schon auf den richtigen Ordner zeigt.

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

Ein **falsches** `MetricScaling` fällt nicht auf: der Wert ist dann um den
Skalierungsfaktor hoch 1, 2 oder 3 daneben und sieht trotzdem plausibel aus. Welche
Dimension eine Metrik bekam, hält `effective-config.json` fest.

**`MeshMetrics`** ([Core/Utilities](Core/Utilities/MeshMetrics.cs)) nimmt einem
Projekt die Maße ab: Volumen, Oberfläche und Hüllquader aus einer Dreiecksliste, in
einem Durchgang. Anlass ist die Oberfläche — PicoGKs `CalculateProperties` liefert
Volumen und Hüllquader, die Oberfläche **nicht**, und die Schleife dafür wäre in
jedem Projekt dieselbe. Für ein PicoGK-Netz gibt es
`PicoGkMeshMetrics.Measure(mesh)`; die Rechnung selbst bleibt PicoGK-frei im Kern.
Das Volumen ist **mit Vorzeichen** abfragbar: negativ heißt, die Dreiecke sind nach
innen gedreht — im Betrag wäre dieser Fehler unsichtbar. Rein additiv; MantaAuv
rechnet weiter mit `CalculateProperties` und bekommt exakt die Zahlen wie zuvor.

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
`JsonNode`-Ebene zusammen, in der Reihenfolge der [vier
Schichten](#die-vier-schichten). Jede Stufe überschreibt nur die Schlüssel, die sie
nennt; verschachtelte Objekte werden verschmolzen, alles andere ersetzt.

Vor dem Übernehmen gleicht [`ConfigSchema`](Core/Configuration/ConfigSchema.cs) jeden
Schlüssel gegen die Properties des Zieltyps ab (unbekannt ⇒ Abbruch mit Vorschlag per
Levenshtein-Abstand). In ein `Dictionary` steigt die Prüfung nicht ab: `"Length"`
unter `BaseParameters` ist ein Parametername und kein Property-Name.

Der Loader protokolliert außerdem, **welche Datei welchen Schlüssel gewonnen hat**.
Daraus entstehen der `*.local.json`-Warnblock und
[`EffectiveConfigWriter`](Core/Configuration/EffectiveConfigWriter.cs), der die
`effective-config.json` neben die Ergebnisse legt.

Wo die Projekte liegen, löst [`ProjectPaths`](Core/Configuration/ProjectPaths.cs):
Aufrufargument → `SIM_PROJECTS_DIR` → aufwärts suchen nach `src/projects/`. Bewusst
**kein** `CopyToOutputDirectory` in der csproj — die JSONs lägen dann in `bin/`, und
eine Änderung an der Quelldatei würde erst nach einem Rebuild wirken. Diese Sorte
Verwirrung will man bei einem mehrstündigen Lauf nicht.

Eine Besonderheit: `ProjectConfig.ParameterBounds` ist ein
`Dictionary<string, (float Min, float Max)>`. ValueTuples kann `System.Text.Json`
nicht serialisieren, deshalb gibt es [`ProjectConfigDto`](Core/Configuration/ProjectConfigDto.cs)
als JSON-Abbild mit `{ "Min": 30, "Max": 100 }`.

## Zielfunktion ändern

Die Formel steht in
[`MantaFitnessCalculator.CalculateFitness`](../projects/MantaAuv/MantaFitnessCalculator.cs).
Die **Zahlen** darin kommen aus `OptimizationTargets` der `project.json`, die
**Struktur** ist C#. Wer statt Widerstand den Auftrieb maximieren will, ändert hier
die Gleichung — und lässt den Solver die passende Größe liefern (`ResultMetrics` in
`solvers/su2.json`).

Zwei Prüflisten derselben Klasse, beide billig und beide eine Nachtschicht wert:

* `RequiredTargets` — Zahlen aus der `project.json`. Geprüft im **Konstruktor**, also
  beim Verdrahten, vor der ersten Simulation.
* `RequiredMetrics` — Messwerte aus Geometrie und Solver. Die entstehen erst mit der
  ersten Variante; der Controller prüft sie **danach** und bricht dort ab.

## Neuen Parameter hinzufügen

Am Beispiel eines Spoilerwinkels:

1. In `src/projects/MantaAuv/project.json` ergänzen:
   ```jsonc
   "BaseParameters":   { "SpoilerAngle": 15.0 },
   "MaxDeviations":    { "SpoilerAngle": 2.5 },
   "ParameterBounds":  { "SpoilerAngle": { "Min": 0.0, "Max": 45.0 } }
   ```
2. Dieselben drei Einträge in `MantaProjectConfig.Create()` — das ist die
   Code-Schicht darunter.
3. **Nur wenn es eine Länge ist**, zusätzlich in `DimensionalParameters` eintragen.
   Ein Winkel gehört dort **nicht** hin.
4. Den Parameter in `MantaGeometryGenerator` auslesen und verbauen:
   ```csharp
   float angle = parameters.TryGetValue("SpoilerAngle", out var a) ? a : 15f;
   ```

CSV-Export, Algorithmen und Skalierung ziehen automatisch nach — sie arbeiten über
die Namen im Dictionary, nicht über feste Felder.

## Neues Projekt anlegen

```powershell
.\scripts\new-project.ps1 Propeller
```

Das kopiert `src/projects/_Vorlage/` und setzt den Namen ein. Die sechs Schritte
danach stehen in [`src/projects/README.md`](../projects/README.md).

**Am Framework-Code ändert sich nichts** — und zwar buchstäblich nichts: kein `case`,
keine Liste, kein Eintrag in `Program.cs`. Die
[`ProjectRegistry`](Core/Configuration/ProjectRegistry.cs) findet die neue
`IProjectDefinition` per Reflection. Von Hand ist ein Projekt drei C#-Dateien:

```csharp
public sealed class PropellerProject : CfdProjectDefinition
{
    public override string Name => "Propeller";              // = Ordnername

    public override ProjectConfig CreateDefaults() => new ProjectConfig { /* Zahlen */ };

    public override IGeometryGenerator CreateGeometry(ProjectContext c)
        => new PropellerGeometryGenerator();

    public override IFitnessCalculator CreateFitness(ProjectContext c)
        => new PropellerFitnessCalculator(c.Project);
}
```

Wer eine andere Kette braucht, überschreibt `CreateStages`; wer ohne Voxel-Kernel
auskommt, `CreateKernel`. Beides im eigenen Projektordner.

## Neuen Solver oder Mesher anbinden

`ISimulationSolver` bzw. `IMeshGenerator` implementieren und im **Projekt** als
weitere `SolverStage` eintragen — nicht in `Program.cs`:

```csharp
public override SolverStage[] CreateStages(ProjectContext c) => new[]
{
    new SolverStage(
        new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
        new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2"))),
    new SolverStage(femMesher, femSolver)     // eigenes Netz
};
```

Ein Solver schreibt seine Ergebnisse als benannte Werte in
`record.PassiveParameters` — mehr weiß der Kern nicht über ihn. Zahlen und Pfade
gehören in eine eigene Options-Klasse und nach
`src/projects/<Name>/solvers/<name>.json`; `ProjectContext.LoadSolverOptions<T>` lädt
sie über alle vier Schichten. Ein neuer Solver bringt seine JSON also selbst mit,
ohne dass der Kern davon wissen muss.

## Tests

```bash
dotnet test Automatisierung.sln        # 251 Tests
```

Die Tests laufen ohne PicoGK, Gmsh und SU2: Der Controller wird mit gemockten
Interfaces gefahren, die Zufallsquellen von `EvolutionaryAlgorithm`,
`RsmOptimizationAlgorithm` und `ChampionValidator` sind über den Konstruktor
injizierbar (fester Seed = reproduzierbar).

Mit abgedeckt sind unter anderem: Reihenfolge der Solver-Kette, Mesh-Cache,
Rückskalierung der Metriken, Verhalten bei Fehlern, der Türsteher in allen
Verzweigungen, das IDW-Ersatzmodell gegen bekannte Stützstellen, der STL-Glätter,
die Schichtreihenfolge des Loaders samt Abbruch bei unbekanntem Schlüssel, das
Auflösen der Projekte und — wichtig — dass die mitgelieferten JSON-Dateien dieselben
Zahlen enthalten wie die Code-Vorgaben. Ein Zahlendreher in einer Projektdatei fällt
dadurch im Test auf.

Drei Testgruppen sind eigentlich keine Tests, sondern Fangnetze:

* **`ProjectLayoutTests`** hält die Struktur fest — dass die vier JSONs im
  Projektordner liegen, dass der Code-Standard der Programmpfade PATH-relativ bleibt,
  dass in `config/` keine eingecheckte JSON auftaucht, die niemand mehr liest.
* **`TemplateProjectTests`** hält die Vorlage vollständig, aktuell und nicht startbar.
  Dass sie überhaupt compiliert, prüft schon der Build dieses Testprojekts.
* **`ScriptContractTests`** liest die Startskripte als **Text**: es gibt kein bash auf
  dem Entwicklungsrechner. Sie schlagen an, wenn jemand den stillen Projekt-Standard
  wieder einbaut oder die Kette `-Project` → `SIM_PROJECT` → `dotnet "$PROJECT_NAME"`
  auftrennt. Einen Lauf auf dem Server ersetzen sie nicht.

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
  `"FrontalArea"` im Ordner `Core/` wäre ein Rückschritt. Auch der
  `WorkflowController` bekommt die Pflicht-Metriken nur als **Namensliste**, nicht den
  `IFitnessCalculator` — sonst käme eine Abhängigkeit zurück, die absichtlich
  entfernt wurde.
* **Kein stiller Standard beim Projektnamen.** Weder in `Program.cs` noch in
  `sim-runner.sh`. Ein Vertipper soll abbrechen und nicht stundenlang das falsche
  Projekt rechnen.
* **Der Ordnername ist der Projektname.** Ein Feld in einer Datei wäre eine zweite
  Stelle, die mit dem Ordner auseinanderlaufen kann. Die `ProjectRegistry` prüft
  beide Richtungen beim Start.
* **`_Vorlage` wird vom Testprojekt compiliert, nicht vom Programm.** Eine Vorlage,
  die nie durch den Compiler geht, ist nach der nächsten Schnittstellenänderung
  stillschweigend kaputt.

## Offene Punkte

* **Der Zahlenvergleich gegen `main`** steht noch aus. Die Kette selbst läuft: am
  20.09.2026 ist ein zweites Projekt (Manta-Logik, 2 × 2 Varianten) auf dem Server
  fehlerfrei durchgelaufen, samt `deploy`, `doctor` und Bouncer-Kontrollrechnungen.
  Offen ist der Vergleich mit einem `main`-Lauf bei denselben Startwerten. Erwartete
  Abweichung ist genau eine: `TailTaper` wird jetzt korrekt *nicht* mehr mitskaliert.
  Mehr als größenordnungsweise geht der Vergleich nicht — die DoE-Phase des RSM zieht
  ihre Punkte mit einem unbesäten `Random`.
* **`BouncerTolerance` nachjustieren** nach dem ersten echten Lauf.
* **`ModelRecord.MeshPath` führt nur einen Pfad** — den der ersten Solver-Stufe.
  Wer mehrere Netze protokollieren will, braucht dort eine Liste.
* **Parameter „festschrauben"** geht derzeit nur über gleiche `Min`/`Max`-Grenzen.
  Eine ausdrückliche Möglichkeit, einen Parameter aus der Optimierung zu nehmen,
  fehlt noch.
* **`PicoGkMeshMetrics` ist nicht getestet** — jeder Aufruf braucht eine laufende
  PicoGK-Umgebung und die native `libpicogk`. Geprüft ist die Rechnung dahinter
  (`MeshMetrics`), nicht die Schleife über das PicoGK-Netz.
* **Die Geometrie der Vorlage ist noch nie gelaufen.** Der Weg über
  `new-project.ps1` ist bewiesen (das Testprojekt vom 20.09.2026 entstand so), aber
  dessen Quader war für den Test durch die Manta-Geometrie ersetzt. Ein Lauf mit dem
  Platzhalter-Quader — und damit mit `MeshMetrics` im Einsatz — fehlt.
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

Die vollständigen Umbaupläne mit Begründungen stehen in
[`.plans/architektur_refactoring.md`](../../.plans/architektur_refactoring.md) (der
Kern: Interfaces, JSON-Konfiguration, austauschbares PicoGK) und
[`.plans/projektbuendel.md`](../../.plans/projektbuendel.md) (ein Ordner pro Projekt,
Umschalten nur über das Startskript).
