# Plan: Netzstrategie, physikalische Skalierung & SU2-Konvergenzabbruch

> **Stand: 13.11.2026 — Abstimmung abgeschlossen, Bereit zur Umsetzung.**
> Dieses Dokument bündelt alle Erkenntnisse aus der Auswertung des `MantaAuv`-Serverlaufs
> (`BIC_12`, Stand `96e6e92b3c518c95`, Vergleichsstände `20261110_093805`, `20261110_144920`,
> `20261113_113149`) sowie die darauf aufbauenden Architektur- und Physikentscheidungen.
>
> **Wichtiger Leitgedanke für spätere Leser und Agenten:** Zu jeder Maßnahme steht hier ausdrücklich,
> **warum** diese Richtung gewählt wurde (inklusive der verworfenen Alternativen) und **welche konkrete
> Verbesserung** sie bringt. Änderungen an diesen Stellschrauben beeinflussen direkt Rechenzeit und
> physikalische Belastbarkeit der gesamten Optimierungspipeline.

---

## 1. Ausgangslage & gemessene Befunde (`MantaAuv`)

Die jüngsten Fixes aus [.old/wandschicht_zellzahl.md](.old/wandschicht_zellzahl.md) (geglättete Normalen via
`Using View[0]`, `MaxCells3D = 5M`) haben die Grenzschicht deutlich stabilisiert. Im Lauf `20261113_113149`
liefen alle 7 Kalibrierstufen (`Kal1`–`Kal7`) sauber mit **21 Prismenschichten (~2,22 mm Dicke)** durch,
ohne auf 16 Schichten zurückzufallen:

