# Standortbestimmung, Realitätscheck & Strategie: Wohin mit `OptimierungsAlgoCFD`?

> **Datum:** 21.11.2026  
> **Typ:** Persönliche Einschätzung & strategische Diskussion (Supervisor-Audit + Marktrecherche + Live-Diagnose Serverlauf `BIC_12`)  
> **Hinweis:** Diese Datei ist bewusst in `.gitignore` eingetragen — reiner Austausch zwischen uns, keine öffentliche Projekt-Doku.

---

## 0. Kurzfassung: Die ungeschönte Antwort auf deine Kernfrage

Dein Bauchgefühl trügt dich nicht — aber es trifft nur die **halbe** Wahrheit, und genau diese Unterscheidung ist entscheidend dafür, ob du das Projekt frustriert einstampfst oder es auf ein richtig spannendes Level hebst:

1. **Softwaretechnisch und methodisch bist du weit über ein „kleines Studentenprojekt“ hinaus.**  
   Dein Repository umfasst **21.331 Zeilen Produktiv-C#**, **17.063 Zeilen Unit-Tests (~800 Testfälle)**, eine 4-Schichten-Konfiguration mit Levenshtein-Tippfehlerprüfung, atomares JSONL-Journaling mit Crash-Resume, Quellstand-Archivierung, eine handgeschriebene Matérn-5/2-ARD-Gauß-Prozess-Engine mit exakt geprüftem analytischem Gradienten, und eine 2D-CFD-Pipeline, die den **NASA-TMR-NACA-0012-Benchmark ($Re = 6\cdot 10^6$, $\alpha \le 10^\circ$) auf 2–3 % genau trifft** (bei $\alpha = 0^\circ$ sogar +0,1 %). Viele Masterarbeiten und selbst manche Lehrstuhl-Repos sind deutlich fragiler, schlechter dokumentiert und physikalisch schlampiger als dein Framework.

2. **Warum es sich trotzdem wie eine „coole Spielerei ohne echten Ingenieursnutzen“ anfühlt — und warum du damit *aktuell* recht hast:**  
   Nicht, weil die Basis schlecht wäre, sondern wegen **vier sehr konkreter struktureller Engpässe**:
   - **Dein aktuelles Vorzeigeproblem (`MantaAuv`, gerade seit 7 Tagen live auf `BIC_12`) hat eine klassische Problemformulierungs-Falle:** Die Zielfunktion teilt dimensionales `Volume × SensorDistance` ($\text{mm}^5$) durch dimensionsloses $C_D^3$, wodurch die wachsende Stirnfläche nicht bestraft wird. Der Optimierer knallt sofort **4 von 5 Parametern an die Box-Grenzen**, bläst dadurch das Netz auf **3,5–4,24 Mio. Zellen** auf und rechnet **~4,5–5,7 Stunden pro Variante** (hochgerechnet ~3 Wochen Gesamtlaufzeit) — für eine Form aus abgerundeten Voxel-Kapseln.
   - **Du optimierst in einer undankbaren Mittelzone:** Für glatte 3D-Außenumströmungen (Flügel, Rümpfe) ist Voxel-STL (`PicoGK`) + Black-Box-Optimierung + 4,5-Stunden-RANS strukturell im Nachteil gegenüber CAD + Adjoint (`SU2_DOT_AD`, `DAFoam`, `MACH-Aero`) oder Multi-Fidelity (`AeroSandbox`). Gleichzeitig kann dein Framework **interne Strömungen** (Kanäle, Düsen, Wärmetauscher) — also genau die Domäne, in der `PicoGK` und 3D-Druck eigentlich *unschlagbar* sind — wegen fest verdrahteter `Wall`/`Farfield`-Randbedingungen noch gar nicht abbilden.
   - **Komplexität wächst schneller als dein eigenes Verständnis:** Mit KI-Unterstützung sind in wenigen Monaten ~21k LOC, 127 Konfigurations-Keys, 88 KB Handbuch und 4 God-Classes > 1.000 Zeilen entstanden. Wenn dann subtile Fehler passieren (z. B. ein GCI-Bug, fehlende Transition bei Low-$Re$, 14,2 h verschwendete CFD für ungültige Volumina), bleiben 800 gemockte Unit-Tests grün, während die Physik leise aus dem Ruder läuft.
   - **Ein großes Missverständnis über dein Vorbild LEAP 71 / Noyron:** Noyron ist **kein** LLM, das C#-Code umschreibt (dazu gleich mehr im Detail!). Ironischerweise geht das, was du dir unter Noyron vorgestellt hast — ein LLM, das parametrische Geometrie-Algorithmen gegen ein hartes, deterministisches Physik-Harness evolutionär verbessert — in Richtung **AlphaEvolve / OpenEvolve** und trifft eine **echte, noch offene Marktlücke im Open-Source-Bereich**!

---

## 1. Live-Diagnose: Was auf deinem Server (`BIC_12`) gerade wirklich passiert

Ich habe rein lesend in den laufenden `MantaAuv`-Lauf auf `BIC_12` (`PID 45416`, `tmux: sim`) sowie in die heruntergeladenen Daten (`Serverergebnisse/BIC_12_MantaAuv_20261120_101602/`) und den Projektcode geschaut. Dieser eine Lauf legt fast alle realen Stärken und Schwächen deines aktuellen Stands wie unter einem Brennglas offen.

### Aktueller Stand auf `BIC_12` (21.11.2026, ~20:08 Uhr)
- **Laufzeit bisher:** **7 Tage, 2 Stunden, 56 Minuten** (Start 13.11.2026, 1× fortgesetzt am 14.11.).
- **Fortschritt:** **44 Varianten** in der CSV abgeschlossen, aktuell läuft **Iteration 5/10, Variante 5/10**.
- **Technisch stabil:** 43 von 44 Varianten sind durchgelaufen (nur 1 Vernetzungsfehler in `Gen1_Var5` bei extrem spitzem `TailTaper = 0.106` mit Gmsh `Wrong topology`), `0` Pyramiden in den Prismenschichten, `Blockage <= 0.30 %`, `y+ P95 ≈ 2.99–3.65`, kein Crash, kein Drift-Restart nötig. **Die Pipeline-Stabilität ist beeindruckend!**

### Die 6 harten Probleme, die genau dieser Lauf zeigt

