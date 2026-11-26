# Gesamt-Roadmap v2: FluidForge (Plan A)

> **Stand: 26.11.2026** — **Schritt 0 sowie Phasen 1, 2, 3 und 3.5 vollständig abgeschlossen.** Aktuell läuft **Phase 4** (Validierung, Physik-Benchmarks B1–B3, Showcase `StatorCoolingRing`, Übernahme der vaskulären Engine ins Hauptprogramm, Gesamtauswertung/LaTeX-Bericht & Community-Release).  
> **Sprache:** Pläne und Diskussionen in `.plans/` bleiben bewusst **auf Deutsch**. Produktcode, Logs, Fehlermeldungen, CSVs, Tests und Nutzer-Doku sind konsequent auf **Englisch**.  
> **Archiv:** Alle abgeschlossenen Phasen (`1`–`3.5`) sowie ältere Zwischenpläne und v1-Dokumente liegen unter [`.plans/.old/`](.old/).

---

## 1. Strategische Vision & Zielbild

`FluidForge` entwickelt sich zu einer **schlanken, physikalisch verifizierbaren, international nutzbaren Open-Source-Plattform für Computational Engineering** (inspiriert von LEAP 71 / Noyron, `PicoGK` + `LEAP71_ShapeKernel` und Code-Evolution à la AlphaEvolve/OpenEvolve):

- **Klare Nische (Plan A):** Komplexe, topologisch variable, 3D-druckbare **interne Fluidik** (Rohrkrümmer, Strömungsverteiler/Manifolds, emergente Gefäßnetze, Kühlkanäle) mit **`PicoGK` + `LEAP71_ShapeKernel` + `OpenFOAM`**, ergänzt um die NASA-TMR-validierte 2D-/3D-Außenumströmungs-Pipeline mit **SU2** und **OpenFOAM**.
- **Alleinstellungsmerkmal („Verifiable Engineering Harness“):** Deterministische Prüftore (`--smoke-test`, `checkMesh`, Massenbilanz, `y+`, Konvergenzrichter inkl. Wirbel-/Stationaritätserkennung, GCI, Bouncer, Hash-Schutz) garantieren physikalisch belastbare Ergebnisse — für Ingenieure (Phase 4) und später als Verifikations-Backend für **LLM-gestützte Topologie-/Code-Evolution** (Phase 5).
- **Qualitätsprinzip:** Erst physikalisch sauber validieren, skaleninvariante Presets absichern und das Showcase + Gefäßnetz-Modul rund machen — dann auswerten, veröffentlichen und in Phase 5 das LLM andocken.

---

## 2. Verbindliche Grundsatzentscheidungen (`E1`–`E7`)

| # | Thema | Beschlossene Linie |
| :--- | :--- | :--- |
| **E1** | **Alt-Archiv (`MantaAuv`)** | **Erledigt (21.11.2026):** 20-GB-Vollarchiv auf `BIC_12`, 415-MB-Analyse-Paket lokal unter `Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/`. |
| **E2** | **Ziele, Constraints & Optimierer** | Getrennte Pre-CFD- & Post-CFD-Constraints, Single- & Multi-Objective (Pareto). Eingebauter C#-`Bayes` als Zero-Dependency-Standard; optionale JSON-Ask/Tell-Bridge zu Python (`BoTorch` `qLogEI`/`qNEHVI`, `pymoo` `NSGA-II`). |
| **E3** | **Solver-Rollen (OpenFOAM vs. SU2)** | **OpenFOAM** als primärer 3D-Standard (`Internal3D` & nach B3-Validierung auch `External3D`); **SU2** als `External2D`-Standard, lokaler Windows-Testpfad und optionale `External3D`-/kompressible Variante. Beide Solver bekommen einheitlich hohe Konvergenz- und Auto-Meshing-Qualität. |
| **E4** | **Presets & Profi-Transparenz** | 3×3-Presets (`External2D`/`External3D`/`Internal3D` × `FastConcept`/`StandardRans`/`VerificationFine`) mit ~15 sichtbaren Projekt-Keys; **jede** Detail-Einstellung bleibt per JSON überschreibbar (`effective-config.json` & `--print-config`). |
| **E5** | **Sprache & Name** | Projektname **`FluidForge`**; gesamter Produktcode, Logs, CSVs, Tests und Nutzer-Doku vollständig auf **Englisch** (`.plans/` auf Deutsch). |
| **E6** | **Validierung vor Release** | Erst objektive Literatur-Benchmarks (B1–B3) + Optimierer-Benchmark (`Naca2D` 3-Seed ✔) + Showcase (`StatorCoolingRing`) + Übernahme der vaskulären Engine ins Hauptprogramm; danach Gesamtauswertung, Prof-/Literatur-Abgleich der Presets, LaTeX-Bericht und Community-Release. |
| **E7** | **Reihenfolge Phase 5 & FEM** | **LLM-Code-Evolution** und optionales **Docker-/Apptainer-Setup** erst in **Phase 5** nach Community-Feedback. **FEM (`CalculiX`)** bleibt bewusst geparkt (Abschnitt 5). |

