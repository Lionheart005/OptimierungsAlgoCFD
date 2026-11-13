# Bedienhandbuch: Was stelle ich wo ein?

Dieses Handbuch erklärt **ohne Programmierkenntnisse**, wo man das Programm einstellt, was jede
Einstellung bewirkt und welche man besser in Ruhe lässt. Es beschreibt den Stand vom
**13.11.2026**. Neue Einstellungen kommen hier dazu, sobald es sie gibt: Ein Test im Projekt
schlägt Alarm, wenn eine Einstellung hier fehlt.

> **Weiterführende Dokumente:**
> - Neues Bauteil / Projekt programmieren: [`src/projects/README.md`](README.md)
> - Framework-Architektur & C#-Entwicklung: [`src/OptimierungsAlgoCFD/README.md`](../OptimierungsAlgoCFD/README.md)
> - Server-Steuerung, Deployment & Einrichtung: [`scripts/README.md`](../../scripts/README.md)
> - Lokale Ausnahmedateien (`*.local.json`): [`config/README.md`](../../config/README.md)

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
9. [Grenzen des Systems](#9-grenzen-des-systems)

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

**Die Vorlage `_Vorlage3D/`** enthält *jede* Einstellung mit ihrem Standardwert und einer
Erklärung. Sie ist das Nachschlagewerk zum Abschreiben. Ein neues Projekt legt man mit
`.\scripts\new-project.ps1 <Name>` an; das kopiert die Vorlage.

---

## 3. „Ich will …" – die häufigsten Aufgaben

| Ich will … | Datei | Einstellung | Beispiel / Hinweis |
| --- | --- | --- | --- |
| … einen **kurzen Testlauf** | `simulation.json` | `MaxIterations`, `VariantsPerIteration` | `1` und `2` ergibt 2 Varianten plus eine Nachprüfung. Danach wieder zurückstellen! |
| … **mehr Varianten** rechnen (gründlicher) | `simulation.json` | `MaxIterations`, `VariantsPerIteration` | 10 × 10 = 100 Varianten, bei ~3 min je Variante etwa 5–6 h |
| … dass der Lauf **von selbst aufhört**, wenn kaum noch etwas besser wird | `simulation.json` | `AutoStop` | `"Mode": "On"`; `MaxIterations` × `VariantsPerIteration` bleibt die Obergrenze (siehe Abschnitt 4.2) |
| … dass SU2 **länger/genauer** rechnet | `simulation.json` | `Su2MaxIterations`, `CauchyTolerance` | mehr Iterationen bzw. kleinere Toleranz (`"1e-5"`) |
| … **schneller** rechnen | `simulation.json` | `MpiCores`, `GmshCores` | höchstens so viele, wie der Server Kerne hat; dazu ggf. `Su2MaxIterations` senken |
| … ein **feineres Netz** an der Oberfläche | `solvers/gmsh.json` | `BoundaryLayerSizeMin` | kleiner = feiner = langsamer. Dazu verfeinert `MeshSizeFromCurvature` Krümmungen automatisch (begrenzt durch `CurvatureMinSizeFactor`); `AutoVoxelResolution` / `VoxelResolutionAdvisor` prüfen beim Start gegen zu grobe Voxel (`BaseVoxelResolution`) |
| … eine **andere Strömungsgeschwindigkeit** | `solvers/su2.json` | `FreestreamVelocity` | in m/s, z. B. `2.0`. Ist sie 0, gilt der alte Weg über `MachNumber` |
| … **Luft statt Wasser** | `solvers/su2.json` | `Density`, `DynamicViscosity` | Luft: `1.225` und `1.81e-5`; Meerwasser: `1025` und `0.001001` |
| … zusätzlich den **Auftrieb** (oder andere Kennwerte) haben | `solvers/su2.json` | `ResultMetrics` | `"Lift": "CL"` dazuschreiben; in der Bewertungsformel (C#) verwenden |
| … ein **anderes Turbulenzmodell** | `solvers/su2.json` | `TurbulenceModel` | `"SA"` (Standard) oder `"SST"` |
| … den **Optimierer wechseln** | `simulation.json` | `OptimizationAlgorithm` | `"Bayes"` (Standard: lernendes Ersatzmodell, spart teure Rechnungen; siehe Abschnitt 4.2), `"Rsm"` (einfacher, braucht keine Parametergrenzen) oder `"Evolution"` |
| … **Startwerte** oder **Grenzen** eines Parameters ändern | `project.json` | `BaseParameters`, `ParameterBounds` | Startwert muss innerhalb der Grenzen liegen |
| … einen **Parameter fest einstellen** (nicht optimieren) | `project.json` | `ParameterBounds` | `Min` und `Max` auf denselben Wert setzen — der Parameter wird dann immer auf diesen Wert geklemmt |
| … dass der Optimierer **größere/kleinere Sprünge** macht | `project.json` | `MaxDeviations` | Streuung je Parameter, in dessen Einheit |
| … die **Ziele** der Bewertung ändern (z. B. Volumengrenzen) | `project.json` | `OptimizationTargets` | welche Namen es gibt, bestimmt die Bewertungsformel des Projekts |
| … dass Gewinner **strenger/milder** nachgeprüft werden | `simulation.json` | `BouncerTolerance` | `0.2` = 20 % Abweichung erlaubt |
| … einen **größeren Windkanal** | `solvers/gmsh.json` | `TunnelSizeX/Y/Z` | in mm; größer = weniger Randeinfluss, aber mehr Zellen |
| … ein **neues Projekt** | — | `.\scripts\new-project.ps1 <Name>` | danach Form und Bewertung im neuen Ordner ausfüllen |
| … das **Projekt wechseln** | — | `-Project <Name>` beim Befehl | z. B. `.\scripts\sim.ps1 run -Project Naca2D` |
| … **Ergebnisse holen** | — | `.\scripts\sim.ps1 fetch -Project <Name>` | **`-Project` immer angeben**, sonst wird MantaAuv geholt (siehe Abschnitt 7) |
| … eine **Rechnung von Hand prüfen** | — | `fetch`, dann `plot_history.py` | Konvergenz prüfen; bei Bedarf `fetch -Champion` und ParaView für die VTUs (Details in `scripts/README.md`) |
| … nachsehen, **womit ein Lauf gerechnet hat** | — | `effective-config.json`, `run-info.json` | siehe [Abschnitt 5](#5-welche-werte-galten-wirklich) |
| … einen Lauf **exakt wiederholen** | `simulation.json` | `RandomSeed` | den Seed aus `run-info.json` (oder der Logzeile `Zufalls-Seed`) eintragen |
| … einen **abgebrochenen Lauf fortsetzen** (Server-Neustart, Stromausfall) | — | `.\scripts\sim.ps1 resume -Project <Name>` | siehe [Abschnitt 6.1](#61-abgebrochenen-lauf-fortsetzen) |

---

## 4. Alle Einstellungen, Datei für Datei

**So liest du die Tabellen:**
- **Wann anfassen:** 🟢 *oft* (dafür ist die Einstellung da) · 🟡 *gelegentlich* (wenn man weiß,
  wozu) · 🔴 *nur mit Grund* (Falsche Werte verfälschen Ergebnisse oder brechen den Lauf).
- **Wirkung:** ⏱ beeinflusst vor allem die Rechenzeit · 🎯 beeinflusst vor allem die
  Genauigkeit · ⚙ technische Einstellung.
- **Längen in Netz und Windkanal** wirken in **echten mm**: Ist der Gummiband-Skalierer an
  (`UseRubberBandScaler`), verkleinert er die Form nur intern für PicoGK auf `TargetPicoGkSize`;
  `GmshCfdMesher` skaliert seit AP-2/AP-3 alle konfigurierten mm-Zellgrößen und Kanalabstände
  einheitlich mit `scaleFactor`, sodass sie in der realen Bauteilgröße greifen. Beim Start prüft
  `VoxelResolutionAdvisor` zudem, ob `BaseVoxelResolution` zur Oberflächen-Zellgröße passt.

### 4.1 `project.json` – was optimiert wird

| Einstellung | Bedeutung | Einheit | Beispiel (MantaAuv) | Wann |
| --- | --- | --- | --- | --- |
| `BaseParameters` | Startwerte der Parameter, die der Optimierer verändern darf. Mit diesen Werten wird die allererste Variante gerechnet. | je Parameter | `"Length": 50.0` | 🟢 |
| `MaxDeviations` | Wie weit der Optimierer einen Parameter anfangs ungefähr verstellt (Streuung). | wie der Parameter | `"Length": 5.0` | 🟡 |
| `ParameterBounds` | Harte Grenzen je Parameter (`Min`, `Max`). Kein Vorschlag liegt außerhalb. | wie der Parameter | `"Length": { "Min": 30, "Max": 100 }` | 🟢 |
| `OptimizationTargets` | Zahlen, die die Bewertungsformel braucht (z. B. erlaubtes Volumen). Welche Namen gelten, legt der Bewertungscode des Projekts fest; fehlt ein benötigter Name, bricht der Start mit einer Liste ab. | je Ziel | `"MinimumAllowedVolume": 2000.0` | 🟢 |
| `DimensionalParameters` | Welche Parameter **Längen** sind. Nur diese werden vom Gummiband-Skalierer mitskaliert; Verhältniszahlen (z. B. `TailTaper`) gehören nicht hinein. | Liste von Namen | `[ "Length", "Width", … ]` | 🔴 |

> **Tipp – Anstellwinkel als aktiver Optimierungsparameter:**
> Soll der Anstellwinkel aktiv variiert und optimiert werden, tragen Sie `"AngleOfAttackDeg"` (oder `"AngleOfAttack"`) als aktiven Parameter in `project.json` (`BaseParameters`, `MaxDeviations` und `ParameterBounds`) ein. Der Solver erkennt dies automatisch, steuert den Anstellwinkel für jede Variante gezielt an, gibt ihn im Konsolen-Log aus (`    -> Anstellwinkel (aktiv optimiert): ...°`) und legt den Wert für die Fitnessberechnung in `PassiveParameters["AngleOfAttackDeg"]` ab.

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
| `AutoStop.Mode` | Automatisches Ende: `"On"` oder `"Off"`. Bei `"On"` prüft das Programm nach jeder Runde, ob sich der beste Wert in den letzten `WindowVariants` Varianten noch um mindestens `MinImprovement` verbessert hat; sonst endet die Optimierung (die Feinprüfung am Ende läuft wie gewohnt). `MaxIterations` × `VariantsPerIteration` bleibt die **Obergrenze**, die Restzeit im Log ist deshalb ein Höchstwert. Neue Projekte aus den Vorlagen haben `"On"` mit Fenster `10` (2 Runden zu 5 Varianten); wer `VariantsPerIteration` erhöht, zieht das Fenster mit. | `"Off"` | ⏱ | 🟢 |
| `AutoStop.MinImprovement` | Mindestverbesserung des Bestwerts, relativ zum Bestwert vor dem Messfenster: `0.01` = 1 %. Nicht kleiner wählen als die Genauigkeit einer Einzelrechnung, sonst entscheidet das Rechenrauschen. | `0.01` | ⏱🎯 | 🟢 |
| `AutoStop.WindowVariants` | Über so viele zuletzt gerechnete Varianten wird die Verbesserung gemessen. Frühestens nach `WindowVariants` + 1 Varianten kann der Lauf enden. Mindestens so groß wählen wie der Versuchsplan am Anfang (bei Bayes `max(VariantsPerIteration, 2 × freie Parameter + 2)`), bewährt sind 2 Runden. Fehlgeschlagene Varianten zählen mit. | `20` | ⏱🎯 | 🟢 |
| `BaseVoxelResolution` | Kantenlänge eines Voxels (Bausteins) der Geometrie in mm. Kleiner = feinere, glattere Form, aber deutlich langsamer und speicherhungriger. Dünne Stellen unter ~1,2 Voxeln können abreißen. | `0.5` | 🎯⏱ | 🟡 |
| `AutoVoxelResolution` | Passt bei 3D-`GmshCfdMesher`-Läufen die effektive Start-Voxelgröße beim Kern-Start automatisch nach unten an (Untergrenze `0.1` mm), wenn der Realraum-Voxel größer als 80 % der Oberflächen-Zielzellgröße (`SurfaceCellSize` / `BoundaryLayerSizeMin` × `MeshRefinementFactor`) wäre. Verändert nicht `simulation.json` oder den `--resume`-Fingerprint; bei `false` erscheint im groben Fall nur eine Warnung. | `false` | 🎯⏱ | 🟡 |
| `TargetPicoGkSize` | Auf diese Größe (mm, größte Länge) wird das Modell für PicoGK skaliert, wenn der Gummiband-Skalierer an ist. Zusammen mit `BaseVoxelResolution` bestimmt das, wie viele Voxel ein Modell hat. | `50.0` | 🎯⏱ | 🔴 |
| `UseRubberBandScaler` | Gummiband-Skalierer an/aus. **Aus**, wenn das Projekt keine Längen-Parameter hat (z. B. nur Prozentwerte wie bei NACA), sonst wird falsch skaliert. | `true` | 🎯 | 🔴 |
| `UseStlSmoothing` | Glättet die Oberfläche nach dem Voxel-Bau (gegen die „Treppenstufen"). | `true` | 🎯 | 🟡 |
| `UseProjectedFrontalArea` | Berechnet `FrontalArea` in 3D als wahre projizierte Silhouette (Schattenwurf in der YZ-Ebene quer zur Anströmung) statt als Rechteck des Hüllquaders (`Size.Y × Size.Z`). Bei `false` wird das umschließende Rechteck (`FrontalBoxArea`) verwendet. | `true` | 🎯 | 🟡 |
| `VoxelSmoothingIterations` | Anzahl der Glättungsdurchgänge (formerhaltend). | `40` | 🎯 | 🔴 |
| `VoxelSmoothingPremeltingSteps` | Zusätzliche grobe Vorglättungen vor den formerhaltenden Durchgängen. Diese schrumpfen die Form leicht, also klein halten. | `2` | 🎯 | 🔴 |

**Strömungsrechnung (SU2) und Rechenleistung**

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `MachNumber` | **Veralteter/optionaler Rückfallwert** zur Geschwindigkeit: Geschwindigkeit = `MachNumber` × `SpeedOfSound` (aus `su2.json`). Wirkt **nur**, wenn in `su2.json` `FreestreamVelocity` 0 ist. In Standardprojekten zugunsten von `FreestreamVelocity` aus `simulation.json` entfernt. | `0.1` | 🎯 | 🟡 |
| `MeshRefinementFactor` | Globaler Faktor für alle Zellgrößen im Netz. `1.0` = Standardgrößen; kleiner als 1,0 verfeinert das Netz (z. B. `0.5` halbiert die Zellkanten), größer als 1,0 vergröbert es für schnelle Vorabläufe. **Nur bei `MeshCalibration.Mode = "Manual"` eintragen;** im Auto-Modus (Standard) bricht ein eingetragener Faktor den Start ab, die Vorlagen lassen ihn deshalb weg. | `1.0` | ⏱🎯 | 🟡 |
| `Su2MaxIterations` | Höchstzahl der SU2-Rechenschritte je Variante. Ist sie erreicht, hört SU2 auf, auch wenn die Lösung noch nicht ruhig ist. 3000 reicht für RANS mit y+ ≈ 1 in 2D und 3D; eine ruhige Rechnung hört über das Cauchy-Kriterium früher auf. | `3000` | ⏱🎯 | 🟡 |
| `CauchyElements` | Über so viele Schritte prüft SU2, ob sich der Widerstand noch ändert (Abbruchkriterium). | `300` | ⏱🎯 | 🔴 |
| `CauchyTolerance` | Wie wenig er sich dabei noch ändern darf. **In Anführungszeichen**, z. B. `"1e-5"`. Kleiner = genauer, länger. | `"1e-5"` | ⏱🎯 | 🟡 |
| `MpiCores` | Rechenkerne für SU2. | `12` | ⏱ | 🟡 |
| `GmshCores` | Rechenkerne für die Netzerzeugung. | `12` | ⏱ | 🟡 |

**Programmpfade** (wo SU2, Gmsh und mpirun auf dem Rechner liegen)

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `MpiRunPath` | Pfad zu `mpirun`/`mpiexec` (startet SU2 auf mehreren Kernen). **Das SU2-Programm braucht MPICH.** Bei dem Namen `mpirun` oder `mpiexec` nimmt das Programm automatisch das MPICH-Environment `~/miniconda/envs/mpich/bin/mpiexec`, falls es existiert (legt `setup_server.sh` an); sonst gilt der PATH. Ein ausdrücklich eingetragener Pfad gilt unverändert; zeigt er auf OpenMPI, warnt das Programm beim Start. | `mpirun` | 🔴 |
| `Su2Path` | Pfad zu `SU2_CFD`. | `SU2_CFD` | 🔴 |
| `GmshPath` | Pfad zu `gmsh`. | `gmsh` | 🔴 |

Der Standard ist nur der Programmname; das funktioniert, wenn der Rechner die Programme „von
allein" findet. **Auf dem Hochschulserver tut er das nicht**, deshalb tragen MantaAuv und
Naca2D die vollen Pfade ein. Ob die Pfade stimmen, prüft `.\scripts\sim.ps1 doctor`.

**Optimierer**

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `OptimizationAlgorithm` | **Standard `"Bayes"`** (im 10 × 10-Vergleich auf Naca2D bei 100 Rechnungen klar am besten, siehe `.plans/vergleich_optimierer.md`). `"Rsm"` = Ersatzmodell-Verfahren: lernt aus allen bisherigen Rechnungen eine „Landkarte" der Fitness und schlägt die vielversprechendsten Stellen vor. Gut bei wenigen, teuren Rechnungen. `"Evolution"` = evolutionär: verändert die besten Varianten zufällig weiter. `"Bayes"` = Bayes-Optimierung: lernt ebenfalls eine Landkarte, **zeichnet aber zusätzlich den „Nebel" ein**, also wo sie sich nicht auskennt, und rechnet dort, wo die Chance am größten ist, den bisher Besten zu schlagen. Spart bei teuren Rechnungen die meisten Varianten (Entscheidungshilfe unten). Ein unbekannter Name führt zu einer Warnung und `"Bayes"`. | `"Bayes"` | 🟡 |
| `RandomSeed` | Startwert des Zufalls (Optimierer und Türsteher). `0` = bei jedem Lauf neu ziehen; der gezogene Wert steht im Log (`[SYSTEM] Zufalls-Seed: …`) und in `run-info.json`. Wer einen Lauf **exakt wiederholen** will, trägt diesen Wert hier ein. | `0` | 🟢 |

*Nur für `"Rsm"`:*

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `RsmVirtualSimulations` | Wie viele Kandidaten auf der „Landkarte" probeweise bewertet werden, bevor die besten echt gerechnet werden. Kostet kaum Zeit. | `50000` | 🔴 |
| `RsmIdwPower` | Wie stark die Landkarte nahe gelegenen echten Rechnungen vertraut (höher = örtlicher). | `3.0` | 🔴 |
| `RsmExploitationRatio` | Anteil der Kandidaten, die rund um den bisher Besten gesucht werden; der Rest wird frei im ganzen Bereich gesucht. Höher = gezielter, aber Gefahr, ein besseres Gebiet zu übersehen. | `0.7` | 🟡 |
| `RsmExploitationSigma` | Wie weit um den Besten gesucht wird, als Anteil des Parameterbereichs (`0.1` = 10 %). | `0.1` | 🟡 |

*Nur für `"Bayes"` (Abschnitt `Bayes` in `simulation.json`, alles optional, die Vorgaben passen meist):*

> **Entscheidungshilfe:** Standard ist `"Bayes"`: Sind die Rechnungen **teuer** (Minuten bis Stunden je
> Variante) und gibt es **bis etwa 20 Parameter**, ist es ab etwa 40 Rechnungen deutlich besser als die
> anderen. Bei einem **sehr kleinen Budget** (bis etwa 25 Rechnungen) ist `"Rsm"` gleichwertig und
> einfacher, es braucht keine Parametergrenzen. Für einen **Vergleich oder eine Gegenprobe**: `"Rsm"`
> oder `"Evolution"`. Alle drei verwenden dieselbe Bewertungsformel und denselben Türsteher. MantaAuv
> rechnet seit 09.11.2026 ebenfalls mit `"Bayes"`.
>
> **Zwei Regeln für `"Bayes"`:** (1) Für **jeden** Parameter müssen Grenzen in `ParameterBounds`
> stehen (`project.json`); fehlt eine, bricht der Lauf **vor der ersten Rechnung** ab und nennt den
> Parameter. (2) `MaxDeviations` wirkt hier **nicht**, die Grenzen bestimmen den Suchraum. Ganzzahlige
> Parameter (z. B. eine Flossenzahl) behandelt das Verfahren als stetig; der Generator rundet, das
> Modell sieht dann Stufen, was vertretbar ist.

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `Bayes` | Abschnitt für den Bayes-Optimierer. Meist nicht nötig: `"Bayes"` läuft ohne jede Einstellung. In den Vorlagen steht er deshalb nur als Kommentar. | `{}` | 🟡 |
| `SurrogateTarget` | Was das Ersatzmodell lernt. `"Fitness"` = die Note als Ganzes. `"Metrics"` = **jeden Kennwert einzeln** (z. B. Widerstand, Volumen); die Bewertungsformel des Projekts rechnet daraus die Note. Das ist besser, wenn die Formel Sprünge hat (Strafen, Grenzen), weil die Kennwerte selbst glatt verlaufen. `"Auto"` nimmt `"Metrics"`, wenn die Bewertungsformel ihre Kennwerte nennt (`RequiredMetrics`), sonst `"Fitness"`. Liest die Formel in Wahrheit mehr Kennwerte, als sie nennt, wechselt der Lauf mit einer Warnung selbst auf `"Fitness"`. | `"Auto"` | 🟡 |
| `InitialSamples` | Wie viele Startrechnungen (Versuchsplan) vor dem ersten Modell. `0` = automatisch: das Größere von `VariantsPerIteration` und `2 × freie Parameter + 2`. | `0` | 🔴 |

**Was `"Bayes"` im Lauf tut:** Variante 1 der ersten Runde ist der Startwert, danach folgt der
Versuchsplan (gleichmäßig im Raum verteilt). Ab genügend Rechnungen lernt das Modell und schlägt je
Runde `VariantsPerIteration` neue Varianten vor. Bei **4 oder mehr Varianten je Runde** ist die letzte
ein **Erkunder**: Er sucht die größte Unsicherheit, aber nur unter Stellen, die nach Ansicht des Modells
noch gut sein könnten, als Absicherung gegen ein selbstsicheres, aber falsches Modell. Ist nirgends mehr
eine nennenswerte Verbesserung zu erwarten (das Optimum ist gefunden, oder der Beste ist ein Ausreißer
nach oben), **verfeinert** das Verfahren dort, wo die vorhergesagte Note am höchsten ist, statt in
Randgebieten zu raten. Bis zum 08.11.2026 verbrauchte es in dieser Lage ganze Runden an den Rändern des
Suchraums (Serverlauf: Anstellwinkel 15 bis 30°). Rechnungen, die abgestürzt sind oder vom Türsteher verworfen
wurden, lernt das Modell nicht, und es sucht in ihrer Umgebung nicht weiter. Stürzen Rechnungen ab
(Netz oder SU2), lernt ein zweites, kleines Modell, **wo** das passiert, und das Verfahren meidet diese
ganze Zone, nicht nur die Stelle des einzelnen Absturzes. Das hilft, wenn das beste Gebiet direkt an einer
Absturzzone liegt (z. B. kurz vor dem Strömungsabriss). Schwankt das Ergebnis einer Rechnung
(Wirbelablösung, siehe `Uncertainty_<Kennwert>`), zählt es im Modell entsprechend weniger stark.
**Jeder Vorschlag wird echt gerechnet**; das Modell entscheidet nur, welche Varianten gerechnet werden.

Die inneren Stellschrauben (Größe des Kandidatenpools, Deckel der Lernmenge, Vertrauensbereich ab 10
freien Parametern) sind feste Vorgaben und nicht einstellbar; im Test hatten andere Werte keinen klaren
Vorteil.

*Zurück zu `"Rsm"`:*

In der ersten Runde zieht `"Rsm"` die Varianten zufällig (außer Variante 1 = Startwerte), um
eine erste Landkarte zu bekommen. Bevor das Ersatzmodell greift, benötigt `"Rsm"` mindestens
`((k+1)(k+2))/2 + 2` **gelungene** Simulationen bei `k` Parametern (bei 5 Parametern also 23);
solange würfelt es gleichverteilt im erlaubten Bereich (Log: `Benötige noch N Basis-Simulationen (DoE)`).

*Nur für `"Evolution"` (stehen nicht in den Vorlagen; wer sie ändern will, trägt sie in die `simulation.json` des Projekts ein, sonst gelten die Vorgaben):*

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
| `BouncerTolerance` | Erlaubte Abweichung der Fitness bei der Nachprüfung (`0.2` = 20 %). Kleiner = strenger. Schwanken die Rechnungen von Natur aus (Wirbelablösung, `Uncertainty_<Kennwert>`), gilt mindestens die doppelte Schwankung beider Rechnungen; das Log nennt dann die erweiterte Grenze. | `0.2` | 🟡 |
| `BouncerJitter` | Um wie viel der eine Parameter verstellt wird, **in dessen Einheit** (nicht in Prozent!). Bei Parametern mit kleinen Zahlen (z. B. 0,02) ist `0.01` schon riesig. | `0.01` | 🔴 |

**Genauigkeit der Konvergenz (TODO-W3, TODO-W4)**

Ob eine Rechnung „fertig“ ist, entscheidet nicht mehr nur SU2 (Residuum, Cauchy-Kriterium), sondern
eine **Drift-Prüfung nach dem Lauf**: Im Endfenster (die letzten `CauchyElements` Zeilen, nie mehr als
die halbe Historie) werden die Mittelwerte der ersten und zweiten Fensterhälfte jedes Kennwerts
verglichen. Pendelt der Wert um einen festen Mittelwert (Wirbelablösung), ist die Drift ~0; wandert er
noch, ist sie deutlich. Als Ergebnis zählt das **Mittel des Endfensters**. Der **iterative Fehler**
soll nach Celik et al. (2008) mindestens eine Größenordnung unter dem Netzfehler liegen, den die
Kalibrierung misst: erlaubt ist ein Bruchteil der Netz-Toleranz (`MeshCalibration` → `Tolerance`).

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `IterativeToleranceFraction` | Erlaubter iterativer Fehler **normaler Varianten** als Bruchteil der Netz-Toleranz. `0.25` bei 2 % Toleranz = 0,5 % Drift im Endfenster (plus der Mindestbetrag aus `AbsoluteTolerances`, ebenfalls mit diesem Bruchteil). | `0.25` | 🎯⏱ | 🟡 |
| `ControlRuns` | **Strenges Profil nur für Kontrollrechnungen**: Netzkalibrierung (`Kal…`), Feinprüfung (`Fein…`) und Recheck. Normale Varianten und die Türsteher-Kontrolle (Variante 99) rechnen mit den normalen Werten, damit sie vergleichbar bleiben. Es gilt nie weniger Iterationen als bei normalen Varianten. | siehe unten | 🎯⏱ | 🟡 |

Innerhalb von `ControlRuns`:

| Einstellung | Bedeutung | Standard |
| --- | --- | --- |
| `Su2MaxIterations` | Höchstzahl der SU2-Iterationen je Kontrollrechnung. Sie dürfen Stunden dauern, es sind wenige. | `20000` |
| `CauchyElements` | Länge des Konvergenzfensters. | `500` |
| `CauchyTolerance` | SU2-eigene Schwelle (`CONV_CAUCHY_EPS`). Standard `"1e-5"`, damit stabile Läufe sauber abbrechen; bei Rest-Drift setzt `AutoRestartOnDrift` automatisch per Restart fort, und am Ende entscheidet die Drift-Prüfung. | `"1e-5"` |
| `IterativeToleranceFraction` | Erlaubter iterativer Fehler als Bruchteil der Netz-Toleranz (`0.1` = ein Zehntel). | `0.1` |

Im Log: `Kontrollrechnung: strenges Profil …`, bei nicht auskonvergierten Rechnungen
`[WARNUNG] SU2 nicht auskonvergiert … driftet im Endfenster um … %`. In der CSV steht je Kennwert
`Su2_IterErr_<Metrik>` (≤ 1 = in Ordnung). Eine Rechnung, die `ITER` erreicht, deren Kennwerte aber
stehen, gilt als konvergiert (SU2s Cauchy-Kriterium ist bei schwingenden Rechnungen oft zu streng).
Ist das Endfenster kürzer als 20 Zeilen, gilt die alte Regel („SU2 hat vor `ITER` aufgehört“).

**Netzkalibrierung (`MeshCalibration`, TODO-42)**

Ermittelt automatisch oder per Befehl den optimalen Netzverfeinerungsfaktor gegenüber einer feinen Referenzstufe.

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `MeshCalibration` | Abschnitt für die automatisierte Netzkalibrierung. | `{}` | 🟡 |
| `Mode` | Modus der Kalibrierung: `"Auto"` (Standard: Faktor wird am Laufanfang kalibriert; `MeshRefinementFactor` darf dann nicht gesetzt sein) oder `"Manual"` (`MeshRefinementFactor` gilt). In 2D dauert die Kalibrierung Sekunden bis Minuten, in 3D Stunden (MantaAuv ~4 h je Laufstart). | `"Auto"` | 🟡 |
| `Tolerance` | Erlaubte relative Abweichung aller Ergebnisgrößen (`ResultMetrics`) gegenüber der feinsten Referenzstufe (`0.05` = 5 %). Bei schwankenden Rechnungen (Wirbelablösung) gilt mindestens die doppelte Schwankung beider Stufen (`Uncertainty_<Kennwert>`), sonst entschiede Rauschen über die Netzfeinheit. | `0.05` | 🟡 |
| `Use2DPrecalibration` | Schnelle 2D-Vorkalibrierung vor der 3D-Bestätigung nutzen (TODO-48). | `false` | 🔴 |
| `AbsoluteTolerances` | **Fester Mindestbetrag** der zulässigen Abweichung je Kennwert (TODO-W1). Zulässig ist `|Δ| ≤ Tolerance · |Referenz| + Mindestbetrag`. Bei großen Werten entscheidet also die relative Abweichung, bei Werten nahe 0 (Auftrieb eines symmetrischen Profils bei 0°) der Mindestbetrag. Leer = Vorgaben: `Drag` 0,0001, `Lift` 0,005, `CSF` 0,005, `CMx`/`CMy`/`CMz` 0,001; unbekannte Kennwerte 0 (rein relativ). Einträge ergänzen/überschreiben je Name, z. B. `{ "Lift": 0.01 }`. Berichte zeigen zusätzlich die **normierte Abweichung** `|Δ| / (Tolerance·|Referenz| + Mindestbetrag)`: **höchstens 1 = in Ordnung**. | `{}` | 🔴 |
| `MaxCells2D` | Obergrenze der **gemessenen** Zellzahl einer Kalibrierstufe in 2D. Die Kalibrierung verfeinert weiter, bis die Toleranz hält oder diese Grenze erreicht ist (TODO-W5). Geprüft wird nach dem Vernetzen, bevor SU2 rechnet; eine Stufe darüber wird nicht gerechnet. Die Vorschau für die nächste Stufe nutzt den Zellzahl-Zuwachs der beiden letzten Stufen und lässt eine Stufe aus, die mehr als das 1,5-Fache der Grenze erwarten lässt. | `2000000` | 🔴 |
| `MaxCells3D` | Dasselbe für 3D (gemessene Zellzahl, geprüft vor SU2). Standard 3 Mio.; MantaAuv trägt 5 Mio. ein. Eine Stufe mit rund 1,7 Mio. Zellen rechnete im Servertest über 7 Stunden, mit 4 Mio. sind es grob 10 Stunden und mehr. Dazu zählt auch: der gewählte Faktor gilt danach für alle Varianten der Optimierung. | `3000000` | 🔴 |

**Gewinner fein nachrechnen (`ChampionVerification`, TODO-43)**

Überprüft den Sieger oder die besten Varianten auf einem feineren Netz (`wirksamer Faktor / √2^RefinementSteps`), um Netzabhängigkeit auszuschließen.

| Einstellung | Bedeutung | Standard | Wann |
| --- | --- | --- | --- |
| `ChampionVerification` | Abschnitt für die Verifikation der Gewinner auf feinem Netz. | `{}` | 🟡 |
| `Mode` | Verifikationsmodus: `"Off"` (aus), `"Final"` (prüft den Gesamtsieger am Ende), `"FinalTopN"` (Standard, prüft die Top-N am Ende) oder `"EachIteration"` (prüft den Sieger jeder Runde). | `"FinalTopN"` | 🟡 |
| `TopN` | Anzahl der besten Varianten am Ende bei `Mode` = `"FinalTopN"`. | `3` | 🟡 |
| `RefinementSteps` | Anzahl der Verfeinerungsstufen gegenüber dem wirksamen Faktor (÷ √2 je Stufe). | `1` | 🟡 |
| `Tolerance` | Relative Abweichung, ab der laut vor Netzabhängigkeit gewarnt wird (`0.05` = 5 %). Auch hier gilt bei schwankenden Rechnungen mindestens die doppelte Schwankung beider Werte. | `0.05` | 🟡 |

### 4.3 `solvers/su2.json` – die Strömungsrechnung

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `FreestreamVelocity` | **Anströmgeschwindigkeit in m/s.** Ist sie größer als 0, gilt sie; dann spielt `MachNumber` keine Rolle. `0` = alter Weg über `MachNumber` × `SpeedOfSound` (in Wasser unrealistisch). Standard `2.0` passt zum Standardfluid Meerwasser (übliche AUV-Geschwindigkeit). | `2.0` | 🎯 | 🟢 |
| `Density` | Dichte des Fluids in kg/m³. Meerwasser `1025`, Süßwasser ~`998`, Luft `1.225`. | `1025` | 🎯 | 🟢 |
| `DynamicViscosity` | Zähigkeit des Fluids in Pa·s. Meerwasser `0.001001`, Luft `1.81e-5`. | `0.001001` | 🎯 | 🟢 |
| `TurbulenceModel` | Rechenmodell für Turbulenz: `"SA"` (Spalart-Allmaras) oder `"SST"`. | `"SA"` | 🎯 | 🟡 |
| `SpeedOfSound` | Schallgeschwindigkeit in m/s, **nur** für den alten Weg über `MachNumber`. Der Standard ist der Wert für **Luft**; in Wasser ergäbe Mach 0,1 unrealistische 34 m/s. | `343.2` | 🎯 | 🔴 |
| `ReferenceAreaMetric` | Welche Fläche der Geometrie als Bezugsfläche für die Beiwerte (CD, CL) dient, z. B. `"FrontalArea"` (Stirnfläche). Fehlt die Fläche, warnt der Lauf laut, denn die Beiwerte wären dann um Größenordnungen falsch. | `"FrontalArea"` | 🎯 | 🔴 |
| `ReferenceLength` | Bezugslänge in m; wirkt nur auf die Momente (CMx/y/z). | `0.01` | 🎯 | 🔴 |
| `ResultMetrics` | Welche SU2-Kennwerte unter welchem Namen ankommen. Links der Name für die Bewertung, rechts die SU2-Spalte: `CD` Widerstand, `CL` Auftrieb, `CSF` Seitenkraft, `CMx`/`CMy`/`CMz` Momente. **Eintragen ergänzt nur**: `Drag` bleibt immer dabei. Fehlt eine eingetragene Spalte, gilt die Variante als fehlgeschlagen. | `{ "Drag": "CD" }` | 🎯 | 🟡 |
| `ConvergenceField` | An welchem Kennwert SU2 entscheidet, ob die Rechnung fertig ist: `"DRAG"` oder bei Auftriebsprojekten `"LIFT"`. | `"DRAG"` | 🎯⏱ | 🟡 |
| `HistoryOutput` | Welche Spaltengruppen SU2 mitschreibt. `AERO_COEFF` liefert alle Beiwerte; ohne sie findet `ResultMetrics` nichts. | `["ITER","RMS_RES","AERO_COEFF"]` | ⚙ | 🔴 |
| `OscillationWarnLimit` | Schwelle für die Schwingungswarnung: Schwingt ein Beiwert über das Cauchy-Fenster um mehr als diesen relativen Wert `(max − min) / |mean|` (z. B. `0.05` = 5 %), warnt der Solver vor instationärer Strömung (Wirbelablösung). Nur eine Warnung. | `0.05` | 🎯 | 🟡 |
| `TreatUnconvergedAsFailed` | Ob Rechnungen, deren Kennwerte im Endfenster noch **gesichert weiterwandern** (echte Drift), als fehlgeschlagen gewertet werden (`SimulationFailed = true`). Standard `true`: driftende Rechnungen gehen nicht in die Optimierung ein (`false` = Wert wird trotzdem übernommen). **Eine Rechnung, die nur schwingt (Wirbelablösung), ist davon nicht betroffen:** Sie gilt als „instationär, aber stabil" (`Su2_Unsteady = 1`), es zählt der Mittelwert des Endfensters, und sie ist kein Fehlschlag. | `true` | 🎯 | 🟡 |
| `AutoRestartOnDrift` | Wenn `true` (Standard), setzt ein stationärer SU2-Lauf nach frühem Cauchy-Stopp vor `Su2MaxIterations` automatisch per `RESTART_SOL= YES` und verschärftem `CONV_CAUCHY_EPS` fort, falls im Endfenster noch systematische Drift (keine stationäre Schwingung) vorliegt. | `true` | 🎯⏱ | 🟡 |
| `MaxConvergenceRestarts` | Höchstzahl automatischer stationärer Restart-Blöcke je Variante (`0` deaktiviert die automatische Fortsetzung). | `3` | ⏱ | 🟡 |
| `ResultAveraging` | Wie Beiwerte aus der `history.csv` übernommen werden: `"Last"` = Wert der letzten Iteration (früheres Verhalten); `"CauchyWindowMean"` = Mittelwert über das Cauchy-Fenster (`CauchyElements`), dämpft Schwingungen bei instationärer Strömung; `"TailMean"` = Mittelwert des Endfensters (die letzten `CauchyElements` Zeilen, nie mehr als die halbe Historie, also ohne Anlaufphase; Standard). **Kontrollrechnungen und instationär-stabile Rechnungen nutzen immer `"TailMean"`.** Unbekannter Wert → Warnung und `"Last"`. | `"TailMean"` | 🎯 | 🟡 |
| `CflNumber` | Schrittweite des SU2-Verfahrens beim Start. Größer = schneller, aber Gefahr, dass die Rechnung „explodiert". | `5.0` | ⏱ | 🔴 |
| `CflAdapt` | Ob SU2 die Schrittweite selbst anpasst. | `true` | ⏱ | 🔴 |
| `CflAdaptFactorDown` | Wie stark verkleinert wird, wenn es unruhig wird. | `0.5` | ⏱ | 🔴 |
| `CflAdaptFactorUp` | Wie stark vergrößert wird, wenn es gut läuft. | `1.2` | ⏱ | 🔴 |
| `CflAdaptMin` | Kleinste erlaubte Schrittweite. | `1.0` | ⏱ | 🔴 |
| `CflAdaptMax` | Größte erlaubte Schrittweite. | `50.0` | ⏱ | 🔴 |
| `TargetYPlus` | Zielwert für y+ an der Wand (Standard 1.5 in 3D, 1.0 in 2D). Bestimmt die Referenzhöhe für Warnungen. | `1.5` | 🎯 | 🟡 |
| `YPlusWarnFactor` | Warnfaktor für die y+-Prüfung: Liegt das 95 %-Quantil (`YPlus_P95`) über `TargetYPlus` × `YPlusWarnFactor`, warnt der Solver vor unzureichender Grenzschichtauflösung. | `3.0` | 🎯 | 🟡 |
| `AngleOfAttackDeg` | Anstellwinkel in Grad (0 = symmetrische Anströmung). Kann auch als aktiver Optimierungsparameter in `project.json` definiert werden. | `0.0` | 🎯 | 🟢 |
| `Dimensions` | Dimension der Rechnung: `2` für 2D-Rechnungen, `3` für 3D-Rechnungen (Standard). | `3` | ⚙ | 🟡 |
| `TimeDomain` | Block für zeitgenaue CFD-Rechnungen (URANS, Dual-Time-Stepping, TODO-37). Details siehe unten. | `{}` | 🎯⏱ | 🟡 |

**Zeitgenauer Modus (URANS / Dual-Time-Stepping, TODO-37)**

Für instationäre Strömungen (z. B. Wirbelablösung, periodische Schwingungen von Auftrieb und Widerstand) kann SU2 zeitgenau mit Dual-Time-Stepping 2. Ordnung betrieben werden. Gestartet wird aus dem stationären Feld; pro physikalischem Zeitschritt löst SU2 mehrere innere Pseudo-Iterationen (`InnerIterations`).

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `Enabled` | Zeitgenaue Rechnung (URANS) aktivieren (`TIME_DOMAIN= YES`, `TIME_MARCHING= DUAL_TIME_STEPPING-2ND_ORDER`). | `false` | 🎯⏱ | 🟡 |
| `TimeStep` | Physikalischer Zeitschritt $\Delta t$ in Sekunden. `0.0` = automatisch aus `StrouhalEstimate` und `StepsPerPeriod` berechnen. | `0.0` | 🎯⏱ | 🟡 |
| `StepsPerPeriod` | Zeitschritte pro Wirbelablöseperiode $T = L / (\text{St} \cdot U)$ bei automatischer Zeitschrittwahl. | `50` | 🎯⏱ | 🟡 |
| `StrouhalEstimate` | Geschätzte Strouhal-Zahl St für die Frequenzabschätzung (Standard 0.2 für stumpfe Körper / Nachläufe). | `0.2` | 🎯 | 🟡 |
| `TimeIterations` | Anzahl der physikalischen Zeitschritte, die gerechnet werden (bei Neustart aus der stationären Lösung zusätzlich zu `RestartIteration` gezählt; SU2s `TIME_ITER` ist ein absoluter Endindex). **1000 = 20 Perioden** bei 50 Schritten je Periode (bis TODO-W7: 200 = 8 Perioden, die Mittelung begann mitten im Einschwingen). | `1000` | ⏱ | 🟡 |
| `InnerIterations` | Anzahl der inneren Pseudo-Zeitschritte pro physikalischem Zeitschritt (`INNER_ITER`). | `30` | ⏱ | 🟡 |
| `AveragingStartFraction` | Bruchteil der Zeitschritte, ab dem die Zeitmittelung der Beiwerte startet (`WINDOW_START_ITER`). `0.5` bei 20 Perioden = Mittel über die **letzten 10 Perioden**. | `0.5` | 🎯 | 🟡 |
| `RestartIteration` | Neustart aus einer stationären Lösung (TODO-W7): `0` = Start aus dem Freistrom, `2` = Start aus `restart_00000.dat` und `restart_00001.dat` (SU2 braucht für die 2. Ordnung zwei Zeitstände). **Der Recheck setzt das selbst**: erst eine stationäre Rechnung auf dem feinen Netz, dann die zeitgenaue Rechnung daraus. | `0` | 🎯 | 🔴 |

**Wo die Strömung herkommt und hinfließt:** Das steht in keiner Einstellung. Alle sechs Seiten des
Windkanals sind ein gemeinsamer „Fernfeld"-Rand; die Strömung läuft immer in **+x-Richtung**
(von x-min nach x-max). Am Modell gilt Haftbedingung (das Fluid klebt an der Wand).

### 4.4 `solvers/gmsh.json` – das Rechennetz

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `TunnelShape` | Form des Windkanals: `"Box"` (Quader) oder `"Cylinder"` (Zylinder entlang x). Unbekannter Name → Warnung und `"Box"`. | `"Box"` | 🎯 | 🟡 |
| `TunnelSizeX` | Länge des Quaders in Strömungsrichtung (mm). Die Vorgabe passt zu einem Modell von etwa 50 mm (6 L davor, 14 L dahinter); ein größeres Modell braucht einen proportional größeren Kanal. | `1000` | 🎯⏱ | 🟡 |
| `TunnelSizeY` | Breite des Quaders (mm). | `300` | 🎯⏱ | 🟡 |
| `TunnelSizeZ` | Höhe des Quaders (mm). | `300` | 🎯⏱ | 🟡 |
| `TunnelCenterX` | Mittelpunkt des Kanals, x (mm). Das Modell sitzt im Nullpunkt; ein positiver Wert schiebt den Kanal nach hinten → mehr Platz für den Nachlauf (Empfehlung: ≥ 10 Modelllängen hinter dem Modell). | `200` | 🎯 | 🟡 |
| `TunnelCenterY` | Mittelpunkt, y (mm). | `0` | 🎯 | 🔴 |
| `TunnelCenterZ` | Mittelpunkt, z (mm). | `0` | 🎯 | 🔴 |
| `TunnelDiameter` | Durchmesser des Zylinders (mm), nur bei `"Cylinder"`. | `300` | 🎯⏱ | 🟡 |
| `TunnelLength` | Länge des Zylinders (mm), nur bei `"Cylinder"`. | `1000` | 🎯⏱ | 🟡 |
| `TunnelSegments` | Wie rund der Zylinder ist (Anzahl Kanten am Umfang). | `64` | ⚙ | 🔴 |
| `TunnelMinUpstreamFactor` | Mindestabstand stromaufwärts vor der Modellnase als Vielfaches der Modelllänge `L`. Ist der konfigurierte Kanal kürzer, wird das Einlass-Ende automatisch nach außen erweitert (nie verkleinert). | `5.0` | 🎯 | 🟡 |
| `TunnelMinDownstreamFactor` | Mindestabstand stromabwärts hinter dem Modellheck als Vielfaches der Modelllänge `L` (Nachlauf-Auslauf). | `15.0` | 🎯 | 🟡 |
| `TunnelMinLateralFactor` | Mindestabstand quer zur Strömung in `±Y` und `±Z` als Vielfaches der Modellbreite `B` bzw. -höhe `H`. | `5.0` | 🎯 | 🟡 |
| `TunnelMaxBlockagePercent` | Maximal erlaubter Versperrungsgrad $A_{\text{frontal}} / A_{\text{Kanal}}$ in Prozent. Wird er überschritten, weitet sich der Querschnitt automatisch auf (`<= 0` deaktiviert). | `0.3` | 🎯 | 🟡 |
| `BoundaryLayerSizeMin` | Kleinste Zellgröße nahe am Modell (mm). Kleiner = genauer, aber viel mehr Zellen. | `1.2` | 🎯⏱ | 🟡 |
| `BoundaryLayerSizeMax` | Größte Zellgröße weit weg vom Modell (mm). | `120` | 🎯⏱ | 🟡 |
| `BoundaryLayerDistMin` | Bis zu diesem Abstand vom Modell gilt die kleinste Zellgröße (mm). | `3.0` | 🎯⏱ | 🟡 |
| `BoundaryLayerDistMax` | Ab diesem Abstand gilt die größte Zellgröße; dazwischen wird übergeblendet (mm). | `40` | 🎯⏱ | 🟡 |
| `WakeRefinement` | Nachlauf-Verfeinerung an/aus. Bei `true` wird eine dynamische Nachlauf-Box hinter dem Modell eingefügt. | `true` | 🎯⏱ | 🟡 |
| `WakeLengthFactor` | Länge der Nachlauf-Box stromabwärts als Vielfaches der Modelllänge `lenX`. | `2.0` | 🎯⏱ | 🟡 |
| `WakeWidthFactor` | Halbe Überbreite der Nachlauf-Box in Y und Z als Vielfaches von `lenY` bzw. `lenZ`. | `0.5` | 🎯⏱ | 🟡 |
| `WakeCellSize` | Zellgröße in der Nachlauf-Box in mm auf Stufe 1.0; `0.0` = automatisch `lenX / 6.0`. | `0.0` | 🎯⏱ | 🟡 |
| `DetailBox` | **Detailbox** um und hinter dem Modell: eine feine Box, damit Nachlauf und Wirbel in Modellnähe stimmen (TODO-W6). Skaliert mit dem Netzfaktor, die Kalibrierung prüft sie also mit. Standard **an**; sie kostet in 3D viele Zellen (Manta: einige hunderttausend), `MaxCells3D` deckelt die Kalibrierung. | `true` | 🎯⏱ | 🟡 |
| `DetailBoxLengthFactor` | Länge der Detailbox hinter dem Modell als Vielfaches der Modelllänge `lenX`. | `1.5` | 🎯⏱ | 🟡 |
| `DetailBoxWidthFactor` | Überstand der Detailbox in Y als Vielfaches von `lenY` und Basis-Überstand in Z als Vielfaches von `lenZ`. | `0.25` | 🎯⏱ | 🟡 |
| `DetailBoxMinHeightLengthFactor` | Mindest-Z-Puffer der Detailbox als Anteil der Modelllänge `L` (`<= 0` deaktiviert die Mindestlänge), damit flache Körper nicht an einem zu kleinen `DetailBoxWidthFactor × lenZ` verhungern. | `0.15` | 🎯⏱ | 🟡 |
| `DetailBoxCellSize` | Zellgröße in der Detailbox in mm auf Stufe 1.0; `0.0` = automatisch 2,5 × Oberflächen-Zellgröße. | `0.0` | 🎯⏱ | 🟡 |
| `MeshSizeFromCurvature` | Krümmungsabhängige Verfeinerung aktivieren (`Mesh.MeshSizeFromCurvature`). | `true` | 🎯⏱ | 🟡 |
| `BoundaryLayerMode` | Modus der Grenzschicht: `"Prism"` (Vorgabe: flache Prismen-Schichten an der Wand, y+ ≈ 1,5, **Empfehlung für Voxel-Körper**), `"Threshold"` (unstrukturiert, **y+ ≈ 30–70**, für das SA-Modell zu grob, nur für schnelle Vorabläufe) oder `"Hexa"` (flache Quader-Schichten, nur für glatte Körper ohne scharfe Kanten). Erläuterung unten. | `"Prism"` | 🎯⏱ | 🟢 |
| `TargetYPlus` | Ziel-y+ für die erste Zellhöhe an der Wand (Standard 1.5). Daraus errechnet `BoundaryLayerCalculator` die erste Zellhöhe y1. | `1.5` | 🎯 | 🟡 |
| `BoundaryLayerMinLayers` | Mindestanzahl an Schichten in der Grenzschicht (Standard 4). | `4` | 🎯 | 🟡 |
| `BoundaryLayerGrowthRate` | Wachstumsfaktor von Schicht zu Schicht (Standard 1.28). | `1.28` | 🎯 | 🟡 |
| `BoundaryLayerLayers` | Feste Schichtanzahl; `0` = automatisch aus der Grenzschichtdicke delta berechnen. | `0` | 🎯 | 🟡 |
| `BoundaryLayerCoverage` | Faktor auf die abzudeckende Grenzschichtdicke: die Wandschicht wird so dick wie `Faktor × δ` (nur bei `BoundaryLayerLayers` = `0`). Die Literatur empfiehlt oft 1,2–1,5, damit die Grenzschicht ganz in der strukturierten Zone liegt. Mit 1.0 deckt die Schicht δ gerade ab; mehr Schichten kosten Zellen (Kal6: 29 statt 21 Schichten rund +23 % Zellen). | `1.0` | 🎯⏱ | 🟡 |
| `SurfaceCellSize` | Zellgröße auf der Bauteiloberfläche in mm auf Stufe 1.0; `0.0` = nutzt `BoundaryLayerSizeMin`. | `0.0` | 🎯⏱ | 🟡 |
| `CurvatureElements` | Anzahl Elemente pro 2π bei krümmungsabhängiger Netzverfeinerung (`Mesh.MeshSizeFromCurvature`). | `20` | 🎯⏱ | 🟡 |
| `CurvatureMinSizeFactor` | Untergrenze für die krümmungsabhängige Verfeinerung als Faktor auf die Oberflächen-Zellgröße (intern auf `[0.1, 1.0]` geklammert), verhindert Zell-Explosionen an feinen Voxel-Splittern. | `0.5` | 🎯⏱ | 🟡 |
| `ClassifyAngleDeg` | Winkelgrenze in Grad für Gmsh `ClassifySurfaces` bei der Neuvernetzung der Oberfläche. | `40.0` | ⚙ | 🔴 |

**Faustregeln für den Windkanal:** vor dem Modell ≥ 5 Modelllängen (`TunnelMinUpstreamFactor`),
dahinter ≥ 15 (`TunnelMinDownstreamFactor`), seitlich ≥ 5 B/H (`TunnelMinLateralFactor`) und
Versperrung ≤ 0,3 % (`TunnelMaxBlockagePercent`). Die Vorgabe (`TunnelSizeX = 1000`, `TunnelCenterX = 200`)
ergibt bei 50 mm Modell 6 L vor und 14 L hinter dem Modell; reicht eine konfigurierte Box nicht aus,
weitet `GmshCfdMesher` die Kanalgrenzen in realen mm automatisch nach außen auf (nie nach innen).

**Wandschicht: "Prism" oder "Hexa"?** (historischer Vergleich auf BIC_12 vom 06.11.2026 bei `TargetYPlus = 1.0` / `GrowthRate = 1.2`;
aktueller 3D-Standard ist `TargetYPlus = 1.5` mit `BoundaryLayerGrowthRate = 1.28`, also ca. 14–16 Schichten,
sowie `MeshSizeFromCurvature = true`, `CurvatureElements = 20` und Untergrenze `CurvatureMinSizeFactor = 0.5`)

Beide Modi legen direkt an die Wand flache, längliche Zellen in Schichten (erste Zellhöhe aus
`TargetYPlus`, jede weitere um `BoundaryLayerGrowthRate` dicker). Weiter außen füllen Tetraeder
den Kanal. Das Netz entsteht in zwei Gmsh-Schritten: `Wall_<Variante>.geo` vernetzt die
Oberfläche neu, `Meshing_<Variante>.geo` baut darauf die Schichten und das Volumen.

| | `"Prism"` (Empfehlung) | `"Hexa"` |
| --- | --- | --- |
| Zellform an der Wand | Prismen: Grundfläche Dreieck | Quader: Grundfläche Viereck |
| Übergang zu den Tetraedern | direkt, ohne Zwischenzellen | über Pyramiden |
| MantaAuv (scharfe Voxel-Kanten) | historisch (06.11.2026): 21 Schichten bei `y+ ~ 1,0` / `1,2` (aktuell 3D-Standard `y+ ~ 1,5` / `1,28` ≈ 14–16 Schichten), praktisch keine ungültigen Zellen, SU2 konvergiert, y+ Median 1,1 / P95 2,3 | höchstens 12 Schichten möglich, ~5 % der Pyramiden an den Flügelkanten gekippt, **SU2 divergiert** |
| Glatter Körper (Kugel) | geht | geht, SU2 konvergiert |

Für die Wandauflösung (y+) sind beide gleich gut, denn sie hängt an der Höhe der ersten Zelle,
nicht an ihrer Grundfläche. Quader haben auf glatten Flächen etwas weniger numerische Diffusion.

**Wenn Gmsh die Wandschicht nicht vernetzen kann** (seit 10.11.2026): Die Schichtzahl, das Wachstum und die erste
Zellhöhe stehen fest und ändern sich nie. Früher ließ das Programm äußere Schichten weg (21 → 16 → 12 …) und verfälschte damit
die Netzstudie, weil Varianten und Kalibrierstufen mit verschiedenen Wandnetzen rechneten. Heute ändert sich bei einem Fehler
nur die **Extrusionsrichtung und Dicke** der Schicht, in Stufen:

| Stufe | Was geschieht | Wirkung auf den Vergleich |
| --- | --- | --- |
| A (Standard) | Die Richtungen, in die die Prismen wachsen, werden über die Nachbarknoten geglättet (höchstens 30° von der Flächennormalen). Volle Dicke überall. | keine |
| B | Zusätzlich wird die Dicke nur dort verringert, wo sich die Schicht faltet (Kehlen) oder zu wenig Platz ist (Spalt zwischen zwei Körperteilen). Meist unter 2 % der Wandfläche. | gering |
| C (Ersatznetz) | Letzter Ausweg: die Schicht wird überall halbiert bzw. geviertelt. Die Variante wird trotzdem gerechnet, aber gekennzeichnet (`MeshFallback` = `1`) und am Laufende aufgelistet („Formen mit Ersatznetz“). Für die Optimierung (Bayes) zählt sie nicht als Lerndaten. | deutlich: nur eingeschränkt vergleichbar |

Ursache des früheren Problems (MantaAuv, 09.11.2026): In Kehlen mit Radius R laufen die Normalen zusammen, und die Schicht faltet sich ab einer Dicke von etwa R. Die Sensorkugel am Flügel hat dort einen Verrundungsradius von rund 2,4 mm, die Wandschicht war 2,2 mm dick. Feine Oberflächennetze bilden die Kehle schärfer ab, deshalb scheiterte es erst ab Netzfaktor 0,71 und feiner.

Scheitert auch das Ersatznetz, gilt die Variante als „nicht vernetzbar“ (`MeshFailed` = `1`). Das ist ein **Vernetzungsfehler und kein Strömungsabsturz**: Der Bayes-Optimierer schreckt davon nur die enge Umgebung ab, nicht die ganze Formzone. Die erste Gmsh-Fehlerzeile steht im Log.

Je Rechnung stehen die Netzkennwerte in der Ergebnis-CSV (`Simulation_Results.csv`):
`BoundaryLayerLayers` (Schichtzahl), `BoundaryLayerThicknessMm` (Solldicke), `BoundaryLayerFirstCellMm` (erste Zellhöhe),
`BoundaryLayerStage` (1 = A, 2 = B, 3 = Ersatznetz), `BoundaryLayerMaxTiltDeg` (größte Kippung der Richtung),
`BoundaryLayerMinScale` (kleinster Dickenfaktor, 1 = nirgends verringert), `BoundaryLayerThinnedShare` (Flächenanteil unter 90 % der Dicke),
`MeshFallback`, `MeshFailed` und `CellLimitExceeded`, dazu Zellklassen (`MeshCells_Tri`, `MeshCells_Quad`, `MeshCells_Prism`, `MeshCells_Hexa`, `MeshCells_Tet`, `MeshCells_Pyramid`) sowie Kanalmaße (`TunnelBlockagePercent`, `TunnelLengthMm`, `TunnelWidthMm`, `TunnelHeightMm`, `TunnelUpstreamL`, `TunnelDownstreamL`). Der Kalibrierbericht (`calibration.csv`) hat dazu die Spalten `Schichten`, `SchichtDicke_mm`, `Wandstufe`, `Tri;Quad;Prism;Hexa;Tet;Pyramiden`, `Blockage_%`, `CD_p` und `CD_v`
und warnt, wenn die Stufen unterschiedliche Schichtzahlen haben.

**Zeit der Kalibrierung:** Das Log nennt nach jeder Stufe deren Dauer („⏱ Dauer Stufe 5 (Faktor 0.50): 3 h 10 min“) und am Ende die Gesamtdauer der Kalibrierung.
Dauert sie im Verhältnis zur Optimierung zu lange, lohnt sich eine gröbere `Tolerance` oder eine kleinere `MaxCells3D`.

`"Threshold"` behält das rein unstrukturierte Netz früherer Versionen bei.

### 4.5 `solvers/gmsh.json` (2D-Projekte, `Gmsh2DMesherOptions`)

Für zweidimensionale CFD-Simulationen (z. B. Profilschnitte wie `Naca2D` oder die 2D-Vorkalibrierung für 3D-Projekte, TODO-45/48) erzeugt `Gmsh2DMesher` ein ebenes Rechennetz um die Kontur. Die Bezugslänge ist die Sehne $L$.

| Einstellung | Bedeutung | Standard | Wirkung | Wann |
| --- | --- | --- | --- | --- |
| `FarfieldRadiusFactor` | Kreisförmiges Fernfeld als Vielfaches der Sehnenlänge $L$. **500** wie die NASA-TMR-Gitter (in 2D klingt die Zirkulation nur mit $1/r$ ab, ein kleines Fernfeld verfälscht den Auftrieb); die Zellen wachsen nach außen, das große Fernfeld kostet deshalb nur wenige tausend Zellen. Bis TODO-W6 war die Vorgabe 50. | `500.0` | 🎯 | 🔴 |
| `FarfieldCircleSegments` | Anzahl der Segmente für die Fernfeld-Kreisbögen (Standard 64). | `64` | ⚙ | 🔴 |
| `TargetYPlus` | Ziel-y+ für die erste Wandzellhöhe $y_1$ (Standard 1.0). | `1.0` | 🎯 | 🟡 |
| `BoundaryLayerGrowthRate` | Geometrischer Wachstumsfaktor der Schichten in der Grenzschicht (Standard 1.2). | `1.2` | 🎯 | 🟡 |
| `BoundaryLayerMinLayers` | Mindestanzahl an Wandschichten (Standard 4). | `4` | 🎯 | 🟡 |
| `BoundaryLayerLayers` | Feste Schichtanzahl (`0` = automatisch aus Grenzschichtdicke $\delta$ ermitteln). | `0` | 🎯 | 🟡 |
| `SurfaceCellSizeFactor` | Zellgröße an der Konturoberfläche als Bruchteil von $L$ auf Stufe 1.0 (`0.01` = $L/100$). | `0.01` | 🎯⏱ | 🟢 |
| `SurfaceCellSizeMm` | Feste Kontur-Zellgröße in mm auf Stufe 1.0; $> 0$ überschreibt `SurfaceCellSizeFactor` × $L$. | `0.0` | 🎯⏱ | 🟡 |
| `NearfieldCellSizeFactor` | Nahfeld-Zellgröße als Bruchteil von $L$ auf Stufe 1.0 (`0.04` = $L/25$). | `0.04` | 🎯⏱ | 🟡 |
| `FarfieldCellSizeFactor` | Zellgröße am Fernfeldrand als Vielfaches von $L$ auf Stufe 1.0. Faustregel: Radius / 20. | `25.0` | 🎯⏱ | 🔴 |
| `NearfieldGrowthDistanceFactor` | Abstand (als Vielfaches von $L$), über den die Zellgröße vom Nahfeld auf die Fernfeldgröße wächst. `0` = automatisch 0,4 × `FarfieldRadiusFactor`. Ein langer, gleichmäßiger Übergang vermeidet verzerrte Zellen. | `0.0` | 🎯⏱ | 🔴 |
| `WakeRefinement` | Nachlauf-Verfeinerungsbox stromabwärts aktivieren/deaktivieren (Standard `true`). | `true` | 🎯⏱ | 🟡 |
| `WakeLengthFactor` | Länge der Nachlauf-Box hinter der Hinterkante als Vielfaches von $L$ (Standard 3.0). | `3.0` | 🎯⏱ | 🟡 |
| `WakeWidthFactor` | Halbe Überbreite der Nachlauf-Box in Y als Vielfaches von $L$ (Standard 0.6). | `0.6` | 🎯⏱ | 🟡 |
| `WakeCellSizeFactor` | Zellgröße in der Nachlauf-Box als Bruchteil von $L$ auf Stufe 1.0 (`0.04` = $L/25$). | `0.04` | 🎯⏱ | 🟡 |
| `DetailBox` | **Detailbox** um und hinter dem Profil: sehr feine Box, damit Nachlauf und Wirbel in Modellnähe stimmen. Gilt für jede Rechnung (keine Sonderbehandlung des Anstellwinkels), skaliert mit dem Netzfaktor, die Kalibrierung prüft sie also mit. Standard an (TODO-W6). | `true` | 🎯⏱ | 🟡 |
| `DetailBoxLengthFactor` | Länge der Detailbox hinter der Hinterkante als Vielfaches von $L$. | `1.0` | 🎯⏱ | 🟡 |
| `DetailBoxWidthFactor` | Überstand der Detailbox oberhalb und unterhalb des Profils als Vielfaches von $L$. | `0.25` | 🎯⏱ | 🟡 |
| `DetailBoxCellSizeFactor` | Zellgröße in der Detailbox als Bruchteil von $L$ auf Stufe 1.0 (`0.01` = $L/100$). | `0.01` | 🎯⏱ | 🟡 |
| `EdgeRefinementFactor` | Wandzellgröße an **Vorder- und Hinterkante** als Bruchteil der Konturzellgröße (`0.25` = ein Viertel). Saugspitze und Hinterkante bestimmen Auftrieb und Widerstand am stärksten (NASA-TMR). `1` = keine Sonderverfeinerung. | `0.25` | 🎯⏱ | 🟡 |
| `EdgeRefinementDistanceFactor` | Abstand um Vorder- und Hinterkante (als Vielfaches von $L$), über den die Verfeinerung ausläuft. | `0.1` | 🎯⏱ | 🔴 |

---

## 5. Welche Werte galten wirklich?

Nach einem Lauf lässt sich immer nachvollziehen, womit gerechnet wurde, auch wenn die Dateien
inzwischen geändert sind:

| Wo | Was steht dort |
| --- | --- |
| **Kopf des Logs** (`simulation.log`) | Projekt, Code-Stand, und für jede Datei, welche Einstellungen *aus ihr* kamen (`[CONFIG] Herkunft der wirksamen Werte`). Alles Ungenannte war der Standardwert. Darunter die Anströmung (`Anströmung: 2 m/s (…)`) |
| **`Ergebnisse/<Projekt>/effective-config.json`** | Alle wirksamen Werte des Laufs, `WirksamerNetzfaktor`, `NetzfaktorQuelle`, `KalibrierBericht`, je Einstellung mit der Datei, aus der sie kam, plus jede `*.local.json`-Überschreibung (`MaschinenspezifischeUeberschreibungen`). **Das ist die Akte eines Laufs.** `fetch` holt sie immer mit |
| **`Ergebnisse/<Projekt>/run-info.json`** | Der **Fahrtenschreiber** des Laufs: Code-Stand (`Commit`, `Branch`, `DeployHash`, `DeployZeit`, `UncommittedChanges`), Programmversionen (SU2, Gmsh, mpirun, PicoGK), `ZufallsSeed`, Rechner, Aufruf, `Status` (`laeuft`/`abgeschlossen`/`abgebrochen`) und jede Wiederaufnahme. Steht nach einem Server-Neustart noch `laeuft` darin, wurde der Lauf hart unterbrochen. `fetch` holt sie immer mit |
| **`Ergebnisse/<Projekt>/Quellstand/`** | Kopie des Projektcodes (`*.cs` und `*.json` aus `src/projects/<Projekt>/`) zum Start des Laufs. Damit ist auch Monate später klar, mit welcher Geometrie und welcher Bewertungsformel gerechnet wurde. `fetch` holt sie immer mit |
| **`Ergebnisse/<Projekt>/Wiederaufnahme/`** | Lauf-Journal `journal.jsonl` für `resume` (eine Zeile je fertiger Rechnung). Ältere Journale heißen `journal_<Zeitstempel>.jsonl` |
| **`Ergebnisse/<Projekt>/Simulation_Results.csv`** | Je Variante eine Zeile (semikolongetrennt, öffnet direkt in Excel): Iteration, Variante (`Variant 99` = Kontrollrechnung des Bouncers), alle aktiven Parameter, alle passiven Kennwerte (Geometrie und SU2), `ScaleFactor`, Fitness, Dateipfade (`StlPath`, `MeshPath`), `SimulationFailed` (1 = Rechnung abgebrochen), `Disqualified` (1 = vom Türsteher als instabil verworfen), `MeshRefinementFactor`, `FineVerified` (1 = verifiziert), `FineDeviation` (relative Abweichung zur feinen Rechnung), `Su2_Iterations` (tatsächlich gerechnete Schritte), `Su2_Converged` (1 = konvergiert, 0 = nicht konvergiert), `Su2_Osc_<Metrik>` (relative Schwingung über das Cauchy-Fenster), `Su2_IterErr_<Metrik>`, `Su2_Unsteady` (1 = instationär, aber stabil: der Wert pendelt ohne Trend, es zählt der Mittelwert des Endfensters), `Uncertainty_<Metrik>` (relative Unsicherheit des Werts, 1σ, aus Blockmitteln des Endfensters: nahe 0 bei ruhiger Rechnung, z. B. 0,05 = ±5 % bei Wirbelablösung), `MeshCells` (Zellzahl aus dem `.su2`-Kopf), `MeshCells_Tri`/`Quad`/`Prism`/`Hexa`/`Tet`/`Pyramid` (Zellklassen), `BoundaryLayerHexShare` (Quader-Anteil in der Grenzschicht), `TunnelBlockagePercent`, `TunnelLengthMm`, `TunnelWidthMm`, `TunnelHeightMm`, `TunnelUpstreamL`, `TunnelDownstreamL` (reale Kanalmaße und Blockage) sowie `YPlus_Median`, `YPlus_P95`, `YPlus_Max` (y+-Wandauflösung) und `Su2_CD_p`, `Su2_CD_v` (Druck- und Reibungsanteil des Widerstands). Bei `"Bayes"` kommen `Bayes_PredictedFitness`, `Bayes_PredictedLow`, `Bayes_PredictedHigh` (vorhergesagte Fitness samt 95-%-Band) und `Bayes_Explorer` (1 = Erkunder) hinzu; Zeilen des Versuchsplans zeigen dort `0`, weil es für sie keine Vorhersage gibt. **Hinweis:** Während einer laufenden Iteration steht die Spalte `Fitness` zunächst auf `0`, bis die gesamte Runde samt Bouncer-Nachprüfung abgeschlossen ist. Die beste Variante des Gesamtlaufs steht außerdem am Ende von `simulation.log` unter `ABSOLUTER CHAMPION`. |
| **`Ergebnisse/<Projekt>/Analyseergebnisse/Bayes_Verlauf.csv`** | Nur bei `"Bayes"`: eine Zeile je Runde, nach jeder Runde neu geschrieben. Spalten: `Iteration`, `Modus` (`DoE` = noch Versuchsplan, sonst `Fitness` oder `Metrics`), `Lernpunkte` (aus wie vielen Rechnungen das Modell lernte), `Vertrauensbereich_L` (Größe des Suchwürfels, leer = aus), `Abweichung_Mittel` (mittlere relative Abweichung zwischen Vorhersage und Ergebnis der Runde), `Anteil_im_Band` (Anteil der Varianten, die im 95-%-Band lagen), `BesteFitness` und `Relevanz_<Parameter>` (1 = wichtigster Parameter, nahe 0 = hat kaum Einfluss). **Lesebeispiel:** `Abweichung_Mittel` 0.06 und `Anteil_im_Band` 0.9 heißt: Das Modell lag im Mittel 6 % daneben und hat 9 von 10 Varianten richtig eingeschätzt, es taugt. Liegt `Anteil_im_Band` dauerhaft unter 0.5, ist das Modell unsicher oder die Ergebnisse rauschen stark; dann hilft ein Vergleichslauf mit `"Rsm"`. Die letzte Runde eines Laufs steht nicht darin, weil danach nichts mehr vorbereitet wird. Dieselben Angaben stehen als `[BAYES]`-Zeilen im Log. |
| **`Ergebnisse/<Projekt>/Verifikation.csv`** | Vergleichstabelle der feinen Nachrechnung (TODO-43): Iteration, Variante, normaler Rang, feiner Rang, normale/feine Fitness, maximale Kennwertabweichung, Rangstabilität (`RankingHolds`) |
| **`Ergebnisse/<Projekt>/Kalibrierung/`** | Kalibrierberichte der automatischen oder manuellen Netzkalibrierung (TODO-42): `calibration.csv`, `calibration.json` sowie `latest_calibration.*` (inkl. Zellklassen `Tri;Quad;Prism;Hexa;Tet;Pyramiden`, `Blockage_%`, `CD_p` und `CD_v`) |
| **`Ergebnisse/<Projekt>/Analyseergebnisse/Verlaeufe/`** | Vollständiger Verlauf jeder gerechneten Variante: `History_Gen{i}_Var{v}.csv` (SU2-Iterationen mit allen Beiwerten und Residuen) und `Config_Gen{i}_Var{v}.cfg` (wirksame SU2-Steuerdatei). Im Abbruchfall liegen beide in `FehlerLogs/` |
| **`.\scripts\sim.ps1 doctor -Project <Name>`** | *Vor* einem Lauf: welche Programmpfade wirklich gelten und ob die Programme gefunden werden |

---

## 6. Die Befehle

Alles wird von Windows aus mit `.\scripts\sim.ps1 <befehl> -Project <Name>` gesteuert. Die
Einzelheiten stehen in [`scripts/README.md`](../../scripts/README.md).

| Befehl | Was er tut |
| --- | --- |
| `run` | Der Alltagsbefehl: Code hochladen, bauen, Lauf starten. Danach kann das Fenster zu. |
| `resume` | Einen abgebrochenen Lauf fortsetzen (nach Server-Neustart, Stromausfall, Absturz oder `stop`). Lädt nichts hoch und baut nicht. Siehe [6.1](#61-abgebrochenen-lauf-fortsetzen). |
| `calibrate` | Netzkalibrierung starten (TODO-42): ermittelt den optimalen `MeshRefinementFactor` für die Startgeometrie und gibt die empfohlene Zeile aus. |
| `recheck` | Einzelne Variante (`-Variant Gen3_Var2`) auf feinerem Netz (`-Finer 1`) re-simulieren und vergleichen (TODO-43). Mit `-Unsteady` (TODO-W7): erst stationär auf dem feinen Netz, dann zeitgenau (URANS, 20 Perioden) **aus dieser Lösung**; der Bericht stellt stationär und zeitgemittelt gegenüber. Ein großer Unterschied heißt: die Strömung ist instationär. |
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

### 6.1 Abgebrochenen Lauf fortsetzen

Die tmux-Sitzung schützt einen Lauf nur davor, dass die Konsole zugeht oder die Verbindung
abreißt. **Ein Neustart des Servers, ein Stromausfall oder ein Absturz beendet ihn trotzdem.**
Deshalb schreibt jeder Lauf ein Journal mit: nach jeder fertigen Rechnung eine Zeile.

**Was tun nach einem Server-Neustart?**

1. `.\scripts\sim.ps1 status -Project <Name>` meldet
   `Abgebrochener Lauf gefunden: 37 Rechnungen gesichert. Fortsetzen mit: …`.
2. `.\scripts\sim.ps1 resume -Project <Name>` setzt den Lauf fort.
3. Im Log stehen zuerst Zeilen wie `[WIEDERAUFNAHME] Gen3_Var4 aus dem Journal übernommen (37/52)`.
   Das geht in Sekunden (`DurationSeconds` aus dem Originallauf bleibt dabei erhalten). Danach wird ganz normal weitergerechnet.

**Was geht verloren?** Höchstens die eine Variante, die beim Abbruch gerade gerechnet wurde.
Sie wird neu gerechnet. Am Ende sind CSV und Ergebnis genau so, als wäre der Lauf nie
unterbrochen worden.

**Warum klappt das?** Der Lauf läuft mit demselben Zufalls-Seed noch einmal von vorn. Der
Optimierer schlägt deshalb dieselben Varianten vor, und deren Ergebnisse stehen schon im
Journal. Erst wo das Journal endet, wird wieder gerechnet.

**Warum braucht geänderter Code oder eine geänderte Einstellung einen neuen Lauf?**

* Hat sich eine Einstellung geändert (z. B. Netz oder Anströmung in `simulation.json` oder
  `solvers/su2.json`), wären die alten und neuen Rechnungen nicht vergleichbar. `resume`
  bricht dann ab und nennt die Datei. Entweder die Änderung zurücknehmen oder mit `run` neu
  starten. Ausgenommen ist `RandomSeed`: beim Fortsetzen gilt immer der Seed aus dem Journal.
* `resume` lädt keinen Code hoch. Liegt lokal neuerer Code, gibt es nur eine Warnung, und es
  wird mit dem Stand auf dem Server weitergerechnet. Neuer Code heißt neuer Lauf: `run`.
* Ein neues `run` beginnt immer frisch. Das alte Journal wird dabei nicht gelöscht, sondern
  als `journal_<Zeitstempel>.jsonl` aufgehoben.

### 6.2 Lokale Befehle (`dotnet`) und direkte Server-Aufrufe

Für Trockentests der Konfiguration (läuft auf Windows ohne installierte Solver), Unit-Tests oder lokale Simulationsläufe kann das Programm direkt über `dotnet` aufgerufen werden:

| Aufgabe | Befehl |
| --- | --- |
| **Alle Unit-Tests ausführen** (ohne Solver lauffähig) | `dotnet test OptimierungsAlgoCFD.sln` |
| **Vorhandene Projekte auflisten** | `dotnet run --project src/OptimierungsAlgoCFD -- --list-projects` |
| **Konfiguration & Pfade prüfen (Dry-Run)** | `dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv --print-config` |
| **Simulation lokal starten** | `dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv` |
| **Lokale Netzkalibrierung** | `dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv --calibrate` |
| **Lokaler Recheck (stationär / URANS)** | `dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv --recheck=Gen3_Var2 --finer=1 [--unsteady]` |
| **Lokalen Lauf fortsetzen** | `dotnet run --project src/OptimierungsAlgoCFD -- MantaAuv --resume` |
| **Direkt auf dem Linux-Server per SSH** | `SIM_PROJECT=MantaAuv bash scripts/sim-runner.sh start` |

> **Kein stiller Standard beim direkten Aufruf:** Ohne Projektnamen bricht `dotnet run` (ebenso wie `sim-runner.sh`) sofort ab und listet die verfügbaren Projekte auf, damit ein Tippfehler niemals stundenlang das falsche Projekt rechnet. Optional kann als zweites Argument ein abweichender Pfad zu `src/projects` übergeben oder `SIM_PROJECTS_DIR` gesetzt werden.

---

## 7. Häufige Meldungen und was zu tun ist

| Meldung / Symptom | Bedeutung | Was tun |
| --- | --- | --- |
| `'…' nennt einen Schlüssel, den der Typ … nicht kennt` | Tippfehler in einer JSON-Datei. | Die Meldung nennt den vermutlich gemeinten Namen bzw. alle gültigen. Korrigieren. |
| `[WARNUNG] Eine maschinenspezifische Datei ueberschreibt dieses Projekt` | Eine `*.local.json` verändert still Werte. | Gehören die Werte zum Projekt: in die Projektdatei übernehmen und die local-Datei löschen. |
| `Kein -Project angegeben, es gilt die Vorgabe 'MantaAuv'` | Der Befehl wirkt auf MantaAuv. | Gilt bei `run`/`deploy`/`doctor`. Bei `fetch` und `status` fragt das Skript automatisch das letzte Projekt auf dem Server ab. |
| `FEHLER: Simulation_Results.csv fuer Projekt '…' fehlt nach dem Entpacken` | Für dieses Projekt liegen noch keine Ergebnisse auf dem Server vor. | Die Meldung listet die vorhandenen Projekte auf dem Server auf. Projektname prüfen oder Lauf erst abwarten. |
| `[WARNUNG] Das geholte Log gehoert laut Kopfzeile zu Projekt '…'` | `simulation.log` stammt von einem anderen Projekt als die geholten Ergebnisse. | Das gewünschte Projekt mit `-Project` angeben oder einen neuen Lauf starten. |
| `[WARNUNG] … Referenzfläche …` / `Die Metrik '…' fehlt` | Die Bezugsfläche (`ReferenceAreaMetric`) gibt es nicht. CD/CL sind dann falsch. | Den Namen in `su2.json` an die Metrik anpassen, die die Geometrie meldet (die Meldung listet sie auf). |
| `[WARNUNG] Ergebnisgröße '…' <- Spalte '…'` | Ein Eintrag in `ResultMetrics` passt zu keiner SU2-Spalte. Die Variante gilt als fehlgeschlagen. | Spaltennamen korrigieren (die Meldung listet die vorhandenen). |
| `[WARNUNG] SU2 hat ITER=… erreicht, ohne dass das Kriterium erfüllt war – … ist der Stand bei Iteration …, kein konvergierter Wert.` | Die Strömungsrechnung hat die maximale Iterationszahl erreicht, bevor der Wert stabil war. Der Wert ist eine Momentaufnahme und noch nicht verlässlich. | `Su2MaxIterations` in `simulation.json` erhöhen (z. B. 1000) oder prüfen, ob die Strömung schwingt. Soll der Lauf unvollständige Rechnungen verwerfen: `TreatUnconvergedAsFailed` in `su2.json` auf `true` setzen. |
| `[HINWEIS] Instationär, aber statistisch stabil (kein gesicherter Trend im Endfenster): es zählt der Mittelwert des Endfensters (Drag ±… %).` | Der Wert pendelt um einen festen Mittelwert, typisch für Wirbelablösung an stumpfen Körpern. Die Rechnung ist gültig, ihr Ergebnis aber unsicherer (Spalte `Uncertainty_<Kennwert>`). | Nichts. Für genaue Werte des Champions: `recheck -Unsteady` (zeitgenaue Rechnung). Tritt es fast überall auf und soll die Strömung stationär sein: Netz und Geometrie prüfen. |
| `[WARNUNG] Start-Voxelgröße … ist grob …` / `[INFO] AutoVoxelResolution: …` | Die Start-Voxelgröße (`BaseVoxelResolution`) ist im Realraum gröber als 80 % der Oberflächen-Zielzellgröße. Bei `AutoVoxelResolution = true` verfeinert das Programm sie beim Start automatisch (bis minimal `0.1` mm). | Bei `AutoVoxelResolution = false` entweder `BaseVoxelResolution` senken oder `AutoVoxelResolution = true` setzen, falls die feinere Voxelauflösung gewünscht ist. |
| `[SU2] Drift nach … -> automatischer Restart …` | SU2 hat per Cauchy-Kriterium vor `Su2MaxIterations` gestoppt, aber im Endfenster lag noch systematische Drift vor. `AutoRestartOnDrift` setzt die Rechnung automatisch per `RESTART_SOL= YES` fort. | Nichts; ist die Drift nach `MaxConvergenceRestarts` behoben, zählt das Ergebnis regulär. |
| `[WARNUNG] … schwingt um ±… % – Hinweis auf instationäre Strömung (Wirbelablösung).` | Der Beiwert schwankt im Cauchy-Fenster stärker als `OscillationWarnLimit` (z. B. ±2 %). Meist lösen sich periodisch Wirbel ab (z. B. Kármánsche Wirbelstraße bei stumpfen Körpern). | Prüfen, ob die Geometrie zu stumpf ist, oder `ResultAveraging` in `su2.json` auf `"CauchyWindowMean"` setzen, um über die Schwingung zu mitteln. Schwellenwert mit `OscillationWarnLimit` anpassen. |
| `[WARNUNG] y+ (95 %-Quantil = …, Max = …) überschreitet das Limit … (YPlusWarnFactor × TargetYPlus) – Grenzschichtauflösung prüfen.` | Der dimensionslose Wandabstand y+ ist an der Bauteilwand zu groß (das 95 %-Quantil P95 übersteigt das Limit). Das Turbulenzmodell (SA/SST) löst die viskose Unterschicht nicht optimal auf, wodurch der Reibungswiderstand ungenau werden kann. | `BoundaryLayerMode` ("Prism") aktivieren, `TargetYPlus` prüfen, `BoundaryLayerSizeMin` bzw. `SurfaceCellSize` verfeinern oder `YPlusWarnFactor` erhöhen, falls ein größerer Wandabstand für die Grobauslegung ausreicht. |
| `CFD-Simulation für … abgebrochen` | SU2 ist gescheitert. | Details in `Ergebnisse/<Projekt>/FehlerLogs/`. Häufig: Netz kaputt (dünne Stellen), Schrittweite zu groß (`CflNumber`). |
| `Gmsh konnte … nicht erstellen` | Das Netz ließ sich nicht bauen. | Meist reißt die Geometrie an dünnen Stellen ab: `BaseVoxelResolution` verkleinern oder Mindestdicken im Geometrie-Code erhöhen. |
| `Nach der ersten gerechneten Variante fehlt/fehlen die Metrik(en) …` | Die Bewertungsformel braucht einen Wert, den niemand liefert. | Fehlt ein SU2-Wert: in `ResultMetrics` eintragen. Fehlt ein Geometriewert: im Geometrie-Code melden. |
| `⚠ HINWEIS: Das Optimum liegt an der Grenze des Suchraums` (am Ende des Laufs, gelb) | Der Gewinner liegt bei einem oder mehreren Parametern höchstens 1 % der Spanne von einer Grenze in `ParameterBounds` entfernt. Dann bestimmt die Grenze das Ergebnis, und ein besserer Wert liegt möglicherweise dahinter. Das Framework erweitert Grenzen **nie** von selbst. | Prüfen, ob die Grenze fachlich sinnvoll ist (Festigkeit, Baubarkeit, Physik). Wenn nicht: in `project.json` unter `ParameterBounds` erweitern und neu rechnen. |
| `[WARNUNG] [MPI] MpiRunPath '…' ist OpenMPI. …` | `MpiRunPath` zeigt auf OpenMPI. Das offizielle SU2-Binary ist mit MPICH gebaut: unter OpenMPI läuft jeder Kern als eigene serielle Kopie und überschreibt die Dateien der anderen, ohne Fehlermeldung. | `MpiRunPath` auf das `mpiexec` des MPICH-Environments setzen (Server: `~/miniconda/envs/mpich/bin/mpiexec`, siehe `scripts/README.md`). `sim.ps1 doctor` prüft das ebenfalls. |
| `[ABGELEHNT] Modell ist instabil. Disqualifiziert.` | Der Bouncer hat einen Zufallstreffer aussortiert. Das ist normal. | Passiert es fast immer: `BouncerTolerance` lockern oder `BouncerJitter` verkleinern. |
| `[WIEDERAUFNAHME] Kein abgebrochener Lauf gefunden` | Für dieses Projekt gibt es kein Journal (noch nie gerechnet, oder Ergebnisse mit `delete` gelöscht). | Neu starten mit `.\scripts\sim.ps1 run -Project <Name>`. |
| `[WIEDERAUFNAHME] Die Konfiguration hat sich seit dem Start des Laufs geändert: …` | Eine der genannten Dateien hat andere Werte als beim Start. Ein Weiterrechnen würde Ergebnisse mischen. | Die Änderung zurücknehmen (die Werte des Laufs stehen in `effective-config.json`) oder mit `run` neu starten. |
| `[WIEDERAUFNAHME] Der Lauf ist bereits abgeschlossen, nichts zu tun.` | Der Lauf ist regulär zu Ende gegangen. | Nichts. Ergebnisse mit `fetch` holen. |
| `[WARNUNG] Wiederaufnahme: Aufruf … weicht vom Journal ab` | Beim Fortsetzen hat das Programm eine andere Variante vorgeschlagen als beim ersten Mal (meist: anderer Code). | Nichts. Alles bis dahin wurde übernommen, ab dort wird neu gerechnet. Das alte Journal liegt als `journal_abweichung_<Zeitstempel>.jsonl` daneben. |
| `[KONFIGURATION] Der Bayes-Optimierer braucht für jeden Parameter eine Grenze …` | Für den genannten Parameter fehlt in `ParameterBounds` das Min/Max. Der Bayes-Optimierer braucht den Suchraum. | Grenzen in `src/projects/<Projekt>/project.json` unter `ParameterBounds` eintragen (bei einem festen Parameter `Min` = `Max`). |
| `[KONFIGURATION] Bayes.SurrogateTarget = 'Metrics' verlangt, dass die Fitness … ihre Kennwerte nennt` | `"Metrics"` geht nur, wenn die Bewertungsformel des Projekts `RequiredMetrics` angibt. | `"Auto"` oder `"Fitness"` eintragen. |
| `[BAYES] Die Fitness liest mehr als die deklarierten Kennwerte …` | Die Gegenprobe hat gezeigt, dass die Formel Werte benutzt, die sie nicht als `RequiredMetrics` nennt. Der Lauf geht mit `"Fitness"` weiter. | Nichts, der Lauf ist gültig. Wer `"Metrics"` nutzen will: die fehlenden Kennwerte in `RequiredMetrics` der Bewertungsformel nachtragen. |
| `[BAYES] Modell noch unsicher oder Ergebnisse stark verrauscht.` | Weniger als die Hälfte der Varianten der Runde lag im vorhergesagten Band. | Bei den ersten Runden normal. Hält es an: Netz/Konvergenz prüfen (Rauschen) oder Vergleichslauf mit `"Rsm"`. |
| `[BAYES] Das Ersatzmodell konnte nicht gelernt werden …` | Rechnerisches Problem beim Lernen. Der Lauf bricht nicht ab: Die Runde wird mit Versuchsplan-Punkten gefüllt. | Nichts. Tritt es dauernd auf, die Ausgabe an die Entwicklung geben. |
| `[BAYES] Vertrauensbereich ausgereizt, erkunde neues Gebiet.` | Der Suchwürfel wurde nach vielen Misserfolgen zu klein; die Suche beginnt an einer neuen Stelle. | Nichts, das ist gewollt. |
| `Unbekanntes Projekt '…'` | Projektname vertippt (Groß-/Kleinschreibung zählt). | `.\scripts\sim.ps1 projects` zeigt die gültigen Namen. |
| `sim.ps1` bricht nach ein bis drei Minuten mit einem SSH-Fehler ab | Die Verbindung (VPN) war tot. | VPN prüfen, Befehl wiederholen. **Ein laufender Lauf auf dem Server rechnet trotzdem weiter.** |
| `[KONFIGURATION] Im Auto-Modus wird der Netzfaktor kalibriert. Bitte 'MeshRefinementFactor' aus … entfernen` | Im Auto-Modus kalibriert das Framework den Netzfaktor selbst; ein fester Faktor würde dem widersprechen. | `MeshRefinementFactor` aus der genannten Datei löschen und nur `Tolerance` unter `MeshCalibration` angeben. |
| `[WARNUNG] Ergebnis netzabhängig – Abweichung im feinen Netz beträgt …` | Die feine Nachrechnung des Champions weicht um mehr als `Tolerance` vom normalen Lauf ab. Die Optimierung hat sich auf eine Netzabhängigkeit gestützt. | `MeshCalibration` prüfen und mit einem feineren Netzfaktor (z. B. `0.71` statt `1.00`) neu rechnen. |
| `[HINWEIS] Letzte Kalibrierung (…) empfahl …, eingetragen ist …` | Der in `simulation.json` hinterlegte manuelle Netzfaktor weicht von der letzten Kalibrierung ab. | Überprüfen, ob der empfohlene Faktor in `simulation.json` übernommen werden soll. |
| `[HINWEIS] Kalibrierung veraltet (Konfiguration oder Startgeometrie hat sich seitdem geändert).` | Der Fingerabdruck über Geometrie und Solver-Dateien passt nicht mehr zum letzten Kalibrierbericht. | `.\scripts\sim.ps1 calibrate` ausführen, um eine frische Empfehlung zu erhalten. |

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
| **Netzfaktor (`MeshRefinementFactor`)** | Globaler Verfeinerungsfaktor für alle Zellgrößen des Windkanals (< 1.0 feiner, > 1.0 gröber). Wird im Auto-Modus oder per `calibrate` kalibriert. |
| **Grid Convergence Index (GCI)** | Maß für die numerische Gitterunsicherheit nach Celik et al. (2008), berechnet aus mindestens drei Netzstufen. Schätzt die prozentuale Abweichung zur theoretisch feinsten Gitterlösung. |
| **Windkanal / Fernfeld** | Der Rechenraum um das Modell. Am Rand herrscht die ungestörte Strömung. |
| **CD, CL** | Widerstands- bzw. Auftriebsbeiwert: Kraft geteilt durch (Staudruck × Bezugsfläche). |
| **Bezugsfläche** | Die Fläche, auf die CD und CL bezogen werden (z. B. Stirnfläche). Eine andere Fläche ergibt andere Zahlen, bei der Optimierung kürzt sie sich oft heraus. |
| **Reynoldszahl (Re)** | Kennzahl aus Geschwindigkeit, Größe und Zähigkeit: Sie sagt, wie „turbulent" eine Strömung ist. MantaAuv bei 2 m/s: etwa 100 000. |
| **Turbulenzmodell (SA, SST)** | Vereinfachte Beschreibung der Wirbel, die man nicht einzeln ausrechnen kann. |
| **Grenzschicht** | Die dünne Fluidschicht direkt an der Wand, in der die Geschwindigkeit von 0 auf die Strömung ansteigt. Dort entsteht der Reibungswiderstand. |
| **y+** | Wie dick die erste Netzzelle an der Wand im Vergleich zur Grenzschicht ist. Für SA ohne Wandfunktion sollte y+ ≈ 1 sein. |
| **Cauchy-Kriterium** | SU2 hört auf, wenn sich die überwachte Ergebnisgröße (`ConvergenceField`, z. B. Widerstand) über die letzten `CauchyElements` Schritte um weniger als `CauchyTolerance` ändert. Das ist SU2s eigenes Abbruchkriterium. Ob die Rechnung als konvergiert gilt (`Su2_Converged = 1`), entscheidet seit TODO-W3 die **Drift-Prüfung** im Endfenster (siehe „Genauigkeit der Konvergenz“). Über dasselbe Fenster misst der Analyzer die echte Schwingungsamplitude nach Abzug des Trends (`Su2_Osc_<Metrik>`). |
| **CFL-Zahl** | Schrittweite des Rechenverfahrens: größer = schneller, aber weniger stabil. |
| **RSM (Ersatzmodell)** | Optimierer, der aus allen bisherigen Rechnungen eine geschätzte Fitness-Landkarte baut und dort sucht. |
| **Bayes-Optimierung** | Optimierer, der nach jeder Runde ein Ersatzmodell lernt und die nächsten Varianten dort rechnen lässt, wo die Chance am größten ist, den Besten zu schlagen. Spart bei teuren Rechnungen viele Varianten. |
| **Ersatzmodell (Gauß-Prozess)** | Das Modell dahinter: Es schätzt die Fitness an jeder Stelle **und** wie sicher es sich dabei ist („Nebel"). Es lernt in jedem Lauf neu, in Sekunden, auf dem normalen Prozessor. |
| **Instationär, aber stabil** | Eine Rechnung, deren Ergebnis pendelt (Wirbel lösen sich ab), aber nicht wegläuft. Sie liefert einen gültigen Mittelwert mit Unsicherheit. Erst ein gesichertes Weiterwandern (Drift) gilt als nicht konvergiert. |
| **Erkunder** | Die letzte Variante einer Runde bei `"Bayes"` (ab 4 Varianten): Sie liegt dort, wo das Modell sich am wenigsten auskennt, aber nur an Stellen, die noch gut sein könnten. |
| **Vertrauensbereich** | Bei vielen Parametern (ab 10) sucht `"Bayes"` nur in einem Würfel um den besten Punkt, der nach Erfolgen wächst und nach Misserfolgen schrumpft. |
| **Parameter-Wichtigkeit** | Wie stark ein Parameter die Note beeinflusst, aus dem Ersatzmodell gelesen (`Relevanz_<Parameter>`). Hilft zu erkennen, welche Parameter man festsetzen kann. |
| **Evolutionär (EA)** | Optimierer, der gute Varianten zufällig weiterverändert („Mutation"). |
| **`*.local.json`** | Notausgang für Einstellungen, die nur auf einem Rechner gelten sollen. Soll es normalerweise nicht geben. |

---

## 9. Grenzen des Systems

- **Fluch der Dimensionalität:** Das RSM-Ersatzmodell (`"Rsm"`) arbeitet besonders effizient bei etwa 3 bis 15 Parametern. Mit steigender Parameterzahl wächst die Zahl der nötigen DoE-Stützstellen quadratisch (`((k+1)(k+2))/2 + 2`). Bei sehr vielen freien Parametern empfiehlt sich `"Evolution"` oder `"Bayes"`; `"Bayes"` schaltet ab 10 freien Parametern einen Vertrauensbereich zu und ist für etwa 5 bis 20 Parameter gedacht. Darüber (über 30) ist noch kein Verfahren vorbereitet.
- **Voxel-Auflösung (Konzeptphase):** 3D-Voxelgeometrie aus PicoGK ist trotz Oberflächenglättung für die frühe Konzeptphase und den Vergleich von Makroformen ausgelegt. Für die Submillimeter-Feinpolitur glatter Profilkonturen sind direkte analytische Konturen (wie der 2D-Kernel `Shape2D` in `Naca2D`) im Vorteil.
- **Physikalischer Modellrahmen:** Gerechnet wird inkompressibles RANS (`SOLVER= INC_RANS` mit `SA` oder `SST`), wahlweise stationär oder zeitgenau via Dual-Time-Stepping (URANS). Mehrphasenströmungen (freie Oberfläche, Kavitation), Kompressibilität bei hohen Mach-Zahlen sowie bewegte/rotierende Netze sind damit nicht abgedeckt.