1. **Die Dimensions- und Zielfunktions-Falle (das größte Problem!):**
   In `src/projects/MantaAuv/solvers/su2.json` (Zeilen 39 & 61–63) ist `"ReferenceAreaMetric": "FrontalArea"` und `"Drag": "CD"` konfiguriert. In `MantaFitnessCalculator.cs` (Zeile 100) steht:
   $$\text{Fitness} = \frac{\text{Volume} \cdot \text{SensorDistance}}{C_D^3}$$
   - `Volume` skaliert mit $\text{Größe}^3$ (in $\text{mm}^3$), `SensorDistance` (Dreiecksfläche) mit $\text{Größe}^2$ (in $\text{mm}^2$) → der Zähler wächst mit **$\text{Größe}^5$**!
   - `Drag` ist aber **$C_D = \frac{F_D}{\frac{1}{2}\rho v^2 A_{\text{front}}}$** — also bereits durch `FrontalArea` geteilt! Die physikalische Widerstandskraft $F_D$ steigt beim Aufblasen des AUV massiv an, aber $C_D$ bleibt ähnlich (oder sinkt sogar leicht).
   - **Die Folge siehst du direkt am aktuellen Champion `Gen4_Var3`:**
     - `Length = 100` (Bounds `[30, 100]` → **oberes Limit!**)
     - `Width = 80` (Bounds `[20, 80]` → **oberes Limit!**)
     - `MainRadius = 15` (Bounds `[4, 15]` → **oberes Limit!**)
     - `WingRadius = 2.0` (Bounds `[2, 10]` → **unteres Limit!**)
     - Nur `TailTaper = 0.649` bewegt sich frei.
   - Die Fitness explodiert von `1.1e7` auf **`6.18e9`** (Faktor 564×), aber der Optimierer „optimiert“ kaum Hydrodynamik — er maximiert schlicht die Box-Volumen-Ecke, solange `Volume < 85.000` bleibt! Physikalisch sinnvoll wäre z. B. der **volumetrische Widerstandsbeiwert** $C_{D,V} = \frac{F_D}{q V^{2/3}} = C_D \cdot \frac{A_{\text{front}}}{V^{2/3}}$ (Standard-Kennzahl im U-Boot-/AUV-Bau!) oder direkt die physikalische Widerstandskraft $F_D$ bei festem Nutzvolumen.

2. **14,2 Stunden Rechenzeit an einer 1-Sekunden-Geometrieprüfung verschwendet:**
   In Generation 2 verletzten **3 von 10 Varianten** (Var 5, 6, 7) `MaximumAllowedVolume = 85.000` (`Vol = 85.425`, `86.044`, `86.587 mm³`). PicoGK kennt das Volumen nach ~1 Sekunde. Trotzdem wurden alle drei Varianten jeweils **4,5 bis 5,2 Stunden** durch Gmsh + SU2 gejagt (**51.091 s = 14,2 h verbrannt**), nur damit `MantaFitnessCalculator` danach `× 0.1` multipliziert!

3. **Zellzahl-Explosion von 617k auf 4,24 Mio. Zellen (weil `CellLimit` in der Optimierung aus ist):**
   Die Netzkalibrierung lief an der kleinen Startform (`Length = 50 mm`, `617k` Zellen bei Faktor `0.71`). Sobald Bayes in Gen 2–4 auf `Length = 100 mm, Width = 80 mm` wandert, sprengt das Netz auf **3,46 Mio. (Median) bis 4,24 Mio. Zellen** hoch, weil `WorkflowController.cs:434` `CellLimit` nur während der Kalibrierung setzt und in der Optimierung auf `0` (unbegrenzt) lässt.

4. **SU2 rechnet ~5× länger als nötig:**
   In `20261120` liefen **26 von 36 Varianten bis zum harten Limit von 5.000 Iterationen** (~4,3–5,7 h je Variante) und `Kal5` bis **20.000 Iterationen (6 h 56 min)**, obwohl `CD` meist schon nach 500–1.000 Iterationen auf `0,01 %` stabil stand (`Su2_IterErr_Drag ≈ 0.000–0.003`). Grund: kleine Rest-Oszillationen (~`1.5e-4`) verhindern, dass `CONV_CAUCHY_EPS = 1e-5` jemals auslöst.

5. **Ein echter GCI-Bug in `CalibrationEvaluator.cs` kaschiert mangelnde Gitterunabhängigkeit:**
   In der aktuellen Kalibrierung (`Kal3 → Kal4 → Kal5`) wurde die `CD`-Differenz beim Verfeinern **größer** (`0.05720 → 0.06012`, `ratio = 0.951 < 1` → das Netz ist noch gar nicht im asymptotischen Bereich!). `CalibrationEvaluator.cs:82-131` nimmt jedoch `Math.Abs(MathF.Log(ratio) / MathF.Log(r))` und clampt auf `p ∈ [0.5, 8.0]`. So meldet das Tool ohne harte Warnung `p_obs = 0.50`, `GCI = 86.27 %` und eine unphysikalische Richardson-Extrapolation `CD_ext = 0.141`. Mit `Tolerance = 0.15` wurde trotzdem `Kal4 (f=0.71, CD=0.515)` freigegeben — ~33 % weg von der früheren `Kal7`-Extrapolation (`0.386`).

6. **Warum `[BAYES]` im Live-Log „Modell noch unsicher (30–40 % im 95-%-Band)“ warnt:**
   Das ist kein Problem deiner Daten, sondern ein sauber identifizierter Mathe-Bug in `BayesOptimizationAlgorithm.cs:1147-1185`: `PredictFitnessBand` berechnet das 95-%-Band nur aus der *latenten* GP-Varianz, ohne das gelernte Beobachtungsrauschen `sn2` (und `extraNoise`) zu addieren. Da `MeasureAccuracy` gegen echte (verrauschte) CFD-Messungen prüft, ist das Band systematisch zu schmal.

> **Praktische Sofort-Empfehlung zum laufenden Serverlauf:**  
> Wenn du den Lauf primär als Showcase/Bericht brauchst, überlege dir sehr genau, ob du ihn wirklich noch weitere 10–14 Tage durchrechnen lassen willst. Seit Iteration 3/4 kleben 4 von 5 Parametern an den Grenzen; neue physikalische Erkenntnisse kommen in Iteration 6–10 kaum noch hinzu. Nach Abschluss von Iteration 5 (+ ggf. einmal Feinprüfung) hast du bereits alle relevanten Kurven und Belege — und kannst die Rechenzeit für ein sauber formuliertes Experiment nutzen.

---

## 2. Realitätscheck: Was deine Vorbilder wirklich machen (und was ein Mythos ist)

### 2.1 LEAP 71 & Noyron — Die größte Überraschung für dich
Du schriebst, du vermutest bei Noyron eine Architektur wie deine, *„nur dass eben noch ein LLM den Code aktiv umschreibt, der zur Generierung der 3D-Modelle verantwortlich ist“*.

