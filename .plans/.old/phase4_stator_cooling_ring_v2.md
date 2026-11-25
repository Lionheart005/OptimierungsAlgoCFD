# Phase 4 Showcase — `StatorCoolingRing v2` (Organisch-Dendritischer, Asymmetriefähiger E-Motor-Kühlverteiler)

> **Status:** ✔ Abgeschlossen (25.11.2026 — umgesetzt via `/delegate`-Pipeline mit gleichrangigem Peer-Reviewer `Gemini 4 Hydrogen (XHigh)` & Worker `Gemini 4 Hydrogen (High)` bis finales beidseitiges **`APPROVE`**; `dotnet build -warnaserror`: **0 Warnungen / 0 Fehler**; **1073/1073 Unit-Tests** grün; lokaler `--smoke-test` **`PASS (6/6 Probes, 13/13 echte Fluid-Sensitivität, 0 Warnungen)`**; strikter Core-Feature-Freeze eingehalten!)  
> **Ziel:** Komplettüberarbeitung des Phase-4-Showcase-Projekts `StatorCoolingRing` weg vom starren Kreisrohr hin zu einem vollwertigen, 3D-druckbaren, mechanisch-physikalisch realistischen **Computational-Engineering-Funktionsbauteil** mit mehrstufiger organischer Verästelung (`1 -> 2 -> 6 -> 12`), freier Symmetrie-/Asymmetrie-Entscheidung des Optimierers, realistischer asymmetrischer Störkontur (EV-3-Phasen-HV-Kabelbaum + Sensorleitung), 4× M5-DIN-912-Zylindersenkungsdomen, G3/8"-Einschraubgewinde-Boss inkl. SW-24-Sechskant & Stützkonsole, O-Ring-Dichtnuten, Passstiftbohrungen sowie exportierbarem Umgebungs-Referenzmodell (`*_context_environment.stl`).

---

## 1. Ziel & Kontext
- Die erste Version von `StatorCoolingRing` erzeugte lediglich ein rundes Kreisrohr mit 12 identischen Abgängen, konstanter 2,5-mm-Wandstärke, freitragendem Einlassstutzen, unvollständigen Bauraum-Constraints (kein Innenradius-Keepout, Schrauben-Keepout endete bei `Z = 12 mm`) und fehlenden realen Anschluss-/Befestigungsdetails.
- **Strikter Core-Feature-Freeze eingehalten:** Keine Änderungen in `src/FluidForge/Core/` oder `src/FluidForge/Solvers/`. Alle Erweiterungen leben sauber modularisiert unter `src/projects/StatorCoolingRing/` und `tests/FluidForge.Tests/StatorCoolingRingTests.cs`.
- **Pareto-Ziele (unverändert 2-Ziel-Pareto):** `PressureDrop` (Min) und `FlowImbalance` (Min) via `BoTorch` `qNEHVI`. Keine Masse-Zielfunktion — stattdessen vollständige, harte physikalische Bauraum-, Kollisions- und Volumen-Constraints (Pre-CFD).

---

## 2. Architektur & Getroffene Entscheidungen (Konsens Supervisor ⇄ `reviewer` `APPROVE`)

Abgestimmt im zweistufigen Pre-Decision Review, Zwischen-Freigabe (`BoltCircleRadiusMm = 84.0`), Post-Implementation Review und Delta Review mit `reviewer` (`gemini-4-hydrogen-xhigh`, finales Votum **`APPROVE`**):

