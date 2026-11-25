# Phase 4 — AP 4.1 (Zero-to-First-Run auf `BIC_09`) & AP 4.2 B1 (`BenchmarkNaca0012` auf `BIC_12`)

> **Status:** ✔ Abgeschlossen (25.11.2026 — umgesetzt via `/delegate`-Pipeline mit Peer-Reviewer `Gemini 4 Hydrogen (XHigh)`)
> **Ziel:** (1) Phase 4 AP 4.1 vollständig umsetzen und auf einem sauberen Ubuntu-Server (`BIC_09`) real testen (Rootless-Umgebung behalten, 0 laufende Jobs / Test-Results bereinigt), (2) `External2D:VerificationFine` & SU2-Konfiguration literaturkonform prüfen/korrigieren, `BenchmarkNaca0012` auf `BIC_12` (18 Kerne) rechnen, Ergebnisse nach `results/BenchmarkNaca0012/` herunterladen und Auswertung inkl. Plots unter `docs/report/BenchmarkNaca0012_Report.md` ablegen.

---

## 1. Ziel & Nutzervorgaben
- **`BIC_09`:** Ausschließlich für AP-4.1-Zero-to-First-Run-Tests; am Morgen 100 % frei (`0` laufende Jobs, Test-Results bereinigt), aber installierte Rootless-Umgebung (`~/.dotnet`, `~/miniconda`, `~/software`, `~/Documents/FluidForge`) erhalten.
- **`BIC_12`:** 18 Kerne über `config/simulation.local.json`; `BenchmarkNaca0012` inkl. Preset-Audit, Lauf, Download nach `results/BenchmarkNaca0012/` und Bericht unter `docs/report/BenchmarkNaca0012_Report.md`. Optionaler 3-Seed-`Naca2D`-Vergleich nur innerhalb eines 7-Stunden-Fensters -> nach realer Laufzeitmessung (`Cal1`–`Cal6` ca. 4 h; `Cal7` 1,71 Mio. Zellen ~13 h ETA) im Konsens mit `reviewer` sauber auf `BenchmarkNaca0012` fokussiert.
- **Git:** Saubere lokale Commits (ohne `git push`).
- **Anti-Hang:** Strikt ohne blockierende Worker-Warteschleifen; SSH mit `BatchMode=yes` & Timeouts; lange Läufe detached in `tmux` + Supervisor-Timer (`schedule`).

---

## 2. Architektur & Getroffene Entscheidungen (inkl. Reviewer-Konsens)
Alle wesentlichen Entscheidungen wurden vorab und bei Kurskorrekturen mit `reviewer` (`Gemini 4 Hydrogen (XHigh)`) bis `APPROVE` abgestimmt:

1. **SU2-Numerik & `External2D:VerificationFine` (`891347d`, `b85091c`):**
   - `Su2SolverOptions` & `Su2ConfigGenerator`: `SlopeLimiterFlow` (Default `"VENKATAKRISHNAN"`, `VENKAT_LIMITER_COEFF= 0.05` nur bei `VENKATAKRISHNAN*`) und `FreestreamNuFactor` (`float?`, Default `null`, Ausgabe `FREESTREAM_NU_FACTOR` nur bei `KIND_TURB_MODEL= SA`) ergänzt; `schemas/su2.schema.json` + Docs synchronisiert.
   - `External2D:VerificationFine`: `FreestreamNuFactor = 3.0f` (NASA-TMR-SA-Standard), `CauchyTolerance = "1e-6"`, `BoundaryLayerGrowthRate = 1.15f` direkt im Preset. Empirisch auf `BIC_12` nachgewiesen: `SLOPE_LIMITER_FLOW= NONE` divergiert auf Gmsh-2D-Hybridnetzen selbst bei `CFL=1` (`Residual > 10^20`), daher `SlopeLimiterFlow = "VENKATAKRISHNAN"` im Preset beibehalten.
   - **SU2-Divergenz-Bug behoben (`Su2Solver.cs`, `Su2DiagnosticsExtractor.cs`):** Bisher wertete `Su2Solver` bei `exitCode != 0` (`MPI_Abort`) trotzdem `history.csv` als `Analysis succeeded` (`CD = 1e19`), wenn `>= 5` Zeilen vorlagen. Jetzt wird `exitCode != 0` (Erstlauf & Restarts) sowie nicht-finite / `|val| > 1e6` konsequent als `SimulationFailed = true` + `ArchiveFailureArtifacts` behandelt (3 neue Unit-Tests).
   - `CauchyTolerance = "1e-6"` nach Abschluss bewusst **nicht** nachträglich geändert (1:1-Konsistenz zu den gemessenen `Config_*.cfg`-Dateien); Empfehlung `"5e-6"` im Bericht dokumentiert.
