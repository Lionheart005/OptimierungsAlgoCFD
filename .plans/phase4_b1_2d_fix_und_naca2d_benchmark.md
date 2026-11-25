# Phase 4 B1 Offene Punkte — 2D-Geometrie-/Meshing-/Cauchy-Fix, B1-Re-Run & 3-Seed `Naca2D`-Vergleich

> **Status:** In Umsetzung (25.11.2026 — `/delegate`-Pipeline mit Peer-Reviewer `Gemini 4 Hydrogen (XHigh)`)
> **Ziel:** Die unter Phase 4 AP 4.2 B1 offenen Punkte sauber umsetzen und auf `BIC_12` (18 Kerne) rechnen:
> 1. Behebung der 3 in [`BenchmarkNaca0012_Report.md`](../docs/report/BenchmarkNaca0012_Report.md) diagnostizierten Ursachen (Clipper2-Quantisierung, Gmsh-Spline-Split-Kink & Nasenfehler, SU2-Cauchy-Limit-Cycle) + Re-Run `BenchmarkNaca0012` auf `BIC_12`.
> 2. Sauberer 3-Seed-Optimierer-Vergleich auf `Naca2D` (`Bayes` vs. `IdwBaseline` vs. `BoTorch` × 3 Seeds) auf `BIC_12` inkl. Auswertung & Plots.

---

## 1. Ziel & Kontext
- Vorausgegangener B1-Lauf (`Cal1`–`Cal6` auf `BIC_12`, Commit `b85091c`, archiviert unter `results/BenchmarkNaca0012/BIC_12_2026-11-25/`) traf $C_L(10^\circ)$ auf `+0,014 %` (Prandtl–Glauert) und $C_{D,v}(0^\circ)$ auf `-1,52 %`, zeigte jedoch `±5 µm` Ringing, `227 µm` Nasenfehler, einen `+31 %` $C_{f,x}$-Spike bei $x/c = 0{,}08066$ und `20.000`-Iter.-Durchläufer bei `CONV_CAUCHY_EPS = 1e-6`.
- Nach dem 2D-Fix wird `BenchmarkNaca0012` auf `BIC_12` neu gerechnet und in `docs/report/BenchmarkNaca0012_Report.md` (Vorher/Nachher) dokumentiert; anschließend läuft der 3-Seed `Naca2D`-Vergleich auf demselben Rechner (`BIC_12`, 18 Kerne).

---

## 2. Architektur & Getroffene Entscheidungen (Konsens Supervisor ⇄ `reviewer` `APPROVE`)

1. **`Shape2D` & `Lattice2D` Clipper2-Präzision (`ClipperPrecision = 6`):**
   - Zentrale Konstante `Shape2D.ClipperPrecision = 6` (`1 nm`-Auflösung, feiner als `float`-ULP bei `c = 1000 mm`) für `Clipper.Union`, `Difference`, `Intersect` und `InflatePaths` (auch in `Lattice2D`), statt des bisherigen Clipper2-Defaults `2` (`0,01 mm`).
2. **`Gmsh2DGeoScriptBuilder` Glatte Spline-Topologie + `Distance`/`Threshold`-Kantenverfeinerung:**
   - Splines werden **nur noch an scharfen `CornerIndices`** geteilt (`corners.Count == 1` -> eine durchgehende `Spline` von der TE-Ecke über LE zurück zu TE; bei `corners.Count == 0` Split an `leIdx`/`teIdx`; bei `corners.Count >= 2` Split an allen Ecken). Der `AppendContours`-Loop bei `splitIndices.Count == 1` (`fromIdx == toIdx`) wird explizit gefixt.
   - `EdgeRefinementDistanceFactor`-Spline-Splits entfallen; stattdessen `Field[7] = Distance` (`PointsList = edgeIdx`, scharfe `CornerIndices` wegen B14 weiterhin ausgeschlossen) + `Field[8] = Threshold` (`SizeMin = edgeSize`, `SizeMax = surfaceSize`, `DistMin = 0.02 * distX`, `DistMax = distX`, `StopAtDistMax = 1`) kombiniert in `Field[5] = Min`. Auf `BIC_12` mit Gmsh 4.11.1 vorab verifiziert (`Cal3` `2,64 s`, `Cal6` `5,95 s`, `max_le_dy = 2,08e-8`, `max_te_dy = 3,90e-10`).
   - `BenchmarkNaca0012GeometryGenerator.ChordStations = 400`, `Naca2DGeometryGenerator.ChordStations = 300`.