1. **Modulare Projektstruktur unter `src/projects/StatorCoolingRing/`:**
   - `StatorCoolingRingTypes.cs`: Konstanten, `StatorCoolingRingTargets`, 13 aktive Parameter (`StatorCoolingRingParams`), `ObstacleCapsule`, `StatorCoolingRingPackagingReport`, `BranchCurveSamples`.
   - `StatorCoolingRingTopologyBuilder.cs`: Reine Mathematik/Kinematik (`SampleCubicHermite`, 1→2→6→12 Verästelung, frame-invarianter 45°-DfAM-Teardrop `EvaluateTeardropCrossSectionRadius`, lokal variable Wandstärken, analytische 3D-Bauraum-/Bolzen-/HV-Störkontur-SDF-Prüfung) + PicoGK-Fluidvolumen- & Variable-Wall-Shell-Aufbau.
   - `StatorCoolingRingSolidBuilder.cs`: PicoGK + `LEAP71_ShapeKernel` Solid-Engine für das einbaufertige 3D-Druckteil (`*_printable.stl`) sowie das transparente Umgebungsmodell (`*_context_environment.stl`).
   - `StatorCoolingRingGeometryGenerator.cs`: Orchestriert Multi-Patch-Fluidvolumen, `MultiPatchFluidExporter` (mit festem `FixedFluidSeedPoint = (-100, 0, 20.0)`), Solid- und Context-Export.
   - `StatorCoolingRingGeometryConstraints.cs`: 9 harte Pre-CFD-Constraints (`InnerRadiusMm`, `OuterRadiusMm`, `TotalHeightMm`, `MinBoltClearanceMm`, `MinObstacleClearanceMm`, `MinBranchRadiusMm`, `Volume_Min/Max`, `PrintableSolidVolume`).
   - `StatorCoolingRingFitnessCalculator.cs`: 2-Ziel-Pareto (`PressureDrop`, `FlowImbalance`) + erweiterte `RawComparisonMetrics` (inkl. `InnerRadiusMm`, `MinObstacleClearanceMm`).

2. **Feste industrielle Interfaces & realistische EV-Stator-Umgebung:**
   - **12 axiale Auslassdüsen (`outlet_01..outlet_12`):** Ø `4.0 mm` (`R = 2.0 mm`) auf `Z = 0`, `R_pitch = 65.0 mm`, Winkel `theta_k = 15° + 30°k`.
   - **Fester radialer Einlass (`inlet`):** Ø `13.0 mm` (`R = 6.5 mm`) fest bei `(-108.0, 0.0, 20.0)` mm (`FixedFluidSeedPoint = (-100.0, 0.0, 20.0)` — 100 % parameterinvariant!).
     - **G3/8"-Einschraubgewinde-Boss (DIN EN ISO 228-1 / DIN 3852):** Dreigeteilt — DIN-3852-Planflächenkragen (`X = -108..-106.5`, `R = 11.8`), komplett freiliegender **SW-24-Sechskant** (`X = -106.5..-96.5`, `inscribedRadius = 12.0 mm` ohne überlagernden Zylinder -> 24-mm-Gabelschlüssel passt!), Verstärkungshals (`X = -96.5..-85.5`, `R = 11.8`); innen Gewindekernloch Ø `15.0 mm` (`R = 7.5`, Tiefe `12 mm`) + 45°-Einführfase + konischer Übergang auf Ø `13.0 mm`.
     - **Integrierte Einlass-Stützkonsole:** `consoleFoot` (`Z = 0..6.5`, `X = -97..-78`, `Y = ±13`) + `consoleWeb` (`Z = 5..18.5`, `X = -95.5..-80.5`, `Y = ±5`) mit explizitem `LocalFrame(pos, UnitZ, UnitX)` + Seitenstreben zum Flansch (`165°/195°`) — bündig auf `Z = 0`, hinter dem Sechskant, hält `180°`-Passstift frei.
   - **4× M5-Befestigungsdome mit DIN-912-Zylindersenkung:** auf `R_bolt = 84.0 mm` bei **`60°, 120°, 240°, 300°`** (Dom-Außenradius `92.2 <= 94.0 mm`, `27.1 mm` Düsenabstand, > `30 mm` zum HV-Kabel!):
     - M5-Durchgangsbohrung Ø `5.5 mm` (`R = 2.75`, `Z = 0..7.0 mm` Klemmauflage),
     - DIN-912-Zylindersenkung Ø `10.0 mm` (`R = 5.0`, `Z = 7.0..60.0 mm`, Dome-Kragen `R = 8.2`, `H = 13.5 mm`),
     - **Durchgängiger Werkzeug-Keepout-Zylinder** `R = 6.2 mm` über die volle Bauhöhe `Z = 0..54.0 mm`!
     - **4× Adaptive organische Lastpfad-Rippen:** Gekrümmte `Lattice`-Streben von `R = 75.5, Z = 7.0` hoch zum jeweiligen Trunk bei `60°/120°/240°/300°`.
   - **Bodenflansch (`Z = 0..6.0 mm`, `R = 53.0..76.8 mm`):**
     - 2 konzentrische O-Ring-Dichtnuten (für Ø `2.5 mm` Schnur: Breite `3.2 mm` × Tiefe `2.0 mm` = exakt `8×5` Voxel bei `0.4 mm`!) bei `R = 56.8 mm` & `72.4 mm`,
     - 2× Ø `4.0 mm` Passstiftbohrungen (Tiefe `6.0 mm`) bei `R = 65.0 mm`, `theta = 0°` & `180°`.
   - **Realistische asymmetrische Störkontur (EV 3-Phasen-HV-Kabelbaum + NTC/Resolver-Sensorleitung bei `theta = 90°`):**
     - `HvPhaseRiser` `(0, 43.0, 0..13.8), R = 4.2`, `HvPhaseConduit` `(0, 43.0..102.0, 13.8), R = 4.2`, `SensorRiser` `(6.6, 42.5, 0..13.8), R = 2.4`, `SensorConduit` `(6.6, 42.5..102.0, 13.8), R = 2.4` — vollständig entkoppelt von Flansch & O-Ring-Nut (`6 mm` innen, `3.6 mm` über Flansch, `3.4 mm` zu `outlet_03`).
     - Exportiert als **`*_context_environment.stl`** (inkl. Rotor-Innenhülse `R = 44..45.8`, Außenring `R = 94.2..96.2`, Deckelring `Z = 54`, 4× M5-DIN-912-Schrauben + Werkzeugsäulen und alle 4 HV-/Sensor-Kapseln) für transparente ParaView-/Blender-Überlagerung.

