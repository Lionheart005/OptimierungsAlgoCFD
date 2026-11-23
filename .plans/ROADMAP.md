# Gesamt-Roadmap v2: Große Überarbeitung & Neuausrichtung (Plan A)

> **Stand:** 23.11.2026 — **Schritt 0, Phase 1, Phase 2 und Phase 3 (`LEAP71_ShapeKernel`, OpenFOAM ESI `v2506` `Internal3D`/`External3D`-Overlay, Hybrid-Optimierer-Bridge `BoTorch`/`pymoo`, Multi-Fidelity `TwoStageFilter`, Schemas & Docs; 984/984 Unit-Tests + `BIC_12`-Live-Verifikation) vollständig abgeschlossen; bereit für Phase 4.**  
> **Sprache:** Pläne und Diskussionen in `.plans/` bleiben bewusst **auf Deutsch**. Der gesamte Produktcode, Logs, Fehlermeldungen, CSVs, Tests und Nutzer-READMEs werden ab Phase 1 konsequent auf **Englisch** umgestellt.  
> **Archiv:** Die vorherige Roadmap (`ROADMAP_v1_2026-11-08.md`) sowie alle abgeschlossenen bzw. abgelösten Einzelpläne liegen vollständig unter [`.plans/.old/`](.old/). Alle noch offenen Punkte aus v1 wurden unten sauber übernommen.

---

## 1. Strategische Vision & Zielbild

Dieses Projekt entwickelt sich von einem experimentellen 3D-CFD-Studentenprojekt zu einer **schlanken, physikalisch verifizierbaren, international nutzbaren Open-Source-Plattform für Computational Engineering** (inspiriert von LEAP 71 / Noyron, `PicoGK` + `LEAP71_ShapeKernel` und moderner Code-Evolution à la AlphaEvolve/OpenEvolve):

- **Klare Nische (Plan A):** Komplexe, topologisch variable, 3D-druckbare **interne Fluidik** (Rohrkrümmer, Strömungsverteiler/Manifolds, Mischer, Kühlkanäle, Düsen) mit **`PicoGK` + `LEAP71_ShapeKernel` + `OpenFOAM`**, ergänzt um die bewährte 2D-/3D-Außenumströmungs-Pipeline mit **SU2** (NASA-TMR-validiert).
- **Alleinstellungsmerkmal („Verifiable Engineering Harness“):** Deterministische Prüftore (`--smoke-test`, `checkMesh`, Massenbilanz, `y+`, Konvergenzrichter, GCI, Bouncer, Hash-geschützte Bewertung) garantieren physikalisch belastbare Ergebnisse — erst für menschliche Ingenieure (Phasen 1–4) und später als kugelsicheres Verifikations-Backend für **LLM-gestützte Topologie-/Code-Evolution** (Phase 5).
- **Wichtigstes Qualitätsprinzip:** **Weniger, aber rund.** Keine Parameter-Explosion, keine ungetesteten Features, keine vorzeitige Überfrachtung. Erst entschlacken, validieren und einfach bedienbar machen — dann erst in der Community testen und danach das LLM andocken.

---

## 2. Getroffene Grundsatzentscheidungen (Stand 21.11.2026)