3. **SU2-Cauchy-Toleranz & konfigurierbarer Floor:**
   - Neue Option `SimulationConfig.CauchyToleranceFloor` (Default `"1e-7"` erhält bestehende `Su2AutoRestartTests`; synchronisiert in `schemas/simulation.schema.json` & `docs/config-reference.md`).
   - `Su2RestartController.TightenTolerance(current, floor)` enthält einen Never-Loosen-Guard (`value <= floor` hebt nie an); `Su2Solver` bricht die Restart-Schleife sauber ab, falls `nextEps == currentBlockEps`.
   - `External2D:VerificationFine`: `CauchyTolerance = "5e-6"`, `CauchyToleranceFloor = "3e-6"` (oberhalb des Venkatakrishnan-Limit-Cycles `~1,23e-6`).
4. **3-Seed `Naca2D`-Optimierer-Vergleich (`BIC_12`, 18 Kerne):**
   - Matrix: `Bayes` vs. `IdwBaseline` vs. `BoTorch` × Seeds `{20261108, 424242, 777}` (`10 × 10 = 100` Evaluationen).
   - Nach Abschluss des 2D-Fixes einmalige `--calibrate` auf `Naca2D`, danach für alle 9 Läufe derselbe feste `MeshRefinementFactor` (`MeshCalibration.Mode = "Manual"`), `AutoStop.Mode = "Off"` und aktives `ChampionVerification` (`FinalTopN`, `TopN = 3`, `RefinementSteps = 1`) über `src/projects/Naca2D/simulation.local.json` (ohne Core-CLI-Erweiterung). Archivierung je Zelle nach `results/Naca2D_Benchmark/<Algo>_seed<Seed>/`.

---

## 3. Umsetzung & Arbeitsstand
- [x] Discovery (`research-worker`) & empirischer Gmsh-4.11.1- und Cauchy-Nachweis auf `BIC_12`
- [x] Pre-Decision-Review mit `reviewer` (`gemini-4-hydrogen-xhigh`) bis `APPROVE`
- [x] **AP 1 (`worker-2d-fix`, `high`):** `Shape2D`, `Lattice2D`, `Gmsh2DGeoScriptBuilder`, `BenchmarkNaca0012`/`Naca2D` Stationen, `SimulationConfig`/`SimulationPresets`/`Su2RestartController`/`Su2Solver`, Schemas/Docs & Tests
- [x] **AP 2 (`worker-bench-scripts`, `medium`):** Server-Runner `scripts/run_naca2d_benchmark.sh` für die 3×3 `Naca2D`-Matrix
- [x] **AP 3:** Lokale Verifikation (`dotnet build -warnaserror`: 0W/0E; **1056/1056 Unit-Tests** grün; `BenchmarkNaca0012` & `Naca2D` `--smoke-test` PASS) & Code-/Delta-Review (`reviewer`) bis `APPROVE`
- [ ] **AP 4 (`BIC_12`):** Deploy, `doctor`, `BenchmarkNaca0012` Re-Run (`Cal1`–`Cal6`) + Download & Vorher/Nachher-Auswertung
- [ ] **AP 5 (`BIC_12`):** Einmalige `Naca2D --calibrate`, dann 9-Lauf `Naca2D` 3-Seed-Vergleich + Auswertung & Plots unter `docs/report/`
- [ ] **AP 6:** Finaler Review (`reviewer`), lokale Commits & `.plans/`-Abschlussaktualisierung

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
- **Gmsh-Vorabtest (`BIC_12`):** `Field[8].SizeMax = farfieldSize` ließ die Rampe praktisch kollabieren; `SizeMax = surfaceSize` + `StopAtDistMax = 1` liefert die exakte lineare Verfeinerung (`0.600 mm -> 2.150 mm -> 2.998 mm`).
- **SU2 `Cal6_OP2`-Verlauf:** Ab Iteration 4700 hält der Venkatakrishnan-/CFL-Limit-Cycle (`rms_d ~ 1.03e-6`, `half_diff ~ 2e-6`) die `1e-6`-Schwelle bis `20.000` unerreicht, obwohl die Physik ab Iteration ~4000 (`0.14 %` Drift) auskonvergiert ist.

---

## 5. Ausstehende Arbeitsschritte & Nächste Schritte
1. AP 1 + AP 2 umsetzen und lokal testen.
2. Code-Review durch `reviewer`, danach Deploy & Benchmark-Läufe auf `BIC_12`.