3. **Hierarchische organische Fluid-Topologie (`1 -> 2 -> 6 -> 12`) & stetige 13D-Parametrisierung:**
   - **13 stetige Parameter** (`project.json`, `16 × 6 = 96` Evals in `simulation.json`, `BoTorch qNEHVI`): `TrunkBaseRadiusRatio`, `TrunkTaperRatio`, `InletSplitAsymmetry`, `InletSplitSpread`, `TrunkBaseHeightMm`, `FarSideElevationMm`, `ObstacleArchHeightMm`, `TrunkRadialAsymmetryMm`, `CrossSectionAspect`, `BranchSplitHeightFraction`, `PrimaryBranchSweepDeg`, `BranchHubRadialOffsetMm`, `NearToFarThroatBias`.
   - **Frame-invarianter 45°-DfAM-Teardrop:** In `SurfaceModulation` via `dot(dirWorld, +Z)` mit exakter 45°-Ellipsentangente `|x| + z = sqrt(a^2 + b^2)` (verifiziert am 3D-STL-Schnitt bei `X = 0`: exakter 45,0°-Dachwinkel!).
   - **Organische Außenhülle:** Lokal variable Wandstärken (`2.5..4.2 mm`) + kontrolliertes Voxel-Closing `voxOverOffset(2.0f, 0.0f)` + `voxSmoothen(0.5f)` **vor** Abzug des exakten Fluidkanals und der Funktionsflächen.
   - **OpenFOAM-Mesh (`solvers/openfoam.json`):** `BaseCellSizeMm = 1.6`, `SurfaceRefinementLevelMin = 2`, `Max = 3`, `BoundaryLayerLayers = 4`, `TargetYPlus = 25.0`.

