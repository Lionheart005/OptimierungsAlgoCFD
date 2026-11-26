# Phase 4 — `VerificationFine`-Presets (Manuell, Höchste Detailstufe), Benchmark-Anpassung & Calibration-Bugfix

> **Status:** ✔ Abgeschlossen (25.11.2026 — `/delegate`-Pipeline mit Peer-Reviewer `Gemini 4 Hydrogen (XHigh)` bis finales `APPROVE`; `0` Warnungen / `0` Fehler, **1074/1074** lokale Unit-Tests grün [6 server-/Python-spezifische Tests übersprungen], **4/4** Benchmark-`--print-config` & `--smoke-test` PASS; laufende `Naca2D`-3-Seed-Läufe auf `BIC_12` bleiben unberührt!)
> **Ziel:**
> 1. Alle 3 `*:VerificationFine`-Presets auf manuelle höchste Detailstufe umstellen (`MeshCalibration.Mode = "Manual"`, `ChampionVerification.Mode = "Off"`, feste hohe Auflösung) — ohne Kalibrierung und ohne abschließende Feinprüfung.
> 2. Alle 4 Physik-Benchmark-Projekte (`BenchmarkNaca0012`, `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam`, `BenchmarkSuboffSu2`) sauber über ihre `*:VerificationFine`-Presets ohne Optimierung/Kalibrierung vorbereiten.
> 3. Den auf `BIC_12` entdeckten Non-Monotonic / False-Coarse-Match-Bug in der automatischen Netzkalibrierung (`CalibrationEvaluator` & `MeshCalibration`) unabhängig von den laufenden Seeds beheben und absichern.
> 4. Bericht-Anmerkungen unter `docs/report/report_notes.md` und Handover-Befehle für den sofortigen Benchmark-Start nach Abschluss der laufenden `Naca2D`-Seeds festhalten.

---

## 1. Ziel & Kontext
- Die laufenden `Naca2D`-3-Seed-Läufe auf `BIC_12` (`Bayes`, `IdwBaseline`, `BoTorch` × 3 Seeds bei festem `MeshRefinementFactor = 2`) laufen unverändert weiter: Sie vergleichen gezielt die **Optimierer auf identischer Simulationsbasis** ohne Simulationsunterschiede.
- Die **Physik-Benchmarks (B1–B3)** prüfen dagegen gezielt die **höchsten Solver-Presets (`*:VerificationFine`)** gegen Literaturwerte — ohne automatische Kalibrierung, ohne Optimierung und ohne abschließende Feinprüfung. Zeigen sich physikalische Abweichungen, werden gezielt die Presets nachgeschärft.
- Das **Showcase-Projekt (`StatorCoolingRing`)** zeigt später den kompletten praktischen Optimierungs-Workflow (mittlere Feinheit `StandardRans`, automatische Netzkalibrierung, Pre-/Post-Constraints, Pareto-Optimierung und abschließende Feinprüfung).
- Beim `Naca2D --calibrate`-Lauf auf `BIC_12` trat ein echter Auswahl-Bug zutage: Die grobe Stufe `Level 1` (`factor 2.00`, `CD = 0.01826`) traf `Level 6` (`factor 0.35`, `CD = 0.01884`) zufällig innerhalb `5 %`, obwohl alle feineren Zwischenstufen (`Level 2..5`) um `9–23 %` abwichen (Minimum `0.01446` bei `Level 3`). Das darf nicht als „grob ist näher an der Realität als mittel“ gewertet werden.

---

## 2. Architektur & Getroffene Entscheidungen (Konsens Supervisor ⇄ `reviewer` `APPROVE`)

