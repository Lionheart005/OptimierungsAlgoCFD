# Entwicklungs-Roadmap (Post-Merge `MajorPhysikFixes`)

> **Status:** Aktiv — Stand: 07.11.2026  
> **Basis:** Konsolidierte Anforderungen aus `.plans/Anmerkungen.txt` (Z. 30–61) sowie den Detailplänen `.plans/ki_optimierung.md` und `.plans/fem_ausblick.md`.  
> **Ausgangslage:** Erfolgreicher Merge von `MajorPhysikFixes` in `main`. Alle 557 Unit-Tests grün, stabiles 2D/3D-Netzfundament mit Wandauflösung ($y^+ \approx 1$), adaptiver Kalibrierung, Nachlauf-Detailbox, scharfem Hinterkantenschutz (B14) und robustem Champion-Validator (KP-2).

---

## Übersicht der Meilensteine

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 1: 2D-Referenzlauf & Python-Auswerte-Vorlagen (scripts/)        │
│ ├── AP 1.1: Generische Python-Auswerteskripte (History, Opt, Calibration)   │
│ └── AP 1.2: Kompakter 5×5-Testlauf Naca2D mit Auto-Kalibrierung & Detailbox │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 2: KI- & Machine-Learning-Integration (ki_optimierung.md)       │
│ ├── AP 2.1: Datenhygiene & Bouncer-Markierung (TODO-53)                     │
│ ├── AP 2.2: Gauß-Prozess-Surrogatkern & Cholesky-Numerik (TODO-54)          │
│ ├── AP 2.3: Bayes-Optimierer mit Expected Improvement (TODO-55)             │
│ ├── AP 2.4: Mehrziel-Kennwert-Modellierung "Metrics" (TODO-56)              │
│ ├── AP 2.5: Vertrauensbereiche & Lernmengen-Deckel TuRBO (TODO-57)          │
│ ├── AP 2.6: Transparenz, Treffsicherheit & Verlauf-CSV (TODO-58)            │
│ ├── AP 2.7: 5×5-Vergleichslauf (RSM vs. Evolution vs. Bayes, TODO-60)       │
│ └── AP 2.8: Architektur-Bewertung für LLM-Geometriegenerierung              │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 3: Große LaTeX-Gesamtdokumentation & Showcase                   │
│ ├── AP 3.1: Wissenschaftliche Methodik & Gitterverifikation (GCI, y+)       │
│ ├── AP 3.2: 2D-Showcase (Naca2D: Polaren, Druckverteilungen, Profilform)    │
│ ├── AP 3.3: 3D-Showcase (MantaAuv: Druck/Reibung, Wirbel, ParaView-Szenen) │
│ └── AP 3.4: Algorithmen-Benchmark (Evolution vs. RSM vs. Bayes-KI)          │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 4: FEM-Integration & Fluid-Struktur-Kopplung (fem_ausblick.md)  │
│ ├── AP 4.1: Multi-Stage-Konfiguration ("Stages": ["Cfd", "Fem"])            │
│ ├── AP 4.2: CalculiX (ccx) Solver-Anbindung & Gmsh-Tetraeder-Export         │
│ ├── AP 4.3: Drucklast-Transfer & Randbedingungs-Zonen                       │
│ └── AP 4.4: Kombinierte Festigkeits- & Strömungs-Fitness                    │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Meilenstein 1: 2D-Referenzlauf & Python-Auswerte-Vorlagen (`scripts/`)

### Ziel
Schaffung eines standardisierten, universellen Post-Processing-Toolings in Python sowie Durchführung eines sauberen 2D-Referenzlaufs (`Naca2D`), um das neue Vernetzungs- und Kalibrierungsfundament vollständig zu verifizieren und für Dokumentationen aufzubereiten.

### Arbeitspakete

#### AP 1.1: Universelle Python-Auswerteskripte in `scripts/`
Die Skripte sollen projektunabhängig funktionieren, d. h. beliebige Spalten aus `Simulation_Results.csv`, `calibration.csv` und `history.csv` dynamisch erkennen, ohne auf feste Parameternamen (wie früher in `Plot_RSM.py`) angewiesen zu sein.

