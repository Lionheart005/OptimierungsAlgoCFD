# Phase 4 B1 Offene Punkte — 2D-Geometrie-/Meshing-/Cauchy-Fix, B1-Re-Run & 3-Seed `Naca2D`-Vergleich

> **Status:** Code-Umsetzung & Server-Automation abgeschlossen (25.11.2026 — `/delegate`-Pipeline mit Peer-Reviewer `Gemini 4 Hydrogen (XHigh)` bis `APPROVE`); **Serverläufe laufen aktuell vollständig detached auf `BIC_12`** (PC kann geschlossen werden).
> **Ziel:**
> 1. Behebung der 3 in [`BenchmarkNaca0012_Report.md`](../docs/report/BenchmarkNaca0012_Report.md) diagnostizierten Ursachen (Clipper2-Quantisierung, Gmsh-Spline-Split-Kink & Nasenfehler, SU2-Cauchy-Limit-Cycle) + Re-Run `BenchmarkNaca0012` auf `BIC_12`.
> 2. Sauberer 3-Seed-Optimierer-Vergleich auf `Naca2D` (`Bayes` vs. `IdwBaseline` vs. `BoTorch` × 3 Seeds) auf `BIC_12` inkl. Auswertung & Plots.

---

## 1. Ziel & Kontext
- Vorausgegangener B1-Lauf (`Cal1`–`Cal6` auf `BIC_12`, Commit `b85091c`, archiviert unter `results/BenchmarkNaca0012/BIC_12_2026-11-25/`) traf $C_L(10^\circ)$ auf `+0,014 %` (Prandtl–Glauert) und $C_{D,v}(0^\circ)$ auf `-1,52 %`, zeigte jedoch `±5 µm` Ringing, `227 µm` Nasenfehler, einen `+31 %` $C_{f,x}$-Spike bei $x/c = 0{,}08066$ und `20.000`-Iter.-Durchläufer bei `CONV_CAUCHY_EPS = 1e-6`.
- Nach dem 2D-Fix laufen auf `BIC_12` (18 Kerne via `config/simulation.local.json`) detached:
  1. `BenchmarkNaca0012` (in `tmux`-Session `sim`), danach automatisch
  2. `Naca2D --calibrate` + 9-Lauf `Naca2D`-3-Seed-Vergleich (in `tmux`-Session `b1_chain`).

---

## 2. Architektur & Getroffene Entscheidungen (Konsens Supervisor ⇄ `reviewer` `APPROVE`)

1. **`Shape2D` & `Lattice2D` Clipper2-Präzision (`ClipperPrecision = 6`):**
   - Zentrale Konstante `Shape2D.ClipperPrecision = 6` (`1 nm`-Auflösung, feiner als `float`-ULP bei `c = 1000 mm`) für `Clipper.Union`, `Difference`, `Intersect` und `InflatePaths` (auch in `Lattice2D`), statt des bisherigen Clipper2-Defaults `2` (`0,01 mm`).
2. **`Gmsh2DGeoScriptBuilder` Glatte Spline-Topologie + `Distance`/`Threshold`-Kantenverfeinerung:**
   - Splines werden **nur noch an scharfen `CornerIndices`** geteilt (`corners.Count == 1` -> eine durchgehende `Spline` von der TE-Ecke über LE zurück zu TE; bei `corners.Count == 0` Split an `leIdx`/`teIdx`; bei `corners.Count >= 2` Split an allen Ecken). Der `AppendContours`-Loop bei `splitIndices.Count == 1` (`fromIdx == toIdx`) wurde explizit gefixt.
   - `EdgeRefinementDistanceFactor`-Spline-Splits entfallen; stattdessen `Field[7] = Distance` (`PointsList = edgeIdx`, scharfe `CornerIndices` wegen B14 weiterhin ausgeschlossen) + `Field[8] = Threshold` (`SizeMin = edgeSize`, `SizeMax = surfaceSize`, `DistMin = 0.02 * distX`, `DistMax = distX`, `StopAtDistMax = 1`) kombiniert in `Field[5] = Min`. Auf `BIC_12` mit Gmsh 4.11.1 vorab verifiziert (`Cal3` `2,64 s`, `Cal6` `5,95 s`, `max_le_dy = 2,08e-8`, `max_te_dy = 3,90e-10`).
   - `BenchmarkNaca0012GeometryGenerator.ChordStations = 400`, `Naca2DGeometryGenerator.ChordStations = 300`.