1. **`*:VerificationFine`-Presets (`src/FluidForge/Core/Configuration/SimulationPresets.cs`):**
   - Alle 3 `*:VerificationFine`-Presets setzen `MeshCalibration: { Mode: "Manual", Tolerance: 0.02f }` (behält `0.02f` für SU2-Drift-Skalierung) und `ChampionVerification: { Mode: "Off" }`.
   - **`External2D:VerificationFine`:** `MeshRefinementFactor = 0.5f` (~450–475k Zellen bei `BenchmarkNaca0012`), `CauchyTolerance = "5e-6"`, `CauchyToleranceFloor = "2e-6"` (1 Restart-Schritt `5e-6 -> 2e-6` über dem `1.23e-6` Venkatakrishnan-Limit-Cycle, enger als `3e-6` wo `Cal4/Cal5` bei `0,54–0,57 %` Drift stoppten).
   - **`External3D:VerificationFine`:** `MeshRefinementFactor = 0.8f`, `CauchyTolerance = "1e-5"`, `CauchyToleranceFloor = "1e-6"` (`BenchmarkSuboffSu2`: Wandzelle `0.96 mm` ohne OOM-Risiko; `BenchmarkSuboffOpenFoam`: ~27k `blockMesh`-Zellen, sicher `< 99,999`).
   - **`Internal3D:VerificationFine`:** `MeshRefinementFactor = 1.0f` (die hohe Detailstufe steckt bereits in `BaseCellSizeMm = 1.2f`, `SurfaceRefinementLevelMin/Max = 2/3`, `BoundaryLayerLayers = 8`, `EndTime = 2000`, `ResidualTarget = 5e-5f` — ein Faktor `< 1` würde bei `BenchmarkPipeBend90` das `MaxGlobalCells = 8_000_000`-Limit sprengen).
2. **Physik-Benchmark-Projekte (`src/projects/Benchmark*/simulation.json`):**
   - Redundantes `"ChampionVerification": { "Mode": "Off" }` entfernt, damit das `*:VerificationFine`-Preset Single Source of Truth ist.
   - `BenchmarkNaca0012` rechnet `Alpha0` (0°) und `Alpha10` (10°) sequenziell via `OperatingPoints` ohne Optimierung (`MaxIterations = 1`, `VariantsPerIteration = 1`); `BenchmarkPipeBend90`, `BenchmarkSuboffOpenFoam` und `BenchmarkSuboffSu2` laufen analog direkt auf höchster Preset-Stufe.
3. **Bugfix in der automatischen Netzkalibrierung (Suffix-Monotonie / Finest-Downward-Auswahl):**
   - **`CalibrationEvaluator.EvaluateCalibration`:** Kandidaten (`s.Factor > refStage.Factor`) werden **fein → grob** (`OrderBy(s => s.Factor)`) geprüft. Sobald eine feinere Stufe nicht konvergiert, `y+` verletzt oder die Toleranz reißt, sofort `break` — keine gröbere Stufe dahinter darf durch Zufallstreffer gewählt werden. Bei übersprungenem gröberem Zufallstreffer wird ein klarer `non-monotonic`-Hinweis an `SummaryMessage` angehängt (bestehender Prefix `"No coarse level achieves..."` bleibt erhalten).
   - **`CalibrationEvaluator.EvaluateConfirmation3D`:** `mediumStage` wird zwingend vor `coarseStage` geprüft; `coarseStage` darf nur gewählt werden, wenn **beide** passen; bei abgelehntem Zufallstreffer wird die exakte Ausfallursache (`did not converge` / `violated y+` / `exceeded tolerance`) im `non-monotonic`-Hinweis genannt.
   - **`MeshCalibration.RunIntermediateStages`:** Reihenfolge umgestellt auf **fein → grob** (`Cal4 (0.71) -> Cal3 (1.00) -> Cal2 (1.41)`). `Cal4` und `Cal3` laufen immer (sichert gleichmäßiges `r = √2` für GCI & `AdaptiveCalibrationTests`); nach `Cal3` wird `Cal2` nur übersprungen, falls `!tempDecision.Success || tempDecision.SelectedStage == null || tempDecision.SelectedStage.Factor < 1.0f - 1e-4f` (verhindert, dass das vorab gerechnete `Cal1` an einem ungetesteten `Cal2` vorbeirutscht).
