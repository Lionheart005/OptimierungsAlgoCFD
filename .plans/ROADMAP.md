# Entwicklungs-Roadmap (Post-Merge `MajorPhysikFixes`)

> **Status:** Aktiv — Stand: 08.11.2026  
> **Basis:** Konsolidierte Anforderungen aus `.plans/Anmerkungen.txt` (Z. 30–61) sowie den Detailplänen `.plans/ki_optimierung.md`, `.plans/vergleich_optimierer.md`, `.plans/llm_geometrie_bewertung.md` und `.plans/fem_ausblick.md`.  
> **Ausgangslage:** Meilenstein 1 (Python-Auswerteskripte, Log-Redesign & 10×10 `Naca2D`-Referenzlauf) ist abgeschlossen und archiviert (`.plans/.old/ms1_auswertung_und_logdesign.md`). **Meilenstein 2 (KI-Integration) ist auf Branch `KI_Integration` umgesetzt:** Server-Vergleich 5×5 und 10×10 ausgewertet, **Bayes ist bei 100 Rechnungen klar am besten** (`vergleich_optimierer.md`). Am 08.11.2026 folgte ein Aufräum-Durchgang (Bayes-Fehler der späten Phase behoben, Projekt in `OptimierungsAlgoCFD` umbenannt, Beispielprojekt `TestprojektNACA` entfernt, `_Vorlage` zu `_Vorlage3D`).

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
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Meilenstein 5: Englische Gesamtfassung (nach der LaTeX-Dokumentation)       │
│ ├── AP 5.1: LaTeX-Bericht, README und Handbücher ins Englische              │
│ └── AP 5.2: Konsolen-/Logtexte und Fehlermeldungen (zweisprachig oder EN)   │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Meilenstein 2: KI- & Machine-Learning-Integration (`ki_optimierung.md`) — ✔ umgesetzt

