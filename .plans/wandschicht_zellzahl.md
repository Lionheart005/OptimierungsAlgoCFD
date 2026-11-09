# Wandschicht ohne Ausdünnung, formunabhängige Vernetzung, echte Zellzahl

> **Stand: 09.11.2026 — PLAN, vom Nutzer freigegeben, noch nicht umgesetzt.**
> Umsetzung, sobald die Kalibrierung des laufenden MantaAuv-Laufs fertig ist (Abschnitt 8).
> Auslöser: Bericht-Lauf MantaAuv (Start 09.11.2026 12:54, Bayes, Auto-Kalibrierung, Toleranz 15 %).
>
> **Ziele:**
> 1. Alle Varianten und Kalibrierstufen bekommen **dieselbe Wandschicht-Regel**. Es werden nie still Schichten
>    weggelassen, und es bleiben **mindestens 21 Schichten**.
> 2. **Die Vernetzung darf keine Form ausschließen.** Gerade ungewöhnliche, neue Formen sollen gerechnet werden
>    und die Suche nicht abschrecken.
> 3. Die Zellgrenze prüft **echte** Zellzahlen (Grenze 3D: **5 Mio.**).
> 4. Schichtkennwerte stehen je Rechnung in der CSV.
> 5. Die Dauer jeder Kalibrierstufe steht auch im Log (TODO-WZ10).

---

## 1. Befund

### 1.1 Der Rückfall 21 → 16 Schichten verfälscht die Netzstudie

Seit dem Servertest vom 06.11.2026 (Befund B11, `.old/servertest_2026-11-06.md`) gilt: Scheitert Gmsh in Schritt 2,
lässt `GmshCfdMesher` ein Viertel der äußeren Schichten weg und versucht es erneut (`NextLayerCount`, 21 → 16 → 12 …,
`Solvers/Cfd/GmshCfdMesher.cs` Zeile 129–160). y1 bleibt dabei gleich, die **Gesamtdicke** schrumpft aber stark
(21 Schichten = 2,22 mm, 16 Schichten = 0,86 mm, nur noch 39 %).

Kalibrierung im Bericht-Lauf (Gen1_Var1, Re_L = 1,2·10⁵, δ_turb ≈ 2,07 mm):

| Stufe | Faktor | Zellen (gemessen) | Schichten | Dicke | CD | CD-Sprung zur Vorstufe |
|---|---|---|---|---|---|---|
| Kal1 | 2,00 | 47 611 | 21 | 2,22 mm | 1,4437 | – |
| Kal2 | 1,41 | 85 565 | 21 | 2,22 mm | 1,0969 | ×1,32 |
| Kal3 | 1,00 | 159 713 | 21 | 2,22 mm | 0,8511 | ×1,29 |
| Kal4 | 0,71 | 321 952 | **16** | **0,86 mm** | 0,6197 | **×1,37** (Schichtwechsel) |
| Kal5 | 0,50 | 696 802 | 16 | 0,86 mm | 0,5340 | ×1,16 |
| Kal6 | 0,35 | 1 695 747 | 16 | 0,86 mm | läuft | – |

* Der größte CD-Sprung liegt genau beim Schichtwechsel. Bei der Kalibrierung vom 06.11.2026 (W10_06) war das Muster
  gleich (×1,39 → ×1,18 → ×1,17). Das ist ein starkes Indiz, aber noch kein Beweis (Beweis: TODO-WZ9, Schritt 2).
* Das widerspricht zwei eigenen Festlegungen: **F4** (die Wandschicht wird nicht mitskaliert, sonst „liefe die Studie
  über verschiedene Wandmodelle“) und dem Rückfall-Grundsatz aus TODO-40 („kein stiller Rückfall … weil sonst
  Varianten mit verschiedenen Wandnetzen verglichen würden“), beide in `.old/anmerkungen_umsetzung.md`.
* **Präzedenzfall 2D:** In W10_02 (NASA-TMR, α = 0°) fiel CD in Stufe 6/7 um 10–16 %, „weil der Hinterkanten-Retry
  die Wandschichtdicke auf 13 % kürzte“. In 2D wurde danach die Ursache behoben (B14), in 3D nie.
* **Optimierung:** Jede Variante wird einzeln vernetzt. Variante A kann 21 Schichten bekommen und Variante B 16.
  Die Schichtzahl steht nur im Konsolenlog, nicht in der CSV.
* Das Programm sieht den Gmsh-Fehlertext nicht (`-v 0`). Es wiederholt bei **jedem** Gmsh-Fehler mit weniger Schichten.

### 1.2 Vernetzungsfehler schrecken die Suche von ganzen Formzonen ab

* Bayes trainiert ein **Erfolgsmodell** (Gauß-Prozess auf 1 = gelungen, 0 = `SimulationFailed`) und multipliziert die
  Auswahlregel damit. Es „meidet die ganze Zone, nicht nur die Umgebung einzelner Abstürze“
  (`Core/Algorithms/BayesOptimizationAlgorithm.cs` Zeile 444–455, `Core/Surrogates/SurrogateTrainingSet.cs` Zeile 60–65).