---

## 3. Umsetzung & Arbeitsstand
- [x] Kontext-Analyse, Code-Audit & Pre-Decision Review mit `reviewer` (`gemini-4-hydrogen-xhigh`, Votum **`APPROVE`**)
- [x] **AP 1 (`worker-stator-v2`, `high`):** Komplette Modularisierung & Umsetzung (`StatorCoolingRingTypes.cs`, `StatorCoolingRingTopologyBuilder.cs`, `StatorCoolingRingSolidBuilder.cs`, `StatorCoolingRingGeometryGenerator.cs`, `StatorCoolingRingGeometryConstraints.cs`, `StatorCoolingRingFitnessCalculator.cs`, `project.json`, `simulation.json`, `solvers/openfoam.json`, `README.md`, `tests/FluidForge.Tests/StatorCoolingRingTests.cs`)
- [x] **AP 2 (Verifikation & Post-Implementation Review):** Supervisor- & Reviewer-Audit deckten 5 gezielte Fehler auf (s. Abschnitt 4) -> gebündelter Rework-Pass durch `worker-stator-v2`
- [x] **AP 3 (Finale Verifikation & Delta-Review `APPROVE`):**
  - `dotnet build FluidForge.sln -warnaserror`: **0 Warnungen / 0 Fehler**
  - `dotnet test FluidForge.sln --no-build --filter "Category!=Integration"`: **1073/1073 bestanden** (davon 12/12 in `StatorCoolingRingTests`)
  - Lokaler `--smoke-test` in frisch bereinigtem Ordner `results/StatorCoolingRing/smoke-test/`: **`PASS (6/6 probes valid, 13/13 parameter checks passed, 0 Warnungen)`**, alle 6 Probes watertight (`Open=0, NonMan=0`), echte Fluid-Sensitivität (`MaxRelDelta = 1.415E-3 .. 1.681E-1`, alle 13 `SHA256 changed`)
  - Python-3D-Geometrie-Audit auf `results/StatorCoolingRing/smoke-test/probe_1_Base/Model_Gen0_Var1_printable.stl` (`1,395,036` Tris, `69.8 MB`, Volumen `146.2 cm³`) + `Model_Gen0_Var1_context_environment.stl` (`49.0 MB`): `consoleFoot` bündig auf `Z = 0` (`8370` Z=0-Vertices), SW-24-Sechskant exakt `12.00 mm` Schlüsselweite, 45,0°-Teardrop-Dachwinkel verifiziert, M5-DIN-912-Senkungen/O-Ring-Nuten/Passstifte/Rippen alle verifiziert, **0 HV-/Sensor-Kollisionsvertices (`hvHits=0, sensHits=0`)**!
  - Finales Delta-Review mit `reviewer` (`gemini-4-hydrogen-xhigh`): **`APPROVE`**; alle Swarm-Worker sauber beendet.

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
- **Pre-Decision-Funde (alle vor Umsetzung bereinigt):**
  1. HV-Riser ursprünglich bei `R = 72 mm` hätte Bodenflansch & O-Ring durchdrungen -> in innere Wickelkopfkrone (`R = 43 mm`) verlegt.
  2. `Frames.EFrameType.CYLINDRICAL`/`Z` richten `fPhi = 0` nicht nach `+Z` aus -> frame-invariante Projektion `dot(dirWorld, +Z)` mit exakter 45°-Tangentenformel.
  3. Unstetige `FarSideBridgeRatio < 0.20`-Klippe entfernt; stetige 13D-Parametrisierung + `16×6 = 96` Evals (`min_init = 26`).
  4. Vorab-Rechnung durch `worker-stator-v2`: Bei durchgehendem Bolzen-Keepout (`Z = 0..54`) war `BoltCircleRadiusMm = 80.0` zu eng für `Base`/`Mid` -> mit `reviewer`-`APPROVE` auf `DefaultBoltCircleRadiusMm = 84.0f` erhöht (`92.2 <= 94.0 mm`).
