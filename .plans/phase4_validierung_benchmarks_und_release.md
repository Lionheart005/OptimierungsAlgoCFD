# Phase 4: Validierung, Literatur-Benchmarks, Feinschliff & Community-Release

> **Status:** Geplant (strikter Feature-Freeze nach Abschluss von Phase 3!)  
> **Ziel:** Das fertige System ohne neue Features rund, stabil, nachweisbar genau und so einfach bedienbar machen, dass externe Ingenieure & CFD-Communities es sofort ausprobieren und fachlich bewerten können.

---

## 1. Ziel & Kontext

Bis hierhin stehen:
- ein sauberer, entschlackter, englischsprachiger Kern (Phase 1),
- intuitive Presets, getrennte Objectives/Constraints und saubere Physik (Phase 2),
- `LEAP71_ShapeKernel`, OpenFOAM (`Internal3D`) und die Hybrid-Optimierer-Bridge für Pareto/Multi-Fidelity (Phase 3).

**Phase 4 hat ein klares Verbot: keine neuen Großfeatures!** Stattdessen zählen vier Dinge, die aus einem guten Projekt ein *wissenschaftlich glaubwürdiges, nutzbares Open-Source-Tool* machen:
1. **Objektive Verifikation an Literatur-Benchmarks** (2D extern, 3D intern, 3D extern).
2. **Ein starkes, problemunabhängiges Showcase-Projekt** (Auswahl bewusst flexibel — wird erst am Start von Phase 4 final festgelegt), das alle Stärken des Frameworks voll ausspielt.
3. **Ergonomie & „First-15-Minutes-Experience“** für fremde Nutzer.
4. **Veröffentlichung & ehrliches Feedback aus der CFD-/Computational-Engineering-Community** (inkl. hochwertigem technischen Dokument / LaTeX-Bericht auf Basis echter, validierter Daten).

---

## 2. Dauerhafte Literatur-Benchmark-Suite (Objektiver Genauigkeitsbeweis)

Jeder externe CFD-Ingenieur fragt als Erstes: *„Woher weiß ich, dass deine automatische Pipeline keine hübschen bunten Bilder mit 40 % Fehler rechnet?“* Diese **3 Standard-Benchmarks** beantworten das schwarz auf weiß:

| # | Benchmark | Projekt(e) | Domäne / Solver | Literatur-Referenz | Was er beweist |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **B1** | **NASA TMR NACA 0012** ($Re = 6\cdot 10^6$, $\alpha = 0^\circ, 10^\circ$) | `BenchmarkNaca0012` | `External2D` / **SU2** | NASA Turbulence Modeling Resource (`CL`, `CD`, `Cp`) | 2D-RANS-Genauigkeit innerhalb 2–3 %, GCI-Konvergenz, `y+ ~ 1` |
| **B2** | **90°-Rohrkrümmer / Diffusor** (intern, turbulent) | `BenchmarkPipeBend90` | `Internal3D` / **OpenFOAM** + `ShapeKernel` | Idelchik / Miller Druckverlustbeiwert $\zeta$ (ggf. + Literatur-CFD) | Korrekte interne `PicoGK`/`ShapeKernel` → `snappyHexMesh` → `simpleFoam`-Kette, Massenbilanz `< 1 %`, realer Druckverlust in Minuten! |
| **B3** | **DARPA SUBOFF (Bare Hull)** | `BenchmarkSuboffOpenFoam` + `BenchmarkSuboffSu2` (Zwillingsprojekte) | `External3D` / **OpenFOAM** vs. **SU2** (jeweils mit Symmetrieebene!) | Publizierter Schleppkanal-Widerstand $C_D$ & Druckverteilung | Korrekte 3D-Außenumströmung inkl. Symmetrieebene, GCI und Freigabe von **OpenFOAM als neuem 3D-Standard** (direkter Solver-Quervergleich!) |

*Zusätzlich (offener v1-Punkt):* Sauberer **3-Seed-Benchmarkvergleich** auf dem aktualisierten `Naca2D` (Multi-Point) zwischen eingebautem C#-`Bayes`, `IdwBaseline` und optionalem `BoTorch`-Backend.

---

## 3. Ausgewähltes Showcase-Projekt (vorbereitet für Phase 4)

Für den Bericht & Community-Post wurde als primäres Showcase-Projekt **`StatorCoolingRing`** vorbereitet (ergänzt um optionales 2D-Multi-Point-Profil):