* Ein **Gmsh-Fehler zählt dort genauso wie ein Strömungsabsturz.** Eine ungewöhnliche Form, an der nur der Vernetzer
  scheitert, schreckt die Suche also dauerhaft von ihrer ganzen Umgebung ab. Das ist genau das, was nicht passieren darf (Ziel 2).

### 1.3 Ursache (nachgestellt am 09.11.2026)

**Nachstellung:** `Wall_Gen1_Var1_Kal4.mesh` und `Meshing_Gen1_Var1_Kal4.geo` vom Server geholt. Die Extrude-Zeile wurde
durch die 21-Schichten-Zeile aus `Meshing_Gen1_Var1_Kal3.geo` ersetzt. Dann lief `gmsh <geo> -3 -format su2 -v 4`.
Ergebnis nach 2–4 s, **identisch mit Gmsh 4.15.2 (lokal) und 4.11.1 (Server)**, ein Versionsproblem ist es also nicht:

```
Error : Invalid boundary mesh (segment-facet intersection) on surface 3, intersection (20.4584,17.4484,-2.67117)
```

Die Kontrolle mit dem Original (16 Schichten) auf dem Server ergibt dieselbe Zellzahl (321 952). Die Nachstellung ist deterministisch.

**Ort:** Kehlen (konkave Ecken) an der Unterseite.
* Die **Sensorkugel unter der Flügelspitze** (`s2`/`s3`, Radius 3 mm, z = −0,8·WingRadius,
  `MantaGeometryGenerator.cs` Zeile 45–46, 73–74) sitzt auf einem dort nur ~1,7 mm dicken Flügel (y ≈ ±17 mm).
  Die Unterseite fällt von z = −0,9 auf −3,2 mm, die Faltungen liegen am Rand dieser Ausbuchtung.
* Zusätzlich gibt es einzelne Faltungen am Übergang Rumpf–Flügel (y ≈ ±5 mm).

**Mechanismus:** Gmsh extrudiert entlang der rohen Knotennormalen. In einer Kehle mit Radius R laufen sie zusammen,
und die Schicht faltet sich, sobald ihre Dicke T ≥ R ist. Danach schneidet sich die Oberseite der Schicht, und die
Tetraeder-Vernetzung des Kerns bricht ab.
* Die Kehlen entstehen durch `Offset(+r) / Offset(−r)` mit r = **0,6·MainRadius = 2,4 mm** (Zeile 78–80).
  MainRadius = 4 ist die **Untergrenze** des Parameterraums, die Startvariante hat also die engsten Kehlen.
* Wandschicht 2,22 mm gegen Kehlenradius 2,4 mm ist schon vom Entwurf her knapp. Ein feineres Oberflächennetz bildet
  die Kehle schärfer ab (Voxelrauschen nach der Glättung), dann kippt es:

| Wandnetz | Kantenlänge (Median) | Dreiecke, die bei T umklappen (rohe Normalen, Faltprüfung Abschnitt 7) | Gmsh mit 21 Schichten |
|---|---|---|---|
| Kal3 (1,00) | 1,00 mm | 0 bei 2,22 mm | geht |
| Kal4 (0,71) | 0,71 mm | 4 bei 2,22 mm, 0 bei 1,84 mm | scheitert (16 gehen) |
| Kal5 (0,50) | 0,54 mm | 1 bei 1,84 mm, ~15 bei 2,22 mm, 40 bei 2,4 mm | scheitert (16 gehen) |
| Kal6 (0,35) | – | ~70 bei 2,22 mm, ~125 bei 2,4 mm | – |

Die einfache Faltprüfung sagt das Gmsh-Verhalten in allen geprüften Fällen richtig voraus. Sie ist eher vorsichtig.

### 1.4 Lösung getestet: geglättete Extrusionsrichtungen (Hauptweg) und örtliche Dicke (Reserve)

Gmsh übernimmt über `Using View[0]` ein Feld aus einer `.pos`-Datei
(`Merge "<feld>.pos";` vor dem Wand-Merge, dann `Extrude { Surface{wall[]}; Layers{...}; Using View[0]; Recombine; }`):
* **Vektorfeld (`VT`)**: gibt die **Extrusionsrichtung** je Knoten vor. Die Vektorlänge skaliert die Dicke.
  Geprüft: Feld (0,0,1) lässt die Schicht nur nach oben wachsen. Feld = rohe Normalen scheitert exakt wie das Original.
* **Skalarfeld (`ST`)**: skaliert nur die Dicke. Geprüft: 0,5 ergibt halbe Dicke.
* Schichtzahl, y1 und Wachstumsrate bleiben in beiden Fällen unverändert.

**A) Geglättete Richtungen, volle Dicke überall** (Glättung der Normalen über Nachbarknoten, Kippung höchstens 30°
gegen die rohe Normale):