| # | Thema | Beschlossene Linie |
| :--- | :--- | :--- |
| **E1** | **Schritt 0 (`MantaAuv`-Serverlauf)** | **Erledigt (21.11.2026):** Lauf auf `BIC_12` sauber gestoppt; 20-GB-Vollarchiv liegt unter `~/Archive/MantaAuv_2026-11-21_final/` auf `BIC_12`, kompaktes Analyse- & Modellpaket (415,4 MB: alle 49 STLs, 44 CSV-Zeilen, `Bayes_Verlauf.csv`, `Kalibrierung/`, `Quellstand/`, Logs, Champion- & Start-VTUs) liegt lokal unter `Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/`. |
| **E2** | **Zielfunktion, Constraints & Optimierer-Backend** | **Single-Objective + getrennte Pre-CFD- & Post-CFD-Constraints** als einfacher, transparenter Standard; Schnittstelle direkt **multi-objective-fähig (Pareto-Front)**. **Hybrid-Backend:** Gefixter C#-`Bayes` bleibt eingebauter Zero-Dependency-Standard; optionale JSON-Ask/Tell-Bridge zu einem Conda-Python-Env (**BoTorch/Ax** für Constrained `qLogEI`, `qNEHVI` Pareto & Multi-Fidelity, **pymoo** für `NSGA-II/III`). |
| **E3** | **Rollenverteilung OpenFOAM vs. SU2** | **OpenFOAM** wird langfristig der **primäre 3D-Standard-Solver** (zuerst `Internal3D` in Phase 3, nach Benchmark-Validierung auch Standard für `External3D`, inkl. Perspektive auf Transition `kOmegaSSTLM` & Wärme `chtMultiRegionSimpleFoam` — weniger 3D-Doppelpflege, 10× größere Community). **SU2** bleibt gezielt erhalten als: (1) **`External2D`-Standard** (NASA-TMR, echte 2D-Netze), (2) **Windows-native lokale Test-Pipeline** ohne WSL und (3) **optionale `External3D`- / spätere kompressible High-Speed-Variante** (sowie 3D-Basis in Phase 1–2). Alle Detail-Parameter beider Solver bleiben für Profis vollständig per JSON überschreibbar (kein Code-Zwang). |
| **E4** | **Bedienung & Konfigurations-Diät (Progressive Disclosure)** | Radikale Entschlackung der **Standard-Vorlagen** von ~127 sichtbaren Keys auf **~15 Projekt-Keys** durch Presets (`External2D`/`External3D`/`Internal3D` × `FastConcept`/`StandardRans`/`VerificationFine`), Single Source of Truth und modulare englische Doku statt 88-KB-Monolith. **Wichtig:** Der manuelle Profi-Weg geht ausdrücklich **nicht** verloren — **jede** Detail-Einstellung (Solver, Mesher, Grenzschicht, Konvergenz, Optimierer) bleibt transparent per JSON überschreibbar (`Preset` + Einzel-Overrides oder voll manuell), ohne C#-Code ändern zu müssen; `effective-config.json` und `--print-config` zeigen jederzeit alle wirksamen Detailwerte. |
| **E5** | **Sprache & Projektname** | **Umgesetzt in Phase 1 (22.11.2026):** Projekt vollständig in **`FluidForge`** umbenannt; Code, Logs, Fehlermeldungen, Output-Pfade, CSV-Header, Skripte, aktive READMEs und alle Tests sind komplett auf Englisch umgestellt (`.plans/` bleibt Deutsch; das Alt-Handbuch `BEDIENHANDBUCH.md` wird in Phase 2 `AP 2.1` durch modulare englische `docs/` ersetzt). |
| **E6** | **Benchmarks, Showcase & LaTeX-Bericht** | LaTeX-Dokument wird bewusst **zurückgestellt bis Phase 4** (nach vollständigem Feinschliff). Dauerhafte **Literatur-Benchmarks** (NASA TMR 2D, 90°-Rohrkrümmer 3D intern, DARPA SUBOFF 3D extern) werden eingebaut; das konkrete große Showcase-Projekt bleibt problemunabhängig und wird erst in Phase 4 final gewählt. |
| **E7** | **Reihenfolge LLM & FEM** | **LLM-Anbindung** erst in **Phase 5**, nachdem Kern, OpenFOAM, Benchmarks und Community-Feedback (Phase 4) stehen (Schnittstellen werden aber schon in Phasen 1–3 sauber vorbereitet). **FEM** bleibt bewusst Zukunftsmusik (siehe Abschnitt 6). |

---

## 3. Phasen-Übersicht & Abhängigkeiten