> **Ergebnis (08.11.2026):** `"OptimizationAlgorithm": "Bayes"` ist implementiert (C#, ohne neue Abhängigkeit) und getestet
> (alle Tests grün, 0 Warnungen). Vergleichsläufe auf dem Server (`Naca2D`): bei 25 Rechnungen (5×5) gleichauf mit Rsm; **bei 100 Rechnungen (10×10, 3 Seeds) klar am besten**:
> nach der Feinprüfung Bayes 65,9, Rsm 54,7, Evolution 48,5, und Bayes findet auf jedem Seed dasselbe Optimum. Details: **[`vergleich_optimierer.md`](vergleich_optimierer.md)**.
> AP 2.8 steht in **[`llm_geometrie_bewertung.md`](llm_geometrie_bewertung.md)**. **Offen bleibt die Entscheidung, ob `Bayes` Standard wird**
> (`OptimizationAlgorithmFactory.DefaultAlgorithm`; Empfehlung: ja, ab etwa 40 Rechnungen) sowie ein Wiederholungslauf nach der Korrektur der späten Phase.
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
   - Mathematische Formulierung: Response Surface Methodology (RSM/IDW) vs. Evolutionsverfahren (Mutation um den jeweils Besten; CMA-ES ist nicht umgesetzt) vs. Bayesian Optimization (GP-EI).
   - Benchmark-Ergebnisse aus dem identisch gesedeten 2D-Vergleichslauf.
4. **2D-Showcase: Aerodynamische Profiloptimierung (`Naca2D`):**
   - Inkompressible Umströmung, Anstellwinkel-Optimierung.
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

### Vorgaben für die Läufe im Bericht (aus `Anmerkungen.txt`, Stand 08.11.2026)

Der Bericht erklärt zuerst das Framework (Architektur, Schichten, Konfiguration, Wiederaufnahme), wie oben gegliedert. Für die Ergebnisse gilt:

**2D `Naca2D` (Vergleich und Genauigkeit)**
- **Jeder Algorithmus (Rsm, Evolution, Bayes) genau einmal, mit Netzkalibrierung und gleichen Konfigurationen**, damit Fitness und Ergebnisse direkt vergleichbar sind.
- **Bayes zusätzlich einmal mit manuell gesetztem, sehr feinem Netz** und gegen den kalibrierten Durchlauf vergleichen: Rechenzeit und Genauigkeit (lohnt sich die Kalibrierung?).
- **Werte mit NASA-Daten (TMR) vergleichen** und auf Realismus prüfen, auch gegen professionelle Quellen (Literatur, Windkanaldaten). Gehört dazu: stimmt das grob optimierte Ergebnis mit dem fein nachgerechneten überein (Feinprüfung)?
- Hinweis: Die Läufe vom 08.11.2026 (`vergleich_optimierer.md`) verwendeten ein festes grobes Netz (Faktor 2,0) und den Stand vor der Korrektur der späten Phase; für den Bericht sauber wiederholen (kalibriert, Code-Stand nach Korrektur).

**3D `MantaAuv` (Showcase, kein Vergleich)**
- **Nur ein Durchlauf**, aber detailliert und genau (Kalibrierung, Feinprüfung), und **schön dokumentiert** (ParaView-Bilder, Druck/Reibung, Wirbel, Nachlauf).
- Vorher klären: Der eingecheckte Netzfaktor 0,71 stammt aus dem Servertest und ist nicht netzunabhängig (CD 0,171 grob gegen 0,146 fein); für genaue Absolutwerte kalibrieren (`sim.ps1 calibrate`, ca. 4 h).

**Ausblick:** FEM-Kopplung (Meilenstein 4).

**Übersetzung:** Der gesamte Bericht samt README und Handbüchern wird anschließend ins Englische übersetzt (Meilenstein 5).

### Benötigte Assets & Abbildungen
- Vektorgrafiken (`.pdf`) aus `plot_history.py`, `plot_optimization.py` und `plot_calibration.py`.
- Hochauflösende ParaView-Renderings aus den vorhandenen `.vtu`-Dateien (Oberflächen-Druckfelder, Schnittebenen im Strömungsfeld, 3D-Wirbelkerne).
- Geometrievergleiche (Überlagerung von Ausgangs- und Optimalgeometrie).

---

## Meilenstein 3.5: Englische Gesamtfassung (nach der LaTeX-Dokumentation)

### Ziel
Das Projekt wird auf GitHub unter echtem Namen veröffentlicht. Nach Abschluss der LaTeX-Dokumentation (Meilenstein 3) wird alles ins Englische übersetzt, damit es international nutzbar ist.

### Arbeitspakete
1. **AP 5.1: Dokumentation.** LaTeX-Bericht, `README.md`, `BEDIENHANDBUCH.md`, `src/projects/README.md`, `scripts/README.md`, `config/README.md`, die Architektur- und Kernel-READMEs sowie die JSON-Kommentare in den Vorlagen. Deutsch bleibt als zweite Fassung erhalten oder wird archiviert (zu entscheiden).
2. **AP 5.2: Programmtexte.** Konsolenausgabe, Logs, Fehlermeldungen und Spaltennamen der Ergebnisdateien sind deutsch; viele Tests prüfen Meldungstexte (`Assert.Contains("…")`). Empfehlung: Texte zentral sammeln (eine Ressourcen-Datei oder statische Klasse), dann zweisprachig oder umschaltbar machen, statt alles in 160 Dateien zu ersetzen.
3. **AP 5.3: Bezeichner und Kommentare im Code** (XML-Kommentare, Testnamen sind bereits englisch bzw. gemischt).
4. **Begleitend:** Lizenzdatei, README mit Voraussetzungen, Schnellstart und einem Ergebnisbild (siehe „Release-Reife“ unten).

---
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



## Offene Punkte aus dem Aufräum-Durchgang (08.11.2026)

Erledigt am 08.11.2026 (Details: `ki_optimierung.md`, Abschnitt „Nachtrag“): Bayes-Fehler der späten Phase, MPI-Falle (Warnung beim Start und in `doctor`, Handbuch korrigiert), doppelte CSV-Spalte und Dezimaltrenner, Unsicherheit bei Türsteher/Feinprüfung/Kalibrierung, `--print-config`, toter Code, Umbenennungen.

Offen:
- [x] **Entscheidung `DefaultAlgorithm` (09.11.2026): `Bayes` ist Standard.** Umgesetzt in `OptimizationAlgorithmFactory`, Vorlagen und Naca2D (kein Eintrag mehr). MantaAuv bleibt ausdrücklich bei `Rsm`, bis Bayes im 3D-Fall (Modus `Metrics`) gemessen ist; dann Zeile und Test `Repository_Simulation_Json_Selects_Rsm` bewusst ändern.
- [ ] **Wiederholungslauf Bayes auf dem Server** (Naca2D 10×10, drei Seeds) mit dem korrigierten Stand; erwartet: derselbe Sieger, bessere Ausbeute der letzten Runden. Dabei den alten Ordner `src/Automatisierung_v2` auf dem Server löschen (das Deploy räumt Quelldateien ab, nicht `bin/obj`).
- [x] **Grenzen (09.11.2026):** Werden nicht automatisch erweitert. Stattdessen gibt der Lauf am Ende einen sichtbaren Hinweis aus, wenn der Gewinner an einer Grenze liegt (`BoundaryHint`, 1 % der Spanne). Offen bleibt die fachliche Frage für `Naca2D` (Optimum in der Ecke `MaxCamber = 8`, `MaxThickness = 6`): erweitern oder nicht.
- [ ] **MantaAuv mit Bayes (Modus `Metrics`) auf dem Server testen** und erst dann den Standard dort umstellen; die Bayes-Verbesserung gegenüber Rsm ist für 3D noch nicht gemessen.
- [ ] **Release-Reife GitHub:** `LICENSE` (MIT, 09.11.2026) und `THIRD_PARTY_NOTICES.md` sind angelegt und im README verlinkt; SU2/Gmsh/MPICH/ParaView-Angaben vor der Veröffentlichung gegenprüfen. Noch offen: README um Voraussetzungen, Schnellstart und ein Ergebnisbild ergänzen, alte Branches (`WindschiffProjekt`, `MajorPhysikFixes`) aufräumen, `KI_Integration` nach `main` mergen.
- [ ] **Servername im Repo:** Der Server-Ordner heißt weiter `~/Documents/AutomatisierungCleanVersion` (`-RemoteDir` in `sim.ps1`); zum Vereinheitlichen auf `OptimierungsAlgoCFD` umstellen, dann beim ersten Deploy neu bauen (die Bibliothek legt der Runner selbst an).
- [ ] **Rsm/Evolution-Tweaks** sind bewusst nicht gemacht (Rsm-Startplan gemessen, gemischt; CMA-ES nicht umgesetzt).

---

## Priorisierte Umsetzungsreihenfolge

| Schritt | Paket | Beschreibung | Primäres Ziel / Nutzen | Stand |
| :--- | :--- | :--- | :--- | :--- |
| ~~1~~ | ~~AP 1.1–1.2~~ | ~~Python-Auswerteskripte und 2D-Referenzlauf `Naca2D`~~ | ~~Auswertungsbasis und Validierung in 2D~~ | ✔ Meilenstein 1, archiviert in `.old/` |
| ~~2~~ | ~~AP 2.1–2.7~~ | ~~KI-Bayes-Optimierer und 5×5-Vergleichslauf (RSM vs. Evolution vs. Bayes)~~ | ~~Beschleunigung der Konvergenz, quantitativer Vergleich~~ | ✔ umgesetzt, Auswertung in `vergleich_optimierer.md` |
| ~~3~~ | ~~AP 2.8~~ | ~~Bewertung LLM-Geometriegenerierung~~ | ~~Entscheidungsgrundlage~~ | ✔ `llm_geometrie_bewertung.md` |
| **4** | **AP 3.1–3.4** | Große LaTeX-Gesamtdokumentation & Showcase | Hochwertige wissenschaftliche Präsentation der Ergebnisse | offen |
| **5** | **AP 4.1–4.4** | CalculiX-FEM-Integration | Multiphysik (Fluid-Struktur-Kopplung) | offen |
| **6** | **AP 5.1–5.3** | Englische Gesamtfassung (nach der LaTeX-Dokumentation) | Internationale Nutzbarkeit | offen |
| **vorab** | siehe „Offene Punkte aus dem Aufräum-Durchgang“ | Standard-Algorithmus, Wiederholungslauf, Lizenz, README | Release-Reife | offen |