| Test | Glättung | Kippung median / max | Ergebnis |
|---|---|---|---|
| Kal5, 21 Schichten, 2,22 mm | 10 Schritte | 2,4° / 22° | **geht** (lokal 4.15.2 **und Server 4.11.1**), 672 087 Zellen |
| Kal5, 21 Schichten, 2,22 mm | 30 Schritte | 6,3° / 30° | geht |
| **Kal6 (feinstes Netz)**, 21 Schichten, 2,22 mm | 10 Schritte | 1,4° / 15° | **geht**, 1 604 145 Zellen, 31 s |
| Kal6, **29 Schichten**, Wachstum 1,12, 2,12 mm | 10 Schritte | 1,4° / 15° | **geht**, 1 972 367 Zellen (+23 %) |
| Kal6, 22 Schichten, **2,67 mm (≈ 1,3 δ)** | 30 Schritte | 3,5° / 28° | **geht** |

* **Netzqualität unverändert:** „ill-shaped tets“ (gleiche Gmsh-Version) Original 16 Schichten gegen neu:
  Kal5 41 gegen 45, Kal6 132 gegen 131. Mit dem Original hat SU2 normal gerechnet. Die SU2-Prüfung des neuen Netzes steht aus (TODO-WZ9).
* Die Kippung ändert die wandnormale Höhe der ersten Zelle nur um den Faktor cos(Kippung): 15° → −3,4 %, für y+ unerheblich.

**B) Örtliche Dicke (Skalarfeld, nur noch Reserve):** Mit rohen Normalen gehen Kal4 und Kal5 mit 21 Schichten, wenn die
Dicke nur in den Kehlen sinkt (min. Faktor 0,62–0,74, unter 0,9 auf 0,9–1,4 % der Wandfläche). Kal5 mit 30 Schichten
(2,38 mm) geht ebenso (unter 0,9 auf 2,3 %). Das wird nur gebraucht, wenn Glättung allein nicht reicht.

### 1.5 Zellzahl-Hochrechnung

* Die Prüfung nach Stufe 1 rechnet kubisch hoch, (2/0,5)³ = 64×. Vorhergesagt waren für Faktor 0,5 **3,05 Mio.**, gemessen
  sind es **0,70 Mio.** (4,4× zu hoch). Allein deshalb wurde am 09.11.2026 `MaxCells3D` auf 4 Mio. angehoben (`simulation.json`).
* Gemessene Schrittfaktoren je ÷√2: 1,80 · 1,87 · 2,02 · 2,16 · 2,43. Kubisch wären es 2,83. Der Exponent steigt
  mit feinerem Netz (Wandschicht und Oberfläche wachsen nur quadratisch, das Volumen kubisch).
* Adaptive Schätzung für 0,35: 2,03 Mio. Gemessen: 1,70 Mio.
* **Folge für den laufenden Lauf:** Für 0,25 schätzt die Kalibrierung 1,70 × 2,74 = 4,65 Mio. > 4 Mio. Sie endet also nach Kal6
  (echt wären es ~4,1 Mio.). Ergebnis in beiden Fällen: Optimierung bei Faktor 0,50, entweder als Treffer oder als Rückfall.
* Rechenzeiten (Kontrollprofil, 20 000 Iterationen): Kal5 (0,70 Mio.) ≈ 3,2 h (13:09 → 16:19), Kal6 (1,70 Mio.) > 3,8 h.
  Eine 0,25-Stufe (~4,1 Mio.) dürfte grob 10 h und mehr kosten.

---

## 2. Gibt es ein besseres Netzverfahren? (bewertet am 09.11.2026)

| Verfahren | Formunabhängig? | Passt zu SU2? | Bewertung |
|---|---|---|---|
| **Block-strukturiert (reine Hexaeder)** | **Nein.** Braucht eine Blockaufteilung je Topologie, für freie Voxelformen nicht automatisierbar | ja | ungeeignet: Genau ungewöhnliche Formen würden scheitern |
| Gmsh `"Hexa"` (Quad-Oberfläche extrudiert) | mäßig | ja | B11: kippt an scharfen Voxelkanten, SU2 divergiert |
| Kartesisch/Octree mit Schichten (snappyHexMesh, cfMesh) | ja, sehr robust | **nein**: Polyeder-Zellen, SU2 liest sie nicht, das hieße Wechsel zu OpenFOAM | zu großer Umbau. Schichten werden dort an Kehlen sogar weggelassen |
| **Hybrid: Prismenschicht + Tetraeder, geglättete Richtungen, örtliche Dicke** | **ja** | ja | **Empfehlung.** Das ist das übliche Vorgehen automatischer Grenzschicht-Vernetzer (Normalenglättung, örtliche Schichtkompression an Kehlen und Engstellen), und es ist getestet (1.4) |

Fazit: Beim Hybridnetz bleiben, es aber robust machen. „Strukturiert“ ist das Netz dort, wo es zählt, schon: Die
Prismenschicht an der Wand ist strukturiert (gleiche Schichtzahl, gleiches y1 überall). Nur der Kern ist unstrukturiert,
und gerade das macht das Verfahren formunabhängig.