2. **AP 4.1 Zero-to-First-Run & Diagnostics (`891347d`, `b85091c`, `75e9355`):**
   - `src/FluidForge/setup_server.sh`: Umstellung auf **Miniforge3 mit `-p "$HOME/miniconda"`** (keine Anaconda-ToS-Blocker bei 100 % Pfadkompatibilität), `--override-channels -c conda-forge`, `unzip`-Auto-Install, `xvfb-run`-Fallback-Wrapper, `opt-py` abgeglichen mit `environment_opt.yml`, **`PicoGKRuntime` auf letzten Standalone-CMake-Commit `1713d29` gepinnt** (nach Upstream-Umbruch `45b7cf4` am 11.11.2026!), CMake-RPATH (`-DCMAKE_BUILD_RPATH`/`-DCMAKE_INSTALL_RPATH`) für `libboost_iostreams.so` eingebettet, idempotente `~/.bashrc`-Einträge.
   - `scripts/sim.ps1`: `BatchMode=yes` + generöse Timeouts (`ConnectTimeout=60`, `ServerAliveInterval=30`, `ServerAliveCountMax=6`) gegen stille SSH-Hänger; `doctor` baut bei frischem Server automatisch `FluidForge.dll` vor dem Config-Check (Chicken-and-Egg-Fix).
   - `scripts/sim-runner.sh`: **Echter SSH-Hänger-Fix** (`</dev/null >/dev/null 2>&1` auf allen 4 `tmux new-session -d`-Aufrufen); `doctor` mit `timeout 10` auf allen Probes, `libpicogk.so` + `libpicogk.26.2.so` + `ldd`, `SU2_CFD -h`, `python -W ignore`, `tmux -V 2>/dev/null`, OpenFOAM System vs. Conda-Warnung und verpflichtendem `opt-py`-Check bei `BoTorch`/`Pymoo`.
   - `OptimizeCommand.Report`: Handlungsorientierte `[ERROR]`-Diagnosen für `DllNotFoundException`, `Failed to load PicoGK library`, `Win32Exception`, `FileNotFoundException` und `DirectoryNotFoundException`.
   - `README.md` & `docs/server-workflow.md`: Veraltete OpenFOAM-Aussage korrigiert, `BoTorch`/`pymoo`, `setup_server.sh` und `-Server <Alias>` dokumentiert.

---