1. **Primär-Showcase (`StatorCoolingRing`):** **3D-druckbarer 1-zu-12 E-Motor-Stator-Kühlmittel-Ringverteiler mit `ShapeKernel` + `OpenFOAM` + `BoTorch` `qNEHVI` Pareto & druckbarer Solid-STL**
   - *Ziele (Multi-Objective):* (1) Minimiere Druckverlust $\Delta p$, (2) Minimiere Massenstrom-Ungleichverteilung zwischen den 12 Auslässen.
   - *Pre-CFD-Constraints & Fertigung:* Bauraum, Mindestwandstärke für 3D-Druck, maximales Materialvolumen, Export einer druckbaren Solid-STL.
   - *Warum stark:* Zeigt exakt, wo `PicoGK`/`ShapeKernel` + `OpenFOAM` + `BoTorch` (`qNEHVI` Pareto-Front) glänzen — und liefert ein real 3D-druckbares Industrie-nahes Bauteil!
2. **Ergänzung 2D:** **Multi-Point-Profiloptimierung (`Naca2D` oder CST/Spline-Profil)** mit SU2 — zeigt in Minuten den kompletten Workflow von Startprofil über Konvergenz bis zur GCI-verifizierten Feinprüfung.
3. **Optional 3D extern:** Repariertes `MantaAuv` (oder sauberer Myring-AUV-Rumpf) mit volumetrischem $C_{D,V}$, Pre-CFD-Volumen-Constraint und Symmetrieebene — als direkter Vorher/Nachher-Beleg gegenüber dem archivierten 2026-11-21-Lauf.

---

## 4. Arbeitspakete (Checkliste)

### AP 4.1: Stabilitäts-, Ergonomie- & „Fremdnutzer“-Audit
- [x] **Fresh-Clone-Test („Zero-to-First-Run“):** Auf sauberem Ubuntu-Server `BIC_09` vollständig rootless via `setup_server.sh` (Miniforge3 `-p ~/miniconda`, `PicoGKRuntime` Pin `1713d29` + RPATH, SU2, Gmsh, MPICH, OpenFOAM 2506, `opt-py`, .NET 9) + `sim.ps1 doctor` + 2D (`Naca2D`) & 3D (`MantaAuv --smoke-test`, `PipeBend90`) erfolgreich durchgeführt; alle gefundenen Hürden sofort behoben (`891347d`, `b85091c`, `75e9355`, Details siehe [`phase4_ap41_und_b1_naca0012.md`](phase4_ap41_und_b1_naca0012.md)).
- [x] **Fehlermeldungen & Diagnostics polieren:** Handlungsorientierte `[ERROR]`-Meldungen in `OptimizeCommand.Report` (`DllNotFoundException`, PicoGK, `Win32Exception`, fehlende Dateien/Ordner), SSH-`BatchMode=yes` & `tmux`-FD-Hänger-Fix sowie vollständiger `sim.ps1 doctor`-Check (SU2, Gmsh, OpenFOAM, PicoGK + `ldd`, MPICH, `opt-py`, Auto-Build bei Erstkontakt).

> *Hinweis:* Das optionale Docker-/Apptainer-Container-Setup wurde auf Phase 5 verschoben (siehe [`phase5_docker_container.md`](phase5_docker_container.md)); in Phase 4 bleibt `setup_server.sh` der einzige unterstützte Server-Setup-Pfad.

### AP 4.2: Benchmark-Suite durchrechnen & dokumentieren
- [x] **Methodik-Schärfung (`*:VerificationFine` manuell) & Non-Monotonic-Calibration-Bugfix (25.11.2026):** Alle 3 `*:VerificationFine`-Presets auf höchste manuelle Detailstufe ohne automatische Kalibrierung und ohne abschließende Verifikation umgestellt (`External2D: 0.5x` / `CauchyFloor 2e-6`, `External3D: 0.8x` / `CauchyFloor 1e-6`, `Internal3D: 1.0x`, `MeshCalibration.Mode = "Manual"`, `ChampionVerification.Mode = "Off"`); alle 4 Benchmark-`simulation.json`s bereinigt. Zudem wurde der auf `BIC_12` entdeckte **Non-Monotonic / False-Coarse-Match-Bug** in `CalibrationEvaluator` & `MeshCalibration` per strikter Fein→Grob-Suffix-Prüfung behoben und mit Regressionstests abgesichert (`1074/1074` Tests grün). Bericht-Notizen unter [`docs/report/report_notes.md`](../docs/report/report_notes.md) und vollständiger Handover unter [`phase4_presets_benchmarks_und_calibration_fix.md`](phase4_presets_benchmarks_und_calibration_fix.md).
- [ ] **B1 (`BenchmarkNaca0012` / NASA TMR)** inkl. 3-Seed-Optimierer-Vergleich (`Naca2D`): 2D-Fix umgesetzt (`6cb8d67`); laufender 9-Lauf `Naca2D`-3-Seed-Vergleich (`tmux b1_chain` auf `BIC_12`, fester Faktor `2.0` zum reinen Optimierer-Vergleich) läuft fertig durch. Danach Ergebnisse holen, neuen `VerificationFine`-Stand deployen und `BenchmarkNaca0012` direkt im manuellen `External2D:VerificationFine`-Modus (`Alpha0` 0° & `Alpha10` 10°, ohne Kalibrierung/Optimierung) auf `BIC_12` rechnen (Details & Befehle: [`phase4_presets_benchmarks_und_calibration_fix.md`](phase4_presets_benchmarks_und_calibration_fix.md)).
- [ ] **B2 (`BenchmarkPipeBend90` / `Internal3D:VerificationFine` OpenFOAM)** direkt auf höchster manueller Preset-Stufe durchrechnen und Druckverlustbeiwert $\zeta$ + Massenbilanz gegen Literaturwerte dokumentieren.
- [ ] **B3 (`BenchmarkSuboffOpenFoam` + `BenchmarkSuboffSu2` / `External3D:VerificationFine` Zwillingsprojekte mit Symmetrie)** direkt auf höchster manueller Preset-Stufe durchrechnen und $C_D$ im Solver-Quervergleich gegen Literaturwerte dokumentieren.

