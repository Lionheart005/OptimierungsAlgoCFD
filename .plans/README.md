# Projektpläne & Roadmap v2 (Einstiegspunkt für KI-Sessions)

> **Stand: 26.11.2026** — **Phasen 1, 2, 3 & 3.5 vollständig abgeschlossen** (`.plans/.old/`).  
> **Aktueller Stand in Phase 4:** `AP 4.1` (Server-Audit `BIC_09`) und der 3-Seed-Optimierer-Vergleich (`Naca2D`) sind **fertig**. Als **unmittelbar nächster Schritt** steht das Upgrade der allgemeinen OpenFOAM-/SU2-Presets & Konvergenzlogik inkl. sauberem Re-Run der 4 Physik-Benchmarks (B1–B3) auf `BIC_12` an ([`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md)).  
> **Sprachregel:** Pläne in `.plans/` bleiben auf **Deutsch**; Produktcode, Logs, CSVs, Tests und Nutzer-Doku sind konsequent auf **Englisch**.

---

## 1. Aktive Dokumente in `.plans/` (aufgeräumt — nur diese lesen!)

### Übersicht & Steuerung
1. **[`ROADMAP.md`](ROADMAP.md) — Gesamt-Roadmap v2:**
   - Ein-Blick-Status über alle Phasen (`0`–`5`), Grundsatzentscheidungen (`E1`–`E7`) und exakte Reihenfolge der verbleibenden Arbeitspakete.
2. **[`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) — Masterplan Phase 4:**
   - Gesamtcheckliste von `4.1` (✔) über `4.2` (Physik-Benchmarks ▶), `4.3` (Showcase-Review & Lauf), `4.4` (Vaskuläre Engine ins Hauptprogramm) bis `4.5` (Gesamtauswertung, LaTeX-Bericht, Prof-/Literatur-Preset-Check & Community-Release).

### Konkrete Arbeitspaket-Pläne (in Ausführungsreihenfolge)
3. **[`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md) — ▶ JETZT AKTIV (`AP 4.2b`):**
   - `BenchmarkNaca0012` ($\alpha=0^\circ$) A/B-Diagnose, OpenFOAM-Konvergenz auf SU2-Niveau (`ResultMetrics`-Filter, `MetricTolerance`, Stationarität/Wirbel, `AutoRestartOnDrift`), `MaxCells3D = 15M` + Doku, skaleninvariantes OpenFOAM-Auto-Meshing + `TargetYPlus`-Layer-Sizing + `nutUSpaldingWallFunction`, sowie Re-Run & Auswertung aller 4 Physik-Benchmarks auf `BIC_12`.
4. **[`phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md`](phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md) — DANACH (`AP 4.3a`):**
   - Grundlegende Überarbeitung des vaskulären Routing-Kerns auf **`v4` (Geodätischer BOT/GCO-Gefäßbaum + PBD-Krümmungsprojektion + bewusste hydraulische Wegverlängerung)** inkl. objektivem A/B-Vergleich gegen die [`v3`-FDEB-Baseline](phase4_stator_cooling_ring_v3_emergent_vascular.md) zur Kürung der finalen Showcase-Version.
5. **[`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md) — REFERENZ / SHOWCASE (`AP 4.3b`):**
   - Technische Spezifikation der `v3`-Basis (Mechanik-Solid, Interfaces, OpenFOAM-Setup ✔) als Baseline für `AP 4.3a` und Rahmen für den großen Pareto-Lauf auf `BIC_12` (`AP 4.3b`, gerechnet mit der in `AP 4.3a` gekürten Gewinner-Version!).
6. **[`phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md) — DANACH (`AP 4.4`):**
   - Übernahme der **besten, im Showcase (`AP 4.3a/b`) gekürten und verwendeten Version** des vaskulären Routing-Algorithmus als wiederverwendbares Kernmodul (`FluidForge.Kernels.Vascular` + `VascularVoxelBuilder`) und fertiges Projekt-Preset (`_TemplateVascular3D`, `new-project.ps1 -Template Vascular3D`) ins Hauptprogramm inkl. Dogfooding auf `StatorCoolingRing`.

### Phase 5 (Ausblick — erst nach Phase 4 & Community-Feedback)
7. **[`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md) — Phase 5 Hauptkonzept:**
   - LLM-gestützte Topologie-/Code-Evolution auf dem verifizierten `FluidForge`-Harness (`AP 5.1`–`5.4`).
8. **[`phase5_docker_container.md`](phase5_docker_container.md) — Phase 5 Zusatzplan (`AP 5.5`):**
   - Optionales Docker-/Apptainer-Toolchain-Image (`base`/`full`) als Ergänzung zu `setup_server.sh`.

### Persönliche Notizen
9. **[`Anmerkungen.txt`](Anmerkungen.txt)** — Freie Notizen des Nutzers (nicht automatisch überschreiben).

---

## 2. Historisches Archiv in `.plans/.old/`

*Abgeschlossene und abgelöste Pläne — für neue Sessions **nicht** mehr nötig:*
- **Phasen 1–3.5 & Strategie:** `phase1_entschlackung_und_kern.md`, `phase2_bedienung_physik_und_ziele.md`, `phase3_plan_a_openfoam_shapekernel_hybrid.md`, `phase3_5_korrekturplan_defaults_und_multifidelity.md`, `strategie_und_projektbewertung.md`
- **Abgeschlossene Phase-4-Zwischenschritte:** `phase4_projektvorbereitung.md`, `phase4_ap41_und_b1_naca0012.md`, `phase4_stator_cooling_ring_v2.md`, `phase4_b1_2d_fix_und_naca2d_benchmark.md`, `phase4_presets_benchmarks_und_calibration_fix.md`
- **Alte v1-Pläne (vor 21.11.2026):** `ROADMAP_v1_2026-11-08.md`, `fem_ausblick.md` u. a.