4. **Bericht-Philosophie (`docs/report/report_notes.md`):**
   - Klare Dreiteilung: (a) Physik-Benchmarks prüfen höchste Presets, (b) `Naca2D`-3-Seed vergleicht Optimierer unter identischer Simulationsbasis, (c) Showcase (`StatorCoolingRing`) zeigt den realen Optimierungs-Kompromiss mit Kalibrierung & Feinprüfung.
   - Kernaussage: Das Potenzial für vollständig akkurate Simulationen ist durch `VerificationFine` nachgewiesen; welcher Kompromiss aus Rechenzeit und Abweichung sinnvoll ist, liegt in der Hand des Nutzers und seiner Rechenkapazität.

---

## 3. Umsetzung & Arbeitsstand
- [x] Discovery (`research-worker`, `medium`) & Pre-Decision-Review (`reviewer`, `xhigh`) bis `APPROVE`
- [x] **AP 1 (`worker-presets-calib`, `high`):** `SimulationPresets.cs`, `CalibrationEvaluator.cs`, `MeshCalibration.cs`, 4 Benchmark-`simulation.json`s, Unit-Tests (`CalibrationAndVerificationTests`, `AdaptiveCalibrationTests`, `PresetAndSchemaTests`, `Phase4BenchmarksTests`), `docs/physics-presets.md`, `docs/report/report_notes.md`
- [x] **AP 2 (Verifikation & Post-Implementation Review):** `dotnet build -warnaserror` (0W/0E), `dotnet test` (**1074 bestanden, 0 fehlgeschlagen, 6 übersprungen**), `--print-config` & `--smoke-test` für alle 4 Benchmarks PASS, Post-Implementation Review mit `reviewer` (`APPROVE`) + 3-Zeilen-Feinschliff in `EvaluateConfirmation3D` (`failCause`-Differenzierung)
- [x] **AP 3 (Handover):** `.plans/phase4_validierung_benchmarks_und_release.md` & `.plans/Anmerkungen.txt` aktualisiert

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
- **Reviewer-Fund #1 (`Internal3D:VerificationFine` Zelllimit):** `BaseCellSizeMm = 1.2f` + Level `2/3` + `8` Layer erzeugen auf `BenchmarkPipeBend90` bereits bei `1.0f` ~4,2M `blockMesh`-Zellen; `0.75f` hätte `MaxGlobalCells = 8_000_000` gesprengt → `Internal3D:VerificationFine` korrekt bei `MeshRefinementFactor = 1.0f`.
- **Reviewer-Fund #2 (`External3D:VerificationFine` `BenchmarkSuboffSu2`):** `0.8f` statt `0.7f` verhindert unnötiges OOM-/Laufzeit-Risiko bei 3D-Prismen (`y+ = 1.0`) und hält `BenchmarkSuboffOpenFoam` bei ~27k `blockMesh`-Zellen (`Phase4BenchmarksTests` prüft nun real gegen `ctx.Simulation.MeshRefinementFactor` statt hardcoded `1.0f`).
- **Reviewer-Fund #3 (`Cal1`-Durchrutsch-Edge-Case in `RunIntermediateStages`):** Da `Cal1 (2.0)` vor `RunIntermediateStages` gerechnet wird, darf `Cal2 (1.41)` bei bestandenem `Cal4`+`Cal3` niemals übersprungen werden — nur bei verletztem `Cal4`/`Cal3`-Suffix (`SelectedStage.Factor < 1.0f - 1e-4f`) greift der Skip.
- **Unberührte Fremdänderungen im Workspace:** `README.md` hatte vorab uncommittete Änderungen des Nutzers und wurde nicht verändert.

---

## 5. Ausstehende Arbeitsschritte & Handover (Stand 26.11.2026)