1. **`scripts/plot_history.py` (Residuen- & Konvergenzverlauf einzelner SU2-Rechnungen):**
   - *Status:* Grundgerüst vorhanden (Modes A, B, C).
   - *Erweiterungen:*
     - Robuste automatische Erkennung der Residuen-Spalten (z. B. `rms[Rho]`, `rms[P]`, `rms[Nu]`, `rms[Tke]`).
     - Zweite y-Achse für Kraftbeiwerte ($C_d$, $C_l$, $C_{my}$) über Iterationen.
     - Visualisierung des Cauchy-Konvergenzfensters (`CONV_CAUCHY_ELEMS` und $\varepsilon$).
     - Export als Vektor-Grafik (`.svg` und `.pdf`) für Publikationen sowie hochauflösendes `.png`.

2. **`scripts/plot_optimization.py` (NEU: Optimierungs- und Evolutionsverlauf):**
   - *Eingabe:* `Simulation_Results.csv` eines beliebigen Projekts.
   - *Funktionen:*
     - **Fitness-Trend:** Streudiagramm aller Varianten je Generation mit Hervorhebung des jeweiligen Generationen-Champions und der "Best-so-Far"-Kurve.
     - **Parameter-Evolution:** Boxplots oder Violin-Plots für jeden freien Entwurfsparameter über die Generationen hinweg, um Konvergenz oder Drift der Parameter zu visualisieren.
     - **Beiwerte-Entwicklung:** $C_d$- und $C_l$-Verlauf über alle Varianten hinweg.
     - **Pareto-Front:** 2D-Scatter ($C_d$ vs. $C_l$ oder Drag vs. Volume) mit Farbkodierung nach Generation.
     - **Champion-Vergleich:** Balkendiagramm der relativen Parameter-Änderungen des Gesamtsiegers gegenüber dem Basisentwurf (Gen 1, Var 1).

3. **`scripts/plot_calibration.py` (NEU: Gitterkonvergenz & GCI-Plot):**
   - *Eingabe:* `calibration.csv` (erzeugt von `CfdMeshCalibrator`).
   - *Funktionen:*
     - Plot der aerodynamischen Kennwerte ($C_d$, $C_l$) über der Element-/Knotenanzahl $N$ bzw. dem Verfeinerungsfaktor $h$.
     - Richardson-Extrapolation: Darstellung des asymptotischen Grenzwerts $f_{h \to 0}$ mit Fehlerbalken nach Roache (Grid Convergence Index, GCI).
     - Plot des mittleren und maximalen $y^+$-Werts über den Verfeinerungsstufen.
     - Kennzeichnung des automatisch gewählten Betriebsgitters (`Selected = true`).

#### AP 1.2: Kompakter 5×5-Testlauf in 2D (`Naca2D`)
- **Konfiguration:**
  - 5 Generationen × 5 Varianten (insgesamt 25 Varianten + Kalibrierung).
  - Algorithmus: `Evolution` (oder `Rsm`).
  - Aktive automatische Gitterkalibrierung (3–5 Stufen, $y^+ \le 1$, Quad-Grenzschicht).
  - Nachlauf-Detailbox aktiv (`WakeBox = true`).
  - Parametrisierung: Dickenrücklage, Wölbungsrücklage, Anstellwinkel (`AngleOfAttackDeg`).
- **Verifikation:**
  - Prüfen der Profilkontur-Generierung in `Planar2D` (Sicherstellung, dass Ober- und Unterseite exakt geschlossen und knickfrei sind).
  - Validierung des Knickpunkt-Schutzes an der scharfen Hinterkante (B14).
- **Kurzbericht:**
  - Generierung aller Plots mit den neuen Python-Skripten.
  - Dokumentation von Rechenzeiten, Gitterkonvergenz und erzielter $C_l/C_d$-Gleitzahl-Verbesserung.

---

## Meilenstein 2: KI- & Machine-Learning-Integration (`ki_optimierung.md`)

