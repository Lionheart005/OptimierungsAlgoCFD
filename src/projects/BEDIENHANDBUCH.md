# Bedienhandbuch: Was stelle ich wo ein?

Dieses Handbuch erklärt **ohne Programmierkenntnisse**, wo man das Programm einstellt, was jede
Einstellung bewirkt und welche man besser in Ruhe lässt. Es beschreibt den Stand vom
**04.11.2026**. Neue Einstellungen kommen hier dazu, sobald es sie gibt: Ein Test im Projekt
schlägt Alarm, wenn eine Einstellung hier fehlt.

> **Die wichtigsten drei Regeln**
> 1. **Fehlt eine Einstellung in einer Datei, gilt der Standardwert.** Man muss also nur
>    eintragen, was man ändern will.
> 2. **Ein Tippfehler bricht den Start ab.** Das Programm nennt dann den falsch geschriebenen
>    Namen und den, der vermutlich gemeint war. Es rechnet *nie* stillschweigend mit einem
>    falschen Wert weiter.
> 3. **Umgeschaltet wird nur beim Start**, mit `-Project <Name>`. Man muss nichts im Code ändern.

**Inhalt**
1. [Der Ablauf auf einen Blick](#1-der-ablauf-auf-einen-blick)
2. [Wo die Einstellungen liegen](#2-wo-die-einstellungen-liegen)
3. [„Ich will …" – die häufigsten Aufgaben](#3-ich-will---die-häufigsten-aufgaben)
4. [Alle Einstellungen, Datei für Datei](#4-alle-einstellungen-datei-für-datei)
5. [Welche Werte galten wirklich?](#5-welche-werte-galten-wirklich)
6. [Die Befehle](#6-die-befehle)
7. [Häufige Meldungen und was zu tun ist](#7-häufige-meldungen-und-was-zu-tun-ist)
8. [Begriffe kurz erklärt](#8-begriffe-kurz-erklärt)

---

## 1. Der Ablauf auf einen Blick

Ein **Lauf** besteht aus mehreren **Iterationen** (Runden). In jeder Runde werden mehrere
**Varianten** (Formvorschläge) gerechnet. Jede Variante durchläuft dieselbe Kette:

```text
 Optimierer schlägt Parameter vor          simulation.json  (Verfahren, Laufumfang)
        │                                  project.json     (Startwerte, Grenzen)
        ▼
 Geometrie wird gebaut (PicoGK, Voxel)     simulation.json  (Voxelgröße, Glättung)
        │                                  + C#-Code im Projektordner (die Form selbst)
        ▼
 Netz wird erzeugt (Gmsh)                  solvers/gmsh.json (Windkanal, Zellgrößen)
        │
        ▼
 Strömung wird gerechnet (SU2)             solvers/su2.json  (Fluid, Geschwindigkeit, Ergebnisse)
        │                                  simulation.json   (Iterationen, Abbruch, Kerne)
        ▼
 Bewertung (Fitness)                       project.json     (Ziele, Grenzwerte)
        │                                  + C#-Code im Projektordner (die Formel)
        ▼
 Nach jeder Runde: Gewinner nachprüfen     simulation.json  (Bouncer-Toleranz)
```

Am Ende steht der **Champion**, also die beste Variante des ganzen Laufs, im Log und in der
Ergebnistabelle.

**Was nicht in JSON steht, sondern C#-Code ist:** *wie* die Form gebaut wird
(`…GeometryGenerator.cs`) und *wie* sie bewertet wird (`…FitnessCalculator.cs`). Alle **Zahlen**
stehen in den JSON-Dateien, die **Formeln** im Code. Wie man diesen Code für ein neues Projekt
schreibt, erklärt [`README.md`](README.md) in diesem Ordner.

---

## 2. Wo die Einstellungen liegen

Jedes Projekt ist ein Ordner unter `src/projects/`. **Der Ordnername ist der Projektname.**

```text
src/projects/MantaAuv/
  project.json          WAS optimiert wird: Parameter, Startwerte, Grenzen, Ziele
  simulation.json       WIE der Lauf abläuft: Umfang, Optimierer, Geometrie, Rechenkerne, Pfade
  solvers/su2.json      Die Strömungsrechnung: Fluid, Geschwindigkeit, welche Kennwerte
  solvers/gmsh.json     Das Rechennetz: Windkanal und Zellgrößen
```

Die Dateien dürfen Kommentare enthalten: Alles hinter `//` ist Erklärung.

**Wer gewinnt, wenn ein Wert mehrfach vorkommt?** Es gibt vier Stufen, und die spätere gewinnt:

| Stufe | Wo | Wofür |
| --- | --- | --- |
| 1 | im Programm eingebaut (Standardwerte) | gilt, wenn nichts anderes dasteht |
| 2 | `src/projects/<Name>/….json` | **der Normalfall**: dieses Projekt, auf jedem Rechner |
| 3 | `config/….local.json` | Notausgang: alle Projekte, nur dieser Rechner |
| 4 | `src/projects/<Name>/….local.json` | Notausgang: dieses Projekt, nur dieser Rechner |

**`*.local.json` soll es normalerweise nicht geben.** Solche Dateien werden nie hochgeladen
und nie überschrieben; sie stellen also still Werte um, die man im Repo nicht sieht. Liegt eine
herum, zeigt das Programm beim Start einen auffälligen **Warnblock** mit jedem Wert, den sie
verändert (siehe [Abschnitt 7](#7-häufige-meldungen-und-was-zu-tun-ist)).

**Die Vorlage `_Vorlage/`** enthält *jede* Einstellung mit ihrem Standardwert und einer
Erklärung. Sie ist das Nachschlagewerk zum Abschreiben. Ein neues Projekt legt man mit
`.\scripts\new-project.ps1 <Name>` an; das kopiert die Vorlage.

---

## 3. „Ich will …" – die häufigsten Aufgaben

| Ich will … | Datei | Einstellung | Beispiel / Hinweis |
| --- | --- | --- | --- |
| … einen **kurzen Testlauf** | `simulation.json` | `MaxIterations`, `VariantsPerIteration` | `1` und `2` ergibt 2 Varianten plus eine Nachprüfung. Danach wieder zurückstellen! |
| … **mehr Varianten** rechnen (gründlicher) | `simulation.json` | `MaxIterations`, `VariantsPerIteration` | 10 × 10 = 100 Varianten, bei ~3 min je Variante etwa 5–6 h |
| … dass SU2 **länger/genauer** rechnet | `simulation.json` | `Su2MaxIterations`, `CauchyTolerance` | mehr Iterationen bzw. kleinere Toleranz (`"1e-5"`) |
| … **schneller** rechnen | `simulation.json` | `MpiCores`, `GmshCores` | höchstens so viele, wie der Server Kerne hat; dazu ggf. `Su2MaxIterations` senken |
| … ein **feineres Netz** an der Oberfläche | `solvers/gmsh.json` | `BoundaryLayerSizeMin` | kleiner = feiner = langsamer. Die Oberfläche selbst ist heute so fein wie die Voxel (`BaseVoxelResolution`) |
| … eine **andere Strömungsgeschwindigkeit** | `solvers/su2.json` | `FreestreamVelocity` | in m/s, z. B. `2.0`. Ist sie 0, gilt der alte Weg über `MachNumber` |
| … **Luft statt Wasser** | `solvers/su2.json` | `Density`, `DynamicViscosity` | Luft: `1.225` und `1.81e-5`; Meerwasser: `1025` und `0.001001` |
| … zusätzlich den **Auftrieb** (oder andere Kennwerte) haben | `solvers/su2.json` | `ResultMetrics` | `"Lift": "CL"` dazuschreiben; in der Bewertungsformel (C#) verwenden |
| … ein **anderes Turbulenzmodell** | `solvers/su2.json` | `TurbulenceModel` | `"SA"` (Standard) oder `"SST"` |
| … den **Optimierer wechseln** | `simulation.json` | `OptimizationAlgorithm` | `"Rsm"` (Ersatzmodell, Standard) oder `"Evolution"` |
| … **Startwerte** oder **Grenzen** eines Parameters ändern | `project.json` | `BaseParameters`, `ParameterBounds` | Startwert muss innerhalb der Grenzen liegen |
| … dass der Optimierer **größere/kleinere Sprünge** macht | `project.json` | `MaxDeviations` | Streuung je Parameter, in dessen Einheit |
| … die **Ziele** der Bewertung ändern (z. B. Volumengrenzen) | `project.json` | `OptimizationTargets` | welche Namen es gibt, bestimmt die Bewertungsformel des Projekts |
| … dass Gewinner **strenger/milder** nachgeprüft werden | `simulation.json` | `BouncerTolerance` | `0.2` = 20 % Abweichung erlaubt |
| … einen **größeren Windkanal** | `solvers/gmsh.json` | `TunnelSizeX/Y/Z` | in mm; größer = weniger Randeinfluss, aber mehr Zellen |
| … ein **neues Projekt** | — | `.\scripts\new-project.ps1 <Name>` | danach Form und Bewertung im neuen Ordner ausfüllen |
| … das **Projekt wechseln** | — | `-Project <Name>` beim Befehl | z. B. `.\scripts\sim.ps1 run -Project TestprojektNACA` |
| … **Ergebnisse holen** | — | `.\scripts\sim.ps1 fetch -Project <Name>` | **`-Project` immer angeben**, sonst wird MantaAuv geholt (siehe Abschnitt 7) |
| … eine **Rechnung von Hand prüfen** | — | `fetch`, dann `plot_history.py` | Konvergenz prüfen; bei Bedarf `fetch -Champion` und ParaView für die VTUs (Details in `scripts/README.md`) |
| … nachsehen, **womit ein Lauf gerechnet hat** | — | `effective-config.json` | siehe [Abschnitt 5](#5-welche-werte-galten-wirklich) |

---

## 4. Alle Einstellungen, Datei für Datei

**So liest du die Tabellen:**
- **Wann anfassen:** 🟢 *oft* (dafür ist die Einstellung da) · 🟡 *gelegentlich* (wenn man weiß,
  wozu) · 🔴 *nur mit Grund* (Falsche Werte verfälschen Ergebnisse oder brechen den Lauf).
- **Wirkung:** ⏱ beeinflusst vor allem die Rechenzeit · 🎯 beeinflusst vor allem die
  Genauigkeit · ⚙ technische Einstellung.
- **Längen in Netz und Windkanal** sind in mm *in der Arbeitsgröße* von PicoGK. Ist der
  Gummiband-Skalierer an (`UseRubberBandScaler`) und das Modell größer als `TargetPicoGkSize`,
  wird es vorher verkleinert; Netzgrößen beziehen sich auf diese verkleinerte Form. Bei MantaAuv
  (50 mm) ist der Faktor 1, dort sind es also echte mm.

### 4.1 `project.json` – was optimiert wird

| Einstellung | Bedeutung | Einheit | Beispiel (MantaAuv) | Wann |
| --- | --- | --- | --- | --- |
| `BaseParameters` | Startwerte der Parameter, die der Optimierer verändern darf. Mit diesen Werten wird die allererste Variante gerechnet. | je Parameter | `"Length": 50.0` | 🟢 |
| `MaxDeviations` | Wie weit der Optimierer einen Parameter anfangs ungefähr verstellt (Streuung). | wie der Parameter | `"Length": 5.0` | 🟡 |
| `ParameterBounds` | Harte Grenzen je Parameter (`Min`, `Max`). Kein Vorschlag liegt außerhalb. | wie der Parameter | `"Length": { "Min": 30, "Max": 100 }` | 🟢 |
| `OptimizationTargets` | Zahlen, die die Bewertungsformel braucht (z. B. erlaubtes Volumen). Welche Namen gelten, legt der Bewertungscode des Projekts fest; fehlt ein benötigter Name, bricht der Start mit einer Liste ab. | je Ziel | `"MinimumAllowedVolume": 2000.0` | 🟢 |
| `DimensionalParameters` | Welche Parameter **Längen** sind. Nur diese werden vom Gummiband-Skalierer mitskaliert; Verhältniszahlen (z. B. `TailTaper`) gehören nicht hinein. | Liste von Namen | `[ "Length", "Width", … ]` | 🔴 |

Der **Projektname** steht absichtlich *nicht* in der Datei (es gibt keinen Schlüssel
`ProjectName`): Er ist immer der Ordnername.

**MantaAuv-Bewertung zum Verständnis:** Fitness = (Volumen × Sensorabstand) / Widerstand^`DragBalanceFactor`;
liegt das Volumen außerhalb von `MinimumAllowedVolume`…`MaximumAllowedVolume`, wird die Fitness ×0,1
bestraft. Höhere Fitness = besser.

### 4.2 `simulation.json` – wie der Lauf abläuft

**Laufumfang und Geometrie**

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `MaxIterations` | Anzahl der Runden. | `10` | ⏱ | 🟢 |
| `VariantsPerIteration` | Varianten je Runde. Gesamt = `MaxIterations` × `VariantsPerIteration` (plus je Runde mind. eine Nachprüfung). | `10` | ⏱ | 🟢 |
| `BaseVoxelResolution` | Kantenlänge eines Voxels (Bausteins) der Geometrie in mm. Kleiner = feinere, glattere Form, aber deutlich langsamer und speicherhungriger. Dünne Stellen unter ~1,2 Voxeln können abreißen. | `0.5` | 🎯⏱ | 🟡 |
| `TargetPicoGkSize` | Auf diese Größe (mm, größte Länge) wird das Modell für PicoGK skaliert, wenn der Gummiband-Skalierer an ist. Zusammen mit `BaseVoxelResolution` bestimmt das, wie viele Voxel ein Modell hat. | `50.0` | 🎯⏱ | 🔴 |
| `UseRubberBandScaler` | Gummiband-Skalierer an/aus. **Aus**, wenn das Projekt keine Längen-Parameter hat (z. B. nur Prozentwerte wie bei NACA), sonst wird falsch skaliert. | `true` | 🎯 | 🔴 |
| `UseStlSmoothing` | Glättet die Oberfläche nach dem Voxel-Bau (gegen die „Treppenstufen"). | `true` | 🎯 | 🟡 |
| `VoxelSmoothingIterations` | Anzahl der Glättungsdurchgänge (formerhaltend). | `40` | 🎯 | 🔴 |
| `VoxelSmoothingPremeltingSteps` | Zusätzliche grobe Vorglättungen vor den formerhaltenden Durchgängen. Diese schrumpfen die Form leicht, also klein halten. | `2` | 🎯 | 🔴 |

**Strömungsrechnung (SU2) und Rechenleistung**

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `MachNumber` | **Alter Weg** zur Geschwindigkeit: Geschwindigkeit = `MachNumber` × `SpeedOfSound` (aus `su2.json`). Wirkt **nur**, wenn in `su2.json` `FreestreamVelocity` 0 ist. Bei MantaAuv wirkt sie nicht mehr. | `0.1` | 🎯 | 🟡 |
| `MeshRefinementFactor` | Globaler Faktor für alle Zellgrößen im Netz. `1.0` = Standardgrößen; kleiner als 1,0 verfeinert das Netz (z. B. `0.5` halbiert die Zellkanten), größer als 1,0 vergröbert es für schnelle Vorabläufe. | `1.0` | ⏱🎯 | 🟡 |
| `Su2MaxIterations` | Höchstzahl der SU2-Rechenschritte je Variante. Ist sie erreicht, hört SU2 auf, auch wenn die Lösung noch nicht ruhig ist. | `500` | ⏱🎯 | 🟡 |
| `CauchyElements` | Über so viele Schritte prüft SU2, ob sich der Widerstand noch ändert (Abbruchkriterium). | `150` | ⏱🎯 | 🔴 |
| `CauchyTolerance` | Wie wenig er sich dabei noch ändern darf. **In Anführungszeichen**, z. B. `"1e-4"`. Kleiner = genauer, länger. | `"1e-4"` | ⏱🎯 | 🟡 |
| `MpiCores` | Rechenkerne für SU2. | `12` | ⏱ | 🟡 |
| `GmshCores` | Rechenkerne für die Netzerzeugung. | `12` | ⏱ | 🟡 |

**Programmpfade** (wo SU2, Gmsh und mpirun auf dem Rechner liegen)

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `MpiRunPath` | Pfad zu `mpirun` (startet SU2 auf mehreren Kernen). | `mpirun` | 🔴 |
| `Su2Path` | Pfad zu `SU2_CFD`. | `SU2_CFD` | 🔴 |
| `GmshPath` | Pfad zu `gmsh`. | `gmsh` | 🔴 |

Der Standard ist nur der Programmname; das funktioniert, wenn der Rechner die Programme „von
allein" findet. **Auf dem Hochschulserver tut er das nicht**, deshalb tragen MantaAuv und
TestprojektNACA die vollen Pfade ein. Ob die Pfade stimmen, prüft `.\scripts\sim.ps1 doctor`.

**Optimierer**

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `OptimizationAlgorithm` | `"Rsm"` = Ersatzmodell-Verfahren: lernt aus allen bisherigen Rechnungen eine „Landkarte" der Fitness und schlägt die vielversprechendsten Stellen vor. Gut bei wenigen, teuren Rechnungen. `"Evolution"` = evolutionär: verändert die besten Varianten zufällig weiter. Ein unbekannter Name führt zu einer Warnung und `"Rsm"`. | `"Rsm"` | 🟡 |

*Nur für `"Rsm"`:*

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `RsmVirtualSimulations` | Wie viele Kandidaten auf der „Landkarte" probeweise bewertet werden, bevor die besten echt gerechnet werden. Kostet kaum Zeit. | `50000` | 🔴 |
| `RsmIdwPower` | Wie stark die Landkarte nahe gelegenen echten Rechnungen vertraut (höher = örtlicher). | `3.0` | 🔴 |
| `RsmExploitationRatio` | Anteil der Kandidaten, die rund um den bisher Besten gesucht werden; der Rest wird frei im ganzen Bereich gesucht. Höher = gezielter, aber Gefahr, ein besseres Gebiet zu übersehen. | `0.7` | 🟡 |
| `RsmExploitationSigma` | Wie weit um den Besten gesucht wird, als Anteil des Parameterbereichs (`0.1` = 10 %). | `0.1` | 🟡 |

In der ersten Runde zieht `"Rsm"` die Varianten zufällig (außer Variante 1 = Startwerte), um
eine erste Landkarte zu bekommen.

*Nur für `"Evolution"`:*

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `RoleFineTuner` | Jede Runde verändert den bisher besten Satz. Die ersten 20 % der Varianten einer Runde verändern ihn nur **fein** (ein Zehntel der Streuung). | `0.2` | 🔴 |
| `RoleCautious` | Varianten bis 40 % der Runde: **mittlere** Veränderung (halbe Streuung). | `0.4` | 🔴 |
| `RoleNormal` | Varianten bis 60 %: **normale** Streuung. Der Rest der Runde sucht mit doppelter Streuung weiter weg. | `0.6` | 🔴 |
| `MinimalDeviationExploration` | Mindestabstand eines neuen Vorschlags zu allen bisherigen (in Parametereinheiten), damit nichts doppelt gerechnet wird. | `1.0` | 🔴 |
| `MinimalDeviationExploitation` | Dasselbe für die Feinjustierer, deshalb kleiner. | `0.05` | 🔴 |

**Nachprüfung der Gewinner („Bouncer")**

Nach jeder Runde wird der Beste noch einmal gerechnet, mit **einem** zufällig gewählten Parameter
leicht verstellt. Ändert sich die Fitness zu stark, war der Gewinner ein Zufallstreffer: Er wird
disqualifiziert, und der Nächstbeste wird geprüft. Varianten mit Fitness unter 0,1 werden gar
nicht erst geprüft.

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `BouncerTolerance` | Erlaubte Abweichung der Fitness bei der Nachprüfung (`0.2` = 20 %). Kleiner = strenger. | `0.2` | 🟡 |
| `BouncerJitter` | Um wie viel der eine Parameter verstellt wird, **in dessen Einheit** (nicht in Prozent!). Bei Parametern mit kleinen Zahlen (z. B. 0,02) ist `0.01` schon riesig. | `0.01` | 🔴 |

### 4.3 `solvers/su2.json` – die Strömungsrechnung

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `FreestreamVelocity` | **Anströmgeschwindigkeit in m/s.** Ist sie größer als 0, gilt sie; dann spielt `MachNumber` keine Rolle. `0` = alter Weg über `MachNumber` × `SpeedOfSound`. MantaAuv: `2.0`. | `0.0` | 🎯 | 🟢 |
| `Density` | Dichte des Fluids in kg/m³. Meerwasser `1025`, Süßwasser ~`998`, Luft `1.225`. | `1025` | 🎯 | 🟢 |
| `DynamicViscosity` | Zähigkeit des Fluids in Pa·s. Meerwasser `0.001001`, Luft `1.81e-5`. | `0.001001` | 🎯 | 🟢 |
| `TurbulenceModel` | Rechenmodell für Turbulenz: `"SA"` (Spalart-Allmaras) oder `"SST"`. | `"SA"` | 🎯 | 🟡 |
| `SpeedOfSound` | Schallgeschwindigkeit in m/s, **nur** für den alten Weg über `MachNumber`. Der Standard ist der Wert für **Luft**; in Wasser ergäbe Mach 0,1 unrealistische 34 m/s. | `343.2` | 🎯 | 🔴 |
| `ReferenceAreaMetric` | Welche Fläche der Geometrie als Bezugsfläche für die Beiwerte (CD, CL) dient, z. B. `"FrontalArea"` (Stirnfläche). Fehlt die Fläche, warnt der Lauf laut, denn die Beiwerte wären dann um Größenordnungen falsch. | `"FrontalArea"` | 🎯 | 🔴 |
| `ReferenceLength` | Bezugslänge in m; wirkt nur auf die Momente (CMx/y/z). | `0.01` | 🎯 | 🔴 |
| `ResultMetrics` | Welche SU2-Kennwerte unter welchem Namen ankommen. Links der Name für die Bewertung, rechts die SU2-Spalte: `CD` Widerstand, `CL` Auftrieb, `CSF` Seitenkraft, `CMx`/`CMy`/`CMz` Momente. **Eintragen ergänzt nur**: `Drag` bleibt immer dabei. Fehlt eine eingetragene Spalte, gilt die Variante als fehlgeschlagen. | `{ "Drag": "CD" }` | 🎯 | 🟡 |
| `ConvergenceField` | An welchem Kennwert SU2 entscheidet, ob die Rechnung fertig ist: `"DRAG"` oder bei Auftriebsprojekten `"LIFT"`. | `"DRAG"` | 🎯⏱ | 🟡 |
| `HistoryOutput` | Welche Spaltengruppen SU2 mitschreibt. `AERO_COEFF` liefert alle Beiwerte; ohne sie findet `ResultMetrics` nichts. | `["ITER","RMS_RES","AERO_COEFF"]` | ⚙ | 🔴 |
| `OscillationWarnLimit` | Schwelle für die Schwingungswarnung: Schwingt ein Beiwert über das Cauchy-Fenster um mehr als diesen relativen Wert `(max − min) / |mean|` (z. B. `0.02` = 2 %), warnt der Solver vor instationärer Strömung (Wirbelablösung). | `0.02` | 🎯 | 🟡 |
| `TreatUnconvergedAsFailed` | Ob Rechnungen, die bei Erreichen von `Su2MaxIterations` (ITER) noch nicht konvergiert sind, als fehlgeschlagen gewertet werden (`SimulationFailed = true`). Standard `false`: Wert wird trotzdem übernommen. | `false` | 🎯 | 🟡 |
| `ResultAveraging` | Wie Beiwerte aus der `history.csv` übernommen werden: `"Last"` = Wert der letzten Iteration (Standard); `"CauchyWindowMean"` = Mittelwert über das Cauchy-Fenster (`CauchyElements`), dämpft Schwingungen bei instationärer Strömung. Unbekannter Wert → Warnung und `"Last"`. | `"Last"` | 🎯 | 🟡 |
| `CflNumber` | Schrittweite des SU2-Verfahrens beim Start. Größer = schneller, aber Gefahr, dass die Rechnung „explodiert". | `5.0` | ⏱ | 🔴 |
| `CflAdapt` | Ob SU2 die Schrittweite selbst anpasst. | `true` | ⏱ | 🔴 |
| `CflAdaptFactorDown` | Wie stark verkleinert wird, wenn es unruhig wird. | `0.5` | ⏱ | 🔴 |
| `CflAdaptFactorUp` | Wie stark vergrößert wird, wenn es gut läuft. | `1.2` | ⏱ | 🔴 |
| `CflAdaptMin` | Kleinste erlaubte Schrittweite. | `1.0` | ⏱ | 🔴 |
| `CflAdaptMax` | Größte erlaubte Schrittweite. | `50.0` | ⏱ | 🔴 |
| `TargetYPlus` | Zielwert für y+ an der Wand (Standard 1.0). Bestimmt die Referenzhöhe für Warnungen. | `1.0` | 🎯 | 🟡 |
| `YPlusWarnFactor` | Warnfaktor für die y+-Prüfung: Liegt das 95 %-Quantil (`YPlus_P95`) über `TargetYPlus` × `YPlusWarnFactor`, warnt der Solver vor unzureichender Grenzschichtauflösung. | `2.0` | 🎯 | 🟡 |

**Wo die Strömung herkommt und hinfließt:** Das steht in keiner Einstellung. Alle sechs Seiten des
Windkanals sind ein gemeinsamer „Fernfeld"-Rand; die Strömung läuft immer in **+x-Richtung**
(von x-min nach x-max). Am Modell gilt Haftbedingung (das Fluid klebt an der Wand).

### 4.4 `solvers/gmsh.json` – das Rechennetz

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `TunnelShape` | Form des Windkanals: `"Box"` (Quader) oder `"Cylinder"` (Zylinder entlang x). Unbekannter Name → Warnung und `"Box"`. | `"Box"` | 🎯 | 🟡 |
| `TunnelSizeX` | Länge des Quaders in Strömungsrichtung (mm). | `600` | 🎯⏱ | 🟡 |
| `TunnelSizeY` | Breite des Quaders (mm). | `300` | 🎯⏱ | 🟡 |
| `TunnelSizeZ` | Höhe des Quaders (mm). | `300` | 🎯⏱ | 🟡 |
| `TunnelCenterX` | Mittelpunkt des Kanals, x (mm). Das Modell sitzt im Nullpunkt; ein positiver Wert schiebt den Kanal nach hinten → mehr Platz für den Nachlauf (Empfehlung: ≥ 10 Modelllängen hinter dem Modell). | `0` | 🎯 | 🟡 |
| `TunnelCenterY` | Mittelpunkt, y (mm). | `0` | 🎯 | 🔴 |
| `TunnelCenterZ` | Mittelpunkt, z (mm). | `0` | 🎯 | 🔴 |
| `TunnelDiameter` | Durchmesser des Zylinders (mm), nur bei `"Cylinder"`. | `300` | 🎯⏱ | 🟡 |
| `TunnelLength` | Länge des Zylinders (mm), nur bei `"Cylinder"`. | `600` | 🎯⏱ | 🟡 |
| `TunnelSegments` | Wie rund der Zylinder ist (Anzahl Kanten am Umfang). | `64` | ⚙ | 🔴 |
| `BoundaryLayerSizeMin` | Kleinste Zellgröße nahe am Modell (mm). Kleiner = genauer, aber viel mehr Zellen. | `1.2` | 🎯⏱ | 🟡 |
| `BoundaryLayerSizeMax` | Größte Zellgröße weit weg vom Modell (mm). | `120` | 🎯⏱ | 🟡 |
| `BoundaryLayerDistMin` | Bis zu diesem Abstand vom Modell gilt die kleinste Zellgröße (mm). | `3.0` | 🎯⏱ | 🟡 |
| `BoundaryLayerDistMax` | Ab diesem Abstand gilt die größte Zellgröße; dazwischen wird übergeblendet (mm). | `40` | 🎯⏱ | 🟡 |
| `WakeRefinement` | Nachlauf-Verfeinerung an/aus. Bei `true` wird eine dynamische Nachlauf-Box hinter dem Modell eingefügt. | `false` | 🎯⏱ | 🟡 |
| `WakeLengthFactor` | Länge der Nachlauf-Box stromabwärts als Vielfaches der Modelllänge `lenX`. | `2.0` | 🎯⏱ | 🟡 |
| `WakeWidthFactor` | Halbe Überbreite der Nachlauf-Box in Y und Z als Vielfaches von `lenY` bzw. `lenZ`. | `0.5` | 🎯⏱ | 🟡 |
| `WakeCellSize` | Zellgröße in der Nachlauf-Box in mm auf Stufe 1.0; `0.0` = automatisch `lenX / 6.0`. | `0.0` | 🎯⏱ | 🟡 |
| `MeshSizeFromCurvature` | Krümmungsabhängige Verfeinerung aktivieren (`Mesh.MeshSizeFromCurvature = 1`). | `false` | 🎯⏱ | 🟡 |
| `BoundaryLayerMode` | Modus der Grenzschicht: `"Threshold"` (Vorgabe, unstrukturiert wie bisher), `"Hexa"` (strukturierte Quader/Hexaeder an der Wand, **Empfehlung**) oder `"Prism"` (strukturierte Prismen, Rückfallebene). | `"Threshold"` | 🎯⏱ | 🟢 |
| `TargetYPlus` | Ziel-y+ für die erste Zellhöhe an der Wand. Daraus errechnet `BoundaryLayerCalculator` die erste Zellhöhe y1. | `1.0` | 🎯 | 🟡 |
| `BoundaryLayerMinLayers` | Mindestanzahl an Schichten in der Grenzschicht (Standard 4). | `4` | 🎯 | 🟡 |
| `BoundaryLayerGrowthRate` | Wachstumsfaktor von Schicht zu Schicht (Standard 1.2). | `1.2` | 🎯 | 🟡 |
| `BoundaryLayerLayers` | Feste Schichtanzahl; `0` = automatisch aus der Grenzschichtdicke delta berechnen. | `0` | 🎯 | 🟡 |
| `SurfaceCellSize` | Zellgröße auf der Bauteiloberfläche in mm auf Stufe 1.0; `0.0` = nutzt `BoundaryLayerSizeMin`. | `0.0` | 🎯⏱ | 🟡 |
| `CurvatureElements` | Anzahl Elemente pro 2π bei krümmungsabhängiger Netzverfeinerung (`Mesh.MeshSizeFromCurvature`). | `20` | 🎯⏱ | 🟡 |
| `ClassifyAngleDeg` | Winkelgrenze in Grad für Gmsh `ClassifySurfaces` bei der Neuvernetzung der Oberfläche. | `40.0` | ⚙ | 🔴 |

**Faustregeln für den Windkanal:** vor dem Modell ≥ 5 Modelllängen, dahinter ≥ 10, seitlich ≥ 3.
Heute (600 mm bei 50 mm Modell, mittig) sind es vorn und hinten je 6 Längen.

**Empfehlung Wandschicht ("Hexa" vs. "Prism"):**
Im Modus `"Hexa"` wird die Bauteiloberfläche vor der Extrusion in Vierecke zerlegt (`Mesh.RecombineAll = 1`) und anschließend mit `Recombine` senkrecht zur Wand extrudiert. Aus Vierecken werden **Hexaeder (flache Quader)**, die orthogonal zur Wand stehen und in Strömungsrichtung sauber hintereinanderliegen.
* **Vorteile von Quader-Grenzschichten:** Wesentlich geringere numerische Diffusion, präzise Erfassung der Geschwindigkeitsgradienten in Wandnähe, stabilere und glattere Wandreibungswerte und y+-Profile sowie weniger Gesamtzellen bei gleicher Auflösung als mit Prismen.
* **Quad-Dominant:** Bei komplex gekrümmten Voxel-Oberflächen bleiben vereinzelt Dreiecke übrig; daraus entstehen Prismen zwischen den Quadern. Gmsh erzeugt somit ein „quad-dominantes" Grenzschichtnetz, das SU2 problemlos rechnet.
* Der Modus `"Prism"` dient als Rückfallebene (reine Dreiecks-Extrusion zu Prismen), falls für ein STL keine saubere Viereckzerlegung gelingt. `"Threshold"` behält das rein unstrukturierte Netz früherer Versionen bei.

---

## 5. Welche Werte galten wirklich?

Nach einem Lauf lässt sich immer nachvollziehen, womit gerechnet wurde, auch wenn die Dateien
inzwischen geändert sind:

| Wo | Was steht dort |
| --- | --- |
| **Kopf des Logs** (`simulation.log`) | Projekt, Code-Stand, und für jede Datei, welche Einstellungen *aus ihr* kamen (`[CONFIG] Herkunft der wirksamen Werte`). Alles Ungenannte war der Standardwert. Darunter die Anströmung (`Anströmung: 2 m/s (…)`) |
| **`Ergebnisse/<Projekt>/effective-config.json`** | Alle wirksamen Werte des Laufs, je Einstellung mit der Datei, aus der sie kam, plus jede `*.local.json`-Überschreibung (`MaschinenspezifischeUeberschreibungen`). **Das ist die Akte eines Laufs.** `fetch` holt sie immer mit |
| **`Ergebnisse/<Projekt>/Simulation_Results.csv`** | Je Variante eine Zeile: Iteration, Variante, alle Parameter, alle Kennwerte (Geometrie und SU2), Fitness, Dateipfade, `SimulationFailed` (1 = Rechnung abgebrochen). Neu seit TODO-35: `Su2_Iterations` (tatsächlich gerechnete Schritte), `Su2_Converged` (1 = vor ITER konvergiert, 0 = bei ITER abgebrochen) und `Su2_Osc_<Metrik>` (z. B. `Su2_Osc_Drag`: relative Schwingung über das Cauchy-Fenster); seit TODO-39: `MeshCells` (Zellzahl aus dem `.su2`-Kopf); seit TODO-40/41: `BoundaryLayerHexShare` (Quader-Anteil in der Grenzschicht) sowie `YPlus_Median`, `YPlus_P95`, `YPlus_Max` (y+-Wandauflösung) |
| **`Ergebnisse/<Projekt>/Analyseergebnisse/Verlaeufe/`** | Vollständiger Verlauf jeder gerechneten Variante: `History_Gen{i}_Var{v}.csv` (SU2-Iterationen mit allen Beiwerten und Residuen) und `Config_Gen{i}_Var{v}.cfg` (wirksame SU2-Steuerdatei). Im Abbruchfall liegen beide in `FehlerLogs/` |
| **`.\scripts\sim.ps1 doctor -Project <Name>`** | *Vor* einem Lauf: welche Programmpfade wirklich gelten und ob die Programme gefunden werden |

---

## 6. Die Befehle

Alles wird von Windows aus mit `.\scripts\sim.ps1 <befehl> -Project <Name>` gesteuert. Die
Einzelheiten stehen in [`scripts/README.md`](../../scripts/README.md).

| Befehl | Was er tut |
| --- | --- |
| `run` | Der Alltagsbefehl: Code hochladen, bauen, Lauf starten. Danach kann das Fenster zu. |
| `deploy` | Nur hochladen und bauen. Läuft schon etwas und hat sich der Code geändert, wird neu gestartet (außer mit `-NoRestart`). |
| `build` | Nur bauen. |
| `status` | Läuft etwas? Seit wann? Wie viele Ergebnisse? Letzte Logzeilen. Ohne `-Project` wird das zuletzt gerechnete Projekt abgefragt. |
| `log` | Log live mitlesen. Strg+C beendet nur das Mitlesen, nicht den Lauf (`-NoFollow` = einmal ausgeben, `-Lines 100` = mehr Zeilen). |
| `stop` | Lauf und alle Rechenprozesse beenden. |
| `fetch` | Ergebnisse nach `Serverergebnisse\<Server>_<Projekt>_<Zeitstempel>\` holen: CSV, `effective-config.json`, Log, Verläufe. Mit `-Champion` zusätzlich die VTUs des Champions/Top-N, mit `-All` den ganzen Ergebnisordner (groß). Ohne `-Project` wird automatisch das zuletzt gerechnete Projekt geholt. |
| `doctor` | Prüft auf dem Server Programme und Pfade, ohne etwas zu starten. Vor dem ersten Lauf. |
| `projects` | Welche Projekte kennt der Server, und wo liegen schon Ergebnisse? |
| `delete` | Löscht nach Rückfrage die Ergebnisse **aller** Projekte und die Logs auf dem Server (Code bleibt). Vorher holen, was man behalten will. |

Weitere Schalter: `-Project` (welches Projekt; bei `run`/`deploy`/`doctor` gilt ohne Angabe `MantaAuv`, bei `fetch`/`status` wird automatisch das letzte Projekt vom Server abgefragt), `-Champion` (`fetch`: VTUs des Champions/Top-N mitnehmen), `-All` (`fetch`: kompletten Ergebnisordner holen), `-Server` (Standard `BIC_12`), `-RemoteDir` (Zielordner auf dem Server), `-Force` (hochladen erzwingen bzw. ohne Rückfrage löschen), `-Out` (Zielordner für `fetch`).

Ein neues Projekt: `.\scripts\new-project.ps1 <Name>`.

---

## 7. Häufige Meldungen und was zu tun ist

| Meldung / Symptom | Bedeutung | Was tun |
| --- | --- | --- |
| `'…' nennt einen Schlüssel, den der Typ … nicht kennt` | Tippfehler in einer JSON-Datei. | Die Meldung nennt den vermutlich gemeinten Namen bzw. alle gültigen. Korrigieren. |
| `[WARNUNG] Eine maschinenspezifische Datei ueberschreibt dieses Projekt` | Eine `*.local.json` verändert still Werte. | Gehören die Werte zum Projekt: in die Projektdatei übernehmen und die local-Datei löschen. |
| `[WARNUNG] 'MachNumber' in … ist wirkungslos, weil in solvers/su2.json 'FreestreamVelocity' (… m/s) gesetzt ist.` | In `solvers/su2.json` ist eine explizite Anströmgeschwindigkeit vorgegeben. SU2 rechnet direkt mit dieser Geschwindigkeit; der Wert `MachNumber` aus der Simulationskonfiguration hat keinen Einfluss auf die Anströmung. | Soll wieder über Mach gerechnet werden: `FreestreamVelocity` in `su2.json` auf `0` setzen. Sonst ist die Meldung nur ein Hinweis darauf, dass `MachNumber` wirkungslos ist. |
| `Kein -Project angegeben, es gilt die Vorgabe 'MantaAuv'` | Der Befehl wirkt auf MantaAuv. | Gilt bei `run`/`deploy`/`doctor`. Bei `fetch` und `status` fragt das Skript automatisch das letzte Projekt auf dem Server ab. |
| `FEHLER: Simulation_Results.csv fuer Projekt '…' fehlt nach dem Entpacken` | Für dieses Projekt liegen noch keine Ergebnisse auf dem Server vor. | Die Meldung listet die vorhandenen Projekte auf dem Server auf. Projektname prüfen oder Lauf erst abwarten. |
| `[WARNUNG] Das geholte Log gehoert laut Kopfzeile zu Projekt '…'` | `simulation.log` stammt von einem anderen Projekt als die geholten Ergebnisse. | Das gewünschte Projekt mit `-Project` angeben oder einen neuen Lauf starten. |
| `[WARNUNG] … Referenzfläche …` / `Die Metrik '…' fehlt` | Die Bezugsfläche (`ReferenceAreaMetric`) gibt es nicht. CD/CL sind dann falsch. | Den Namen in `su2.json` an die Metrik anpassen, die die Geometrie meldet (die Meldung listet sie auf). |
| `[WARNUNG] Ergebnisgröße '…' <- Spalte '…'` | Ein Eintrag in `ResultMetrics` passt zu keiner SU2-Spalte. Die Variante gilt als fehlgeschlagen. | Spaltennamen korrigieren (die Meldung listet die vorhandenen). |
| `[WARNUNG] SU2 hat ITER=… erreicht, ohne dass das Kriterium erfüllt war – … ist der Stand bei Iteration …, kein konvergierter Wert.` | Die Strömungsrechnung hat die maximale Iterationszahl erreicht, bevor der Wert stabil war. Der Wert ist eine Momentaufnahme und noch nicht verlässlich. | `Su2MaxIterations` in `simulation.json` erhöhen (z. B. 1000) oder prüfen, ob die Strömung schwingt. Soll der Lauf unvollständige Rechnungen verwerfen: `TreatUnconvergedAsFailed` in `su2.json` auf `true` setzen. |
| `[WARNUNG] … schwingt um ±… % – Hinweis auf instationäre Strömung (Wirbelablösung).` | Der Beiwert schwankt im Cauchy-Fenster stärker als `OscillationWarnLimit` (z. B. ±2 %). Meist lösen sich periodisch Wirbel ab (z. B. Kármánsche Wirbelstraße bei stumpfen Körpern). | Prüfen, ob die Geometrie zu stumpf ist, oder `ResultAveraging` in `su2.json` auf `"CauchyWindowMean"` setzen, um über die Schwingung zu mitteln. Schwellenwert mit `OscillationWarnLimit` anpassen. |
| `[WARNUNG] y+ (95 %-Quantil = …, Max = …) überschreitet das Limit … (YPlusWarnFactor × TargetYPlus) – Grenzschichtauflösung prüfen.` | Der dimensionslose Wandabstand y+ ist an der Bauteilwand zu groß (das 95 %-Quantil P95 übersteigt das Limit). Das Turbulenzmodell (SA/SST) löst die viskose Unterschicht nicht optimal auf, wodurch der Reibungswiderstand ungenau werden kann. | `BoundaryLayerMode` ("Hexa") aktivieren, `TargetYPlus` prüfen, `BoundaryLayerSizeMin` bzw. `SurfaceCellSize` verfeinern oder `YPlusWarnFactor` erhöhen, falls ein größerer Wandabstand für die Grobauslegung ausreicht. |
| `CFD-Simulation für … abgebrochen` | SU2 ist gescheitert. | Details in `Ergebnisse/<Projekt>/FehlerLogs/`. Häufig: Netz kaputt (dünne Stellen), Schrittweite zu groß (`CflNumber`). |
| `Gmsh konnte … nicht erstellen` | Das Netz ließ sich nicht bauen. | Meist reißt die Geometrie an dünnen Stellen ab: `BaseVoxelResolution` verkleinern oder Mindestdicken im Geometrie-Code erhöhen. |
| `Nach der ersten gerechneten Variante fehlt/fehlen die Metrik(en) …` | Die Bewertungsformel braucht einen Wert, den niemand liefert. | Fehlt ein SU2-Wert: in `ResultMetrics` eintragen. Fehlt ein Geometriewert: im Geometrie-Code melden. |
| `[ABGELEHNT] Modell ist instabil. Disqualifiziert.` | Der Bouncer hat einen Zufallstreffer aussortiert. Das ist normal. | Passiert es fast immer: `BouncerTolerance` lockern oder `BouncerJitter` verkleinern. |
| `Unbekanntes Projekt '…'` | Projektname vertippt (Groß-/Kleinschreibung zählt). | `.\scripts\sim.ps1 projects` zeigt die gültigen Namen. |
| `sim.ps1` bricht nach ein bis drei Minuten mit einem SSH-Fehler ab | Die Verbindung (VPN) war tot. | VPN prüfen, Befehl wiederholen. **Ein laufender Lauf auf dem Server rechnet trotzdem weiter.** |

---

## 8. Begriffe kurz erklärt

| Begriff | Erklärung |
| --- | --- |
| **Variante** | Ein einzelner Formvorschlag, der komplett gerechnet wird (Geometrie → Netz → Strömung → Bewertung). |
| **Iteration / Runde** | Eine Gruppe von Varianten; danach wertet der Optimierer aus und plant die nächste Runde. |
| **Fitness** | Die Note einer Variante. Höher = besser. Die Formel steht im Projektcode. |
| **Champion** | Die Variante mit der besten Fitness im ganzen Lauf. |
| **Bouncer** | Der „Türsteher": rechnet den Gewinner einer Runde leicht verändert nach und sortiert Zufallstreffer aus. |
| **Voxel** | Würfelförmiger Baustein, aus dem PicoGK die Form zusammensetzt (wie Pixel in 3D). |
| **Gummiband-Skalierer** | Verkleinert bzw. vergrößert die Form für PicoGK auf eine feste Arbeitsgröße und rechnet alle Kennwerte danach auf die echte Größe zurück. |
| **Netz** | Die Zerlegung des Windkanals in viele kleine Zellen, in denen SU2 rechnet. Feiner = genauer und langsamer. |
| **Windkanal / Fernfeld** | Der Rechenraum um das Modell. Am Rand herrscht die ungestörte Strömung. |
| **CD, CL** | Widerstands- bzw. Auftriebsbeiwert: Kraft geteilt durch (Staudruck × Bezugsfläche). |
| **Bezugsfläche** | Die Fläche, auf die CD und CL bezogen werden (z. B. Stirnfläche). Eine andere Fläche ergibt andere Zahlen, bei der Optimierung kürzt sie sich oft heraus. |
| **Reynoldszahl (Re)** | Kennzahl aus Geschwindigkeit, Größe und Zähigkeit: Sie sagt, wie „turbulent" eine Strömung ist. MantaAuv bei 2 m/s: etwa 100 000. |
| **Turbulenzmodell (SA, SST)** | Vereinfachte Beschreibung der Wirbel, die man nicht einzeln ausrechnen kann. |
| **Grenzschicht** | Die dünne Fluidschicht direkt an der Wand, in der die Geschwindigkeit von 0 auf die Strömung ansteigt. Dort entsteht der Reibungswiderstand. |
| **y+** | Wie dick die erste Netzzelle an der Wand im Vergleich zur Grenzschicht ist. Für SA ohne Wandfunktion sollte y+ ≈ 1 sein. |
| **Cauchy-Kriterium** | SU2 hört auf, wenn sich die überwachte Ergebnisgröße (`ConvergenceField`, z. B. Widerstand) über die letzten `CauchyElements` Schritte um weniger als `CauchyTolerance` ändert. Hört SU2 vor `Su2MaxIterations` auf, gilt die Rechnung als konvergiert (`Su2_Converged = 1`). Über dieses Fenster misst der Analyzer auch Schwingungen (`Su2_Osc_<Metrik>`). |
| **CFL-Zahl** | Schrittweite des Rechenverfahrens: größer = schneller, aber weniger stabil. |
| **RSM (Ersatzmodell)** | Optimierer, der aus allen bisherigen Rechnungen eine geschätzte Fitness-Landkarte baut und dort sucht. |
| **Evolutionär (EA)** | Optimierer, der gute Varianten zufällig weiterverändert („Mutation"). |
| **`*.local.json`** | Notausgang für Einstellungen, die nur auf einem Rechner gelten sollen. Soll es normalerweise nicht geben. |