---

## 3. Entscheidungen

### Vom Nutzer (09.11.2026)

| # | Entscheidung |
|---|---|
| EZ1 | Varianten müssen untereinander vergleichbar bleiben. Die Wandschicht darf nicht je Variante oder Stufe still dünner werden. |
| EZ2 | Mindestens 21 Schichten, gern mehr. Im Vergleich zur Literatur ist die Schicht eher dünn. |
| EZ3 | Erst die Gmsh-Ursache suchen und langfristig beheben (erledigt: Abschnitt 1.3, 1.4). |
| EZ4 | Die Zellzahl-Schätzung wird angepasst. **`MaxCells3D` = 5 Mio.** |
| EZ5 | Schichtkennwerte kommen als Spalten in die CSV. |
| EZ6 | **Die Vernetzung soll möglichst problem- und formunabhängig sein.** Varianten mit neuen, besonderen Formen dürfen nicht an der Vernetzung scheitern und ungerechnet bleiben. |
| EZ7 | Empfehlungen zu O1 und O2 angenommen (siehe unten): **gleiche Regel** je Variante; Schichtzahl nach dem SU2-Vergleich. |

### Eigene Festlegungen

| # | Festlegung | Warum |
|---|---|---|
| FZ1 | **Standard: geglättete Extrusionsrichtungen (Vektorfeld), volle Dicke überall.** Die Glättung ist **abstandsbasiert** (Radius ≈ Schichtdicke), nicht schrittbasiert, damit sie bei jedem Netzfaktor gleich weit wirkt. Kippung höchstens 30°. | 1.4 A: In allen Tests reichte das allein, auch beim feinsten Netz und bei 1,3 δ. |
| FZ2 | **Rückfall-Leiter je Variante, Schichtzahl und y1 bleiben immer gleich:** **Stufe A** Glättung wie FZ1 (wenn die Faltprüfung noch Faltungen zeigt: größerer Radius, bis 30°). **Stufe B** zusätzlich örtliche Dicke aus Falt- und Engstellenprüfung (Sicherheit 0,8 → 0,6 → 0,45, Faktor ≥ 0,3). **Stufe C** (letzter Ausweg, Abschnitt 4) Ersatznetz, gerechnet und gekennzeichnet. Erst wenn auch C scheitert, gilt die Variante als „nicht vernetzbar“. | EZ1 und EZ6: Fast jede Form wird mit der Standardregel gerechnet. Was übrig bleibt, wird gerechnet und sichtbar markiert, statt still anders oder gar nicht. |
| FZ3 | Die Faltprüfung rechnet **exakt statt abgetastet**: Die Orientierung des versetzten Dreiecks ist ein Polynom 2. Grades in T. Die kleinste positive Nullstelle ist die kritische Dicke. | Schnell (unter 1 s für 40 000 Dreiecke) und ohne Abtastfehler. |
| FZ4 | **Engstellenprüfung** zusätzlich zur Faltprüfung: Strahl entlang der Richtung bis zur nächsten fremden Wandstelle (Abstand g). Gilt auch g < 2,2 T, ist es eine Engstelle, und dort wird örtlich gedünnt (Stufe B). | Faltung erfasst nur Kehlen. Spalte zwischen zwei Körperteilen (z. B. Flügel nah an Sensor oder Rumpf, Formen mit Löchern) erfasst nur die Strahlprüfung. Wichtig für neue Formen (EZ6). |
| FZ5 | **Zellgrenze prüft die gemessene Zellzahl** nach dem Vernetzen und **vor** SU2. Die Hochrechnung bleibt nur als Vorschau im Log und nutzt den gemessenen Exponenten. | Vernetzen kostet Sekunden bis Minuten, eine SU2-Stufe Stunden. Gemessen ist ehrlicher als geschätzt. |
| FZ6 | Gmsh-Fehlertext wird mitgelesen und die erste `Error`-Zeile ins Log geschrieben. | Heute weiß das Programm nicht, warum Gmsh scheitert (`-v 0`). |
| FZ7 | **Vernetzungsfehler sind keine Physikfehler.** Ein Fehlschlag bekommt eine Ursache (`FailureKind`: Geometry, Mesh, Solver). Nur Solver-Fehler gehen ins Erfolgsmodell von Bayes. Mesh-Fehler zählen wie `Disqualified` (aus dem Training raus, nur kleine örtliche Abstoßung, keine Zonenmeidung). | 1.2: Sonst meidet die Suche ganze Formzonen, nur weil der Vernetzer dort einmal scheiterte (EZ6). |
| FZ8 | Ergebnisse vor und nach der Umstellung sind **nicht vergleichbar** (Hinweis im Handbuch und im Logkopf, wie bei TODO-W8). | Die Wandschicht ändert CD messbar (1.1). |

### Erledigte offene Fragen