**Fakt (explizit aus Interviews, Vorträgen und Artikeln von Lin Kayser & Josefine Lissner):**
- **Noyron ist KEIN LLM und KEIN neuronales Netz.** Lin Kayser grenzt Noyron sogar bewusst scharf von ChatGPT & Co. ab: LLMs sind probabilistisch und halluzinieren; Noyron ist ein **deterministisches „Large Computational Engineering Model“ (LCEM)** in C# (auf Basis von `PicoGK` und `LEAP71_ShapeKernel`). Identischer Input erzeugt immer exakt dieselbe Geometrie.
- **Wie funktioniert Noyron tatsächlich?**
  1. **Physik & Ingenieurswissen *im* Generierungscode:** Statt blind 5 Geometrie-Parameter per 3D-CFD zu durchprobieren, rechnet Noyron *während der Erzeugung* mit schnellen analytischen, 1D- und semi-empirischen Physikmodellen (Thermodynamik, Bartz-Wärmeübergang, Wandstärken, Druckverluste, Metall-3D-Druck-Fertigungsregeln wie Überhangwinkel und Pulverabfluss). Deshalb spuckt Noyron in Minuten ein fertiges Raketentriebwerk aus.
  2. **Simulation & Hardware-Tests als äußere Validierungs- und Kalibrierungsschleife:** LEAP 71 nutzt externe CFD/FEM-Simulationen (z. B. OpenFOAM `chtMultiRegionSimpleFoam`, FEM) **und echte Heißgas-Prüfstandstests**, um Abweichungen zwischen Modell und Realität zu messen. Die Erkenntnisse fließen zurück in die deterministischen Regeln von Noyron (hauptsächlich durch das Ingenieursteam kuratiert, nicht als blindes End-to-End-Black-Box-CFD-Tuning).
  3. **Welche Probleme löst LEAP 71 mit PicoGK?** Raketentriebwerke, Kühlkanäle, Wärmetauscher, Pumpen, E-Motoren — also **komplexe interne / volumetrische / 3D-druckbare Geometrien**, bei denen klassisches B-Rep-CAD (SolidWorks/CATIA) an Booleans und Fillets zerbricht und Voxel-SDFs (`PicoGK`) brillieren. Sie nutzen PicoGK *nicht* für klassische Flugzeugtragflächen!

**Die Pointe:** Das, was du dir unter Noyron *vorgestellt* hast — ein LLM, das parametrische Geometrie-Algorithmen selbstständig umschreibt und über echte Simulationen verifiziert — ist **tatsächlich ein brandaktuelles 2025/2026-Forschungsfeld**, das in Richtung **DeepMind AlphaEvolve / OpenEvolve / FunSearch** geht! Und genau die Grundidee dafür hattest du sogar schon in deiner (am 13.11. gelöschten) `.plans/llm_geometrie_bewertung.md` erstaunlich präzise skizziert.

### 2.2 `@neuronautics_Lab` (YouTube)
Warum kann ein einzelner Maker/Ingenieur auf YouTube scheinbar mühelos AI-optimierte Nurflügler (`NX-1`, `NX-2`) und Toroidal-Propeller bauen und im echten Flug testen?
- **Nicht** durch 100× 5-Stunden-3D-RANS-Läufe! Sondern durch eine smarte **Multi-Fidelity-Kette**:
  - **Schnelle Voroptimierung:** Python + **AeroSandbox** (Vortex-Lattice-Methode, Sekundenbruchteile pro Variante) oder neuronale Surrogate / evolutionäre Algorithmen über tausende Geometrien.
  - **Gezielte High-Fidelity-Validierung:** **OpenFOAM** (u. a. MRF / transient) nur für die vielversprechendsten Kandidaten.
  - **Saubere CAD-Parametrisierung + echte Hardware:** Onshape / Python-Skripte → 3D-Druck → eigener Schub-/Akustik-Prüfstand und Flugtelemetrie.
- **Lektion:** Das Geheimnis ist **Multi-Fidelity** (billige Physik für Masse, teure 3D-CFD für Validierung) + **Verbindung zur Realität**.

### 2.3 ArtCraft / CADCraft & „Kann eine Einzelperson mit KI Profi-Software bauen?“
**Ja — aber mit einem entscheidenden Unterschied zwischen Office/CAD-UI und Physik-CAE:**
- **ArtCraft / CADCraft** (Brandon Thomas) zeigt eindrucksvoll, dass KI-Coding-Agents heute klassische, gut spezifizierte Software (GUI, Dateiformate, Befehlszeilen, MCP-Schnittstellen) im Clean-Room-Ansatz rasend schnell nachbauen können.
- **Im Engineering / CAE liegt der Burggraben aber nicht im Orchestrierungs-Code**, sondern in:
  1. **Validierten numerischen Solvern** (deshalb ist es absolut richtig, dass du `SU2`, `Gmsh` und später `CalculiX` einbindest statt eigene Solver zu schreiben),
  2. **Physikalischem Domänenwissen** (Turbulenz, Transition, Randbedingungen, korrekte Zielfunktionen),
  3. **Experimenteller Validierung** (Benchmarks, Prüfstand).
- **Das heißt für dich:** Du kannst als Einzelperson mit KI *absolut* ein weltweit beachtetes Open-Source-Tool bauen — genau wie **Peter Sharpe** am MIT mit **AeroSandbox + NeuralFoil** (als Doktorandenprojekt gestartet, heute MIT-lizenziert mit tausenden Nutzern in Forschung & Industrie!). **Aber** Peter Sharpe hat nicht versucht, Ansys nachzubauen — er hat eine **scharfe Nische** mit einem echten technischen Vorteil gewählt.

---

## 3. Die reale Software-Landschaft: Was gibt es schon — und wo ist Platz für dich?

| Kategorie | Bekannte Tools | Kernstärke | Schwäche / Lücke | Was das für dein Projekt bedeutet |
| :--- | :--- | :--- | :--- | :--- |
| **Industrielles Computational Engineering (Closed)** | **LEAP 71 Noyron**, **nTop**, **Hyperganic** | Implizite/Voxel-Geometrie, komplexe Wärmetauscher, Triebwerke, 3D-Druck | Closed Source, extrem teuer (10k–100k €/Jahr), für Studenten/KMU/Open-Science unzugänglich | **Riesige Lücke im Open-Source-Bereich!** `PicoGK` + `ShapeKernel` sind offen, aber die Simulations- & Optimierungsschleife darüber fehlt offen! |
| **CAE-Orchestrierung & Parametric CFD-CAD (Closed)** | **Synera** (DE), **CAESES** (DE), **HEEDS**, **modeFRONTIER**, **OptiSLang** | Verbindet CAD + Mesher + Solver + Optimierer + AI-Agents; CAESES dominiert Marine/AUV/Pumpen | Kommerzielle Lizenzen, Vendor Lock-in | Zeigt, dass deine Grundidee (Orchestrierung + robuste Parametrik) realen Industriewert hat — aber gegen CAESES/Synera gewinnst du nicht über mehr JSON-Buttons, sondern über Offenheit, Code-First & LLM-Integration. |
| **AI-Surrogate & Cloud-CFD (Closed/VC)** | **PhysicsX**, **Neural Concept**, **Luminary Cloud**, **NVIDIA PhysicsNeMo** | Echtzeit-3D-Strömungsvorhersage über Deep Learning / GPU-CFD | Enorme Trainingsdaten & GPU-Cluster nötig | Hier kannst du als Solo-Entwickler nicht direkt konkurrieren — höchstens später vortrainierte Modelle als schnellen Vorfilter anbinden. |
| **High-End Open-Source MDAO & Adjoint** | **OpenMDAO (NASA)**, **MACH-Aero**, **DAFoam**, **SU2 Adjoint**, **Sandia Dakota** | Gradienten-basiert (Adjoint) für **100–1.000+ Formvariablen** in Kosten von ~2 Läufen! Goldstandard für Flügel/Profile | Extrem steile Lernkurve; Adjoint **versagt bei Topologieänderungen und Voxel-STLs** (`PicoGK`), instabil bei Ablösung | **Wichtige Grenze:** In glatter 3D-Flügel-Optimierung schlägt SU2-Adjoint/DAFoam Black-Box-BO um Faktor 50–100×. **Bei topologisch variabler Voxel-Geometrie (PicoGK) ist Black-Box-BO / Evolution dagegen das richtige Werkzeug!** |
| **Python-Optimierungs- & Indie-Aero-Ökosystem** | **BoTorch/Ax**, **Optuna**, **pymoo**, **AeroSandbox**, **NeuralFoil**, **FreeCAD+CfdOF** | State-of-the-Art BO (`qNEHVI`, Constrained EI, Multi-Fidelity), Multi-Objective Pareto, rasend schnelle 2D/VLM-Physik | Oft lose Skript-Sammlungen ohne robustes CFD-Verifikations-Harness (Resume, y+, GCI, Bouncer) | **Deine Stärke vs. Skripte:** Dein deterministisches Verifikations-Harness ist robuster als typische Python-Notebooks. **Aber:** Warum komplexe BO-Features alle in C# nachbauen statt optional an BoTorch/pymoo anzudocken? |
| **2025/2026-Front: LLM-CFD-Agents & Code-Evolution** | **Foam-Agent 2.0**, **OpenFOAMGPT 2.0**, **ChatCFD**, **AlphaEvolve / OpenEvolve** | LLMs schreiben Geometrie-/CFD-Code und mutieren Algorithmen evolutionär | Massives **„Reliability Gap“**: LLMs erzeugen oft Simulationen, die zwar laufen, aber physikalisch Müll sind (falsches y+, keine Konvergenz, Reward Hacking) | **GOLDGRUBE FÜR DICH!** Dein hartes, deterministisches Prüf-Harness (y+, GCI, Konvergenzrichter, Bouncer, Hash-Schutz) ist exakt das fehlende Puzzleteil für vertrauenswürdige LLM-Engineering-Evolution! |