3. **SU2-Cauchy-Toleranz & konfigurierbarer Floor:**
   - Neue Top-Level-Option `SimulationConfig.CauchyToleranceFloor` (Default `"1e-7"` erhält bestehende `Su2AutoRestartTests`; auf Reviewer-Hinweis keine redundante Kopie in `ControlRunConfig`; synchronisiert in `schemas/simulation.schema.json` & `docs/config-reference.md`).
   - `Su2RestartController.TightenTolerance(current, floor)` enthält einen Never-Loosen-Guard (`value <= floor` hebt nie an); `Su2Solver` bricht die Restart-Schleife sauber ab, falls `nextEps == currentBlockEps`.
   - `External2D:VerificationFine`: `CauchyTolerance = "5e-6"`, `CauchyToleranceFloor = "3e-6"` (oberhalb des Venkatakrishnan-Limit-Cycles `~1,23e-6`).
4. **3-Seed `Naca2D`-Optimierer-Vergleich (`BIC_12`, 18 Kerne):**
   - Matrix: `Bayes` vs. `IdwBaseline` vs. `BoTorch` × Seeds `{20261108, 424242, 777}` (`10 × 10 = 100` Evaluationen) via `scripts/run_naca2d_benchmark.sh` (mit `"ExternalOptimizer": { "FallbackToBuiltInBayes": false }`).
   - Nach Abschluss des laufenden `BenchmarkNaca0012` startet `scripts/run_b1_after_benchmark_on_server.sh` (in `tmux` `b1_chain`) automatisch `Naca2D --calibrate`, liest `SelectedFactor` mit `utf-8-sig` (UTF-8-BOM!) aus `results/Naca2D/calibration/latest_calibration.json` und startet dann alle 9 Läufe mit festem `MeshRefinementFactor` (`Mode = "Manual"`), `AutoStop = Off` und aktivem `ChampionVerification` (`FinalTopN`, `TopN = 3`, `RefinementSteps = 1`).

---

## 3. Umsetzung & Arbeitsstand
- [x] Discovery (`research-worker`) & empirischer Gmsh-4.11.1- und Cauchy-Nachweis auf `BIC_12`
- [x] Pre-Decision-Review mit `reviewer` (`gemini-4-hydrogen-xhigh`) bis `APPROVE`
- [x] **AP 1 (`worker-2d-fix`, `high`):** `Shape2D`, `Lattice2D`, `Gmsh2DGeoScriptBuilder`, `BenchmarkNaca0012`/`Naca2D` Stationen, `SimulationConfig`/`SimulationPresets`/`Su2RestartController`/`Su2Solver`, Schemas/Docs & Tests
- [x] **AP 2 (`worker-bench-scripts`, `medium`) & Chain-Skript (`worker-chain-script`):** `scripts/run_naca2d_benchmark.sh` & `scripts/run_b1_after_benchmark_on_server.sh`
- [x] **AP 3:** Lokale Verifikation (`dotnet build -warnaserror`: 0W/0E; **1056/1056 Unit-Tests** grün; `BenchmarkNaca0012` & `Naca2D` `--smoke-test` PASS) & Code-/Delta-Review (`reviewer`) bis `APPROVE` -> lokaler Commit `6cb8d67`
- [x] **AP 4a (Start auf `BIC_12`):** Alter B1-Stand auf `BIC_12` nach `~/Archive/BenchmarkNaca0012_2026-11-25_pre_fix_*` gesichert; Deploy (`1692afaf064f52b8`), `doctor` (`BenchmarkNaca0012` & `Naca2D`) grün, `--dry-run` grün; `BenchmarkNaca0012` in `tmux` `sim` gestartet & erste Live-Prüfung auf `Cal5_OP1` bestätigt (`LE max |dy| = 0,073 µm`, `Mid dy = ±0,03 µm`, kein `Cf_x`-Spike bei `0,08`, `Cal1` in `77 s` statt `5 min`)
- [x] **AP 4b (Unattended Chaining auf `BIC_12`):** `scripts/run_b1_after_benchmark_on_server.sh` in `tmux` `b1_chain` gestartet; wartet auf Ende von `sim` (`FluidForge.dll`), kalibriert `Naca2D` einmalig und startet anschließend automatisch alle 9 `Naca2D`-Läufe bis Sentinel `results/Naca2D_Benchmark/ALL_B1_DONE`
- [ ] **AP 5 (Nächste Session nach Abschluss auf `BIC_12`):** `BenchmarkNaca0012`- & `Naca2D_Benchmark`-Ergebnisse von `BIC_12` herunterladen, Vorher/Nachher-Auswertung in `docs/report/BenchmarkNaca0012_Report.md` ergänzen, `Naca2D`-3-Seed-Vergleichsbericht + Plots erstellen und B1 in `phase4_validierung_benchmarks_und_release.md` abschließen
- [ ] **AP 6 (Danach in Phase 4):** `B2` (`BenchmarkPipeBend90`), `B3` (`BenchmarkSuboffOpenFoam` + `BenchmarkSuboffSu2`) und `AP 4.3` (`StatorCoolingRing`) auf `BIC_12` rechnen

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
- **Live-Messung `Cal5_OP1` nach dem 2D-Fix (`BIC_12`):** Nasenfehler sank von **`227 µm` auf `0,0734 µm`** (`3000×`), Mittelsehnen-Ringing von **`±5 µm` auf `-0,0335 / +0,0220 µm`** (`200×`), `Cf_x`-Verhältnis um `x = 0,08` von **`1,311` auf `1,0157`** (Spike vollständig eliminiert), `Cal1`-Dauer von `5 min` auf `77 s` (`~4×` schneller).
- **UTF-8-BOM in `latest_calibration.json`:** FluidForge schreibt `latest_calibration.json` mit UTF-8-BOM (`\ufeff`); Python-Parser in `run_b1_after_benchmark_on_server.sh` nutzt zwingend `encoding="utf-8-sig"`.
- **Stiller BoTorch-Fallback verhindert:** `ExternalOptimizer.FallbackToBuiltInBayes` wird in `run_naca2d_benchmark.sh` explizit auf `false` gesetzt.
- **`tmux`-Pfad auf `BIC_12`:** `tmux` liegt in `~/miniconda/bin/tmux` (nicht in System-`/usr/bin`), muss bei direktem `ssh`-Check über `PATH="$HOME/miniconda/bin:$PATH" tmux ls` aufgerufen werden.