## 3. Umsetzung & Arbeitsstand
- [x] Discovery (`research-presets`, `research-ap41`) & SSH-Vorabcheck (`BIC_09`, `BIC_12`)
- [x] Pre-Decision-Review & Delta-Reviews mit `reviewer` (`gemini-4-hydrogen-xhigh`) bis `APPROVE`
- [x] AP-A (`worker-presets`) & AP-B (`worker-ap41`) + Live-Hotfixes (`worker-hotfix`) + RPATH/PicoGK-Diagnose -> lokale Commits `891347d`, `b85091c`, `75e9355`
- [x] **Track A (`BIC_09` AP 4.1 Zero-to-First-Run):**
  - `setup_server.sh` komplett rootless auf leerem Home-Verzeichnis durchgelaufen (`SETUP COMPLETED SUCCESSFULLY!`)
  - `sim.ps1 doctor -Server BIC_09` für `Naca2D`, `PipeBend90` und `StatorCoolingRing` (`BoTorch`) alle `VALIDATION SUCCEEDED`
  - `MantaAuv --smoke-test` (3D PicoGK + Xvfb): `PASS (6/6 probes, 5/5 params)` in 9 s
  - `Naca2D` (2D SU2 MPI, 3 OPs, `FastConcept`, 1 Var): `exit code 0` in 8 s (`Surface/Volume_*.vtu` erzeugt)
  - `PipeBend90` (3D PicoGK + ShapeKernel + `snappyHexMesh` 35,9k Zellen, 92,3 % Layer + `simpleFoam` 12 OpenMPI-Kerne): End-to-End fehlerfrei gestartet & berechnet (8 s; Hinweis für B2: kurzes `3.33D`-Outlet hat 2,28 % Tail-Drift)
  - Vollständig bereinigt (`sim.ps1 stop`, `sim.ps1 delete -Force`, temporäre `*.local.json` entfernt -> **0 laufende Jobs**, installierte Umgebung erhalten)
- [x] **Track B (`BIC_12` AP 4.2 B1 `BenchmarkNaca0012`, 18 Kerne):**
  - 6 vollständige Netzstufen gerechnet (`Cal1` 37k → `Cal2` 70k → `Cal3` 125k → `Cal4` 235k → `Cal5` 452k → `Cal6` 900k Zellen; `Cal7` 1,71 Mio. Zellen bei ~13 h ETA im Konsens mit `reviewer` sauber gestoppt)
  - Alle `Cal1`–`Cal6`-Ergebnisse (`Surface_*.csv`, `Surface/Volume_*.vtu`, `History_*.csv`, `Config_*.cfg`, `.geo`, `simulation.log`) heruntergeladen nach `results/BenchmarkNaca0012/BIC_12_2026-11-25/`
  - 4 hochauflösende 300-DPI-Plots (`docs/report/figures/*.png`) und vollständiger Validierungsbericht `docs/report/BenchmarkNaca0012_Report.md` erstellt (`worker-report`)
- [x] Post-Implementation-Review mit `reviewer` (`Gemini 4 Hydrogen (XHigh)`) bis `APPROVE` (inkl. visueller Prüfung aller 4 Plots) & finaler lokaler Commit der `.plans/`-Doku

---