```text
  [✔] Schritt 0: Laufenden MantaAuv-Lauf auf BIC_12 stoppen & doppelt archivieren (21.11.2026)
                                           │
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ Phase 1: Entschlackung, Bugfixes, Architektur-Sanierung, Tests & Englisch  │
  │ Plan: phase1_entschlackung_und_kern.md                                     │
  │ • 6+ gefundene Bugs fixen (GP sn2, GCI, Smoother-Order, CellLimit, Retry)  │
  │ • Core/-Isolation reparieren + Architektur-Test; 4 God-Classes zerlegen    │
  │ • Echte Integrationstests (PicoGK, Gmsh, SU2); Umbenennung & EN-Umstellung │
  └────────────────────────────────────────┬───────────────────────────────────┘
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ Phase 2: Verständliche Bedienung, Presets, Physik & Ziel-/Constraint-Kern  │
  │ Plan: phase2_bedienung_physik_und_ziele.md                                 │
  │ • Konfig-Diät (127 → ~15 Keys über Presets); modulare englische Docs       │
  │ • Trennung Objectives + Pre-CFD- & Post-CFD-Constraints; Bouncer-Fix       │
  │ • Symmetrieebene, besseres SU2-Abbruchkriterium, Multi-Point, --smoke-test │
  │ • C#-Bayes-Feinschliff (Constrained EI, L-BFGS-B, Sobol-QMC)               │
  └────────────────────────────────────────┬───────────────────────────────────┘
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ Phase 3 (Plan A): ShapeKernel, OpenFOAM (3D-Standard) & Hybrid-Optimierer  │
  │ Plan: phase3_plan_a_openfoam_shapekernel_hybrid.md                         │
  │ • LEAP71_ShapeKernel + Multi-Patch-Geometrie (inlet/outlet/wall/seed)      │
  │ • OpenFOAM als 3D-Standard (zuerst Internal3D, danach External3D-Option)   │
  │ • Hybrid-Bridge (JSON Ask/Tell) für BoTorch (Pareto/Multi-Fidelity) & pymoo│
  └────────────────────────────────────────┬───────────────────────────────────┘
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ Phase 4: Validierung, Benchmarks, Feinschliff & Community-Release          │
  │ Plan: phase4_validierung_benchmarks_und_release.md                         │
  │ • STRIKTER FEATURE-FREEZE: Stabilität, 15-Minuten-Quickstart, Feinschliff  │
  │ • 3 Literatur-Benchmarks (NASA TMR 2D, 90°-Krümmer 3D, DARPA SUBOFF 3D)    │
  │ • Großes Showcase-Projekt wählen & rechnen → LaTeX-Bericht → CFD-Community │
  └────────────────────────────────────────┬───────────────────────────────────┘
                                           ▼
  ┌────────────────────────────────────────────────────────────────────────────┐
  │ Phase 5 (Grobkonzept): LLM-Code-Evolution („Open-Source Noyron+AlphaEvolve“)│
  │ Plan: phase5_llm_code_evolution_ausblick.md                                │
  │ • Maschinenlesbare Diagnostik / MCP-Fassade + SHA-Hash-Schutz              │
  │ • Äußere LLM-Schleife mutiert ShapeKernel-Topologien (Human-in-the-Loop)   │
  │ • Großer Vergleichslauf (Baseline vs. Parametrik vs. LLM) + Bericht 2      │
  └────────────────────────────────────────────────────────────────────────────┘
```

| Schritt / Phase | Plan-Datei | Fokus | Status |
| :--- | :--- | :--- | :--- |
| **Schritt 0** | *(in dieser Roadmap dokumentiert)* | `MantaAuv` stoppen, 20 GB auf `BIC_12` + 415 MB lokal archivieren | ✔ erledigt (21.11.2026) |
| **Phase 1** | [`phase1_entschlackung_und_kern.md`](phase1_entschlackung_und_kern.md) | Bugfixes, `Core/`-Sauberkeit, God-Classes, echte Tests, Umbenennung (`FluidForge`) & EN | ✔ erledigt (22.11.2026) |
| **Phase 2** | [`phase2_bedienung_physik_und_ziele.md`](phase2_bedienung_physik_und_ziele.md) | Presets (Konfig-Diät), Objectives & Constraints, Symmetrie, Multi-Point, C#-Bayes | ✔ erledigt (22.11.2026) |
| **Phase 3** | [`phase3_plan_a_openfoam_shapekernel_hybrid.md`](phase3_plan_a_openfoam_shapekernel_hybrid.md) | `LEAP71_ShapeKernel`, OpenFOAM `Internal3D`, Hybrid-Bridge (`BoTorch`/`pymoo`), Multi-Fidelity | ✔ erledigt (23.11.2026) |
| **Phase 3.5** | [`phase3_5_korrekturplan_defaults_und_multifidelity.md`](phase3_5_korrekturplan_defaults_und_multifidelity.md) | Korrekturpaket: `UseStlSmoothing`/`UseRubberBandScaler`-Defaults & Multi-Fidelity-Bugfixes | ✔ erledigt (23.11.2026) |
| **Phase 4** | [`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) + [`phase4_docker_container.md`](phase4_docker_container.md) | Feature-Freeze, 3 Literatur-Benchmarks, optionales Docker-/Apptainer-Setup, Showcase, LaTeX-Bericht, Release | offen |
| **Phase 5** | [`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md) | Grobkonzept LLM-Topologie-Evolution + Verifikations-Harness + Bericht 2 | Ausblick / Grobkonzept |