---

## 5. Ausstehende Arbeitsschritte & Handover für die nächste KI-Session (wenn `BIC_12` fertig ist)
1. **Status auf `BIC_12` prüfen (1 Befehl):**
   ```bash
   ssh -o BatchMode=yes BIC_12 'export PATH="$HOME/miniconda/bin:$PATH"; tmux ls || true; ls -la ~/Documents/FluidForge/results/Naca2D_Benchmark/ALL_B1_* 2>/dev/null || true; tail -n 20 ~/Documents/FluidForge/results/Naca2D_Benchmark/b1_chain.log ~/Documents/FluidForge/results/Naca2D_Benchmark/benchmark.log 2>/dev/null || true'
   ```
   - Wenn `results/Naca2D_Benchmark/ALL_B1_DONE` existiert (und `tmux ls` leer ist), sind sowohl `BenchmarkNaca0012` als auch alle 9 `Naca2D`-Läufe fertig!
2. **Ergebnisse herunterladen:**
   - `BenchmarkNaca0012`: `.\scripts\sim.ps1 fetch -All -Server BIC_12 -Project BenchmarkNaca0012 -Out results/BenchmarkNaca0012` (bzw. sauber nach `results/BenchmarkNaca0012/BIC_12_2026-11-25_fixed/` inkl. `simulation_server.log` ablegen).
   - `Naca2D_Benchmark`: `scp -r BIC_12:Documents/FluidForge/results/Naca2D_Benchmark results/Naca2D_Benchmark_BIC_12_2026-11-25` (enthält `_calibrated_naca2d/`, alle 9 `<Algo>_seed<Seed>/` Ordner mit `results.csv`, `verification.csv`, `effective-config.json`, `run-info.json`, `Bayes_History.csv`, `simulation.log`, `run_summary.json`).
3. **Auswertung & Berichte fertigstellen:**
   - `docs/report/generate_naca0012_figures.py` & `docs/report/BenchmarkNaca0012_Report.md` um den Vorher/Nachher-Vergleich ergänzen (GCI, `CD_p(0°)`, `Cp`/`Cf`/`y+`, Laufzeit).
   - 3-Seed-`Naca2D`-Vergleich (`Bayes` vs. `IdwBaseline` vs. `BoTorch`, Mean/Std/Min/Max über 3 Seeds nach 100 Eval. sowie Feinprüfungs-Ranking) auswerten & unter `docs/report/` (inkl. Plot-Skript) dokumentieren.
   - Danach in `.plans/phase4_validierung_benchmarks_und_release.md` B1 auf `[x]` setzen und mit **B2 (`BenchmarkPipeBend90`)** + **B3 (`BenchmarkSuboffOpenFoam` / `BenchmarkSuboffSu2`)** auf `BIC_12` fortfahren.
- *Hinweis für nachfolgende Modelle: Falls noch `DONE`-Läufe fehlen oder abgebrochen wurden, einfach auf `BIC_12` `tmux new-session -d -s b1_chain "bash scripts/run_naca2d_benchmark.sh --mesh-factor <SelectedFactor>"` fortsetzen — das Skript überspringt automatisch alle Läufe mit `DONE`-Marker.*