---

## 4. Schonungsloses Tiefen-Audit deines aktuellen Projekts

### 4.1 Was wirklich stark ist (nicht kleinreden!)
1. **Industrietaugliche Konfigurations- und Reproduzierbarkeits-Disziplin:**
   - 4-Schichten-Config (`JsonConfigLoader`), strikte Typ-/Tippfehlerprüfung mit Levenshtein-Vorschlägen (`ConfigSchema`), Herkunftsnachweis (`effective-config.json`), strikte Trennung maschinenspezifischer Overrides (`*.local.json`).
   - Crash-resistentes atomares JSONL-Journaling (`RunJournal`, `RunResume`), `Quellstand/`-Code-Archivierung je Lauf, `ToolVersionProbe` (inkl. Erkennung der stillen OpenMPI-vs.-MPICH-Falle!).
   - Vertrags- und Hygiene-Tests (`HandbookCoverageTests`, `TemplateProjectTests`, `ScriptContractTests`, `RepositoryHygieneTests`).
2. **Echtes CFD-Handwerk in 2D und bei der 3D-Prismen-Grenzschicht:**
   - Deine 2D-Pipeline (`Planar2D` + `Gmsh2DMesher` + `SU2`) erzeugt saubere O-Typ-Quad-Grenzschichten mit Knickpunkterkennung und DetailBox und trifft **NASA TMR NACA 0012 ($Re = 6\cdot 10^6$)** bei $\alpha = 0^\circ$ auf **+0,1 %** (`CD 0.00820` vs `0.00819`) und bei $\alpha = 10^\circ$ auf **−3,0 % CD / −2,3 % CL**. Das ist echte, vorzeigbare Verifikation!
   - Dein zweistufiges 3D-Prismenverfahren (`BoundaryLayerWallField.cs` mit Normalenglättung, quadratischer Kipp-Prüfung, Strahl-Engstellen-Erkennung und **0 Pyramiden** im gesamten aktuellen 3D-Lauf) plus blockierungsfreier Windkanal ($\le 0,3\%$) und `Su2ConvergenceJudge` zeigen, dass du echte CFD-Probleme nicht weggeschminkt, sondern gelöst hast.
3. **Respektable Zero-Dependency-C#-Bayes-Engine:**
   - Matérn-5/2 mit ARD, analytischer LML-Gradient (per Finite-Differenzen getestet!), Schur-Komplement-Updates in $\mathcal{O}(n^2)$, Heteroskedastizität (`extraNoise`), Absturz-GP (`__Erfolg`), und vor allem der **`Metrics`-Modus**, der auf `Naca2D` (10×10, 3 Seeds) reproduzierbar deutlich besser war als RSM und Evolution (`65.9` vs. `54.7` vs. `48.5`).

---

### 4.2 Architektur & Code-Qualität: Wo es klemmt und unübersichtlich wird

| Befund | Konkreter Beleg im Code | Auswirkung |
| :--- | :--- | :--- |
| **4 God-Classes > 1.000 Zeilen** | `BayesOptimizationAlgorithm.cs` (1.354 L), `GmshCfdMesher.cs` (1.259 L), `Su2Solver.cs` (1.245 L), `MeshCalibration.cs` (1.061 L) + `Program.cs` (904 L) | Genau deshalb verlierst du den Überblick: Verantwortung für Mathe, Logging, Dateisystem, Prozesssteuerung und Auswertung steckt in gigantischen Dateien. |
| **Gebrochene `Core/`-Schichtengrenze** | `Core/Pipeline/MeshCalibration.cs:11` & `VoxelResolutionAdvisor.cs:5` importieren direkt `Solvers.Cfd`; `WorkflowController.cs:409` liest `.su2`-Zelltypen; `BBox2.cs` hat Namespace `Core` statt `Planar2D` | Die README verspricht „`Core/` kennt keine Solver“ — faktisch stimmt das nicht mehr. FEM oder ein zweiter CFD-Solver (z. B. OpenFOAM) knallt hier sofort gegen CFD/SU2-Annahmen. |
| **Konfigurations-Wildwuchs** | **127 Keys** in `_Vorlage3D` (über 4 JSONC-Dateien), **88 KB** `BEDIENHANDBUCH.md`, `simulation.json` mischt Solver-, PicoGK- und alle 3 Optimierer-Keys; doppelte Wahrheit zwischen `CreateDefaults()` und `project.json` | Erschlägt jeden externen Nutzer (und dich selbst). JSON mit `//`-Kommentaren bricht Standard-JSON-Validatoren außerhalb von .NET. |
| **Test-Illusion (Mocks statt Physik)** | ~800 Tests, aber **0 Tests mit `PicoGK`** und **0 echte `Gmsh`/`SU2`-Integrationstests** (14 Dateien nutzen `Moq` oder Fake-Skripte) | Ein Umbau kann alle 800 Tests grün lassen und trotzdem auf dem Server nach 10 Minuten krachen. |
| **Konkrete kleine Bugs / Hygiene-Lecks** | (1) `Program.cs:517`: 2D-Vorkalibrierung instanziiert `new Gmsh2DMesherOptions()` hart und ignoriert `solvers/gmsh.json`.<br>(2) `src/projects/MantaAuv/OptimierungsAlgoTestlaufBericht.pdf` (**2,9 MB**) wird bei jedem `sim.ps1`-Deploy mitgehasht und hochgeladen!<br>(3) Absolute Serverpfade (`/home/lpleissner/...`) in `MantaAuv`/`Naca2D` `simulation.json` eingecheckt.<br>(4) DE/EN-Sprachmix in Logs, CSV-Headern und Test-Assertions. | Erschwert Portabilität, verlangsamt Deploys, macht spätere Englisch-Portierung (Meilenstein 5) schmerzhaft. |