* **O1 – „gleiche Dicke“ = gleiche Regel (angenommen).** Je Variante deckt die Dicke die Grenzschichtdicke δ(Re_L) ab,
  wie heute in `BoundaryLayerCalculator`. δ wächst mit L^0,8, zwischen L = 30 und 100 mm also um Faktor ~2,6.
  **Schichtzahl N und Wachstumsrate werden für den Lauf eingefroren** (an der Kalibriergeometrie bestimmt). Nur die
  Millimeter skalieren mit δ. Heute wird N je Variante aus δ/y1 neu bestimmt, meist gleich, aber nicht garantiert.
* **O2 – Schichtzahl: nach TODO-WZ9, Schritt 3 (angenommen).** Nach `BoundaryLayerCalculator` (Dicke ≥ δ):
  Wachstum 1,20 → 21 Schichten (heute) · 1,15 → 25 · 1,12 → 29 (Kal6: +23 % Zellen). Einstellbar ohne Code über
  `BoundaryLayerGrowthRate` in `gmsh.json`. Optional zusätzlich ein Reservefaktor `BoundaryLayerCoverage` (Dicke = k·δ,
  Literatur oft 1,2–1,5; Vorgabe 1,0 = heutiges Verhalten). Kal6 mit 1,3 δ ist vernetzbar (1.4).
  Späterer, eigener Punkt: der Übergang zu den Tetraedern. Letzte Schicht 0,38 mm (21 × 1,2) bzw. 0,26 mm (29 × 1,12)
  gegen Wandtetraeder 1,2 mm × Faktor (0,6 mm bei 0,5). Ein großer Sprung ist ungünstig.
* **O3 – `MaxCells3D` = 5 Mio. (Nutzer).** Mit gemessenen Zellen (FZ5) würde 0,25 (~4,1 Mio.) also noch gerechnet,
  grob 10 h und mehr für diese Stufe.
* **O4 (nur Hinweis, nicht Teil des Plans) – Geometrie:** Ein größerer Blend-Radius in `MantaGeometryGenerator`
  würde die Kehlen entschärfen, ändert aber den Entwurf. Mit FZ1 ist er nicht nötig.

---

## 4. Stufe C: Ersatznetz, damit keine Form verloren geht

Scheitern Stufe A und B, wird die Variante trotzdem gerechnet, aber erkennbar anders:
* Netz: wie heute, mit global verringerter Dicke. Schichtzahl und y1 bleiben. Die Dicke wird verringert, nicht die
  Schichtzahl, damit y+ und die Auflösung nahe der Wand gleich bleiben.
* Kennzeichen `MeshFallback = 1` in der CSV und im Log (`[ERSATZNETZ]`).
* Behandlung, **vorläufig** bis TODO-WZ9 Schritt 2 misst, wie stark die Dicke CD verschiebt:
  * nicht im Training des Ersatzmodells und nicht im Erfolgsmodell (wie FZ7). So verzerrt sie die Suche nicht und schreckt sie nicht ab;
  * nicht Champion-fähig ohne Nachprüfung;
  * am Laufende eigene Liste **„Formen mit Ersatznetz“** (Variante, Fitness, STL-Pfad), damit besondere Formen von Hand angeschaut
    und gezielt nachgerechnet werden können.
* Ist der gemessene CD-Einfluss klein (z. B. unter der halben Kalibrier-Toleranz), können Ersatznetz-Varianten danach
  normal ins Training. Entscheidung nach TODO-WZ9.
* Am Laufende kommt eine **Robustheitszeile** ins Log: „Netz: x Varianten Stufe A, y Stufe B, z Ersatznetz, w nicht vernetzbar“.

---

## 5. Arbeitspakete

Alle Pakete in reinem C#. Python wurde nur für die Diagnose benutzt (Abschnitt 7).

### TODO-WZ1 – Gmsh-Fehler sichtbar machen (klein)
* `GmshCfdMesher.RunGmsh` (und `Gmsh2DMesher`): stdout/stderr umleiten, mit `-v 2` (Fehler und Warnungen) aufrufen.
  Die erste `Error`-Zeile kommt in die Ausnahme bzw. ins Log, „ill-shaped tets“ nur als Detail.
* Tests: Parser für die Fehlerzeile (Beispiel aus Abschnitt 1.3).

### TODO-WZ2 – Wandfeld: Richtungen, Falt- und Engstellenprüfung (mittel bis groß)
* Neue Klasse `Solvers/Cfd/BoundaryLayerWallField.cs`:
  * Medit-Leser für `Wall_*.mesh` (Vertices, Triangles; Format siehe Schritt 1 in `GmshCfdMesher`).
  * Rohe Knotennormalen = Mittel der Einheits-Flächennormalen.
  * **Glättung** abstandsbasiert (FZ1): Mittel der Normalen im Radius R (Vorgabe R = T), renormieren, auf einen Kegel
    von höchstens 30° um die rohe Normale begrenzen. Getestet wurde schrittbasiert: 10 Schritte bei Kantenlänge 0,4–0,5 mm ≈ R 1,2–1,7 mm.
  * **Faltprüfung** exakt (FZ3) mit den geglätteten Richtungen: kritische Dicke je Dreieck, je Knoten das Minimum.
  * **Engstellenprüfung** (FZ4): Strahl je Knoten, Beschleunigung über ein einfaches Gitter (Voxel-Buckets) der Dreiecke.
  * Dickenfaktor je Knoten (nur Stufe B): `s = min(1, Sicherheit · t_krit / T, g / (2,2 T))`, weicher Übergang durch
    Glättung mit `s ≤ s_soll`, Untergrenze 0,3.
  * Schreibt `Wall_<Variante>.pos` als **ein** Vektorfeld (`VT`, Richtung × Faktor) für **alle** Wanddreiecke, damit jeder Startknoten im Feld liegt.
  * Liefert Kennwerte: Stufe (A/B), Glättungsradius, Kippung median/max, kleinster Faktor, Flächenanteil mit Faktor < 0,9.
