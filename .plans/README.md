# Projektpläne & Roadmap v2 (Einstiegspunkt für KI-Sessions)

> **Stand: 21.11.2026** — Große Überarbeitung & strategische Neuausrichtung auf **Plan A** (Open-Source Verifiable Computational Engineering Platform: `PicoGK` + `LEAP71_ShapeKernel` + `OpenFOAM`/`SU2` + Hybrid-Optimierer + später LLM-Code-Evolution) beschlossen.  
> **Schritt 0 abgeschlossen:** Der 7-Tage-Serverlauf `MantaAuv` auf `BIC_12` ist sauber gestoppt und doppelt archiviert (`~/Archive/MantaAuv_2026-11-21_final/` auf `BIC_12` + `Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/` lokal).  
> **Sprachregel:** Pläne und Diskussionen hier unter `.plans/` bleiben bewusst **auf Deutsch**. Der Produktcode, Logs, CSVs, Tests und Nutzer-Doku werden ab Phase 1 konsequent auf **Englisch** umgestellt.

---

## 1. Aktive Dokumente in `.plans/`

Neue Sessions und KI-Agenten orientieren sich ausschließlich an diesen aktiven Dokumenten:

1. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Roadmap v2 (zuerst lesen!):**
   - Strategische Vision (Plan A), alle verbindlichen Grundsatzentscheidungen (`E1`–`E7`), Phasen-Übersicht (`0`–`5`), Rückverfolgbarkeit aller übernommenen v1-Restpunkte und der geparkte **FEM-Merkzettel**.
2. **[`phase1_entschlackung_und_kern.md`](phase1_entschlackung_und_kern.md) — Phase 1 (nächster Arbeitsschritt):**
   - Bugfixes (GP `sn2`, GCI-Bug, `StlSmoother`-Messreihenfolge, `CellLimit`, Gmsh-Retry, `Program.cs:517`, Deploy-Hygiene), `Core/`-Isolation + Architektur-Test, Zerlegung der 4 God-Classes, echte Integrationstests (`PicoGK`, `Gmsh`, `SU2`), Umbenennung (Namensentscheidung vor `AP 1.6`) & komplette Englisch-Umstellung.
3. **[`phase2_bedienung_physik_und_ziele.md`](phase2_bedienung_physik_und_ziele.md) — Phase 2:**
   - Konfigurations-Diät (`127 → ~15` Projekt-Keys über Physik-Presets), schlanke modulare englische Doku, Trennung von **Objective(s) + Pre-CFD- & Post-CFD-Constraints**, Bouncer-Entkopplung, Symmetrieebene (`MARKER_SYM`), praxistauglicher SU2-Konvergenzabbruch, Multi-Point-Evaluation, `--smoke-test` und C#-Bayes-Feinschliff (`Constrained EI`, L-BFGS-B, Sobol-QMC).
4. **[`phase3_plan_a_openfoam_shapekernel_hybrid.md`](phase3_plan_a_openfoam_shapekernel_hybrid.md) — Phase 3 (Plan A):**
   - Einbindung von **`LEAP71_ShapeKernel`** + Multi-Patch-Geometrie (`inlet`, `outlet`, `wall`, `FluidSeedPoint`), **OpenFOAM als künftiger 3D-Standard-Solver** (zuerst `Internal3D`, danach auch `External3D` nach Validierung; SU2 bleibt für `External2D`, Windows-Tests und optionale 3D-/High-Speed-Variante) sowie **Hybrid-Optimierer-Bridge** (JSON Ask/Tell) für **BoTorch** (Pareto `qNEHVI`, Multi-Fidelity) & **pymoo** neben dem eingebauten C#-Bayes-Standard.
5. **[`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) — Phase 4:**
   - Strikter Feature-Freeze, Zero-to-First-Run-Ergonomie, **3 dauerhafte Literatur-Benchmarks** (NASA TMR NACA 0012 2D, 90°-Rohrkrümmer 3D intern, DARPA SUBOFF 3D extern), Wahl & Durchführung des großen Showcase-Projekts, englischer LaTeX-Bericht & Release/Feedback in CFD-Communities.
6. **[`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md) — Phase 5 (Grobkonzept / Ausblick):**
   - Äußere LLM-Code-Evolution-Schleife auf `ShapeKernel`-Generatoren (Topologie-Sprünge) mit deterministischen Prüftoren (Build, `--smoke-test`, SHA-Hash-Schutz gegen Reward Hacking, strukturierte Physik-Diagnostik) — Ausdetaillierung erst nach Phase 4.
7. **[`Anmerkungen.txt`](Anmerkungen.txt) — Rohnotizen des Nutzers:**
   - Wird nicht automatisch von KI-Agenten verändert.

