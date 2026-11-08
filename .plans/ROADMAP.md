# Entwicklungs-Roadmap (Post-Merge `MajorPhysikFixes`)

> **Status:** Aktiv — Stand: 08.11.2026  
> **Basis:** Konsolidierte Anforderungen aus `.plans/Anmerkungen.txt` (Z. 30–61) sowie den Detailplänen `.plans/ki_optimierung.md`, `.plans/vergleich_optimierer.md`, `.plans/llm_geometrie_bewertung.md` und `.plans/fem_ausblick.md`.  
> **Ausgangslage:** Meilenstein 1 (Python-Auswerteskripte, Log-Redesign & 10×10 `Naca2D`-Referenzlauf) ist abgeschlossen und archiviert (`.plans/.old/ms1_auswertung_und_logdesign.md`). **Meilenstein 2 (KI-Integration) ist auf Branch `KI_Integration` umgesetzt:** 637 Unit-Tests grün, Server-Vergleichslauf ausgewertet (`vergleich_optimierer.md`).

---

## Übersicht der Meilensteine

```text
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 2: KI- & Machine-Learning-Integration  ✔ UMGESETZT (08.11.2026) │
│ ├── AP 2.1: Datenhygiene & Bouncer-Markierung (TODO-53)                  [x] │
│ ├── AP 2.2: Gauß-Prozess-Surrogatkern & Cholesky-Numerik (TODO-54)       [x] │
│ ├── AP 2.3: Bayes-Optimierer mit Expected Improvement (TODO-55)          [x] │
│ ├── AP 2.4: Mehrziel-Kennwert-Modellierung "Metrics" (TODO-56)           [x] │
│ ├── AP 2.5: Vertrauensbereiche & Lernmengen-Deckel TuRBO (TODO-57)       [x] │
│ ├── AP 2.6: Transparenz, Treffsicherheit & Verlauf-CSV (TODO-58)         [x] │
│ ├── AP 2.7: 5×5-Vergleichslauf (RSM vs. Evolution vs. Bayes, TODO-60)    [x] │
│ └── AP 2.8: Architektur-Bewertung für LLM-Geometriegenerierung           [x] │
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

## Meilenstein 2: KI- & Machine-Learning-Integration (`ki_optimierung.md`) — ✔ umgesetzt

> **Ergebnis (08.11.2026):** `"OptimizationAlgorithm": "Bayes"` ist implementiert (C#, ohne neue Abhängigkeit) und getestet
> (637/637 Tests grün, 0 Warnungen). Vergleichslauf auf dem Server (`Naca2D`, 5×5, gleicher Seed): siehe
> **[`vergleich_optimierer.md`](vergleich_optimierer.md)** (Ergebnis: Bayes klar besser als Evolution, bei 25 Rechnungen und 4 Parametern aber nicht besser als Rsm;
> aussagekräftiger wird ein 10×10-Vergleich oder `MantaAuv`). AP 2.8 steht in
> **[`llm_geometrie_bewertung.md`](llm_geometrie_bewertung.md)**. Offen bleibt nur die Entscheidung, ob `Bayes` Standard wird
> (`OptimizationAlgorithmFactory.DefaultAlgorithm`); sie hängt an der Auswertung im Vergleichsdokument.
> Die folgenden Abschnitte beschreiben den Plan und bleiben als Referenz stehen.

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

#### AP 2.8: Architektur-Bewertung für LLM-gestützte Geometriegenerierung — ✔ siehe [`llm_geometrie_bewertung.md`](llm_geometrie_bewertung.md)
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

| Schritt | Paket | Beschreibung | Primäres Ziel / Nutzen | Stand |
| :--- | :--- | :--- | :--- | :--- |
| ~~1~~ | ~~AP 1.1–1.2~~ | ~~Python-Auswerteskripte und 2D-Referenzlauf `Naca2D`~~ | ~~Auswertungsbasis und Validierung in 2D~~ | ✔ Meilenstein 1, archiviert in `.old/` |
| ~~2~~ | ~~AP 2.1–2.7~~ | ~~KI-Bayes-Optimierer und 5×5-Vergleichslauf (RSM vs. Evolution vs. Bayes)~~ | ~~Beschleunigung der Konvergenz, quantitativer Vergleich~~ | ✔ umgesetzt, Auswertung in `vergleich_optimierer.md` |
| ~~3~~ | ~~AP 2.8~~ | ~~Bewertung LLM-Geometriegenerierung~~ | ~~Entscheidungsgrundlage~~ | ✔ `llm_geometrie_bewertung.md` |
| **4** | **AP 3.1–3.4** | Große LaTeX-Gesamtdokumentation & Showcase | Hochwertige wissenschaftliche Präsentation der Ergebnisse | offen |
| **5** | **AP 4.1–4.4** | CalculiX-FEM-Integration | Multiphysik (Fluid-Struktur-Kopplung) | offen |
