# Projektpläne & Roadmap v2 (Einstiegspunkt für KI-Sessions)

> **Stand: 25.11.2026** — **Phasen 1, 2, 3 & 3.5 vollständig abgeschlossen** und archiviert (`.plans/.old/`). Aktuell läuft **Phase 4** (Validierung, Literatur-Benchmarks B1–B3, Showcase-Projekt `StatorCoolingRing`, technischer LaTeX-Bericht & Community-Release) unter **striktem Core-Feature-Freeze**.  
> **Sprachregel:** Pläne und Diskussionen hier unter `.plans/` bleiben bewusst **auf Deutsch**. Der Produktcode, Logs, CSVs, Tests und Nutzer-Doku sind konsequent auf **Englisch**.

---

## 1. Aktive Dokumente in `.plans/`

Neue Sessions und KI-Agenten orientieren sich ausschließlich an diesen aktiven Dokumenten:

1. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Roadmap v2:**
   - Strategische Vision (Plan A), verbindliche Grundsatzentscheidungen (`E1`–`E7`), Phasen-Übersicht (`0`–`5`) und der geparkte FEM-Merkzettel.
2. **[`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) — Masterplan Phase 4:**
   - Gesamtcheckliste für AP 4.1 (✔ erledigt), AP 4.2 (Literatur-Benchmarks B1–B3), AP 4.3 (Showcase `StatorCoolingRing`), AP 4.4 (LaTeX-Bericht & Community-Release).
3. **[`phase4_b1_2d_fix_und_naca2d_benchmark.md`](phase4_b1_2d_fix_und_naca2d_benchmark.md) — Laufende Simulation auf `BIC_12` (B1 & 3-Seed `Naca2D`):**
   - Dokumentation des 2D-Clipper-/Gmsh-/Cauchy-Fixes (`6cb8d67`) sowie der aktuell auf `BIC_12` laufenden `Naca2D`-3-Seed-Optimierer-Vergleichskette (`tmux b1_chain`, `ALL_B1_DONE`) inkl. Download- & Auswertungsbefehlen.
4. **[`phase4_presets_benchmarks_und_calibration_fix.md`](phase4_presets_benchmarks_und_calibration_fix.md) — Direkt anstehende Benchmark-Läufe (B1–B3 `VerificationFine`):**
   - Umstellung aller `*:VerificationFine`-Presets auf manuelle höchste Detailstufe, Calibration-Suffix-Monotonie-Bugfix (`1074/1074` Tests) sowie alle exakten Deploy-, Start- und Fetch-Befehle für `BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam` und `BenchmarkSuboffSu2` auf `BIC_12`.
5. **[`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md) — Abgeschlossenes Showcase-Upgrade (`StatorCoolingRing v3`):**
   - Vollständig umgesetztes & verifiziertes topologiefreies 12-Pfad-Gefäßnetz (`14D`-DNA, Force-Directed Edge Bundling, 3D-SDF-Hindernis-Lernen, Murray’s Law, maskierte 1D-Vorbalancierung, organische Rippenmatrix, Post-Fillet-SW-24-Sechskant; lokal + `BIC_09` inkl. `69 s` `snappyHexMesh`-Check verifiziert, bereit für AP 4.3 auf `BIC_12`).
6. **[`phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md) — Phase 4.5 (Generalisierte Gefäßnetz-Engine im Kern & `_TemplateVascular3D`):**
   - Extraktion des bewährten `StatorCoolingRing v3`-Pfaderzeugungs-Algorithmus als wiederverwendbares Kernmodul (`src/FluidForge/Kernels/Vascular/` + `VascularVoxelBuilder` unter `Kernels/PicoGk/InternalFlow/`) mit deklarativen Ports/SDF-Hindernissen, DNA-Presets, neuer Vorlage `_TemplateVascular3D` (`new-project.ps1 -Template Vascular3D`) und Dogfooding-Refaktorierung von `StatorCoolingRing` (bewusst nach Phase-4-Feature-Freeze eingeordnet).
7. **[`phase5_docker_container.md`](phase5_docker_container.md) — Phase 5 Zusatzplan (Docker / Apptainer):**
   - Optionales Container-Setup für externe Nutzer und HPC-Cluster (bewusst auf Phase 5 verschoben; `setup_server.sh` bleibt der einzige Phase-4-Pfad).
8. **[`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md) — Phase 5 (Ausblick / Grobkonzept):**
   - Äußere LLM-Code-Evolution-Schleife auf `ShapeKernel`-Generatoren — Ausdetaillierung nach Phase 4 / 4.5.
9. **[`Anmerkungen.txt`](Anmerkungen.txt) — Rohnotizen des Nutzers:**
   - Aktuelle Ablaufnotizen für die laufenden/anstehenden Serverläufe (wird nicht automatisch von KI-Agenten verändert).

---

## 2. Historisches Archiv in `.plans/.old/`

*Zur Schonung des Kontextfensters müssen diese abgeschlossenen bzw. abgelösten Dokumente in neuen Sessions **nicht** mehr eingelesen werden:*

- **Abgeschlossene Phasen 1–3.5 & Strategie:**
  - [`.old/phase1_entschlackung_und_kern.md`](.old/phase1_entschlackung_und_kern.md), [`.old/phase2_bedienung_physik_und_ziele.md`](.old/phase2_bedienung_physik_und_ziele.md), [`.old/phase3_plan_a_openfoam_shapekernel_hybrid.md`](.old/phase3_plan_a_openfoam_shapekernel_hybrid.md), [`.old/phase3_5_korrekturplan_defaults_und_multifidelity.md`](.old/phase3_5_korrekturplan_defaults_und_multifidelity.md), [`.old/strategie_und_projektbewertung.md`](.old/strategie_und_projektbewertung.md)
- **Abgeschlossene bzw. abgelöste Phase-4-Zwischenstände:**
  - [`.old/phase4_projektvorbereitung.md`](.old/phase4_projektvorbereitung.md), [`.old/phase4_ap41_und_b1_naca0012.md`](.old/phase4_ap41_und_b1_naca0012.md), [`.old/phase4_stator_cooling_ring_v2.md`](.old/phase4_stator_cooling_ring_v2.md)
- **Historisches v1-Archiv (vor 21.11.2026):**
  - [`.old/ROADMAP_v1_2026-11-08.md`](.old/ROADMAP_v1_2026-11-08.md) sowie alle älteren v1-Einzelpläne (`netzstrategie_und_su2_abbruch.md`, `zeitschaetzung.md`, `fem_ausblick.md`, `ki_optimierung.md`, `vergleich_optimierer.md`, `wandschicht_zellzahl.md`, `physik_feinschliff.md`, `servertest_2026-11-06.md`, `automatisches_ende.md`, `ms1_auswertung_und_logdesign.md`, `architektur_refactoring.md`, `architektur_kritikpunkte_naca.md`, `projektbuendel.md`, `anmerkungen_umsetzung.md`).