---

## 3. Aktueller Stand & Exakte Reihenfolge (Stand 26.11.2026)

```text
  [✔] Schritt 0 + Phasen 1–3.5: Kern, EN, Presets, ShapeKernel, OpenFOAM, BoTorch/pymoo, Multi-Fidelity
                                           │
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ PHASE 4: Validierung, Showcase, Vascular-Kern & Community-Release          │
  │ Masterplan: phase4_validierung_benchmarks_und_release.md                   │
  │                                                                            │
  │  [✔] 4.1  Server-/Ergonomie-Audit & Zero-to-First-Run (BIC_09, doctor)     │
  │  [✔] 4.2a Optimierer-Vergleich Naca2D (3-Seed × 3 Algos, 9/9 ausgewertet)  │
  │  [▶] 4.2b JETZT: OpenFOAM/SU2-Preset-Reife & Physik-Benchmarks B1–B3       │
  │           → Plan: phase4_openfoam_su2_presets_und_benchmarks.md            │
  │           • Naca0012 0°-Diagnose, OpenFOAM-Konvergenz auf SU2-Niveau       │
  │           • MaxCells3D = 15M, skaleninvariantes Auto-Meshing + y+-Layer    │
  │           • Re-Run aller 4 Physik-Benchmarks auf BIC_12 & Literatur-Check  │
  │  [ ] 4.3a Vaskulären Algorithmus (StatorCoolingRing v3) prüfen & verfeinern│
  │           → Plan: phase4_stator_cooling_ring_v3_emergent_vascular.md       │
  │  [ ] 4.3b Showcase StatorCoolingRing auf BIC_12 vollständig rechnen        │
  │           (StandardRans, Kalibrierung, BoTorch qNEHVI Pareto, Feinprüfung) │
  │  [ ] 4.4  Vaskuläre Routing-Engine als Kernmodul & Preset ins Hauptprogramm│
  │           → Plan: phase4_5_vascular_routing_core_engine.md                 │
  │           (FluidForge.Kernels.Vascular + _TemplateVascular3D + Dogfooding) │
  │  [ ] 4.5  Gesamtauswertung, Plots/ParaView, Prof-/Literatur-Preset-Check,  │
  │           LaTeX-Bericht, GitHub-Release & Community-Feedback               │
  └────────────────────────────────────────┬───────────────────────────────────┘
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ PHASE 5: LLM-Code-Evolution & Optionales Container-Setup                   │
  │ Pläne: phase5_llm_code_evolution_ausblick.md + phase5_docker_container.md  │
  │  [ ] 5.1–5.4  Diagnostik-/MCP-Fassade, SHA-Hash-Tor, LLM-Topologie-Loop,   │
  │               Vergleichslauf (Baseline vs. Parametrik vs. LLM) & Bericht 2 │
  │  [ ] 5.5      Optional: Docker-/Apptainer-Toolchain-Container (base/full)  │
  └────────────────────────────────────────────────────────────────────────────┘
```

