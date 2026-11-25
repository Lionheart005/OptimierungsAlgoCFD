# Phase 4 — Projektvorbereitung (Literatur-Benchmarks, Showcase & Repo-Hygiene)

> **Status:** ✔ Abgeschlossen (24.11.2026 — umgesetzt via `/delegate`-Pipeline mit Peer-Reviewer `Gemini 4 Hydrogen (XHigh)` bis beidseitiges `APPROVE`, **0 Warnungen / 0 Fehler**, **1026/1026 Unit-Tests** & **5/5 `--smoke-test`-Läufe** grün; **keine** Phase-4-Rechenläufe ausgeführt, strikter Core-Feature-Freeze eingehalten!)
> **Ziel:** Alle Projekte für die Phase-4-Validierung (4 dedizierte Literatur-Benchmarks + 1 reales, 3D-druckbares 1-zu-12-Fahrzeug-Showcase-Projekt) sauber vorbereiten, Docker auf Phase 5 verschieben, `.vscode/` aus Git entfernen und `docs/report/` im `.gitignore` für den späteren LaTeX-Bericht anlegen.

---

## 1. Ziel & Kontext
- Bestehende Optimierungsbeispiele (`Naca2D`, `MantaAuv`, `PipeBend90`) sowie der gesamte FluidForge-Kern (`src/FluidForge/`) blieben unberührt (strikter Core-Feature-Freeze).
- Jede Literatur-Benchmark (B1, B2, B3a/B3b) und das große Showcase-Projekt erhielten ein eigenes Projekt unter `src/projects/` inkl. strikt **2-Absatz-`README.md` auf Englisch**.
- Es wurden noch keine Phase-4-Simulationen gestartet; alle Projekte sind verifiziert bereit für den Start von Phase 4.

---

## 2. Architektur & Getroffene Entscheidungen (inkl. Peer-Reviewer-Konsens)
Abgestimmt im Pre-Decision-, Post-Implementation- und Delta-Review mit `reviewer` (`Gemini 4 Hydrogen (XHigh)` — finales `APPROVE`):

1. **Strikter Core-Feature-Freeze:** Keine Änderungen in `src/FluidForge/Core/` oder `src/FluidForge/Solvers/`. Alle Benchmark- und Showcase-Anforderungen wurden sauber auf Projekt-Ebene gelöst.
2. **B1 — `BenchmarkNaca0012` (`External2D` / SU2):**
   - Modifiziertes NASA-TMR-NACA-0012 mit geschlossener Hinterkante (`-0.1036`), Sehne `c = 1000 mm = 1.0 m`, $Re_c = 6 \cdot 10^6$, $M = 0.15$, SA-Modell, 2 OperatingPoints (`Alpha0` 0°, `Alpha10` 10°), `External2D:VerificationFine`, `1×1`, `ChampionVerification.Mode = "Off"`.
   - Aktive Parameter: `MaxThickness` (`12.0`, `[11.5, 12.5]`) und `MaxCamber` (`0.0`, `[0.0, 0.5]`); `CamberPosition = 40.0` intern fixiert (kein `DEAD`-Parameter bei `MaxCamber = 0`).
3. **B2 — `BenchmarkPipeBend90` (`Internal3D` / OpenFOAM + `ShapeKernel`):**
   - Glatter 90°-Krümmer nach Idelchik/Miller: `PipeRadiusMm = 15.0` ($D = 30\text{ mm}$), `BendRadiusRatio = 3.0` ($R_c/D = 1.5$, Bounds `[2.8, 3.2]`), `RadiusModulationMid = 1.0` (`[0.95, 1.05]`), feste Ein-/Auslaufstrecken `InletStraightRatio = 10.0` ($5D$) und `OutletStraightRatio = 20.0` ($10D$), Wasser $v = 3.34\text{ m/s}$ ($Re_D \approx 10^5$), `TargetYPlus = 25.0` (passend zu `nutkWallFunction`).
   - Fitness protokolliert `TotalLossCoeff_Zeta`, Blasius-`StraightFriction_Zeta` und `BendLossCoeff_Zeta`.