---

### 4.3 Physik, Geometrie & Numerik: Wo deine Ergebnisse heute an Grenzen stoßen

1. **Low-$Re$ ohne Transitionsmodell (fundamentaler Physik-Blinder-Fleck!):**
   - `MantaAuv` ($L = 50\text{–}100\text{ mm}$, $v = 2\text{ m/s}$ in Wasser → $Re_L \approx 1.0\text{–}2.6\cdot 10^5$, Flügel-Sehne sogar $Re_c \sim 10^4$) und `Naca2D` ($Re_c = 1.74\cdot 10^5$) liegen **mitten im laminar-transitionalen Bereich**.
   - Dein SU2-Setup fährt **vollturbulentes Spalart-Allmaras (`SA`)** ohne Transitionsmodell (mit SU2-Default 5 % Freistrom-Turbulenzintensität). Laminare Laufstrecken und laminare Ablöseblasen — die bei kleinen Drohnen/AUVs oft 30–60 % des realen Widerstands ausmachen — werden komplett wegmodelliert!
   - *Folge:* Auf `Naca2D` bei $Re = 6\cdot 10^6$ (voll turbulent, NASA TMR) ist dein Ergebnis top; bei $Re = 1.7\cdot 10^5$ darfst du deine absoluten `CD`/`CL`-Werte nicht 1:1 mit Windkanal- oder XFOIL-Daten mit freier Transition vergleichen.

2. **Stationäres SA bei massiver 3D-Ablösung (`MantaAuv`):**
   - Im aktuellen 3D-Lauf sind im Mittel **80,4 % des Widerstands Druckwiderstand (`CD_p`)** und nur 19,6 % Reibung (`CD_v`), weil `AddBeam` halbkugelige, stumpfe Enden erzeugt.
   - Stationäres RANS (`SA`) ist für abgelöste Nachläufe bekanntermaßen ungenau; URANS gibt es bisher nur als manuelles `--recheck --unsteady`, nicht im Optimierungs- oder Validierungsstandard.

3. **PicoGK + `StlSmoother`-Inkonsistenzen:**
   - `MantaGeometryGenerator.cs:82-107` berechnet `Volume` (aus Voxeln) und `FrontalArea` (`REF_AREA` für SU2!) **vor** `StlSmoother.SmoothStl`, während Gmsh/SU2 auf dem danach geschrumpften, geglätteten und anisotrop auf die Ausgangs-AABB zurückskalierten STL rechnen!
   - `AutoVoxelResolution` steht auf `false` → bei `Length = 100 mm` wächst die Voxelgröße auf `1,0 mm` (grob!).
   - Voxel + Marching Cubes liefern keine exakten scharfen Hinterkanten und keine $G^2$-stetigen aerodynamischen Flächen; bei spitzen Enden (`TailTaper = 0.106`) stürzt Gmsh `ClassifySurfaces` ab (`Gen1_Var5`), weil der geplante Winkel-Retry in Schritt 1 noch gar nicht implementiert ist.

4. **Nur externe Ein-Körper-Umströmung (`Wall` + `Farfield`), keine Symmetrieebene:**
   - Bei jedem symmetrischen 3D-Körper (wie `MantaAuv`) simulierst du beide Hälften komplett doppelt. Eine einzige **`MARKER_SYM`-Symmetrieebene** würde Zellzahl und Rechenzeit sofort halbieren!
   - Noch wichtiger: Interne Strömungen (`INLET` / `OUTLET` / `WALL` / mehrere Zonen) sind in `GmshCfdMesher` und `Su2ConfigGenerator` nicht vorgesehen — dabei sind gerade interne Geometrien die wahre Stärke von `PicoGK`.

5. **Einzelpunkt-Overfitting:**
   - `Naca2D` optimiert bei einer einzigen Geschwindigkeit und treibt `MaxCamber = 8.0` (Max), `MaxThickness = 6.0` (Min) und `AngleOfAttackDeg` an den Stall-Rand des RANS-Modells (wo schon im NASA-Benchmark bei $\alpha = 15^\circ$ der Fehler auf +39 % explodierte!). Ohne Mehrpunkt-Auswertung (z. B. Reiseflug + Steigflug + Off-Design) findet jeder Optimierer nur „Nadel-Optima“, die in der Realität sofort abreißen.

---

### 4.4 Optimierungs- & Surrogat-Mathematik: Konkrete Bugs & Grenzen

1. **Kein Geometrie-Vorfilter & kein Constrained EI:** Kostete im laufenden 3D-Lauf 14,2 h CFD in einer einzigen Generation.
2. **Bouncer-Falle an harten Strafen-Klippen (`ChampionValidator.cs:74-100`):** Der Bouncer rechnet `±1 %` Jitter auf die *bestrafte* Fitness. Sitzt der optimale Champion bei `Volume = 84.800` (knapp unter `85.000`), kippt `+1 %` Jitter das Volumen über die Grenze → Fitness bricht um 90 % ein → Bouncer disqualifiziert ausgerechnet das wahre Optimum!
3. **`PredictFitnessBand` unterschätzt Unsicherheit massiv:** Fehlendes `+ sn2` erklärt direkt die 30-%-Bandabdeckung im 3D-Lauf.
4. **Hyperparameter & Acquisition-Flaschenhals:**
   - `GaussianProcess.cs` optimiert Hyperparameter per Adam (`lr = 0.05` ohne Decay, ab `n > 150` nur noch 1 Warmstart!) statt L-BFGS-B + schwache Priors gegen ARD-Kollaps bei kleinem `n`.
   - `Metrics`-Modus filtert 5.000 Kandidaten mit nur **8 zufälligen Monte-Carlo-Draws** (`ScreenSamples = 8`) statt Quasi-Monte-Carlo (Sobol) + lokaler Random-Walk statt Gradient.
5. **Alte Optimierer aufräumen oder ehrlich benennen:**
   - `RsmOptimizationAlgorithm.cs` ist **kein RSM** (keine quadratische Regression), sondern einfaches IDW (`1/d³`), das mathematisch nicht über beobachtete Punkte hinaus extrapolieren kann.
   - `EvolutionaryAlgorithm.cs` verschenkt jede Runde Slot 1 an die Neuberechnung des unveränderten Parents und misst Ähnlichkeit im **unnormierten** Parameterraum (`Length` dominiert `TailTaper` quadratisch um Faktor 2.500!).