### Ziel
Erhöhung der Optimierungseffizienz durch Ersatzmodellierung (Surrogate Modeling) und Bayes-Optimierung (Active Learning). Anstelle blind-heuristischer Suchschritte leitet ein Gauß-Prozess (GP) mit Expected Improvement (EI) die Auswahl der nächsten CFD-Varianten.

### Detail-Fahrplan (Block G–I aus `ki_optimierung.md`)

```text
TODO-53 (Datenhygiene) ──► TODO-54 (Gauß-Prozess) ──► TODO-55 (Bayes-Algorithmus)
                                                             │
┌────────────────────────────────────────────────────────────┴────────────────────────────────┐
▼                                                                                            ▼
TODO-56 (Mehrziel/Metrics)                                                       TODO-58 (Transparenz & Log)
│                                                                                            │
└────────────────────────────┬───────────────────────────────────────────────────────────────┘
                             ▼
               TODO-57 (TuRBO-Vertrauensbereich)
                             │
                             ▼
               TODO-59 (Bedienhandbuch & Tests)
                             │
                             ▼
               TODO-60 (5×5-Vergleichslauf Server)
```

#### Arbeitspakete im Detail
1. **TODO-53: Bouncer-Markierung & Datenhygiene:**
   - Markierung unphysikalischer oder abgestürzter Varianten mit `Disqualified = true`.
   - Ausschluss disqualifizierter Varianten aus dem Training des Ersatzmodells, um Verzerrungen zu vermeiden.
2. **TODO-54: Kern des Gauß-Prozesses (`Core/Surrogates/`):**
   - Implementierung eines parameterfreien Gauß-Prozesses in C# (keine externen Python-Abhängigkeiten zur Laufzeit nötig).
   - Kovarianzfunktion (Matern 5/2 oder Squared Exponential mit automatischer Relevanzbestimmung / ARD).
   - Robuste Cholesky-Zerlegung mit Jitter-Leiter gegen numerische Singularitäten.
3. **TODO-55: `BayesOptimizationAlgorithm` (`IOptimizationAlgorithm`):**
   - Registrierung von `"Bayes"` in `OptimizationAlgorithmFactory`.
   - Initiales DoE (z. B. Latin Hypercube Sampling).
   - Infill-Kriterium: Expected Improvement (EI) über Monte-Carlo-Sampling oder Kriging Believer für synchrone Variantenblöcke.
   - Dedizierter "Erkunder"-Slot für globale Exploration.
4. **TODO-56: Mehrziel- & Physik-Ebene (Modus "Metrics"):**
   - Getrennte Gauß-Prozesse für einzelne Kennwerte ($C_d$, $C_l$, $C_{my}$, Masse).
   - Verrechnung der Fitness auf den vorhergesagten physikalischen Größen anstelle eines einzigen, hochgradig nichtlinearen Fitness-Surrogats.
5. **TODO-57: TuRBO-Vertrauensbereich & Lernmengen-Deckel:**
   - Vertrauensbereichs-Steuerung für hochdimensionale Probleme ($d \ge 10$).
   - Dynamisches Anpassen der Kantenlänge $L$ (Halbieren bei Misserfolgen, Verdoppeln bei Erfolgen, Neustart bei Kollaps).
   - Deckelung der Trainingsmenge (`MaxTrainingPoints`), um kubischen Rechenzeit-Anstieg $\mathcal{O}(n^3)$ zu begrenzen.
6. **TODO-58: Transparenz & Reporting (`Bayes_Verlauf.csv`):**
   - Ausgabe von Vorhersage vs. tatsächlichem CFD-Ergebnis (Median & 95-%-Konfidenzband).
   - Bestimmung der Parameterwichtigkeit aus den Längenskalen $\ell_i$.
   - Schreiben von `Analyseergebnisse/Bayes_Verlauf.csv`.
7. **TODO-59 & 60: Validierung & Benchmark:**
   - Handbuch-Aktualisierung (`BEDIENHANDBUCH.md`).
   - 5×5-Vergleichslauf auf `Naca2D` unter identischem Random-Seed:
     - Lauf 1: `RSM` (Response Surface Methodology)
     - Lauf 2: `Evolution` (Evolutionsstrategie)
     - Lauf 3: `Bayes` (Bayesian Optimization)
   - Quantitativer Vergleich von Konvergenzgeschwindigkeit, bester Fitness und Berechnungsaufwand.