| Stufe | Faktor | Gesamtzellen | Oberflächendreiecke | Prismen (Anteil) | Tetraeder | Laufzeit | `CD` |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Kal1` | `2,00` | 47.810 | 1.928 | 40.488 | 7.322 | ~16 min | `1,32267` |
| `Kal2` | `1,41` | 85.366 | 3.332 | — | — | ~26 min | `1,02211` |
| `Kal3` | `1,00` | 159.779 | 5.960 | — | — | ~52 min | `0,80714` |
| `Kal4` | `0,71` | 332.412 | 11.752 | — | — | ~1 h 47 min | `0,62923` |
| `Kal5` | `0,50` | 672.473 | 20.892 | 438.732 | 233.741 | ~3 h 22 min | `0,53262` |
| `Kal6` | `0,35` | 1.608.032 | 41.800 | 877.800 | 730.232 | ~7 h 41 min | `0,46124` |
| `Kal7` | `0,25` | 3.506.231 | 73.368 | 1.540.728 (43,9 %) | 1.965.503 (56,1 %) | 59.486,3 s (~16 h 31 min) | `0,42456` |

### Drei zentrale Messbefunde aus den Lösungsfeldern

1. **SU2 bricht nie frühzeitig ab und verbrennt ~60–90 % Rechenzeit:**
   * `Kal1`–`Kal7` liefen jeweils volle **20.000 Iterationen**, `Var1`–`Var4` jeweils volle **5.000 Iterationen**.
   * `Kal7` lag in `History_Gen1_Var1_Kal7.csv` bereits ab Iteration ~1.000 innerhalb von `0,02 %` und ab
     ~1.500 innerhalb von `0,003 %` am Endwert (~18.500 Iterationen ≈ ~15 h ohne Nutzwert). Ähnlich `Kal6`
     (~860), `Kal5` (~1.470), `Var1` (~670) und `Var3` (~1.100).
   * Gleichzeitig zeigte `Var2` einen **langsamen Drift** von Iteration ~700 bis ~2.800 (`CD: 0,4252 -> 0,4292`).
     Ein reines Verkürzen auf ein niedriges festes `ITER`-Limit wäre daher gefährlich; nötig ist ein
     adaptiver Abbruch mit Nachlaufkontrolle.
2. **Die Grenzschichtdicke/Schichtzahl ist NICHT der Hauptfehler — 98 % des Netzfehlers stecken im Druckwiderstand `CD_p`:**
   * Der direkte Vergleich zwischen `093805` (Fallback auf 16 Schichten, 0,86 mm Dicke) und `113149`
     (21 Schichten, 2,22 mm Dicke) bei identischem Oberflächennetz zeigt nur `-0,25 %` Unterschied auf
     `Kal5` (`0,53395` vs. `0,53262`) und `+0,18 %` auf `Kal6` (`0,46042` vs. `0,46124`).
   * Die Integration von `Surface_Gen1_Var1_Kal*.csv` zerlegt `CD = CD_p + CD_v`: Der Reibungswiderstand
     `CD_v` ist von `Kal1` (`0,0767`) bis `Kal7` (`0,0810`) praktisch sofort auskonvergiert. Fast die gesamte
     Netzabhängigkeit (`1,2733 -> 0,3437`) liegt in `CD_p` aus zwei Ursachen:
     * **Uniforme Oberflächentriangulierung (`MeshSizeFromCurvature = false`):** Der Nasen-/Vorderkanten-Druckwiderstand
       `CD_p,fwd` fällt von `0,6111` (`Kal1`) über `0,3053` und `0,1133` und `0,0669` auf `0,0525` (`Kal7`),
       begleitet von einem unphysikalischen Staupunkt-Überschwinger (`Cp_max: 1,814 -> 1,446 -> 1,155 -> 1,064 -> 1,036`).
       Flache Rumpfmitte und scharfe Kanten bekommen unnötigerweise dieselbe Dreiecksgröße.
     * **Abgelöste Flügelspitzen-Finnen & `DetailBox`-Tetraeder:** Der Bereich `|y| = 16..24 mm` (12,5 % Fläche,
       Profiltiefe 5,8–8,4 mm, Höhe 6,1–6,4 mm) ist zu 40–46 % abgelöst (`aftCp = -0,44..-0,60`) und erzeugt
       **~40 % des Gesamtwiderstands (`CD_p,aft ≈ 0,168`)**.
   * `Kal5`–`Kal7` konvergieren monoton mit gemessener Ordnung `p = 1,92`, Richardson-Extrapolation
     `CD_ext = 0,38579`, `GCI_fine = 11,42 %` (`Kal7` liegt noch `+10,05 %` über `CD_ext`).
3. **Skalierungs-Bug zwischen `RubberBandScaler`, `GmshCfdMesher` und `Static_Windtunnel`:**
   * `Var2` (Stirnfläche `500,5 mm²`) erzeugte **3,20 Mio. Zellen**, `Var4` (`516,1 mm²`) dagegen nur
     **1,36 Mio. Zellen** (Faktor **2,36×**) bei ähnlicher Größe.
   * Ursache: `GmshCfdMesher.cs` skaliert zwar `y1Geo = FirstLayerHeightMm * scaleFactor` korrekt, lässt
     aber `SurfaceCellSize`/`BoundaryLayerSizeMin`, `BoundaryLayerSizeMax`, `DetailBoxCellSize` und
     `BoundaryLayerDistMin/Max` unskaliert im normierten PicoGK-Raum (`TargetPicoGkSize = 50`).
   * Noch gravierender beim Kanal: Weil `Static_Windtunnel.stl` einmalig in PicoGK-Größe gecacht und
     nach dem Vernetzen über `Mesh.ScalingFactor = (1.0 / scaleFactor) * 0.001` zurückskaliert wird,
     **ändert sich der reale Windkanal heute mit jeder Variante** — von `1024 × 256 × 256 mm` (0,76 % Blockage
     bei `Var2`) bis `2171 × 543 × 543 mm` (0,33 % Blockage bei `Var5`).

---

## 2. Architekturentscheidungen (inkl. Begründung & Nutzen)

### Entscheidung 1: Adaptiver SU2-Konvergenzabbruch über moderates Cauchy-Kriterium + automatischen Restart

* **Was wird geändert:**
  1. In `SimulationConfig.cs` / `Su2SolverOptions.cs` bzw. `Su2ConfigGenerator.cs`: `CONV_CAUCHY_EPS`
     für Produktions- und Kontroll-/Kalibrierläufe moderat ansetzen (z. B. `5e-6` bis `1e-5` bei
     `CONV_CAUCHY_ELEMS = 500`, entsprechend `Naca2D`, wo `1e-5` nach 550–2.410 Iterationen sauber stoppt).
  2. In `Su2Solver.cs`: Nach jedem SU2-Lauf prüft `Su2ConvergenceJudge` (wie bisher, aber mit
     verfeinerter Drift-Prüfung auf den letzten Fenstern). Zeigt die Kurve beim Cauchy-Stopp noch
     einen systematischen Drift (wie `Var2`) statt stationärem Rauschen, startet der Solver einen
     **nahtlosen Fortsetzungsblock (`RESTART_SOL= YES`, `SOLUTION_FILENAME= restart_flow.dat`)** bis
     Konvergenz oder Maximalbudget erreicht ist.
  3. In `Su2ConfigGenerator.cs`: Den irreführenden Kommentar zu `CONV_RESIDUAL_MINVAL = -8`
     korrigieren (SU2 ignoriert das Residuum-Minimum vollständig, sobald `CONV_FIELD = DRAG` aktiv ist;
     belegt in `Kal7` Iter 2975 und `Var2` Iter 706, wo alle 5 Residuen bereits `<= -8` waren).
  4. Grobe Kalibrierstufen (oder stark oszillierende Grobnetze Typ `Kal1`–`Kal3`) erhalten optional ein
     niedrigeres Iterationsbudget oder stationären Oszillations-Check über den gleitenden Mittelwert.
* **Warum genau so (statt externem `kill` beim Live-Polling):**
  * `CONV_CAUCHY_EPS = 1e-6` ist mit dem Venkatakrishnan-Limiter in 3D nicht erreichbar (`minCauchy`
    klemmt bei `1,50e-6..6,45e-6`).
  * Ein hartes Abschießen des SU2-Prozesses durch einen C#-Watchdog riskiert, dass SU2 keine finalen
    `Surface_*.csv`, `Volume_*.vtu` und `restart_flow.dat` ausschreibt — diese Dateien werden aber für
    Diagnose, Visualisierung und eine künftige FEM-Kopplung (siehe [fem_ausblick.md](fem_ausblick.md))
    zwingend gebraucht.
  * Wenn SU2 dagegen über ein moderateres `CONV_CAUCHY_EPS` **nativ und sauber** beendet und C# danach
    bei Bedarf per `RESTART_SOL= YES` verlängert, sind vollständige Ergebnisdateien garantiert und
    langsame Drifts wie `Var2` trotzdem sicher abgefangen.
* **Erwartete Verbesserung:**
  * Einsparung von ca. **60–90 % der SU2-Rechenzeit** (z. B. `Kal7` von ~16,5 h auf schätzungsweise
    1,5–2,5 h; Standardvarianten statt 5.000 Iterationen typischerweise 700–1.800 Iterationen) bei
    gleichbleibender oder höherer Konvergenzsicherheit.

---

### Entscheidung 2: Wandaufgelöste Low-Re-Grenzschicht behalten, aber moderat auf ~14–15 Schichten straffen (`r ≈ 1,28–1,30`, `y+ ≈ 1,5`)

* **Was wurde diskutiert (Vorschlag Prof. Baars: 4 Grenzschichten):**
  * Mit einem Low-Re-Netz (`y1 ≈ 0,01 mm`, Dicke ~`2,2 mm`) würden 4 Schichten eine Wachstumsrate
    von `r ≈ 5,71` erzwingen — numerisch unbrauchbar (AIAA Drag Prediction Workshop fordert `r <= 1,2`,
    im praktischen Kompromiss bis `1,25–1,30`).
  * Eine 4-Schicht-**Wandfunktion** (`y+ >= 30`, `y1 ≈ 0,30 mm`, `r ≈ 1,5`) funktioniert in anliegenden
    Rohr- oder Kanalströmungen bei hohem `Re`, widerspricht aber bei äußerer Umströmung mit starken
    Druckgradienten und Ablösung der validierten Literatur:
    * *NASA Turbulence Modeling Resource (TMR)* & *AIAA Drag Prediction Workshop (DPW)* sowie
      *ERCOFTAC Best Practice Guidelines* warnen ausdrücklich vor Wandfunktionen bei Ablösung;
    * selbst verbesserte Implementierungen wie SU2 `MARKER_WALL_FUNCTIONS` (Nichols & Nelson,
      *AIAA J.* 2004) verschmieren Ablösepunkte;
    * entscheidend ist zudem die kleine Reynolds-Zahl von `MantaAuv`: global `Re ~ 1,2e5`, an den
      ablösenden Flügelspitzen (`c ≈ 6 mm`) lokal nur `Re_c ~ 1,2e4` — hier existiert oft gar keine
      voll ausgebildete logarithmische Wandschicht für ein Wall-Law. Da ~40 % des Widerstands aus
      genau dieser Ablösung stammen, wären Ergebnisse mit 4-Schicht-Wandfunktion für freie
      Optimierungsformen physikalisch nicht belastbar.
* **Entscheidung:**
  * Low-Re-Spalart-Allmaras bleibt verbindlicher Standard.
  * Als pragmatischer Effizienz-Kompromiss wird `TargetYPlus` leicht von `1,0` auf ~`1,5` und
    `BoundaryLayerGrowthRate` von `1,20` auf **`1,28–1,30`** angehoben (mit Konfigurationsmöglichkeit in
    `GmshMesherOptions`).
* **Erwartete Verbesserung:**
  * Reduktion von heute **21–24** Prismenschichten auf ca. **14–15 Schichten** (~**30–40 % weniger
    Prismenzellen** — bei `Kal7` rund 450.000–600.000 Zellen weniger) bei weiterhin sauber aufgelöster
    viscous sublayer (`y+ ~ 1–2`) und belastbarer Ablösevorhersage für beliebige neuartige Formen.

---

### Entscheidung 3: Krümmungsadaptive Oberflächenvernetzung (`MeshSizeFromCurvature = true`)

* **Was wird geändert:**
  * In `GmshMesherOptions.cs` / projektbezogenen `gmsh.json` wird `MeshSizeFromCurvature = true`
    (mit konfigurierbarem `CurvatureElements`, Startwert z. B. `20–24`, plus Ober-/Untergrenze
    relativ zur Ziel-Oberflächenzellgröße) aktiviert und per Vergleichskalibrierung validiert.
* **Warum:**
  * Heute arbeitet Gmsh mit uniformer Oberflächen-Zielgröße (`MeshSizeFromCurvature = false`). Das
    erklärt den Staupunkt-Überschwinger (`Cp_max = 1,814` auf `Kal1`, noch `1,036` auf `Kal7`) und
    die hohe Sensitivität der Nasenkante (`CD_p,fwd: 0,6111 -> 0,0525`), während große schwach
    gekrümmte Rumpfflächen unnötig fein trianguliert werden.
  * Krümmungsbasierte Größensteuerung arbeitet **geometrieagnostisch**: Wo auch immer der
    Optimierer oder künftig das LLM scharfe Kanten, dünne Finnen oder kleine Radien erfindet,
    verdichtet Gmsh automatisch lokal, ohne feste Regions-Boxen im Code vorauszusetzen.
* **Erwartete Verbesserung:**
  * Deutlich geringerer Nasen- und Kantenfehler (`CD_p,fwd`) schon auf mittleren Kalibrierstufen (`Kal4`/`Kal5`)
    und bessere Zellausnutzung auf glatten Flächen.

---

### Entscheidung 4: Nachlauf-`DetailBox` physikalisch statt pauschal vergrößern; Z-Puffer von Körperdicke entkoppeln; Netzgrößen trennen

* **Was wurde diskutiert (`2×` BBox im Nachlauf + feinerer ganzer Außenraum):**
  * Die Auswertung des realen 3D-Nachlaufs in `Volume_Gen1_Var1_Kal7.vtu` zeigte:
    * Geschwindigkeitsdefizit `ux < 0,90 * u_inf`: `X = [-9,6, 130,8]`, `Y = [-23,1, 23,1]`, `Z = [-7,3, 4,7] mm`
    * `ux < 0,95 * u_inf`: reicht bis `X = 169,7 mm`, `Z = [-8,8, 6,6] mm`
    * Bisherige `DetailBox` (`Field[5]`): `X = [-9,8, 112]`, `Y = [-34,5, 34,5]`, `Z = [-8,75, 6,55] mm`
  * Gleichzeitig liegen bei `Kal7` bereits **96 % aller 1,97 Mio. Tetraeder** in Nahfeld/`DetailBox`,
    nur **2 %** im Nachlauf und **1 %** im restlichen Tunnel.
  * Eine pauschale Verdopplung auf `2×` BBox in *Breite und Höhe* plus Verfeinerung des gesamten
    Außenraums (~700× größeres Volumen als die `DetailBox`) würde die Tetraederzahl massiv aufblähen,
    während im ungestörten Fernfeld keinerlei Gradienten existieren, die feine Zellen bräuchten.
  * **Echter Schwachpunkt der heutigen `DetailBox`:** In `Y` gibt es reichlich Reserve, aber stromab
    (`+1,0 * dlenX` = 112 mm) schneidet sie früh ab, und in **`Z` (`±0,25 * dlenZ` = nur ±2,5 mm bei
    flachem Profil!)** ist die Box bei gewölbten oder anstellenden Geometrien viel zu eng.
* **Entscheidung:**
  1. `DetailBox` gezielt anpassen (`GmshCfdMesher.cs`, `GmshMesherOptions.cs`):
     * Stromab ausweiten von `1,0 * dlenX` auf **`1,5 * dlenX`** (konfigurierbar, z. B. `DetailBoxWakeLengthFactor`).
     * `Y`-Aufmaß beibehalten (`~0,25–0,35 * dlenY` + Mindestpuffer).
     * **`Z`-Aufmaß von der Bauteildicke `dlenZ` entkoppeln:** mindestens `max(DetailBoxWidthFactor * dlenZ, DetailBoxMinHeightLengthFactor * dlenX)` (z. B. `>= 0,15 * dlenX` je Seite), damit auch extrem flache Tragflächen mit Abwind komplett in der feinen Zone bleiben.
  2. **Drei Netzgrößen unabhängig konfigurierbar machen** (statt alles gemeinsam mit demselben `MeshRefinementFactor` zu skalieren):
     * `SurfaceCellSize` (direkt kalibrierbar),
     * `DetailBoxCellSize` (eigener Faktor relativ zu Objekt/Kalibrierung, damit Nachlauf fein genug für
       Ablösewirbel bleibt, ohne an extreme Nasen-Krümmungszellen gekoppelt zu sein),
     * `BoundaryLayerSizeMax` (Fernfeldrand bewusst grob belassen, Übergang weich über `BoundaryLayerDistMin`/`DistMax`).
* **Erwartete Verbesserung:**
  * Verhindert abgeschnittene Nachläufe und Wirbelschleppen bei Auftriebs-/Sonderformen, ohne Millionen
    nutzloser Fernfeld-Tetraeder im ungestörten Außenraum zu erzeugen.

---

### Entscheidung 5: `RubberBandScaler`-Kopplung im `GmshCfdMesher` korrigieren — ohne Geometrie-Restriktionen und ohne Zell-Cap

* **Was wurde diskutiert:**
  * Vorschlag „feste Referenzlänge im Generator vorschreiben, um auf den `RubberBandScaler` verzichten zu können“.
  * **Warum das verworfen wurde:** Eine Pflicht zu konstanter Referenzlänge würde bestehende Geometrie-Generatoren
    (`MantaProjectConfig`: `Length` `30..100`) und besonders künftige freie/LLM-Formen einschränken und ein
    Vernetzer-Problem in die Geometrieerzeugung verlagern. Außerdem ist es **physikalisch richtig**, dass
    Formen mit größerer echter Oberfläche mehr Zellen bekommen — eine künstliche Zellzahl-Obergrenze
    pro Variante würde große Formen schlechter auflösen als kleine und den Optimierer in Richtung
    künstlicher Unterauflösung verzerren.
* **Entscheidung:**
  * `RubberBandScaler` bleibt ausschließlich als interne PicoGK-Schutzskalierung erhalten.
  * In `GmshCfdMesher.cs` werden **alle** geometrischen Feldgrößen (`SurfaceCellSize`, `DetailBoxCellSize`,
    `BoundaryLayerSizeMax`, `BoundaryLayerDistMin`, `BoundaryLayerDistMax`, `BoundaryLayerThickness`)
    genauso konsistent mit `scaleFactor` in den PicoGK-Raum umgerechnet wie heute schon `y1Geo = FirstLayerHeightMm * scaleFactor` (oder einheitlich als echte physikalische Millimeter definiert und an einer zentralen Stelle multipliziert).
* **Erwartete Verbesserung:**
  * Beseitigt den gemessenen **2,36×-Zellzahlsprung** zwischen gleich großen Varianten (`Var2` vs. `Var4`).
  * Jede Form bekommt exakt die physikalische Auflösung in Millimetern, auf die die Kalibrierung
    eingestellt ist — völlig unabhängig von ihrem Aspektverhältnis oder `scaleFactor`.

---

### Entscheidung 6: Echten physikalisch ausreichend großen Windkanal garantieren (Entkopplung von `Static_Windtunnel.stl` & `scaleFactor`)

* **Was wurde diskutiert (KI: „Windkanal konstant halten“ vs. Prof. Baars: „groß genug ohne Effekt“):**
  * Überraschender Befund im Code: Durch `Static_Windtunnel.stl` + `Mesh.ScalingFactor = (1.0 / scaleFactor) * 0.001`
    ist der Windkanal heute **weder konstant noch unabhängig vom Objekt**: Lange/schlanke Formen mit
    kleinerem `scaleFactor` blähen den Kanal auf (`Var5`: `2171 × 543 × 543 mm`), während kompakte
    Formen mit größerem `scaleFactor` ihn zusammenschrumpfen lassen (`Var2`: `1024 × 256 × 256 mm`,
    `0,76 %` Blockage).
  * In der CFD-Praxis (ERCOFTAC / AIAA / COST) sind sich beide Empfehlungen einig:
    1. Solange der Fernfeldrand weit genug entfernt ist (**Blockage `< 0,3–0,5 %`**, mindestens `>= 5 L`
       stromauf, `>= 15 L` stromab, `>= 5 B/H` seitlich/vertikal), sind Randeffekte vernachlässigbar.
    2. Weil der Außenraum extrem grobe Tetraeder hat (`BoundaryLayerSizeMax`), kostet ein großzügig
       dimensionierter Kanal fast **keine zusätzlichen Zellen**.
* **Entscheidung:**
  * `Static_Windtunnel` nicht länger unbemerkt durch `scaleFactor` verzerren lassen. Entweder:
    * **Variante A (bevorzugt):** Den Tunnel in realen physikalischen Millimetern relativ zur jeweiligen
      Objekt-Bounding-Box garantieren (`max(FestePhysikalischeGroesse, Sicherheitsfaktor * ObjektBBox)` für
      Vorlauf, Nachlauf, Breite, Höhe), sauber in der `.geo`- bzw. Tunnel-Erzeugung berechnet; oder
    * **Variante B:** Eine feste, ausreichend große reale Kanalgeometrie nutzen, die selbst für das
      größte erlaubte Objekt (`max(Length, Width, Height)`) `< 0,3 %` Blockage einhält, und den Körper
      darin einbetten.
  * Zusätzliche Log-Ausgabe beim Vernetzen: reale Tunnelabmessungen `[mm]`, Blockage `[%]` und
    Mindestabstände in Objektlängen.
* **Erwartete Verbesserung:**
  * Schließt systematisch Blockage-Verfälschungen aus, egal welche Abmessungen Optimierer oder Nutzer
    für ein Projekt wählen — bei minimalem Zellzahl-Einfluss.

---

### Entscheidung 7: Voxelauflösung `BaseVoxelResolution` — Konsistenz-Check & optionale Auto-Kopplung beim Start

* **Technischer Rahmen (warum NICHT dynamisch pro Variante im laufenden Betrieb):**
  * `Program.cs` initialisiert den PicoGK-Kernel **genau einmal pro Prozess** via
    `kernel.RunHosted(context.Simulation.BaseVoxelResolution, ...)`. Ein Wechsel je Variante innerhalb
    desselben Laufs ist architektonisch nicht vorgesehen.
  * Noch wichtiger: Eine wechselnde Voxelgröße pro Variante würde das Oberflächen-Treppenrauschen von
    Variante zu Variante verändern und damit die Fitnessbewertung verrauschen.
* **Problem im Ist-Stand:**
  * Bei `BaseVoxelResolution = 0,5 mm` haben die erzeugten STL-Dreiecke ~`0,502 mm` Kantenlänge (26.036
    Dreiecke bei `Var1`). Bei feinen Kalibrierstufen (`Kal7`: ~`0,28 mm` Netzkante, 73.368 Dreiecke)
    unterteilt Gmsh bereits **flache Voxel-Facetten**, obwohl die zugrundeliegende Geometrie gar keine
    höhere Detailgenauigkeit mehr liefert.
* **Entscheidung:**
  * `BaseVoxelResolution` bleibt während des gesamten Laufs konstant (manuell konfigurierbar wie bisher).
  * Ergänzt wird ein **optionaler Modus `"Auto"` (oder `AutoVoxelResolutionFromMesh = true`) beim Prozessstart**
    sowie eine **Konsistenz-Warnung**: Die Voxelgröße wird beim Start so gewählt (oder geprüft), dass die
    reale Voxel-Auflösung (`BaseVoxelResolution / scaleFactor_typ`) mindestens so fein ist wie ca.
    `0,7–0,8 ×` die kleinste angestrebte Oberflächen-Zellgröße (unter Wahrung des Voxel-RAM-Budgets).
* **Erwartete Verbesserung:**
  * Verhindert unbemerkte „Schein-Genauigkeit“ auf zu groben STLs, ohne die Vergleichbarkeit zwischen
    Varianten durch wechselndes Voxel-Rauschen zu stören.

---

### Entscheidung 8: Beobachtete Telemetrie- & Protokollfehler direkt mitbeheben

1. **Journal-Replay-Dauer (`MeshCalibration.cs`):** Bei `--resume` überschreibt die lokale `Stopwatch`
   `DurationSeconds` von bereits fertigen Kalibrierstufen mit `0,0`. Bereits gemessene Laufzeiten müssen
   aus dem `RunJournal` / Zustand übernommen werden.
2. **Konvergenz-Log-Formatierung (`Su2Solver.cs`):** `"0.0"` formatiert kleine normierte Werte als
   `"0.0-fach"` -> auf `"0.000"` o. ä. ändern.
3. **Kalibrierungs-Transparenz:** Im Kalibrierbericht neben Gesamtzellzahl auch getrennte Anteile
   (Oberflächendreiecke / Prismen / Tetraeder), realen Blockage-Wert und `CD = CD_p + CD_v` loggen,
   damit bei künftigen Läufen sofort sichtbar ist, wo Rechenzeit und Fehler sitzen.

---

## 3. Arbeitspakete & Umsetzungsschritte

| ID | Arbeitspaket | Betroffene Dateien (primär) | Priorität |
| --- | --- | --- | --- |
| `AP-1` | **SU2-Konvergenzabbruch & automatischer Restart:** `CONV_CAUCHY_EPS` moderat einstellbar machen, irreführenden `-8`-Kommentar korrigieren, `RESTART_SOL=YES`-Nachlauf bei erkanntem Drift einbauen, `"0.0-fach"`-Log fixen | `Su2ConfigGenerator.cs`, `Su2Solver.cs`, `Su2ConvergenceJudge.cs`, `SimulationConfig.cs`, `su2.json` | Hoch (sofort größter Zeitgewinn) |
| `AP-2` | **Skalierungs-Bug im `GmshCfdMesher` beheben:** Alle physikalischen Zell- und Distanzgrößen einheitlich über `scaleFactor` in den PicoGK-Raum rechnen (kein `2,36×`-Drift mehr zwischen Varianten) | `GmshCfdMesher.cs`, `GmshMesherOptions.cs` | Hoch |
| `AP-3` | **Windkanal physikalisch absichern:** `Static_Windtunnel` von `RubberBandScaler`-Rückskalierung lösen; Mindestabstände (`>=5L` vor, `>=15L` hinter, `>=5B/H` seitlich, Blockage `<0,3–0,5 %`) in realen mm garantieren und loggen | `GmshCfdMesher.cs`, `TunnelGeometry.cs`, `GmshMesherOptions.cs` | Hoch |
| `AP-4` | **Grenzschicht moderat straffen:** `TargetYPlus ~ 1,5`, `BoundaryLayerGrowthRate ~ 1,28–1,30` testen & als Standard verifizieren (~14–15 statt 21–24 Schichten) | `BoundaryLayerCalculator.cs`, `GmshMesherOptions.cs`, `MantaAuv/solvers/gmsh.json` | Mittel |
| `AP-5` | **`MeshSizeFromCurvature` & `DetailBox`-Entkopplung:** Krümmungsverfeinerung aktivieren/kalibrieren; `DetailBox` stromab `1,5 L`, Z-Puffer von dünnem `dlenZ` entkoppeln; `Surface` / `DetailBox` / `Outer` Größen entkoppeln | `GmshCfdMesher.cs`, `GmshMesherOptions.cs`, `MantaAuv/solvers/gmsh.json` | Hoch |
| `AP-6` | **Voxel-Konsistenz beim Start & Kalibrier-Telemetrie:** Optionales `Auto`-Startup-Voxel-Sizing + Warnung bei zu grober STL; `0,0 s`-Resume-Duration fixen; `CD_p/CD_v` & Zellklassen loggen | `Program.cs`, `SimulationConfig.cs`, `MeshCalibration.cs`, `CalibrationEvaluator.cs` | Niedrig–Mittel |

---

## 4. Verifikations- & Abnahmekriterien

Bevor die Änderungen für volle Serverläufe freigegeben werden:

1. **Unit- & Integrationstests:** Alle bestehenden Projekt-Tests (`dotnet test`) bestehen; neue Tests
   prüfen insbesondere:
   * zwei gleich große Geometrien mit unterschiedlichem `scaleFactor` erhalten dieselbe physikalische
     Oberflächen-, `DetailBox`- und BL-Zellgröße in realen mm (kein `2×`-Zellzahlsprung wie `Var2` vs. `Var4`);
   * realer Windkanal verletzt nie die Mindestabstände / Blockage-Grenze;
   * `Su2Solver` erkennt sowohl sofortige Konvergenz als auch Drift-Fälle (Mock/Test-History) und
     löst `RESTART_SOL=YES` sauber aus;
   * `DurationSeconds` bleibt nach `--resume` erhalten.
2. **Validierungs-Benchmark auf `MantaAuv` (`Gen1_Var1`, `Var2`, `Var4`):**
   * Vergleich `Kal5`–`Kal7` (alt vs. neu): Erreicht das neue Setup bei **<= 50 % der Zellzahl** bzw.
     **<= 25 % der SU2-Wandzeit** mindestens dieselbe Nähe zu `CD_ext ≈ 0,386` (geringerer Nasen-`Cp`-Überschwinger,
     stabil aufgelöste Finnen-Ablösung)?
   * Vergleich `Var2` vs. `Var4`: Verschwindet die unplausible Zellzahl-Spreizung (`3,20M` vs. `1,36M`)?
3. **Regressionscheck auf `Naca2D`:** Bestehende 2D-Kalibrierung / 2D-Konvergenz darf durch die
   Anpassungen nicht beeinträchtigt werden.

---

## 5. Umsetzungsprotokoll (Live)

### Arbeitspakete
- [x] **`AP-1` — SU2-Konvergenzabbruch & automatischer Steady-Restart**
  - **Architektur-/Designentscheidungen:**
    - `ControlRunConfig.CauchyTolerance` auf `"1e-5"` (bei `CauchyElements = 500`, `Su2MaxIterations = 20000`) umgestellt, da `1e-6` in 3D mit Venkatakrishnan-Limiter selten direkt greift.
    - Neue Optionen in `Su2SolverOptions`: `AutoRestartOnDrift = true`, `MaxConvergenceRestarts = 3`.
    - `Su2ConfigGenerator.Generate` behält bei Standardläufen (`steadyRestartFile = null`) identische Nicht-Kommentar-Zeilen; korrigierter ASCII-/<=100-Zeichen-Kommentar zu `CONV_RESIDUAL_MINVAL= -8`; bei aktivem Steady-Restart werden `RESTART_SOL= YES`, `SOLUTION_FILENAME= <datei>` und optional angepasstes `CONV_STARTITER` geschrieben.
    - `Su2Solver.Solve` prüft nach Block 0 (und jedem Restart-Block) mittels `Su2HistoryAnalyzer` + `Su2ConvergenceJudge` (+ `IsStationary`), setzt bei erkanntem systematischen Drift innerhalb des verbleibenden `Su2MaxIterations`-Budgets automatisch fort (Toleranz je Block ÷ 10 bis Untergrenze `1e-7`), bereinigt vor Block 0 alte `restart.dat`/`restart_flow.dat` gegen Cross-Varianten-Leakage und fügt Block-Historien (`StitchHistoryCsv`) monoton aneinander.
    - `"0.0-fach"`-Logformatierung in `Su2Solver.cs` auf `"0.000-fach"` (`InvariantCulture`) behoben; `Program.PrepareUnsteadyRestart` unterstützt `restart_flow.dat`-Fallback und `CleanUnsteadyRestartFiles` schont `restart_flow.dat`.
  - **Erkenntnisse aus lokaler Verifikation (SU2 v8.5.0):** Ein direkter Lauf mit `SU2_CFD.exe` v8.5.0 bestätigte, dass SU2 bei stationärem `RESTART_SOL= YES` `ITER=` als Restbudget ab 0 zählt und `Inner_Iter` wieder bei 0 beginnt (physikalische Fortsetzung von `CD`), weshalb `ITER = remainingBudget` und `StitchHistoryCsv` mit monotonem Iterations-Offset exakt passen.
  - **Geänderte/neue Dateien:** `SimulationConfig.cs`, `Su2SolverOptions.cs`, `Su2ConfigGenerator.cs`, `Su2Solver.cs`, `Program.cs`, `OptimierungsAlgoCFD.csproj`, `_Vorlage3D/{simulation.json,solvers/su2.json}`, `_Vorlage2D/{simulation.json,solvers/su2.json}`, `src/projects/BEDIENHANDBUCH.md`, `ConvergenceAndToleranceTests.cs`, `Su2AutoRestartTests.cs` (neu, 14 Tests).
  - **Tests:** `dotnet test OptimierungsAlgoCFD.sln` nach finaler Bereinigung: **731/731 bestanden** (0 Fehler, 0 übersprungen, 0 Warnungen).
- [x] **`AP-2` — `RubberBandScaler`-Konsistenz im `GmshCfdMesher`**
  - **Architektur-/Designentscheidungen:**
    - Neue Hilfsmethode `SanitizeScaleFactor(float)` (`> 0 && finite`, sonst Fallback `1.0f`) in `GmshCfdMesher`.
    - `WriteGeoScript`, `AddWakeBox` und `AddDetailBox` multiplizieren alle direkt konfigurierten Millimeter-Werte (`BoundaryLayerSizeMin`, `BoundaryLayerSizeMax`, `BoundaryLayerDistMin/Max`, `SurfaceCellSize`, `WakeCellSize > 0`, `DetailBoxCellSize > 0`, `DetailBox`-`VOut` und den `Thickness`-Floor `1.0f * safeScale`) konsistent genau einmal mit `safeScale`, sodass die abschließende Rückskalierung `Mesh.ScalingFactor = (1.0 / safeScale) * 0.001` für jeden `scaleFactor` identische physikalische Metergrößen liefert.
    - Bereits im PicoGK-Raum gemessene bzw. einmal skalierte Größen (`bbox()`/`lenX`, `y1Geo`, `2.5f * baseSizeGeo`) werden nicht doppelt skaliert.
  - **Geänderte/neue Dateien:** `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshCfdMesher.cs`, `tests/OptimierungsAlgoCFD.Tests/ScaleFactorAndTunnelGuaranteeTests.cs` (Tests für `Prism`, `Hexa` und `Threshold` inkl. Fallbacks bei `scaleFactor = 0.35`, `0.42`, `0.5`, `1.0`, `2.0`).
- [x] **`AP-3` — Windkanal physikalisch absichern (`>= 5 L` / `>= 15 L` / `>= 5 B/H` / Blockage `< 0,3 %`)**
  - **Architektur-/Designentscheidungen:**
    - `GmshMesherOptions` um `TunnelMinUpstreamFactor = 5.0f`, `TunnelMinDownstreamFactor = 15.0f`, `TunnelMinLateralFactor = 5.0f`, `TunnelMaxBlockagePercent = 0.3f` (`<= 0` deaktiviert Blockage-Aufweitung) erweitert.
    - `StlWriter.ReadTriangles(string)` hinzugefügt, damit `GmshCfdMesher.GenerateMesh` über `MeshMetrics.Measure` sowohl den Modell-Hüllquader als auch die reale projizierte Stirnfläche in mm² ermitteln kann (Fallback auf `StlWriter.ReadBounds` bzw. `B * H`).
    - Neue Methoden `ResolveEffectiveTunnel(...)`, `EnsureTunnel(workingDirectory, modelBoundsPicoGk, scaleFactor, realFrontalAreaMm2)` und Record `TunnelSpec`: rechnen in realen mm, weiten den konfigurierten Tunnel nur nach außen auf (nie kleiner), prüfen Box und Zylinder-Querschnitt gegen `TunnelMaxBlockagePercent`, skalieren das Tunnel-STL in den PicoGK-Raum (`safeScale`), nutzen deterministische Hash-Namen `Static_Windtunnel_{hash}.stl` bei Expansion oder `scaleFactor != 1` (mit Header-/Größen-Cache; für unverändertes `Static_Windtunnel.stl` zusätzlich Bounds-Prüfung), loggen die effektiven Maße via `SimulationLog.Detail(...)` und schreiben `TunnelBlockagePercent`, `TunnelLengthMm`, `TunnelWidthMm`, `TunnelHeightMm`, `TunnelUpstreamL`, `TunnelDownstreamL` nach `_lastMetrics`.
    - `ModelRecord.IsMeshMetadataKey` filtert `Tunnel*` als Netz-Metadaten (keine Störung von Konvergenz-/Parameter-Auswertungen).
  - **Geänderte/neue Dateien:** `src/OptimierungsAlgoCFD/Core/Utilities/StlWriter.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshMesherOptions.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshCfdMesher.cs` (inkl. Fix `ToString("0.1")` → `"0.0"` in `LogTunnelSummary`), `src/OptimierungsAlgoCFD/Core/Models/ModelRecord.cs`, `src/projects/_Vorlage3D/solvers/gmsh.json`, `src/projects/BEDIENHANDBUCH.md`, `tests/OptimierungsAlgoCFD.Tests/ScaleFactorAndTunnelGuaranteeTests.cs` (34 Tests für AP-2 + AP-3).
  - **Tests & reale Gmsh-Verifikation (`AP-2` + `AP-3`):** `dotnet test OptimierungsAlgoCFD.sln --verbosity minimal` -> **764/764 bestanden**; zusätzlicher End-to-End-Lauf mit lokalem `gmsh.exe` v4.15.2 (`scaleFactor = 1.0` vs. `0.4`) ergab auf 8 Stellen identische Domain-Bounds (`[-0.33 .. 0.93] × [-0.16088109 .. 0.16088109]²` m, Blockage `0.300 %`, Abstand `5.0L vor / 15.0L nach`, `20774` vs. `20766` Zellen).
- [x] **`AP-4` — Grenzschicht moderat straffen (`TargetYPlus ~ 1,5`, `GrowthRate ~ 1,28`)**
  - **Architektur-/Designentscheidungen:**
    - `GmshMesherOptions`: 3D-Standards auf `TargetYPlus = 1.5f` und `BoundaryLayerGrowthRate = 1.28f` gesetzt.
    - `Su2SolverOptions`: `TargetYPlus = 1.5f` analog nachgezogen, damit `YPlusWarnFactor × TargetYPlus` (`3.0 × 1.5 = 4.5`) im 3D-Standard konsistent bleibt.
    - 2D-Konfiguration (`Gmsh2DMesherOptions` auf `1.0f` / `1.2f`, `_Vorlage2D/*`, `Naca2D/*`) bleibt bewusst unverändert für regressionsfreie NASA-TMR-Validierung.
  - **Verifikation:** Bei MantaAuv-Referenzbedingungen (`1025 kg/m³`, `2,0 m/s`, `0,001001 Pa·s`, `L = 50 mm`, `Coverage = 1,0`) sinkt die automatische Schichtzahl von `21` auf `15` (`y1 ≈ 0,0146 mm`, Gesamtdicke ≥ Grenzschichtdicke).
  - **Geänderte Dateien:** `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshMesherOptions.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/Su2SolverOptions.cs`, `src/projects/_Vorlage3D/solvers/gmsh.json`, `src/projects/_Vorlage3D/solvers/su2.json`, `src/projects/BEDIENHANDBUCH.md`, `tests/OptimierungsAlgoCFD.Tests/BoundaryLayerAndYPlusTests.cs`, `tests/OptimierungsAlgoCFD.Tests/ScaleFactorAndTunnelGuaranteeTests.cs`.
  - **Tests:** `dotnet test OptimierungsAlgoCFD.sln --verbosity minimal` -> **767/767 bestanden** (0 Fehler, 0 übersprungen, 0 Warnungen).
- [x] **`AP-5` — `MeshSizeFromCurvature` aktivieren & `DetailBox` stromab/Z-Puffer entkoppeln**
  - **Architektur-/Designentscheidungen:**
    - `GmshMesherOptions`: `DetailBoxLengthFactor = 1.5f`, neue Option `DetailBoxMinHeightLengthFactor = 0.15f`, `MeshSizeFromCurvature = true` (bei `CurvatureElements = 20`) sowie neue Option `CurvatureMinSizeFactor = 0.5f` (intern auf `[0.1, 1.0]` geklammert als Untergrenze `surfaceSize * curvMinFactor` gegen Voxel-Splitter-Explosion). `Gmsh2DMesherOptions` bleibt unverändert.
    - `GmshCfdMesher.AddDetailBox`: entkoppelt den Z-Puffer für flache/schlanke Körper mittels `dz_pad = (dz_width > dz_min_len) ? dz_width : dz_min_len;` (`dz_min_len = Max(0, DetailBoxMinHeightLengthFactor) * dlenX`); `SurfaceCellSize`, `DetailBoxCellSize` und `BoundaryLayerSizeMax` bleiben über Schritt 1/2 sauber getrennt.
    - `GmshCfdMesher.WriteGeoScript`: im `Threshold`-Zweig bleiben die Zeilen direkt hinter `ExtendFromBoundary = 0;` kompatibel und erhalten bei aktivem `MeshSizeFromCurvature` zusätzlich `Mesh.MeshSizeMin = sizeMin * curvMinFactor` und `Mesh.MeshSizeMax = sizeMax`.
    - `MeshCalibration.MesherSchemeVersion` auf `"tunnel-scaled-curvature-2"` erhöht, sodass frühere Kalibrierungen sauber als veraltet erkannt werden.
  - **Geänderte Dateien:** `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshMesherOptions.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/GmshCfdMesher.cs`, `src/OptimierungsAlgoCFD/Core/Pipeline/MeshCalibration.cs`, `src/projects/_Vorlage3D/solvers/gmsh.json`, `src/projects/BEDIENHANDBUCH.md`, `tests/OptimierungsAlgoCFD.Tests/GmshCfdMesherTunnelTests.cs`, `tests/OptimierungsAlgoCFD.Tests/DetailBoxTests.cs`.
  - **Tests:** `dotnet test OptimierungsAlgoCFD.sln --verbosity minimal` -> **773/773 bestanden** (0 Fehler, 0 übersprungen, 0 Warnungen).
- [x] **`AP-6` — Voxel-Konsistenz beim Start, Resume-Dauer & Kalibrier-Telemetrie (`CD_p/CD_v`, Zellklassen)**
  - **Architektur-/Designentscheidungen:**
    - `SimulationConfig`: neue Option `AutoVoxelResolution` (Standard `false`, in `_Vorlage3D` und `_Vorlage2D` dokumentiert).
    - `VoxelResolutionAdvisor`: ermittelt beim Start vor `kernel.RunHosted(...)` anhand `RubberBandScaler.Create(...)` (`BaseParameters`) und der 3D-`GmshCfdMesher`-Zieloberflächenzellgröße die reale Voxelgröße; bei `AutoVoxelResolution = true` Verfeinerung auf max(`0.1 mm`, `0.75 * surfaceMm * scale`) ohne Mutation von `SimulationConfig` (kein Eingriff in `--resume`-Fingerprint); bei weiter bestehendem Missverhältnis (> `0.8 * surfaceMm`) klare `[WARNUNG]`, bei 2D-Pipelines unverändert `BaseVoxelResolution`.
    - `RunJournal` & `WorkflowController`: `DurationSeconds` wird im Journal-Eintrag (`journal.jsonl`) gespeichert und beim `--resume`-Replay in `LastCallDurationSeconds` wiederhergestellt, ohne `ModelRecord.PassiveParameters` oder `Simulation_Results.csv` zu verändern. `MeshCalibration` nutzt optional `durationProvider` in allen Kalibrier-, 2D/3D-Bestätigungs- und adaptiven Stufen.
    - Zellklassen & aerodynamische Zerlegung: `MeshMetrics.ReadSu2CellBreakdown` liest `NELEM` (`Tri`/`Quad`/`Prism`/`Hexa`/`Tet`/`Pyr` bzw. Oberflächen-Marker außer `Farfield`/`Fluid`), `Su2SurfaceAnalyzer.EvaluateAerodynamicDragBreakdown` integriert `Pressure_Coefficient` und `Skin_Friction_Coefficient` aus `surface.vtu` (ASCII + Appended Binary) in 2D/3D zu `Su2_CD_p` und `Su2_CD_v`; `CalibrationEvaluator` / `MeshCalibration` exportieren Tri/Quad/Prism/Hexa/Tet/Pyr, `TunnelBlockagePercent`, `CD_p` und `CD_v` in `calibration.csv`, `calibration.json` und die Konsolenzusammenfassung. `ModelRecord.IsMeshMetadataKey` filtert `Tunnel*` und `MeshCells_*`.
  - **Geänderte/neue Dateien:** `src/OptimierungsAlgoCFD/Core/Models/SimulationConfig.cs`, `src/OptimierungsAlgoCFD/Core/Models/ModelRecord.cs`, `src/OptimierungsAlgoCFD/Core/Pipeline/VoxelResolutionAdvisor.cs` (neu), `src/OptimierungsAlgoCFD/Core/Pipeline/RunJournal.cs`, `src/OptimierungsAlgoCFD/Core/Pipeline/WorkflowController.cs`, `src/OptimierungsAlgoCFD/Core/Pipeline/CalibrationEvaluator.cs`, `src/OptimierungsAlgoCFD/Core/Pipeline/MeshCalibration.cs`, `src/OptimierungsAlgoCFD/Core/Utilities/MeshMetrics.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/Su2SurfaceAnalyzer.cs`, `src/OptimierungsAlgoCFD/Solvers/Cfd/Su2Solver.cs`, `src/OptimierungsAlgoCFD/Program.cs`, `src/projects/_Vorlage3D/simulation.json`, `src/projects/_Vorlage2D/simulation.json`, `src/projects/BEDIENHANDBUCH.md`, `tests/OptimierungsAlgoCFD.Tests/CalibrationTelemetryAndVoxelTests.cs` (neu).
  - **Tests:** `dotnet test OptimierungsAlgoCFD.sln --verbosity minimal` -> **787/787 bestanden** (0 Fehler, 0 übersprungen).
- [x] **`AP-7` — Alle READMEs, Vorlagen (`_Vorlage3D`, `_Vorlage2D`), Projekt-JSONs (`MantaAuv`, `Naca2D`) & `.plans/` abschließend synchronisieren**
  - **Projekt-JSONs & Vorlagen:**
    - `src/projects/MantaAuv/`: `simulation.json` (`AutoVoxelResolution = false`, `CauchyTolerance = "1e-5"`, `ControlRuns` mit `"1e-5"` & `IterativeToleranceFraction = 0.1`, bereinigte 20000-Iterationen-/AP-1..AP-6-Kommentare), `solvers/gmsh.json` (`TunnelMinUpstreamFactor = 5.0`, `TunnelMinDownstreamFactor = 15.0`, `TunnelMinLateralFactor = 5.0`, `TunnelMaxBlockagePercent = 0.3`, `TargetYPlus = 1.5`, `BoundaryLayerGrowthRate = 1.28`, `DetailBoxLengthFactor = 1.5`, `DetailBoxMinHeightLengthFactor = 0.15`, `MeshSizeFromCurvature = true`, `CurvatureElements = 20`, `CurvatureMinSizeFactor = 0.5`), `solvers/su2.json` (`AutoRestartOnDrift = true`, `MaxConvergenceRestarts = 3`, `TargetYPlus = 1.5`, bereinigter `MachNumber`-Kommentar).
    - `src/projects/Naca2D/`: 2D-Werte (`TargetYPlus = 1.0`, `BoundaryLayerGrowthRate = 1.2`, `DetailBoxLengthFactor = 1.0`) gezielt erhalten; `solvers/su2.json` um `AutoRestartOnDrift = true` & `MaxConvergenceRestarts = 3` ergänzt; `simulation.json`-Konvergenzkommentar aktualisiert.
    - `_Vorlage3D/` & `_Vorlage2D/`: JSONs gegen finale C#-Defaults abgeglichen; `src/projects/_Vorlage3D/README.md` um aktuelle 3D-Vorgaben ergänzt.
  - **Dokumentation & Pläne:**
    - `src/projects/BEDIENHANDBUCH.md` (Stand `13.11.2026`, `scaleFactor`-Konsistenz, Windkanal-Garantien, Prism/Hexa-Tabelle `y+ ~ 1,5 / 1,28` & Krümmungsverfeinerung, neue CSV-/`calibration.csv`-Spalten, `--resume`-`DurationSeconds`, neue Warn-/Logzeilen).
    - `src/OptimierungsAlgoCFD/README.md`, `src/OptimierungsAlgoCFD/Kernels/PicoGk/README.md`, `src/projects/README.md`, `scripts/README.md` (`plot_calibration.py`, `plot_optimization.py`, `DurationSeconds`, `calibration.csv`), `README.md` (Link-Korrekturen) sowie `.plans/README.md` (Stand `13.11.2026`, aktiver Verweis auf `netzstrategie_und_su2_abbruch.md`, bereinigte `.old/`-Archivlinks) vollständig synchronisiert.
  - **Offen (Laufzeit-Abnahme auf Server):** Benchmark-/Validierungslauf auf `BIC_12` für `MantaAuv` und `Naca2D` (Abschnitt 4, Punkte 2 & 3).