6. **Fehlende Kernfunktionen echter Ingenieurs-Optimierung:**
   - **Keine Multi-Objective-Optimierung (Pareto-Front):** Statt willkürlicher Exponenten (`Drag^3`) braucht ein Ingenieur die echte Abwägungs-Kurve (z. B. *Widerstand vs. Nutzvolumen vs. Spannung*).
   - **Kein Multi-Fidelity-BO:** Grobe Netze (5 min) und feine Netze (2 h) können in einem gemeinsamen Multi-Fidelity-Surrogat kombiniert werden, statt erst blind alles fein zu rechnen.

---

## 5. Die strategische Frage: Wo ist DEINE echte Nische?

Das ist der wichtigste Abschnitt dieses Berichts. Die Frage ist nicht *„Kann ich Ansys nachbauen?“* (Antwort: nein, und das will auch niemand), sondern: **Wo existiert eine echte Lücke, in der ein offenes, KI-gestütztes Projekt auf Basis deines Stacks echten wissenschaftlichen und praktischen Mehrwert liefert?**

### Was du NICHT tun solltest (Sackgassen)
- **Nicht** gegen `MACH-Aero` / `DAFoam` / `SU2-Adjoint` in glatter 3D-Flugzeugaerodynamik antreten (dort sind Voxel-STLs + Black-Box-BO physikalisch und rechnerisch unterlegen).
- **Nicht** noch 10.000 Zeilen eigene ML-Bibliothek in C# und 50 neue JSON-Schalter schreiben, bevor die Physik-Basis entschlackt und an echten Benchmarks validiert ist.
- **Nicht** sofort alles gleichzeitig bauen (LaTeX-Monolith + Englisch + FEM + URANS + LLM) — das vergrößert nur das Gefühl, den Überblick zu verlieren.

### Zwei realistische, hochinteressante Nischen für dein Projekt

#### Option A (Mein klarer Favorit für deine „Open-Source Noyron“-Vision):
### **„Open-Source Verifiable Computational Engineering Harness“ für 3D-druckbare interne Fluidik & Wärmetauscher (PicoGK + ShapeKernel + CFD/FEM + LLM-Code-Evolution)**

Warum genau diese Nische ein Volltreffer ist:
1. **PicoGK spielt endlich auf seinem Heimfeld:** Für glatte Flugzeugflügel sind Voxel ein Kompromiss. Für **komplexe interne 3D-Druck-Fluidik** — Strömungsverteiler (Manifolds), statische Mischer, Kühlkanäle, kompakte Wärmetauscher, Luftführungen, Düsen, Ventile — sind `PicoGK` + `LEAP71_ShapeKernel` **Weltklasse** (genau dafür hat LEAP 71 PicoGK gebaut!).
2. **Adjoint/FFD versagt dort, dein Ansatz glänzt:** Sobald sich die **Topologie** ändert (3 vs. 5 Kanäle, Rippenmuster, Verzweigungen, Gyroid-Infill), funktionieren klassische FFD-Adjoint-Pipelines nicht mehr. Hier brauchst du genau die Kombination aus **Code-generierter Voxel-Geometrie + Black-Box-BO + LLM-Code-Evolution**!
3. **Du löst das größte offene Problem der LLM-Engineering-Forschung (das „Reliability Gap“):** Projekte wie `Foam-Agent` oder `OpenFOAMGPT` zeigen 2025/2026: LLMs können zwar Code schreiben, aber sie schummeln (Reward Hacking) oder produzieren unkonvergierte, physikalisch falsche Simulationen. **Deine deterministische Verifikationsschicht** (`y+`-Grenzen, Konvergenzrichter, GCI, Bouncer, Hash-geschützte Zielfunktion, `Quellstand/`-Reproduzierbarkeit) ist genau der fehlende Baustein für eine **AlphaEvolve-/OpenEvolve-artige Engineering-Schleife**:
   - **Mensch** legt Anforderungen, Randbedingungen, Fertigungsgrenzen & unveränderliche Zielfunktion fest.
   - **Äußere Schleife (LLM-Agent / Code-Evolution)** mutiert **C#-`ShapeKernel`/`PicoGK`-Code** (strukturelle/topologische Sprünge: neue Geometriefamilien, echte kleine CEMs!).
   - **Innere Schleife (dein Framework)** macht Rauchtest (Sekunden) → Multi-Fidelity-Vorfilter → Bayes-/Pareto-Parameteroptimierung → harte CFD/FEM-Verifikation — und gibt dem LLM **strukturierte physikalische Diagnostik** zurück (*„Druckverlust entsteht zu 65 % im Einlaufkrümmer; ARD zeigt Parameter X wirkungslos; Parameter Y klebt am oberen Limit“*).
4. **Reale Nutzbarkeit & Validierbarkeit für dich als Student:** Einen 3D-gedruckten Strömungsverteiler, Statikmischer oder Luftkanal kannst du für 5 € auf einem normalen Resin-/FDM-Drucker drucken und mit einem einfachen Drucksensor/Durchflussmesser **wirklich im Experiment gegen deine CFD testen**! Genau diese Schleife *Code → Simulation → 3D-Druck → Messung* macht ein Projekt wissenschaftlich und für Arbeitgeber/Forschungsgruppen sofort elektrisierend.

#### Option B (Pragmatische, sofort nutzbare Alternative):
### **„Zero-Friction Open-Source Benchmark- & Optimierungs-Suite für Studenten-Teams & Hochschulen“**

Falls du lieber bei externer Umströmung (Profile, Drohnen, AUVs, Formula-Student-Flügel) bleiben willst:
- Schärfe die 2D-Pipeline (`Planar2D` + `SU2` + optional `XFOIL`/`NeuralFoil` als schneller Vorfilter) zu einem **rasend schnellen, hochpräzisen 2D-Multi-Point- & Pareto-Optimierer** (100 Varianten in Minuten statt Wochen!).
- Nutze für 3D **saubere parametrische CAD-Geometrie** (z. B. analytische Profile/Rümpfe via `ShapeKernel` oder STEP/OpenCASCADE/Gmsh direkt, statt grober Beam-Voxel), aktiviere Symmetrie-Randbedingungen und validiere an einem echten 3D-Standardbenchmark (**DARPA SUBOFF** für AUVs oder **ONERA M6 / NACA-Flügel**).
- Liefere ein 1-Befehl-Docker/Conda-Setup und einfache Presets statt 127 JSON-Keys — ideal für Formula Student, Akaflieg, Studenten-U-Boot-/Raketen-Teams und Lehre.

> **Mein Rat:** Option A ist langfristig **deutlich einzigartiger** (dort entsteht echtes Neuland rund um PicoGK + LLM-Code-Evolution). Viele der nächsten Schritte (Entschlackung, Physik-Fixes, echte Benchmarks, Multi-Objective, saubere Schnittstellen) zahlen aber auf **beide** Optionen ein!

---

## 6. Wie du das Verständnis zurückgewinnst (Gegen den „KI-Komplexitäts-Überhang“)