#### AP 2.8: Architektur-Bewertung für LLM-gestützte Geometriegenerierung
*Hintergrund (Anmerkungen.txt Z. 36):* Frage nach der Machbarkeit von Physics-Informed LLMs zur Code-Synthese auf `Planar2D` / `PicoGK` (Leap71/Nyron-Workflow).
- **Architektur-Konzept:**
  - Trennung zwischen algorithmischer Formfindung (SDF-Code) und deterministischer Validierung.
  - Einsatz eines LLM-Agenten als "Geometrie-Entwickler", der C#-Klassen erzeugt, welche `Planar2D` oder `PicoGK.Voxels` aufrufen.
  - Sandboxed Roslyn-Compiler (`Microsoft.CodeAnalysis.CSharp`), der dynamisch compiliert und geometrische Integritätsprüfungen durchführt (Wasserdichtigkeit, Mindestwandstärke, Krümmung).
  - Einordnung im Vergleich zu klassischer parametrischer Optimierung: Für topologische Neukonzeptionen hochattraktiv, für numerische Feinoptimierung ist der Bayes-Ansatz überlegen.

---

## Meilenstein 3: Große LaTeX-Gesamtdokumentation & Showcase

### Ziel
Erstellung einer umfassenden, wissenschaftlich exakten und optisch herausragenden Gesamtdokumentation im LaTeX-Format, die das gesamte Potenzial des Frameworks anhand der 2D- und 3D-Realläufe demonstriert. (Einmal zeigen was das gute Stück alles kann.)

### Gliederungsstruktur des Dokuments

1. **Einleitung & Systemarchitektur:**
   - Vision: Automatisierte, voxel- und profilbasierte Strömungsoptimierung ohne manuelle CAD-Interaktion.
   - Architekturübersicht: Entkoppelte Schichten (`Core`, `Composition`, `Solvers`, `Projects`), robuster Workflow-Controller, Journaling und Wiederaufnahmefähigkeit.
2. **Numerische Methodik & Gitterunabhängigkeit:**
   - Wandaufgelöste Grenzschichtnetze ($y^+ \approx 1$) in 2D (Quad-Layer mit Knickkantenschutz) und 3D (Prismen-Layer mit Glättung).
   - Nachlauf-Detailbox zur Erfassung von Wirbelnachläufen und Scherschichten.
   - Systematische Gitterverifikation nach ASME V&V 20 / Roache (GCI-Methode, Richardson-Extrapolation).
3. **Optimierungsalgorithmen im direkten Vergleich:**
   - Mathematische Formulierung: Response Surface Methodology (RSM/IDW) vs. Evolutionäre Strategie (CMA-ES/Mutation-Selection) vs. Bayesian Optimization (GP-EI).
   - Benchmark-Ergebnisse aus dem identisch gesedeten 2D-Vergleichslauf.
4. **2D-Showcase: Aerodynamische Profiloptimierung (`Naca2D`):**
   - Transsonische / inkompressible Umströmung, Anstellwinkel-Optimierung.
   - Druckverteilungen ($C_p$-Plots über der Sehne), Grenzschichtprofile und Strömungsabrissverhalten.
   - Entwicklung der Profilgeometrie von Generation 1 bis zum validierten Champion.
   - Vergleich der Ergebnisse mit NASA Werten und Physikalische akuratess, und ob Grobe optimierung am ende beim Champion auch tatsächlich das beste Modell ausspuckt wenn man es detailiert testet
5. **3D-Showcase: Hydrodynamische AUV-Optimierung (`MantaAuv`):**
   - 3D-Geometrieerzeugung über Voxelmorphologie (`PicoGK`).
   - Druck- und Wandschubspannungsverteilungen auf dem Rumpf.
   - ParaView-Visualisierungen: Stromlinien, Wirbelstrukturen ($Q$-Kriterium), Nachlaufschleppe in der Detailbox.
   - Validierung der Champion-Varianten mit verfeinerten Gittern und Jitter-Tests.