4. **B3a & B3b — `BenchmarkSuboffOpenFoam` & `BenchmarkSuboffSu2` (`External3D` Zwillingsprojekte):**
   - Exakte DTRC/SHD-1298-01 DARPA-SUBOFF-AFF-1-Polynome (Groves, Huang & Chang 1989) im Maßstab 1:10 (`L = 435.61 mm`, `D = 50.80 mm`) als direktes analytisches Rotations-Mesh (`DarpaSuboffHullBuilder`, `DirectGeometryKernel` — kein PicoGK-Voxel-Treppeneffekt, exakte `WettedArea ≈ 0.05988 m²`, `Vol ≈ 6.987e5 mm³`).
   - Beide nutzen `SymmetryPlane = "Y0"`, $Re_L = 1.2 \cdot 10^7$ ($U = 3.05\text{ m/s}$, $\rho = 999.1$, $\mu = 1.1062 \cdot 10^{-4}\text{ Pa}\cdot\text{s}$), `External3D:VerificationFine`, `1×1`, `ChampionVerification.Mode = "Off"`, aktive Parameter `RadiusScale` & `MidbodyLengthScale` (`1.0`, `[0.95, 1.05]`).
   - `BenchmarkSuboffOpenFoam`: `BaseCellSizeMm = 80.0` (~14k `blockMesh`-Zellen statt Mrd.-OOM!), `SurfaceRefinementLevelMin/Max = 5/6`, `BoundaryLayerLayers = 8`, explizites `ReferenceArea = 0.05988` (halbiert auf `Aref = 0.02994` bei `Y0`) und `TargetYPlus = 80.0` (sichere Marge über alle `Cal`-Stufen mit `nutkWallFunction`).
   - `BenchmarkSuboffSu2`: `CfdProjectDefinition` mit `CreateKernel() => new DirectGeometryKernel()`, `SST`, `TargetYPlus = 1.0`, `DetailBox = true`.
5. **Showcase — `StatorCoolingRing` (`Internal3D` / OpenFOAM + `LEAP71_ShapeKernel` + `BoTorch` `qNEHVI`):**
   - Literaturverifiziertes reales Fahrzeugbauteil: **1-zu-12 E-Motor-Hairpin-Stator-Kühlmittel-Ringverteiler** (radialer Seiteneinlass Ø 13 mm bei `X = -105 mm`, 10 L/min Wasser 20 °C → 12 axiale Auslassdüsen Ø 4 mm `outlet_01`..`outlet_12` auf $R_{\text{pitch}} = 65\text{ mm}$ in `Z = 0`, 4 feste M5-Schraubenlöcher auf $R_{\text{bolt}} = 80\text{ mm}$ bei 45°/135°/225°/315°).
   - 8 unvoreingenommene Parameter (Baseline = klassischer konstanter Ring mit vertikalen Auslässen; Bounds öffnen organische LEAP-71-Geometrien): `RingRadiusRatio`, `RingTaperRatio`, `InletSplitBlend`, `BranchHeightMm`, `BranchArcSweepDeg`, `FarSideElevationMm`, `RingRadialShiftMm`, `NearPortThroatRatio`.
   - Dual-STL-Export: Multi-Patch-Fluidvolumen + 3D-druckbare Solid-STL (`*_printable.stl` inkl. konformer 2,5-mm-Schale, Flansch, 4 M5-Schraublaschen, Einlasskragen; nur für analytisch feasible Varianten erzeugt).
   - Pre-CFD-Constraints inkl. `OuterRadiusMm`, `TotalHeightMm`, `MinBoltClearanceMm`, `MinBranchRadiusMm`, `Volume` und `PrintableSolidVolume`; Multi-Objective (`PressureDrop` + `FlowImbalance`) mit `BoTorch` `qNEHVI` + abgesichertem `ScalarFitnessOverride`; `BaseVoxelResolution = 0.4`, `AutoVoxelResolution = true`, `TargetYPlus = 25.0`.
