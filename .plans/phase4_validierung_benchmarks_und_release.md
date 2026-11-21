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

| # | Benchmark | Domäne / Solver | Literatur-Referenz | Was er beweist |
| :--- | :--- | :--- | :--- | :--- |
| **B1** | **NASA TMR NACA 0012** ($Re = 6\cdot 10^6$, $\alpha = 0^\circ, 10^\circ$) | `External2D` / **SU2** | NASA Turbulence Modeling Resource (`CL`, `CD`, `Cp`) | 2D-RANS-Genauigkeit innerhalb 2–3 %, GCI-Konvergenz, `y+ ~ 1` |
| **B2** | **90°-Rohrkrümmer / Diffusor** (intern, turbulent) | `Internal3D` / **OpenFOAM** + `ShapeKernel` | Idelchik / Miller Druckverlustbeiwert $\zeta$ (ggf. + Literatur-CFD) | Korrekte interne `PicoGK`/`ShapeKernel` → `snappyHexMesh` → `simpleFoam`-Kette, Massenbilanz `< 1 %`, realer Druckverlust in Minuten! |
| **B3** | **DARPA SUBOFF (Bare Hull)** oder **Myring-Rumpf** | `External3D` / **OpenFOAM** (Vergleich gegen **SU2**, jeweils mit Symmetrieebene!) | Publizierter Schleppkanal-Widerstand $C_D$ & Druckverteilung | Korrekte 3D-Außenumströmung inkl. Symmetrieebene, GCI und Freigabe von **OpenFOAM als neuem 3D-Standard** (direkter Solver-Quervergleich!) |

*Zusätzlich (offener v1-Punkt):* Sauberer **3-Seed-Benchmarkvergleich** auf dem aktualisierten `Naca2D` (Multi-Point) zwischen eingebautem C#-`Bayes`, `Evolution` und optionalem `BoTorch`-Backend.

---

## 3. Empfehlungen & Ideen für das große Showcase-Projekt (Entscheidung erst in Phase 4!)

Da das Framework problemunabhängig aufgebaut ist, legen wir erst nach den Benchmarks fest, welches konkrete Showcase-Bauteil (oder Duo) im Bericht & Community-Post die volle Leistungsfähigkeit (Presets, Pre-/Post-Constraints, Pareto-Front, GCI, Multi-Fidelity, OpenFOAM + SU2) demonstriert. **Konkrete Favoriten als Entscheidungsgrundlage:**

1. **Favorit 1 (Plan-A-Paradebeispiel — inkl. echtem 3D-Druck-Potenzial!):** **3D-druckbarer 1-zu-4 Strömungsverteiler (Manifold) mit `ShapeKernel` + OpenFOAM + Pareto (`BoTorch`)**
   - *Ziele (Multi-Objective):* (1) Minimiere Druckverlust $\Delta p$, (2) Minimiere Massenstrom-Ungleichverteilung zwischen den 4 Auslässen.
   - *Pre-CFD-Constraints:* Bauraum, Mindestwandstärke für 3D-Druck, maximales Materialvolumen.
   - *Warum stark:* Zeigt exakt, wo `PicoGK`/`ShapeKernel` + OpenFOAM + Pareto glänzen — und lässt sich für ~5 € wirklich 3D-drucken und optional experimentell vermessen!
2. **Favorit 2 (Direkt im LEAP-71-Revier):** **Kompakter interner Kühlkanal / statischer Mischer**
   - *Ziele:* Minimiere Druckverlust $\Delta p$ vs. Maximiere benetzte Oberfläche / Mischungsgrad (oder später Wärmeübergang).
   - *Warum stark:* Organische, im normalen CAD kaum modellierbare 3D-Druck-Formen.
3. **Ergänzung 2D:** **Multi-Point-Profiloptimierung (`Naca2D` oder CST/Spline-Profil)** mit SU2 — zeigt in Minuten den kompletten Workflow von Startprofil über Konvergenz bis zur GCI-verifizierten Feinprüfung.
4. **Optional 3D extern:** Repariertes `MantaAuv` (oder sauberer Myring-AUV-Rumpf) mit volumetrischem $C_{D,V}$, Pre-CFD-Volumen-Constraint und Symmetrieebene — als direkter Vorher/Nachher-Beleg gegenüber dem archivierten 2026-11-21-Lauf.

---

## 4. Arbeitspakete (Checkliste)

### AP 4.1: Stabilitäts-, Ergonomie- & „Fremdnutzer“-Audit
- [ ] **Fresh-Clone-Test („Zero-to-First-Run“):** Das Repo auf einer sauberen Umgebung (lokal + Server) nur anhand der englischen `README.md` aufsetzen und ein 2D- sowie ein 3D-Beispiel starten. Jede Hürde sofort beheben.
- [ ] **Fehlermeldungen & Diagnostics polieren:** Klare, handlungsorientierte Fehlermeldungen bei fehlenden Tools, Netzproblemen oder unplausiblen Eingaben; `sim.ps1 doctor` prüft vollständig SU2, Gmsh, OpenFOAM, PicoGK, MPICH und optionales Python/BoTorch-Env.
- [ ] **Optionales Container-/Setup-Paket:** Prüfen, ob ein reproduzierbares `Dockerfile` / `environment.yml` das Ausprobieren für externe Entwickler auf einen Befehl reduziert.

### AP 4.2: Benchmark-Suite durchrechnen & dokumentieren
- [ ] **B1 (`Naca2D` / NASA TMR)** inkl. 3-Seed-Optimierer-Vergleich sauber auf dem Server durchrechnen und GCI + Abweichung zur NASA-Referenz dokumentieren.
- [ ] **B2 (`90°-Rohrkrümmer` / `Internal3D` OpenFOAM)** durchrechnen und Druckverlustbeiwert $\zeta$ + Massenbilanz + GCI gegen Literaturwerte dokumentieren.
- [ ] **B3 (`DARPA SUBOFF` / `External3D` SU2 mit Symmetrie)** durchrechnen und $C_D$ gegen Literaturwerte dokumentieren.

### AP 4.3: Showcase-Projekt wählen, rechnen & visualisieren
- [ ] Konkretes Showcase-Projekt aus Abschnitt 3 auswählen (z. B. 1-zu-4 Manifold + 2D-Multi-Point-Profil).
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
3. Community-Feedback ist eingesammelt und in offene Verbesserungen für Phase 5 gespiegelt.