* Tests: Ebene → keine Kippung, kein Faktor; konvexe Kugel → nichts nötig; konkave Zylinderkehle mit Radius R:
  roh t_krit ≈ R, geglättet t_krit > R; zwei parallele Platten im Abstand g → Faktor ≤ g / (2,2 T);
  Kippung nie über 30°; Format der `.pos`-Datei.

### TODO-WZ3 – Einbau in den 3D-Mesher (mittel)
* `GmshCfdMesher`, Schritt 2: nach Schritt 1 das Wandfeld berechnen, `Merge "Wall_<V>.pos";` **vor** dem Wand-Merge
  (View-Index 0) und `Using View[0];` in der Extrude-Zeile. **Immer**, auch ohne Faltung, damit alle Varianten dieselben
  Regeln durchlaufen.
* Rückfall-Leiter FZ2 statt `NextLayerCount`-Schleife. `NextLayerCount` entfällt (Tests anpassen).
* Schichtzahl und Wachstumsrate für den Lauf einfrieren (O1): an der Kalibriergeometrie bestimmen, in `SimulationContext`
  ablegen, im Mesher verwenden. Im manuellen Modus an der ersten Variante bestimmen.
* Log-Zeile wie heute, ergänzt um „Richtungen geglättet (max. x°)“ bzw. „örtlich gedünnt auf y % der Fläche“.
* `.pos` nach erfolgreichem Vernetzen löschen (Kal6-Wand ~40 000 Dreiecke ≈ 8 MB), bei Fehler nach `FehlerLogs/`.
* Schritt 1 (Oberfläche neu vernetzen) robuster machen: Scheitert `ClassifySurfaces`/`CreateGeometry` (denkbar bei Formen
  mit Löchern oder sehr feinen Stegen), einmal mit anderem `ClassifyAngleDeg` wiederholen und den Fehler mit Ursache melden.
* Tests: `.geo` enthält `Merge` des Felds und `Using View[0]`; Rückfall-Leiter ändert nie Schichtzahl oder y1.

### TODO-WZ4 – Fehlerursache und Ersatznetz in der Optimierung (mittel)
* `ModelRecord.FailureKind` (None, Geometry, Mesh, Solver) und `MeshFallback` (bool). Gesetzt in `WorkflowController`
  bzw. im Mesher (eigene Ausnahme `MeshGenerationException`).
* `SurrogateTrainingSet` (Zeile 60–65): `FailedPoints` (Erfolgsmodell) nur noch für `FailureKind = Solver`.
  Mesh-Fehler und `MeshFallback` landen in `ExcludedPoints` (FZ7, Abschnitt 4). Rsm (`RsmOptimizationAlgorithm.cs` Zeile 80)
  und Evolution sinngemäß.
* `ChampionFineVerifier`/`ChampionValidator`: `MeshFallback`-Varianten sind nicht Champion-fähig.
* Laufende: Liste „Formen mit Ersatznetz“ und Robustheitszeile (Abschnitt 4).
* Tests: Mesh-Fehler geht nicht ins Erfolgsmodell; Ersatznetz nicht im Training; Liste am Laufende.

### TODO-WZ5 – Netzkennwerte in die CSV (klein)
* Neue PassiveParameters je Rechnung: `BoundaryLayerLayers`, `BoundaryLayerThicknessMm` (Solldicke),
  `BoundaryLayerFirstCellMm` (y1), `BoundaryLayerStage` (1 = A, 2 = B, 3 = Ersatznetz), `BoundaryLayerMaxTiltDeg`,
  `BoundaryLayerMinScale` (1 = keine Ausdünnung), `BoundaryLayerThinnedShare` (Flächenanteil mit Faktor < 0,9), `MeshFallback`.
* Weg: optionale Schnittstelle (z. B. `IMeshMetricsSource` mit `LastMeshMetrics`) am Mesher. `WorkflowController`
  übernimmt sie nach `GenerateMesh`, wie heute `MeshCells` (`WorkflowController.cs` Zeile 380–384).