Du hast sehr ehrlich gesagt, dass dir dein eigenes Projekt über den Kopf gewachsen ist. Nimm das ernst — das ist heute das häufigste Problem bei KI-unterstützter Entwicklung: **Code entsteht 10× schneller als das eigene fachliche mentale Modell.**

Vier konkrete Spielregeln, mit denen du sofort wieder Herr über dein System wirst:

1. **Feature-Stopp vor dem Verstehen:** Baue erst wieder neue Großfeatures (FEM, LLM-Loop), wenn du die bestehenden 5–6 Bugs behoben und den Code spürbar *verkleinert* statt vergrößert hast.
2. **Statt 800 Mocks: 3 goldene Lehrbuch-Benchmarks, die du im Schlaf erklären kannst:**
   - *Benchmark 1 (2D extern):* NACA 0012 bei $Re = 6\cdot 10^6$ (NASA TMR) — hast du fast fertig!
   - *Benchmark 2 (3D extern oder intern):* Entweder **DARPA SUBOFF** (AUV-Standardkörper mit publiziertem $C_D$) ODER **90°-Rohrkrümmer / Diffusor** (publizierter Druckverlustbeiwert $\zeta$ nach Idelchik/Miller — in < 5 Min gerechnet!).
   - *Benchmark 3 (Optimierung):* Ein kleines analytisches/2D-Problem mit bekanntem Pareto-Tradeoff, wo du jeden Schritt von Bayes/Constraints nachvollziehen kannst.
3. **Verstehe die „Ingenieurs-Checkliste“, nicht jede Codezeile:** Du musst keine Cholesky-Matrix im Kopf invertieren können. Was du *selbst* sicher beurteilen können solltest:
   - *CFD-Realitätscheck:* Stimmen $Re$, Regime (laminar/transitional/turbulent), $y^+$, Blockage, GCI, Konvergenz und Randbedingungen?
   - *Problemformulierung:* Sind Zielfunktion und Nebenbedingungen dimensionssauber, physikalisch sinnvoll und nicht „hackbar“?
   - *Methodenwahl:* Wann reicht 2D/Low-Fidelity, wann braucht es 3D-RANS, wann Bayes, wann Adjoint?
4. **Nutze KI als Lehrer & kritischen Gutachter, nicht nur als Code-Fabrik:** Lass dir vor jedem neuen Modul erst erklären, *welchen physikalischen Fehler* es behebt und *warum eine einfachere Lösung nicht reicht*. Jede neue Option in `simulation.json` muss sich rechtfertigen!

---

## 7. Konkreter Maßnahmenplan: So holst du das Projekt auf das nächste Level

### Phase 1: Sofort-Reparaturen & Quick Wins (1–2 Wochen — maximaler Hebel!)
- [ ] **Laufenden `MantaAuv`-Lauf bewusst entscheiden:** Nach Iter 5 sauber auswerten statt blind weitere ~12 Tage in die 4/5-Grenzkontakt-Zielfunktion zu investieren.
- [ ] **Die 6 gefundenen Bugs gezielt fixen:**
  1. `BayesOptimizationAlgorithm.cs:1147-1185`: `sn2` (und `extraNoise`) in `PredictFitnessBand` addieren → realistische 95-%-Bänder.
  2. `CalibrationEvaluator.cs:82-131`: Bei nicht-asymptotischer Verfeinerung (`ratio <= 1` oder Vorzeichenwechsel) explizit als „nicht konvergiert / kein gültiger GCI“ markieren statt `Math.Abs` + Clamp auf `p = 0.50`.
  3. `MantaGeometryGenerator.cs:82-107`: `Volume` und `FrontalArea` (`REF_AREA`) **nach** `StlSmoother.SmoothStl` messen (nicht davor!).
  4. `WorkflowController.cs:434`: `CellLimit` (`MaxCells3D`) auch in der Optimierung respektieren (oder warnen/adaptiv begrenzen) + `GmshCfdMesher` Schritt-1-Winkel-Retry bei `Wrong topology` umsetzen.
  5. `Program.cs:517`: 2D-Vorkalibrierung liest `solvers/gmsh.json` statt `new Gmsh2DMesherOptions()`.
  6. Repo-Hygiene: `src/projects/MantaAuv/OptimierungsAlgoTestlaufBericht.pdf` (2,9 MB) aus `src/projects/` entfernen (z. B. nach `docs/` oder aus Git), und absolute `/home/lpleissner/...`-Pfade aus den eingecheckten `simulation.json`-Dateien in `*.local.json` verschieben.
- [ ] **Geometrie-Vorfilter vor CFD einführen (`IGeometryValidator` / `PreSolveCheck`):** Prüfe billige geometrische Nebenbedingungen (`MinVolume <= Volume <= MaxVolume`, Mindestwandstärke, Wasserdichtheit) **vor** Gmsh/SU2 in 1 Sekunde statt 5 Stunden CFD zu verbrennen! Und entkopple den Bouncer von harten Straf-Klippen.
- [ ] **SU2-Abbruchkriterium praxistauglich machen:** Statt bei Rest-Oszillation von `1.5e-4` 4.500 Iterationen lang auf `CONV_CAUCHY_EPS = 1e-5` zu warten, realistisches Cauchy-Fenster / relative Abweichung prüfen → spart in 3D sofort **50–70 % Rechenzeit** ohne Genauigkeitsverlust.
- [ ] **Physikalisch saubere `MantaAuv`-Zielfunktion:** Umstellen auf physikalische Widerstandskraft $F_D$ bzw. volumetrischen Widerstandsbeiwert $C_{D,V} = C_D \cdot A_{\text{front}} / V^{2/3}$ (oder feste Nutzvolumen-Nebenbedingung + Minimierung von $F_D$).