---

## 4. Übernommene offene Restpunkte aus den v1-Plänen (vollständige Rückverfolgbarkeit)

Damit beim Archivieren der alten Pläne nach `.plans/.old/` nichts verloren geht, ist hier jeder noch offene Punkt aus v1 seinem neuen Zielpaket zugeordnet:

| Alter v1-Plan | Noch offener Punkt | Neu eingeordnet in |
| :--- | :--- | :--- |
| `.old/zeitschaetzung.md` | Deploy & Server-Verifikation von `RunTimeEstimator` und `sim.ps1 status`-Prognose | **Phase 1 (`AP 1.1`)** |
| `.old/netzstrategie_und_su2_abbruch.md` | Server-Validierung & praxistauglicher SU2-Konvergenzabbruch (da `1e-5` bei 3D-Rauschen oft 5.000 Iter. durchlief) | **Phase 2 (`AP 2.4`) & Phase 4 (`AP 4.2`)** |
| `.old/wandschicht_zellzahl.md` | Geplanter `ClassifyAngleDeg`-Retry in Gmsh Schritt 1 bei `Wrong topology` (fehlte noch im Code) | **Phase 1 (`AP 1.1`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | Wiederholungslauf `Naca2D` (Bayes 10×10, 3 Seeds) nach Spätphasen-Fix | **Phase 4 (`AP 4.2`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | `Naca2D`-Grenzkontakt (`MaxCamber=8`, `MaxThickness=6`) fachlich lösen | **Phase 2 (`AP 2.4`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | Alten Server-Ordner `src/Automatisierung_v2` löschen, alte Branches aufräumen, `-RemoteDir` vereinheitlichen | **Phase 1 (`AP 1.1` & `AP 1.6`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | Englische Gesamtfassung (Code, Logs, READMEs, Handbücher) | **vorgezogen in Phase 1 (`AP 1.6`) & Phase 2 (`AP 2.1`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | Große LaTeX-Dokumentation, ParaView-Showcases, GitHub-Release & `THIRD_PARTY_NOTICES.md`-Check | **Phase 4 (`AP 4.3` & `AP 4.4`)** |
| `.old/ROADMAP_v1_2026-11-08.md` | FEM-Integration (`CalculiX`) | **Bewusst geparkt (Abschnitt 5 unten)** |

---

## 5. Geparkte Zukunftsmusik: FEM-Strukturkopplung (bewusst noch NICHT in Planung)

> **Status:** Zurückgestellt für die Zeit nach Phase 4/5 — hier festgehalten, damit es nicht aus dem Gedächtnis verschwindet und spätere Änderungen den Weg nicht verbauen. Alle technischen Vorüberlegungen (CalculiX `ccx`, Gmsh `C3D10`, Drucklasttransfer, Randbedingungs-Zonen) bleiben vollständig archiviert in [`.plans/.old/fem_ausblick.md`](.old/fem_ausblick.md).

**Vier Architektur-Leitplanken, die wir in Phasen 1–3 nebenbei gratis einhalten, damit FEM später schmerzfrei andockt:**
1. **`Core/` bleibt physikneutral** (kein `Drag`, kein `.su2`, kein OpenFOAM-Typ im Kern — wird ab Phase 1 per Architektur-Test erzwungen).
2. **Stufenkette & Artefakte statt festem Einzel-Solver:** `ModelRecord.Artifacts` (Phase 1) und konfigurierbare `SolverStage[]` erlauben später einfach `"Stages": ["Cfd", "Fem"]` (oder `"Fem"` als schnellen Vorfilter vor CFD).
3. **Eindeutige Kennwert-Präfixe & echte Multi-Constraint-Unterstützung:** `Fem_MaxVonMises`, `Fem_MaxDisplacement`, `Fem_FirstEigenfrequency` passen ab Phase 2 direkt als Post-Solve-Constraints oder Pareto-Ziele.
4. **Benannte Funktionsflächen im Geometrie-Generator:** Der Multi-Patch-Vertrag aus Phase 3 (`inlet`, `outlet`, `wall`) lässt sich später direkt um mechanische Zonen (`mount`, `load_surface`) erweitern.