* Als Metadaten ausschließen in `ChampionFineVerifier` (Zeile 44–48) und `SimulationLog` (Zeile 210–214, 302).
* `calibration.csv`/`calibration.json`: Spalten Schichtzahl, Dicke, Stufe. **Warnung im Bericht, wenn Stufen
  unterschiedliche Schichtzahlen haben** (darf nach WZ3 nicht mehr vorkommen, die Warnung ist Absicherung).
* **2D ebenso:** `Gmsh2DMesher` dünnt bei Kollision die Dicke global auf ½, ¼ … bis 6,25 % (Zeile 45–81, B12/B14).
  Auch das wird über `BoundaryLayerMinScale` sichtbar. Ob 2D eine örtliche Lösung bekommt, wird nach einem Blick in die
  Naca2D-Logs entschieden (eigener Punkt, die Naca2D-Läufe für den Bericht sind betroffen).
* Tests: Spalten in der CSV; Ausschlusslisten; Kalibrierbericht warnt bei gemischten Schichtzahlen.

### TODO-WZ6 – Zellgrenze mit gemessenen Zellen (mittel)
* **Prüfung vor SU2:** `MeshCalibration` setzt für Kalibrierstufen eine Grenze im `SimulationContext`. `WorkflowController`
  vergleicht `MeshCells` direkt nach dem Vernetzen und bricht die Stufe vor `Solve` ab (Kennzeichen
  `CellLimitExceeded = 1`). `RefineAdaptively` beendet dann mit „Stufe X hätte N Zellen (gemessen), Grenze M“.
  Die abgebrochene Stufe kommt nicht in `stages` (wie B15).
* **Vorschau:** Exponent p aus den zwei zuletzt gemessenen Stufen, `p = ln(N_i/N_{i−1}) / ln(f_{i−1}/f_i)`, begrenzt auf [1,5; 3,0].
  Die Prüfung nach Stufe 1 (nur ein Messpunkt) **warnt nur** und bricht nicht mehr ab. Die echte Prüfung bei Stufe 5 entscheidet.
* Log: „geschätzt ~X (p = …), gemessen Y“.
* Gleiches für `RunWithPrecalibration2D` (eigene Hochrechnung, `MeshCalibration.cs` Zeile 349–357).
* MantaAuv `simulation.json`: `"MaxCells3D": 5000000` mit neuem Kommentar (gemessene Zellen). Code-Standard bleibt 3 Mio.
* Tests: Exponent aus synthetischer Folge (Werte aus Abschnitt 1.5); Stufe über der Grenze ruft den Solver nicht auf;
  Stufe 1 bricht nicht mehr ab.

### TODO-WZ7 – Fingerabdruck der Kalibrierung (klein)
* `MeshCalibration.ComputeFingerprint` nimmt eine Versionskonstante des Mesher-Aufbaus auf (z. B.
  `GmshCfdMesher.WallLayerScheme = "smoothed-view-1"`). So meldet der manuelle Modus alte Kalibrierungen als veraltet.

### TODO-WZ8 – Dokumentation (klein)
* `BEDIENHANDBUCH.md` 4.4: Absatz „Wenn sich die Schichten nicht vernetzen lassen“ neu schreiben (Kehlenradius gegen
  Dicke, geglättete Richtungen, Rückfall-Leiter, Ersatznetz, keine Schichten mehr weglassen). Zeile `MaxCells3D`
  (gemessene Zellen). Neue CSV-Spalten. Abschnitt zu `FailureKind`.
* `src/OptimierungsAlgoCFD/README.md`: Liste der PassiveParameters (Zeile 162), `IMeshGenerator` (Zeile 85),
  Trainingsdaten-Regel (Zeile 143, 264).
* MantaAuv `gmsh.json`: Kommentar zur Wandschicht.
* Nach Abschluss diesen Plan nach `.old/` verschieben und `README.md` hier aktualisieren.

### TODO-WZ9 – Servertest (erst wenn kein Lauf mehr läuft, denn Deploy stoppt laufende Läufe)
1. Nur vernetzen: MantaAuv Gen1_Var1 bei 2,0 / 1,0 / 0,71 / 0,5 / 0,35 mit 21 Schichten. Erwartet: alle Stufe A,
   kein Gmsh-Fehler, Log-Zeile und CSV-Spalten stimmen. Dazu 10–20 Zufallsvarianten aus dem ganzen Parameterraum
   (auch Ecken: MainRadius 4/15, WingRadius 2/10, TailTaper 0,1/1,5), damit die **Formunabhängigkeit** gemessen ist
   (Robustheitszeile).
2. SU2 bei 0,5 mit 21 Schichten (Kontrollprofil). Konvergiert? y+? **CD gegen 0,5340 (16 Schichten)**: So groß ist der
   Einfluss der Ausdünnung. Das entscheidet über ältere Ergebnisse und über die Behandlung des Ersatznetzes (Abschnitt 4).
3. Wie 2., aber mit Wachstum 1,15 bzw. 1,12 (25 bzw. 29 Schichten). Daraus wird O2 entschieden.
4. Neue Auto-Kalibrierung, dann der Bericht-Lauf.