### Gesamt-Status-Tabelle

| Phase / Schritt | Aktive Plan-Datei | Status (26.11.2026) |
| :--- | :--- | :--- |
| **Schritt 0 & Phasen 1–3.5** | *(archiviert in [`.old/`](.old/))* | **✔ komplett erledigt** (`21.–23.11.2026`) |
| **Phase 4.1 — Audit & `BIC_09`** | [`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) | **✔ erledigt** (`25.11.2026`) |
| **Phase 4.2a — `Naca2D` 3-Seed** | *(archiviert in [`.old/phase4_b1_2d_fix_und_naca2d_benchmark.md`](.old/phase4_b1_2d_fix_und_naca2d_benchmark.md))* | **✔ erledigt** (`9/9` Läufe in `results/Naca2D_Benchmark/summary.md`) |
| **Phase 4.2b — Physik-Benchmarks B1–B3 & Solver-Reife** | **[`phase4_openfoam_su2_presets_und_benchmarks.md`](phase4_openfoam_su2_presets_und_benchmarks.md)** | **▶ NÄCHSTER SCHRITT** (1. Lauf ausgewertet; Fixes geplant) |
| **Phase 4.3a/b — Showcase `StatorCoolingRing` (Review + Lauf)** | [`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md) | **v3-Basis ✔** → als Nächstes: Algorithmus-Review/Verbesserung, dann Großlauf auf `BIC_12` |
| **Phase 4.4 — Vaskuläre Engine ins Hauptprogramm** | [`phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md) | **Geplant** (direkt nach Showcase-Lauf) |
| **Phase 4.5 — Auswertung, LaTeX-Bericht & Release** | [`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) | **Geplant** (nach Abschluss 4.2–4.4) |
| **Phase 5 — LLM-Code-Evolution & Docker/Apptainer** | [`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md), [`phase5_docker_container.md`](phase5_docker_container.md) | **Ausblick / Grobkonzept** (nach Phase 4 & Community-Feedback) |

---

## 4. Rückverfolgbarkeit der alten v1-Restpunkte

Alle offenen Punkte aus `ROADMAP_v1_2026-11-08.md` sind inzwischen abgearbeitet (Phasen 1–3.5 & `Naca2D` 3-Seed ✔) — bis auf die bewusst für den Abschluss von Phase 4 eingeordneten Punkte:
- Großer technischer LaTeX-Bericht, ParaView-Visualisierungen, `THIRD_PARTY_NOTICES.md`-Check & GitHub-Release → **Phase 4.5**.
- FEM-Strukturkopplung (`CalculiX`) → **bewusst geparkt (Abschnitt 5)**.

---

## 5. Geparkte Zukunftsmusik: FEM-Strukturkopplung (nach Phase 4/5)

> **Status:** Zurückgestellt; Vorüberlegungen archiviert in [`.old/fem_ausblick.md`](.old/fem_ausblick.md).  
> **Architektur-Leitplanken (bereits aktiv & per Test geschützt):**
> 1. `Core/` bleibt strikt physik- und geometrieneutral.
> 2. Stufenkette & Artefakte (`ModelRecord.Artifacts`, `SolverStage[]`) ermöglichen später `"Stages": ["Cfd", "Fem"]`.
> 3. Eindeutige Kennwert-Präfixe (`Fem_MaxVonMises`, `Fem_MaxDisplacement`) passen direkt ins Constraint-/Pareto-System.
> 4. Multi-Patch-Geometrie (`inlet`, `outlet`, `wall`) lässt sich direkt um mechanische Zonen (`mount`, `load_surface`) erweitern.