---

## 2. Historisches Archiv in `.plans/.old/`

*Zur Schonung des Kontextfensters müssen diese archivierten Dokumente in neuen Sessions **nicht** mehr eingelesen werden (alle relevanten offenen Restpunkte sind in `ROADMAP.md` und Phase 1–4 übernommen):*

* [`.old/ROADMAP_v1_2026-11-08.md`](.old/ROADMAP_v1_2026-11-08.md) — Vorherige Gesamt-Roadmap v1 (abgelöst durch v2).
* [`.old/netzstrategie_und_su2_abbruch.md`](.old/netzstrategie_und_su2_abbruch.md) — Netzstrategie, `scaleFactor`-Konsistenz, Großkanal, Krümmungsverfeinerung & SU2-Auto-Restart (AP-1 bis AP-7 umgesetzt).
* [`.old/zeitschaetzung.md`](.old/zeitschaetzung.md) — `RunTimeEstimator`, Tagesformat, Enddatum & früher Grenz-Hinweis (lokal umgesetzt, Server-Verifikation in Phase 1 übernommen).
* [`.old/fem_ausblick.md`](.old/fem_ausblick.md) — Technischer Merkzettel zur späteren CalculiX-FEM-Kopplung (bewusst geparkt, Leitplanken in `ROADMAP.md` Abschnitt 5 festgehalten).
* [`.old/ki_optimierung.md`](.old/ki_optimierung.md) & [`.old/vergleich_optimierer.md`](.old/vergleich_optimierer.md) — Meilenstein 2 (C#-Gauß-Prozess, Bayes, TuRBO, Metrics-Modus, 2D-Benchmarks).
* [`.old/wandschicht_zellzahl.md`](.old/wandschicht_zellzahl.md), [`.old/physik_feinschliff.md`](.old/physik_feinschliff.md), [`.old/servertest_2026-11-06.md`](.old/servertest_2026-11-06.md), [`.old/automatisches_ende.md`](.old/automatisches_ende.md), [`.old/ms1_auswertung_und_logdesign.md`](.old/ms1_auswertung_und_logdesign.md), [`.old/architektur_refactoring.md`](.old/architektur_refactoring.md), [`.old/architektur_kritikpunkte_naca.md`](.old/architektur_kritikpunkte_naca.md), [`.old/projektbuendel.md`](.old/projektbuendel.md), [`.old/anmerkungen_umsetzung.md`](.old/anmerkungen_umsetzung.md) — Frühere Entwicklungsstufen (abgeschlossen).

---

## 3. Aktueller Systemstatus (Stand 22.11.2026)

* **Phase 1 — Etappe 1 (`AP 1.1` & `AP 1.2`) ✔ erledigt:** Alle 7 gefundenen Code-/Physik-Bugs & Hygiene-Punkte behoben, `Core/` vollständig von `Solvers.Cfd`/`Kernels`/`PicoGK` entkoppelt und durch `ArchitectureBoundaryTests.cs` dauerhaft abgesichert.
* **Phase 1 — Etappe 2 (`AP 1.3`, `AP 1.4`, `AP 1.5`) ✔ erledigt:** Alle 4 God-Classes (`BayesOptimizationAlgorithm`, `GmshCfdMesher`/`Gmsh2DMesher`, `Su2Solver`, `MeshCalibration`) sowie `Program.cs` in schlanke Komponenten (≤ 487 L) zerlegt, `CfdProjectDefinitionBase` vereinheitlicht, `EvolutionaryAlgorithm` entfernt, `RsmOptimizationAlgorithm` ehrlich zu `IdwBaselineAlgorithm` (`Idw*`-Keys + Legacy-Alias `"Rsm"`) bereinigt, und 3 echte ungemockte Integrationstests (`PicoGK`, `NACA0012 2D → Gmsh2D → SU2`, `Gmsh3D Prism`) unter `[Trait("Category", "Integration")]` etabliert und real bestanden.
* **Build & Tests:** **848/848 Unit-Tests** (`Category!=Integration`) + **3/3 echte Integrationstests** (`Category=Integration`) grün, 0 Warnungen, 0 Fehler.
* **Server `BIC_12`:** Kein aktiver Lauf (Lauf `MantaAuv` am 21.11.2026 gestoppt & doppelt archiviert).
* **Nächster konkreter Schritt:** Finalen englischen Projektnamen mit Leonardo festlegen und **Etappe 3 von [`phase1_entschlackung_und_kern.md`](phase1_entschlackung_und_kern.md)** (`AP 1.6` Projekt-Umbenennung & komplette Englisch-Umstellung) starten.