### TODO-WZ10 – Dauer der Kalibrierstufen im Log (klein, Nutzerwunsch 09.11.2026)
* Die Dauer je Stufe wird schon gemessen und steht in `calibration.csv` (`Dauer_s`) und `calibration.json`
  (`DurationSeconds`, `MeshCalibration.cs` Zeile 640–683). **Im Log fehlt sie:** Die Zeile „⏱ Dauer“ druckt nur die
  normale Variantenschleife (`WorkflowController.cs` Zeile 444), nicht die Kalibrierstufen.
* Ergänzen: nach jeder Kalibrierstufe eine Zeile „⏱ Stufe k (Faktor f): Dauer x min“ (in `RunCalibrationStage` und in den
  3D-Bestätigungsstufen von `RunWithPrecalibration2D`). In `PrintSummary` kommt die Gesamtdauer der Kalibrierung dazu.
* Test: Log-Ausgabe enthält Dauer je Stufe und die Summe.

---

## 6. Reihenfolge und Aufwand

```text
WZ1, WZ5, WZ7, WZ10 (klein, unabhängig) → zuerst
WZ2 → WZ3                              (Kern; WZ5 liefert die Kennwerte für WZ3)
WZ4                                    (nach WZ3, braucht MeshGenerationException)
WZ6                                    (unabhängig von WZ2–WZ4)
WZ8                                    (begleitend)
WZ9                                    (Server, nach Ende der laufenden Kalibrierung)
```

Aufwand grob 2–3 Arbeitstage Code und Tests, dazu die Serverläufe (Schritt 1 ~1 h, Schritt 2 und 3 je ~3–4 h).

## 7. Risiken

| Risiko | Gegenmittel |
|---|---|
| Gekippte Prismen (bis 30°) rechnen schlechter als senkrechte | Kippung in den Tests median 1–6°; Grenze 30°; Prüfung in WZ9 Schritt 2 (Konvergenz, y+) |
| Gmsh liest das Feld an Knoten nicht (Lücken im Feld) | Feld über **alle** Wanddreiecke; in allen Tests (4.11.1 und 4.15.2) ohne Ausfall |
| Formen mit Spalten oder Löchern | Engstellenprüfung (FZ4), Schritt-1-Wiederholung (WZ3), Ersatznetz (Abschnitt 4), Messung in WZ9 Schritt 1 |
| Mesh-Fehler oder Ersatznetz verzerren die Suche | FZ7 und Abschnitt 4: nicht im Erfolgsmodell, nicht im Training |
| Große Felddatei bei feinen Netzen (`.pos` ~8 MB) | Nach dem Vernetzen löschen |
| Alte und neue Ergebnisse vermischt | FZ8: Hinweis im Handbuch und im Logkopf |
| 2D hat dasselbe Muster (globale Ausdünnung) | WZ5 macht es sichtbar, eigene Entscheidung danach |

## 8. Diagnose zum Nachmachen (für spätere Sessions)

* Dateien vom Server: `Ergebnisse/MantaAuv/Wall_Gen1_Var1_Kal<k>.mesh`, `Meshing_Gen1_Var1_Kal<k>.geo`,
  `Static_Windtunnel.stl`. Die Pfade im `.geo` auf lokal ändern und die Extrude-Zeile durch die 21-Schichten-Zeile aus `Kal3`
  ersetzen. Lokales Gmsh: `PicoGK/gmsh-4.15.2-Windows64/.../gmsh.exe`. Ein Versuch dauert wenige Sekunden (Kal6 ~30 s).
* Faltprüfung: Jeden Knoten um T·n versetzen; ein Dreieck, dessen versetzte Normale gegen die ursprüngliche zeigt, ist umgeklappt.
* Felder für Gmsh:
  `View "dir" { VT(x1,y1,z1,x2,y2,z2,x3,y3,z3){vx1,vy1,vz1,vx2,vy2,vz2,vx3,vy3,vz3}; ... };` (Richtung × Dickenfaktor)
  bzw. `View "scale" { ST(...){s1,s2,s3}; ... };`, dann `Using View[0]` in der Extrude-Zeile.
* Die Python-Hilfsskripte der Diagnose lagen nur im Scratch-Ordner der Session vom 09.11.2026 und sind nicht im Repo.

## 9. Laufender Lauf (Stand 09.11.2026, 23:15)

* Kal6 (0,35, 1,70 Mio. Zellen, 16 Schichten) rechnet seit 19:08. Danach endet die Kalibrierung (Abschnitt 1.5), und die
  Optimierung startet bei Faktor 0,5, wahrscheinlich mit 16 Schichten, je nach Variante evtl. 21.
* Für den Bericht nicht verwenden (FZ8). Nach dem Ende der Kalibrierung **zuerst `sim.ps1 fetch`** (Kalibrierbericht und
  Log sichern, Datenpunkt 0,35), dann stoppen. **Deployen erst danach**, denn ein Deploy stoppt laufende Läufe.
* Nachträglich prüfbar: Im Log nach „von 21 Schichten“ suchen (welche Varianten wurden ausgedünnt).