- [x] **`Naca2D_Benchmark` heruntergeladen & knapp ausgewertet (26.11.2026):** Vollständig ohne Alt-Backup nach `results/Naca2D_Benchmark/` geladen (`9/9` Läufe `rc=0`, `ALL_B1_DONE`); knappe Wertetabelle unter `results/Naca2D_Benchmark/summary.md` (`Bayes` `53.23 ± 0.08` / Verified `66.92 ± 0.33`, `BoTorch` `53.32 ± 0.14` / Verified `66.97 ± 0.25`, `IdwBaseline` `50.04 ± 1.15` / Verified `59.80 ± 0.60`).
- [x] **Lokaler `results/`-Ordner bereinigt (26.11.2026):** Alte Benchmark-/Seed-Daten (`Benchmark*/BIC_12_2026-11-25`, `Benchmark*/smoke-test`, `Naca2D/`) entfernt; `StatorCoolingRing*`, `MantaAuv`, `PipeBend90` unberührt.
- [x] **Deploy & 4-Benchmark-Chain auf `BIC_12` gestartet (26.11.2026):** Verwaiste `Benchmark*/simulation.local.json` & alte `results/Benchmark*` auf `BIC_12` bereinigt, aktueller Stand (`e0738467fe29e1f6`, 0W/0E) deployed, `doctor` für alle 4 Projekte PASS, sequentielle Kette (`BenchmarkNaca0012` → `BenchmarkPipeBend90` → `BenchmarkSuboffOpenFoam` → `BenchmarkSuboffSu2`) läuft detached in `tmux` `physics_benchmarks` via `scripts/run_physics_benchmarks_on_server.sh` (Live-Check: `BenchmarkNaca0012` rechnet `External2D:VerificationFine`, `MeshFactor=0.50 MANUAL`, `Alpha0` 1/2).

### Nächste Schritte (sobald `PHYSICS_BENCHMARKS_DONE` auf `BIC_12` vorliegt)
1. **Status auf `BIC_12` prüfen:**
   ```powershell
   ssh -o BatchMode=yes BIC_12 'export PATH="$HOME/miniconda/bin:$PATH"; tmux ls 2>&1 || true; ls -la ~/Documents/FluidForge/results/PHYSICS_BENCHMARKS_* 2>/dev/null || true; cat ~/Documents/FluidForge/results/physics_benchmarks.log 2>/dev/null || true'
   ```
2. **Alle 4 Physik-Benchmark-Ergebnisse herunterladen:**
   ```powershell
   .\scripts\sim.ps1 fetch -All -Server BIC_12 -Project BenchmarkNaca0012 -Out results/BenchmarkNaca0012
   .\scripts\sim.ps1 fetch -All -Server BIC_12 -Project BenchmarkPipeBend90 -Out results/BenchmarkPipeBend90
   .\scripts\sim.ps1 fetch -All -Server BIC_12 -Project BenchmarkSuboffOpenFoam -Out results/BenchmarkSuboffOpenFoam
   .\scripts\sim.ps1 fetch -All -Server BIC_12 -Project BenchmarkSuboffSu2 -Out results/BenchmarkSuboffSu2
   ```
   *(Hinweis: Projekt-reine Server-Logs liegen jeweils zusätzlich als `results/<Project>/simulation_server.log` im Remote-Ordner.)*
3. **Auswertung & Bericht (`docs/report/`):**
   - Falls ein Benchmark physikalisch unplausible Ergebnisse liefert, gezielt das zugrunde liegende `*:VerificationFine`-Preset anpassen und neu prüfen.
   - `BenchmarkNaca0012_Report.md` um den `VerificationFine`-Nachher-Vergleich ergänzen, B2/B3 auswerten und die Methodik-Notizen aus `docs/report/report_notes.md` in den späteren Abschlussbericht (AP 4.4) einfließen lassen.
   - Danach `AP 4.3` (`StatorCoolingRing` Showcase mit `StandardRans`, automatischer Kalibrierung & `FinalTopN`-Feinprüfung).