### AP 4.3: Showcase-Projekt wählen, rechnen & visualisieren
- [ ] Ausgewähltes Showcase-Projekt **`StatorCoolingRing`** (1-zu-12 E-Motor-Stator-Kühlmittel-Ringverteiler mit `BoTorch` `qNEHVI` Pareto & druckbarer Solid-STL, ggf. ergänzt um 2D-Multi-Point-Profil) für den Lauf final prüfen.
- [ ] Vollständigen Optimierungslauf (inkl. Kalibrierung, Pre-/Post-Constraints, Pareto-Front / Feinprüfung) durchführen und alle Daten sauber archivieren.
- [ ] Hochwertige Visualisierungen erzeugen: Konvergenz-, GCI-, Pareto- und ARD-Sensitivitätsplots (`scripts/*.py`) + ParaView-Renderings (Stromlinien, Druckfelder, $y^+$, Schnittansichten).
- [ ] *(Bonus, falls Lust & Zeit)*: Gewinner-STL real 3D-drucken (und ggf. einfachen Durchfluss-/Drucktest dokumentieren).

### AP 4.4: Technisches Dokument (LaTeX) & Community-Release
- [ ] **Englisches technisches Dokument / Showcase-Bericht (LaTeX):** Erst jetzt schreiben — kompakt, wissenschaftlich sauber, mit Architekturüberblick, Benchmark-Validierung (B1–B3), Showcase-Ergebnissen, ehrlicher Diskussion der Grenzen (Low-$Re$/Transition, RANS) und Ausblick auf Phase 5 (LLM) & FEM.
- [ ] **GitHub-Release-Reife prüfen:**
  - `LICENSE` (MIT) & vollständige `THIRD_PARTY_NOTICES.md` (PicoGK, LEAP71_ShapeKernel, Clipper2, SU2, Gmsh, OpenFOAM, MPICH, optional BoTorch/pymoo).
  - Starke englische `README.md` mit klarem Wertversprechen, Titelbild/ParaView-Visualisierung, Benchmark-Tabelle und 5-Minuten-Quickstart.
- [ ] **Community-Feedback einholen:** Gezielt vorstellen und ehrliche Kritik einsammeln in z. B. **CFD-Online**, **r/CFD**, **LEAP 71 PicoGK GitHub Discussions**, **SU2 / OpenFOAM Community** — Feedback strukturiert sammeln, bevor Phase 5 startet! 

---

## 5. Verifikations- & Abnahmekriterien

1. Alle drei Literatur-Benchmarks (B1, B2, B3) sind vollständig gerechnet, reproduzierbar dokumentiert und liegen innerhalb physikalisch plausibler RANS-Toleranzen (mit ausgewiesenem GCI und `y+`).
2. Ein Außenstehender kann ohne Vorwissen anhand der englischen `README.md` in < 15 Minuten einen ersten Lauf starten und verstehen.
3. (Notiz von Leonardo: Bevor ich das veröffentliche, will ich auch noch einmal mit meinem Professor bzw mit echter Literatur und Recherche vernüfntige Preset-Einstellungen für die verschiedenen Physik Vorlagen sprechen, sodass man auch wirklich out of the box mit den Vorgaben gute und basierte Ergebnisse bekommt.)
4. Community-Feedback ist eingesammelt und in offene Verbesserungen für Phase 5 gespiegelt.