6. **Repo-Hygiene & Docker-Verschiebung:**
   - `.vscode/` in `.gitignore` aufgenommen und via `git rm -r --cached .vscode` aus Git entfernt (lokale Dateien erhalten).
   - `docs/report/` lokal angelegt und in `.gitignore` aufgenommen.
   - Docker-Setup von Phase 4 nach Phase 5 verschoben ([`phase5_docker_container.md`](phase5_docker_container.md), aktualisiert in [`ROADMAP.md`](ROADMAP.md), [`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md) und [`phase5_llm_code_evolution_ausblick.md`](phase5_llm_code_evolution_ausblick.md)).

---

## 3. Umsetzung & Arbeitsstand
- [x] Literaturrecherche & Verifikation des realen Fahrzeug-Showcase-Bauteils (`StatorCoolingRing`)
- [x] Pre-Decision-Review mit `reviewer` (`gemini-4-hydrogen-xhigh`) bis volles `APPROVE`
- [x] AP 1 (`worker-hygiene`, `medium`): `.gitignore`, `.vscode` aus Git-Index entfernt, `docs/report/` angelegt, Docker auf Phase 5 verschoben
- [x] AP 2 (`worker-benchmarks`, `high`): `BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2` + `tests/FluidForge.Tests/Phase4BenchmarksTests.cs` + je 2-Absatz-`README.md`
- [x] AP 3 (`worker-showcase`, `high`): `StatorCoolingRing` + `tests/FluidForge.Tests/StatorCoolingRingTests.cs` + 2-Absatz-`README.md`
- [x] AP 4 (Supervisor): Verifikation (`dotnet build -warnaserror` 0W/0E; **1026/1026 Unit-Tests** grün [1022 + 4 `BayesRealDataReplayTests`]; `--print-config` & `--smoke-test` auf allen 5 neuen Projekten Exit 0)
- [x] AP 5 (Supervisor ⇄ `reviewer`): Post-Implementation-Review + Delta-Review bis finales `APPROVE`, alle Swarm-Agenten sauber beendet

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
- **Reviewer-Fund #1 (`blockMesh`-OOM bei `External3D` OpenFOAM):** `External3D:VerificationFine` setzt `BaseCellSizeMm = 1.2 mm`, was bei einem 9-m-Windkanal > 3 Mrd. Hintergrundzellen ergäbe. Gelöst per Projekt-Override `BaseCellSizeMm = 80.0` + `SurfaceRefinementLevelMin/Max = 5/6` (~14k Hintergrundzellen, ~1,25 mm auf dem Rumpf).
- **Reviewer-Fund #2 (`TargetYPlus = 1.5` vs. `nutkWallFunction`):** Bei `RelativeLayerSizes = true` und Wall-Functions bricht `CalibrationEvaluator` ab, wenn `YPlus_P95 > TargetYPlus * 3`. Gelöst per `TargetYPlus = 25.0` (`BenchmarkPipeBend90`, `StatorCoolingRing`) bzw. `80.0` (`BenchmarkSuboffOpenFoam`).
- **Live-`--smoke-test`-Fund #1 (Performance `TrimWithPortPlanes` bei 13 Ports):** `MultiPatchFluidExporter.TrimWithPortPlanes` voxelisiert je Port eine `400×400×20 mm`-Box (bei 13 Ports × 2 Aufrufe = 26× Riesen-Boxen!). Durch `TrimPortOverhangsCompact(...)` mit nur 2 kompakten lokalen Boxen (`Z <= 0`, `X <= -105`, `trimPorts: false`) massiv beschleunigt.
- **Live-`--smoke-test`-Fund #2 (`ShapeKernel`-NURBS-Rundungsbug bei `Frames`):** `Frames(points, Vector3.UnitZ, ...)` lieferte bei `nSamples = 117` (`probe_2_Min`) am Rand `fLengthRatio = 1.0f` durch `ControlPointSpline`-Knoten-Rundung einen Nullvektor (`Local Z Coordinate has a length of Zero!`). Umstellung auf `Frames.EFrameType.Z` (Ring/Y-Arme) und `Frames.EFrameType.CYLINDRICAL` (Auslassarme) umgeht diesen `ShapeKernel`-Bug dauerhaft.
- **Hinweis für Serverläufe (`BIC_12`):** Bei `StatorCoolingRing` auf dem ersten Serverlauf `MinLayerCoveragePercent` (`50 %`) an den Ø 4-mm-Düsen kurz im Auge behalten (ggf. per `solvers/openfoam.json` nachjustieren, falls `snappyHexMesh` an engen Düsen weniger Layer extrudiert).

---

## 5. Ausstehende Arbeitsschritte & Nächste Schritte (Handover für Folge-Agenten)
1. **Optionaler Commit & Deploy auf `BIC_12`:** Alle vorbereiteten Dateien (`git status` sauber prüfen) committen; dabei bleiben `.vscode/` und `docs/report/` ignoriert.
2. **Start von Phase 4 ([`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md)):**
   - Zuerst `AP 4.1` (Zero-to-First-Run- & `sim.ps1 doctor`-Audit),
   - dann `AP 4.2` (Benchmark-Suite `--calibrate` + Validierungsläufe für `BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2`),
   - danach `AP 4.3` (`StatorCoolingRing` Pareto-Optimierungslauf mit `BoTorch` `qNEHVI`, ParaView-Renderings & 3D-Druck der `*_printable.stl`-Varianten),
   - abschließend `AP 4.4` (englischer LaTeX-Bericht unter `docs/report/` & Community-Release).
- *Hinweis für nachfolgende Modelle: Alle 5 Phase-4-Projekte sind vollständig gebaut, unit- und smoke-getestet; es wurden noch keine Phase-4-Simulationen ausgeführt.*