- **Post-Implementation-Review-Funde (alle im Rework-Pass behoben):**
  1. `BaseBox` extrudiert `fLength` von `pos.Z` aufwärts (nicht zentriert) und `new LocalFrame(pos, Vector3.UnitZ)` setzt `LocalX = +Y` (90° gedreht!) -> explizites `new LocalFrame(pos, Vector3.UnitZ, Vector3.UnitX)` mit `pos.Z = 0.0` (`consoleFoot`) und `pos.Z = 5.0` (`consoleWeb`) hinter dem Sechskant.
  2. Zylindrischer `bossCyl` (`R = 12.2`) überlagerte SW-24-Sechskant (`12.0 mm`) -> Boss sauber dreigeteilt (`-108..-106.5` DIN-3852-Kragen, `-106.5..-96.5` freiliegender SW-24-Sechskant, `-96.5..-85.5` Hals).
  3. `exportPrintable = variant <= 6` maskierte in `SmokeTestCommand` die 13-Parameter-Sensitivität künstlich über `PrintableSolidVolume` (`Mid > 0` vs. `Sens = 0`) -> auf `iteration > 0 || variant == 1` umgestellt (`Mid` & `Sens_*` symmetrisch auf Fluid-Sensitivität geprüft) und `*_context_environment.stl` bei `iteration >= 1` einmalig geschrieben.
  4. Mini-Typo `Vector3.UnitY` doppelt in `StatorCoolingRingTests.cs:189` -> `-Vector3.UnitY`.
- **Hinweis für den ersten Serverlauf (`BIC_12`):**
  - Ein kurzer `snappyHexMesh`-Check (`Level 2/3` bei `BaseCellSizeMm = 1.6`) auf `BIC_09` lief in das 150s-Timeout / SSH-Hänger und wurde sauber abgebrochen. Beim Start von `AP 4.3` auf `BIC_12` im ersten `StandardRans`-Kalibrierungsschritt kurz die resultierende Zellzahl & Meshing-Zeit von `Level (2, 3)` im Log prüfen — falls `> 1.5M` Zellen oder `> 3 min/Variante`, `SurfaceRefinementLevelMax` in `solvers/openfoam.json` einfach auf `2` (`0.4 mm` Wandzellen) setzen.

---

## 5. Ausstehende Arbeitsschritte & Nächste Schritte (Handover für Folge-Agenten)
1. **Visuelle Kontrolle durch Leonardo:** Frische v2-STLs unter `results/StatorCoolingRing/smoke-test/probe_1_Base/` öffnen und gemeinsam anschauen:
   - `Model_Gen0_Var1_printable.stl` (undurchsichtig, Metall/Farbe)
   - `Model_Gen0_Var1_context_environment.stl` (halbtransparent darüber legen — zeigt Stator-Innenbohrung, Gehäuse-Bauraum, 4× M5-DIN-912-Schrauben + Werkzeugschächte und den asymmetrischen HV-Kabelbaum + Sensorleitung!)
   - `Model_Gen0_Var1.stl` (internes Fluidvolumen mit 45°-Teardrop & 1→2→6→12 Verästelung)
2. **Nächster Phase-4-Ablauf (unverändert laut `.plans/phase4_presets_benchmarks_und_calibration_fix.md`):**
   - Zuerst B1–B3 Benchmarks auf `BIC_12` abschließen,
   - danach `StatorCoolingRing v2` auf `BIC_12` deployen (`.\scripts\sim.ps1 deploy -Server BIC_12 -Project StatorCoolingRing`), kurz Meshing-Zeit/Zellzahl von `Level (2, 3)` prüfen und die vollständige `BoTorch` `qNEHVI` Pareto-Optimierung (`AP 4.3`, `16 × 6 = 96` Varianten + Auto-Kalibrierung + Feinprüfung) durchführen!