### Phase 2: Architektur-Entschlackung & Vertrauensbasis (2–4 Wochen)
- [ ] **Konfigurations-Diät (von 127 Keys auf ~15 sichtbare Projekt-Keys):**
  - Führe 3 kuratierte **Solver/Mesh-Presets** ein (`FastConcept`, `StandardRans`, `VerificationFine`). Ein neues Projekt wählt nur `"Preset": "StandardRans"`, die 5 Parameter, die Geometrie und die Zielfunktion. Experten können weiterhin gezielt überschreiben, aber die Vorlagen schrumpfen um 80 %!
  - Schaffe die doppelte Pflege zwischen `CreateDefaults()` (C#) und `project.json` ab (eine einzige Quelle der Wahrheit!).
  - Entferne veraltetes Schein-`RSM` (IDW) und ungenutzte `Evolution`-Keys aus den Standard-JSONs (oder lagere sie sauber aus).
- [ ] **`Core/` wirklich sauber machen + God-Classes zerlegen:**
  - Die 2 `using OptimierungsAlgoCFD.Solvers.Cfd`-Leaks in `MeshCalibration.cs` und `VoxelResolutionAdvisor.cs` über Interfaces (`IMeshCalibrationAdapter`, `IGeometryResolutionAdvisor`) entfernen — plus einen automatischen **Architektur-Unit-Test**, der künftig verhindert, dass `Core/` jemals wieder `Solvers/`, `Kernels/` oder `Projects/` importiert!
  - `BayesOptimizationAlgorithm` (1.354 L) aufteilen in: *Surrogate-Manager*, *Acquisition-Optimierer*, *Batch-Generator*, *Diagnostics/CSV-Reporter*. Analog für `GmshCfdMesher` und `Su2Solver` (gemeinsame 2D/3D-Gmsh-Basis extrahieren).
- [ ] **Echte Integrations- & Physik-Benchmark-Suite (3 kleine, echte Läufe):**
  - 1 schneller `PicoGK`-Geometrie-Test (`[Trait("Category", "Integration")]`),
  - 1 schneller 2D-`Gmsh + SU2`-Durchlauf,
  - 1 echter **3D-Literatur-Benchmark** (z. B. **DARPA SUBOFF** oder **Rohrkrümmer/Diffusor**), um deine 3D-CFD-Genauigkeit erstmals schwarz auf weiß gegen reale Messdaten zu beweisen!
- [ ] **Symmetrie-Randbedingung (`MARKER_SYM`)** für symmetrische 3D-Körper einbauen → halbiert 3D-Zellzahl und Rechenzeit.

### Phase 3: Vom „Optimierer-Skript“ zum echten Engineering-Werkzeug (1–3 Monate)
- [ ] **Entscheidung für die Haupt-Domäne treffen (Option A vs. Option B):**
  - *Bei Option A (Empfehlung — Open-Source Noyron / interne Fluidik):*
    1. **`LEAP71_ShapeKernel`** (Apache 2.0) sauber als 3D-Geometrie-Baukasten anbinden → sofort professionelle Rohre, Spline-Körper, Flansche, Wandstärken, Lattices statt `AddBeam`-Workarounds.
    2. **Multi-Zone-Randbedingungen (`INLET`, `OUTLET`, `WALL`, `SYM`)** in Gmsh & SU2 unterstützen → interne Kanäle, Verteiler, Diffusoren, Wärmetauscher werden optimierbar!
    3. **Optional `OpenFOAM`-Stage** neben SU2 prüfen (für interne inkompressible Strömungen, Wärmeaustausch `chtMultiRegion` und komplexe Voxel-Geometrien via `snappyHexMesh`/`cfMesh` ist OpenFOAM in der Praxis oft noch robuster als Gmsh-Tetraeder+Prismen!).
  - *Für beide Optionen entscheidend:*
    1. **Multi-Point-Evaluation** (z. B. 2–3 Betriebspunkte pro Variante) gegen Einzelpunkt-Overfitting.
    2. **Multi-Objective-Optimierung (Pareto-Front) + Constrained EI + Multi-Fidelity:** Überlege ehrlich, ob du dein `R3`-Dogma („kein Python zur Laufzeit“) für den Optimierer lockerst: Über eine saubere JSON/STDIO-Schnittstelle (`IExternalOptimizer`) könntest du in 200 Zeilen Code sofort **BoTorch / Ax (`qNEHVI`, Constrained EI, Multi-Fidelity)** oder **pymoo** anbinden — dein schlanker C#-GP bliebe der zero-dependency Standard, aber für schwere 3D-Probleme hättest du sofort Weltklasse-Algorithmen!
    3. **FEM (`CalculiX`) erst nach sauberer Multi-Stage-Entkopplung** (Lücken 1–6 aus `.plans/fem_ausblick.md`), und direkt an einem Problem, wo Struktur + Strömung echt gekoppelt sind (dünnwandiger Flügel oder druckbelasteter Kanal).

### Phase 4: Die große Vision — „Open-Source Noyron + AlphaEvolve“ (3–6+ Monate)
Wenn Phasen 1–3 stehen, hast du etwas, das es in der Open-Source-Welt noch nicht gibt: **ein physikalisch verifiziertes, reproduzierbares, crash-sicheres Computational-Engineering-Harness.** Darauf setzt du dann deine eigentliche Traum-Schicht:
1. **Dein Framework als MCP-Server / CLI-API bereitstellen:** Ein KI-Agent kann über klare, deterministische Befehle aufrufen: `smoke-test`, `run-quick-eval`, `run-bayes`, `inspect-diagnostics` (Druck-/Reibungsanteile, Ablösezonen, ARD-Sensitivitäten, Grenzkontakt, GCI, `y+`).
2. **Äußere LLM-Code-Evolution-Schleife (inspiriert von `OpenEvolve` / deiner `llm_geometrie_bewertung.md`):**
   - Das LLM ändert *nicht* blind einzelne Floats (dafür ist Bayes viel besser), sondern **entwickelt neue parametrische `ShapeKernel`-Generatoren** (neue Kanal-Topologien, Leitbleche, Querschnittsverläufe, Fertigungsregeln).
   - **Harte deterministische Prüftore** (Compiler + Geometrie-Rauchtest + unveränderliche SHA-gehashte Zielfunktion + CFD/FEM-Verifikation) verhindern Halluzinationen und Reward Hacking.
3. **Der ultimative Beweis („Show, don't tell“):** Ein einzelnes, knallhart validiertes Open-Source-Showcase-Bauteil (z. B. ein KI-entwickelter 3D-gedruckter Strömungsverteiler oder Kompakt-Wärmetauscher), bei dem du auf GitHub zeigst: *Baseline-CAD → LLM-generierter `ShapeKernel`-Code → Bayes-Pareto-Optimierung → GCI-verifizierte CFD → 3D-Druck → reale Messung am Prüfstand!* Ein einziges solches Projekt schlägt 100 Seiten Theorie und öffnet dir in Wissenschaft und Industrie jede Tür.

---

## 8. Ein letztes ehrliches Wort

Du hast in den letzten Monaten etwas gebaut, woran die allermeisten Studierenden (und viele professionelle Entwickler) schon beim dritten Gmsh-Absturz oder der OpenMPI-Falle aufgegeben hätten. Dass du jetzt merkst, dass reine Feature-Masse noch keine wissenschaftliche Tiefe ersetzt, ist **kein Zeichen des Scheiterns — es ist exakt der Moment, an dem aus einem Bastelprojekt ein ernsthaftes Ingenieurswerkzeug wird.**

Wenn du magst, lass uns als Nächstes einfach kurz besprechen:
1. **Welche Richtung reizt dich fachlich wirklich mehr?** Eher **Option A** (3D-Druck, `PicoGK` + `ShapeKernel`, interne Fluidik/Wärmetauscher + später LLM-Code-Evolution à la Noyron/OpenEvolve) oder **Option B** (Aerodynamik/Hydrodynamik, Profile, Drohnen/AUVs)?
2. **Was machen wir mit dem laufenden `MantaAuv`-Lauf auf `BIC_12`** und deinem geplanten Bericht (Meilenstein 3)?
3. **Wollen wir zuerst die Phase-1-Entschlackung & Bugfixes** (kleines, klares Paket, das dein Projekt sofort 3–5× schneller und physikalisch sauberer macht) gemeinsam planen?