## 4. Gefundene Fehler, Probleme & Wichtige Erkenntnisse
1. **Upstream-Bruch in `PicoGKRuntime` (`45b7cf4`, 11.11.2026):** LEAP 71 hat `CMakeLists.txt` und alle Submodule von `main` entfernt (`PicoGK.Legacy`). Gelöst durch Commit-Pin `1713d29` in `setup_server.sh`.
2. **SSH-Hänger bei erstmaligem `tmux`-Start (`sim-runner.sh`):** `tmux new-session -d` ohne `</dev/null >/dev/null 2>&1` vererbte SSH-FDs an den tmux-Daemon und blockierte `sim.ps1 run` bis Laufende. Auf allen 4 Aufrufen behoben.
3. **`libpicogk.so` ohne `LD_LIBRARY_PATH` außerhalb `sim-runner.sh`:** Gelöst über CMake-RPATH (`$HOME/miniconda/lib`) in `setup_server.sh` + sprechende Fehlermeldung in `OptimizeCommand.Report`.
4. **SU2 Divergenz vs. `SLOPE_LIMITER_FLOW=NONE`:** `NONE` divergiert auf Gmsh-2D-Hybridnetzen (`Residual > 10^20`, selbst bei `CFL=1`), `VENKATAKRISHNAN` konvergiert stabil. Gleichzeitig wurde der `Su2Solver`-Bug behoben, bei dem `exitCode != 0` trotz `1e19`-Divergenz als `Analysis succeeded` durchging.
5. **Wissenschaftliche Hauptbefunde aus `BenchmarkNaca0012` (`Cal1`–`Cal6`):**
   - **`CL(10°)` & `CD_v` exzellent validiert:** `Cal6` `CL(10°) = 1.0787` trifft die inkompressible Prandtl–Glauert-Theorie (`1.07856`) auf **+0,014 %** (`-1,12 %` zur kompressiblen NASA-TMR-Referenz `1.0909`); `CD_v(0°) = 0.006874` (`-1,52 %` zu TMR `0.00698`, GCI `0,35 %`); `y+` perfekt (`P50 = 0,875`, `P95 = 1,147`).
   - **Geometrie-/Meshing-Wurzelursache für `CD_p(0°)`-Abfall (`0.00010` statt `0.00121`):**
     1. `Shape2D` quantisiert Konturpunkte über Clipper2 auf `0,01 mm` (`±5 µm`), wodurch Gmshs kubische `Spline` bei feiner Diskretisierung (`Cal4`–`Cal6`) `±5 µm` Welligkeit $\to$ `±0,045` `Cp`-Sägezahn und `227 µm` Nasenfehler erzeugt.
     2. `EdgeRefinementDistanceFactor = 0.08` splittet die Gmsh-Spline bei `x = 0.08` $\to$ `+31 %` `Cf_x`-Spike und globales `y+_max = 1,231` exakt bei `x = 0,08066`.
   - **Solver-Laufzeit:** Venkatakrishnan erzeugt einen 2-Schritt-Limit-Cycle (`~1,23e-6`), knapp über `CONV_CAUCHY_EPS = 1e-6` -> `Cal6` `Alpha10` lief bis `ITER = 20000` (2h48m). Mit `5e-6` (0,05 Drag Counts) ließe sich die Laufzeit um Faktor ~3–5 verkürzen.

---

## 5. Ausstehende Arbeitsschritte & Nächste Schritte (Handover für Folge-Agenten)
1. **Optionales 2D-Geometrie-/Meshing-Tuning vor finalem B1-Re-Run (für AP 4.4 LaTeX-Bericht):**
   - `Shape2D` / `BenchmarkNaca0012GeometryGenerator` mit höherer Präzision (`double` / ohne `0,01 mm`-Clipper2-Quantisierung für analytische Profile) und `Gmsh2DGeoScriptBuilder` ohne Spline-Split bei `x = 0.08` (stattdessen kontinuierlicher `BSpline`/Size-Field an LE/TE) + `CauchyTolerance = "5e-6"` testen -> erwartet $C_{D,p}(0^\circ) \approx 0{,}0012$ und $C_D(0^\circ) \approx 0{,}0081$ innerhalb 1–2 % der NASA-TMR-Referenz bei ~3–5× kürzerer Laufzeit!
2. **AP 4.2 B1 Zusatz (3-Seed `Naca2D`-Optimierer-Vergleich):** `Bayes` vs. `IdwBaseline` vs. `BoTorch` auf `BIC_12` (18 Kerne, ~6–8 h Gesamtlaufzeit) in einem separaten Nachtlauf nachholen.
3. **AP 4.2 B2 & B3:** `BenchmarkPipeBend90` (`Internal3D` OpenFOAM) und `BenchmarkSuboffOpenFoam` + `BenchmarkSuboffSu2` (`External3D` Zwillingsvergleich) auf `BIC_12` rechnen und unter `docs/report/` auswerten.
4. **AP 4.3 & AP 4.4:** `StatorCoolingRing`-Showcase (`BoTorch` `qNEHVI`) rechnen, ParaView-Renderings & englischen LaTeX-Bericht in `docs/report/` erstellen, danach Community-Release.
5. **Hinweis zu `BIC_12`:** Auf `BIC_12` liegt `~/Documents/FluidForge/config/simulation.local.json` mit `{"MpiCores": 18, "GmshCores": 18}` (gewünscht für 18-Kern-Läufe); es laufen aktuell **0 Jobs** auf `BIC_09` und `BIC_12`.