6. **Schlussfolgerungen & Ausblick:**
   - Erzielte Widerstandsreduktion und hydrodynamische Effizienz.
   - Roadmap zur FEM-Kopplung.

### Benötigte Assets & Abbildungen
- Vektorgrafiken (`.pdf`) aus `plot_history.py`, `plot_optimization.py` und `plot_calibration.py`.
- Hochauflösende ParaView-Renderings aus den vorhandenen `.vtu`-Dateien (Oberflächen-Druckfelder, Schnittebenen im Strömungsfeld, 3D-Wirbelkerne).
- Geometrievergleiche (Überlagerung von Ausgangs- und Optimalgeometrie).

---

## Meilenstein 4: FEM-Integration & Fluid-Struktur-Kopplung (`fem_ausblick.md`)

### Ziel
Erweiterung des Frameworks zu einer echten multiphysikalischen Optimierungsplattform durch Integration von linearer und nichtlinearer Strukturmechanik (Spannungen, Verformungen, Eigenfrequenzen).

### Geplante Arbeitspakete

1. **AP 4.1: Konfigurationsgestützte Multi-Stage-Pipeline:**
   - Erweiterung von `simulation.json` um `"Stages": ["Cfd", "Fem"]` (oder `"Stages": ["Fem", "Cfd"]` als billiger Vorfilter).
   - Dynamische Instanziierung der Stages in `CfdProjectDefinition` / `WorkflowController`.
2. **AP 4.2: CalculiX (`ccx`) Anbindung:**
   - Wahl des Solvers: CalculiX (`ccx`), quelloffen, über conda-forge/Pfade portabel verfügbar.
   - Netzgenerierung über Gmsh: Automatischer Export von quadratischen Volumentetraedern (C3D10) im `.inp`-Format.
   - Parsing der `.dat`- und `.frd`-Ergebnisdateien in C#.
3. **AP 4.3: Kennwert- und Lasttransfer:**
   - Erweiterung von `ModelRecord.Artifacts` zur Übergabe von Flächendruckfeldern aus SU2 an das FEM-Netz.
   - Struktur-Kennwerte: `Fem_MaxVonMises`, `Fem_MaxDisplacement`, `Fem_FirstEigenfrequency`, `Fem_Mass`.
4. **AP 4.4: Multidisziplinäre Fitness & Geometrie-Randbedingungen:**
   - Formulierung kombinierter Zielfunktionen (z. B. minimaler Strömungswiderstand bei Einhaltung einer maximal zulässigen Von-Mises-Vergleichsspannung und Mindest-Eigenfrequenz).
   - Generatorkonzept für Randbedingungs-Zonen (Einspann- und Lastflächen wandern parametrisch mit der Geometrie mit).

---

## Priorisierte Umsetzungsreihenfolge

| Schritt | Paket | Beschreibung | Primäres Ziel / Nutzen |
| :--- | :--- | :--- | :--- |
| **1** | **AP 1.1** | Python-Auswerteskripte (`scripts/`) fertigstellen | Robuste Auswertungsbasis für alle künftigen Läufe |
| **2** | **AP 1.2** | 2D-Referenzlauf `Naca2D` (5×5) auf Server BIC_12 | Validierung der neuen Physik-/Netz-Features in 2D |
| **3** | **AP 2.1–2.7** | KI-Bayes-Optimierer (Block G–I, TODO-53 bis 60) | Dramatische Beschleunigung der Konvergenz |
| **4** | **AP 2.8** | 5×5-Vergleichslauf (RSM vs. Evolution vs. Bayes) | Quantitativer Beweis der KI-Überlegenheit |
| **5** | **AP 3.1–3.4** | Große LaTeX-Gesamtdokumentation & Showcase | Hochwertige wissenschaftliche Präsentation der Ergebnisse |
| **6** | **AP 4.1–4.4** | CalculiX-FEM-Integration | Multiphysik (Fluid-Struktur-Kopplung) |
