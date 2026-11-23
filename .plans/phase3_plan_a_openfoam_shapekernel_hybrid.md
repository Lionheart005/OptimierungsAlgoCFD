# Phase 3 (Plan A): `LEAP71_ShapeKernel`, OpenFOAM (künftiger 3D-Standard) & Hybrid-Optimierer-Bridge (`BoTorch`/`pymoo`)

> **Status:** ✔ Vollständig abgeschlossen (23.11.2026) — **Alle 4 Arbeitspakete (`AP 3.1`–`AP 3.4`, Checkpoints 1–5) lokal (`984/984` Unit-Tests, `0` Warnungen) und live auf `BIC_12` (`OpenFoamIntegrationTests` 2/2, `Category=PythonOpt` 3/3, `sim.ps1 doctor` für `PipeBend90` & `MantaAuv`) erfolgreich abgenommen. Bereit für Phase 4.**  
> **Vision:** `PicoGK` + `LEAP71_ShapeKernel` auf ihrem wahren Heimfeld einsetzen — komplexe, 3D-druckbare **interne Fluidik** (Verteiler, Kanäle, Mischer, Düsen, Wärmetauscher) mit **OpenFOAM als primärem 3D-Standard** + robuster Multi-Objective-/Multi-Fidelity-Optimierung — **ohne** Parameter-Chaos!

---

## 1. Ziel & Kontext

Mit Phasen 1 und 2 steht ein sauberer, verständlicher, englischsprachiger Kern. In Phase 3 heben wir das Framework funktional auf das nächste Level und erschließen unsere klare Open-Source-Nische:
1. **`LEAP71_ShapeKernel`** als erstklassigen parametrischen 3D-Geometrie-Baukasten anbinden (weg von rohen `Lattice.AddBeam`-Bastelformen hin zu echten parametrischen Rohren, Spline-Kanälen, Flanschen, Gyroids, Wandstärken und benannten Funktionsflächen).
2. **OpenFOAM als langfristigen 3D-Standard-Solver** anbinden — beginnend mit **`Internal3D`** und nach erfolgreicher Benchmark-Validierung auch auf **`External3D`** ausbaubar (mit Perspektive auf Transitionsmodelle `kOmegaSSTLM` & Wärme `chtMultiRegionSimpleFoam`). Streng gekapselt hinter Presets (mit vollem Profi-JSON-Override), sodass die Bedienung simpel bleibt. **SU2** bleibt gezielt für `External2D` (NASA-TMR), Windows-native Tests ohne WSL sowie als optionale 3D-/High-Speed-Variante erhalten.
3. **Hybrid-Optimierer-Bridge (`IExternalOptimizer` via JSON Ask/Tell)** ergänzen: Der eingebaute C#-Bayes bleibt Zero-Dependency-Standard; optional schaltet man ein Python-Conda-Env (`BoTorch/Ax`, `pymoo`) dazu — für echte **Pareto-Fronten (`qNEHVI`, `NSGA-II`)**, **Constrained BO** und **Multi-Fidelity-BO**. Gleichzeitig schafft diese Schnittstelle das Fundament für Phase 5 (LLM-Anbindung).

---

## 2. Architektur & Getroffene Entscheidungen

### Entscheidung 1: Klare Rollenverteilung OpenFOAM vs. SU2 (OpenFOAM als 3D-Standard, SU2 für seine echten Stärken!)
| Domänen-Preset | Mesher | Primär-Solver | Alternative / Einsatzgebiet |
| :--- | :--- | :--- | :--- |
| **`Internal3D`** *(NEU, Fokus Plan A)* | **OpenFOAM `snappyHexMesh` (optional `cfMesh`)** | **OpenFOAM `simpleFoam`** *(später `chtMultiRegionSimpleFoam`)* | **Interne 3D-Druck-Fluidik:** Rohrkrümmer, Verteiler (Manifolds), statische Mischer, Kühlkanäle, Düsen, Ventile |
| **`External3D`** | **OpenFOAM** (`snappyHexMesh`/`cfMesh` oder `gmshToFoam`) *als künftiger 3D-Standard* | **OpenFOAM `simpleFoam`** *(inkl. Option `kOmegaSSTLM`-Transition!)* | **SU2 + Gmsh3D** bleibt als optionale Variante erhalten (für Windows-Tests ohne WSL & spätere kompressible/High-Speed-Strömungen) |
| **`External2D`** | Gmsh 2D (O-Typ Quad-BL) | **SU2** (`INC_RANS`) | Dauerhafter 2D-Standard: 2D-Tragflächen, Profile, NASA-TMR-Validierung (echte 2D-Netze, Windows-nativ, rasend schnell) |

* **Warum OpenFOAM als künftiger 3D-Standard — und welche Nachteile wir aktiv abfangen:**
  - **Stärken:** ~10× größere CFD-Community, eine einzige 3D-Solver-Welt (weniger Doppelpflege), Multi-Patch-Support (`inlet`/`outlet_1..N`/`wall`/`symmetry`), Perspektive auf Wärme (`chtMultiRegionSimpleFoam`), Transition (`kOmegaSSTLM` gegen das Low-$Re$-Problem!) und Rotation (`MRF`).
  - **Bekannte OpenFOAM-Nachteile & unsere Gegenmaßnahmen:**
    1. *Kein natives Windows:* Lokal auf Windows via WSL2/Docker; schnelle Windows-native Integrationstests laufen weiter über SU2 (`External2D`).
    2. *`snappyHexMesh` Layer-Collapse an engen Kehlen:* Automatisches **Layer-Coverage-Prüftor** nach `snappyHexMesh` (wie unser bisheriger `MeshFallback`-Check) + Option auf **`cfMesh`** oder **`gmshToFoam`** (Gmsh-Prismen-Import), falls wandaufgelöste Schichten kollabieren.
    3. *Inkompatible OpenFOAM-Dialekte (`OpenFOAM.com`/ESI vs. `OpenFOAM.org`/Foundation):* **Strikte Fixierung auf genau EINE gepinnte OpenFOAM-Linie & Version** in `setup_server.sh`, Templates und `sim.ps1 doctor` (Versions-Prüfung beim Start!).
    4. *GPL-3.0-Lizenz:* OpenFOAM wird ausschließlich als externer CLI-Subprozess aufgerufen (kein Linking) → voll vereinbar mit unserer MIT-Lizenz; sauberer Hinweis in `THIRD_PARTY_NOTICES.md`.
* **Wie verhindern wir Parameter-Angst — ohne Profis einzuschränken (Progressive Disclosure)?**
  - **Standardfall:** Ein `Internal3D`-Projekt konfiguriert in seiner JSON **nur physikalische Randbedingungen** (z. B. `Fluid`, `InletVelocity` oder `VolumetricFlowRate`, `OutletPressure`, `TurbulenceModel: kOmegaSST`, `Preset: StandardRans`). Sämtliche OpenFOAM-Dictionaries (`system/controlDict`, `fvSchemes`, `fvSolution`, `snappyHexMeshDict`, `0/U`, `0/p`, `0/k`, `0/omega`) werden deterministisch aus getesteten Preset-Templates generiert.
  - **Profi-/Manuell-Fall (100 % über JSON anpassbar, kein C#-Umbau nötig):** Alle relevanten Detail-Parameter (`snappyHexMesh`-Verfeinerungsstufen, Layer-Anzahl, Expansionsrate, `firstLayerThickness`, `fvSolution`-Relaxationsfaktoren, Solver-Toleranzen, Diskretisierungsschemata, `maxIterations`, `checkMesh`-Schwellen, `SIMPLE`/`SIMPLEC`) sind sauber als optionale Keys in `solvers/openfoam.json` verfügbar — und zusätzlich können Profis über optionale `CustomDictionaryOverrides` / eigene Template-Verzeichnisse sogar rohe OpenFOAM-Dictionary-Einträge gezielt überschreiben, ohne C#-Code anzufassen! `effective-config.json` und `--dump-preset` dokumentieren dabei jederzeit alle wirksamen Werte transparent.
* **Rootless-Server-Installation (`setup_server.sh`):**
  - Auf `BIC_12` läuft Miniconda bereits ohne `sudo`. OpenFOAM wird entweder via **`conda-forge` (`openfoam`)** in einem dedizierten Conda-Env (`conda create -n openfoam -c conda-forge openfoam`) oder — falls auf einem Cluster vorhanden — via **Apptainer/Singularity** eingebunden. `ExecutableResolver` + `sim.ps1 doctor` erkennen beide Wege sauber.

### Entscheidung 2: Multi-Patch-Geometrie-Vertrag mit `LEAP71_ShapeKernel`
* **`LEAP71_ShapeKernel` (Apache 2.0)** wird sauber als Git-Submodule oder gepinnte Source-Referenz (inkl. Eintrag in `THIRD_PARTY_NOTICES.md`) eingebunden.
* **Neuer Multi-Patch-Export in `GeometryResult`:**
  - Statt nur einer einzigen anonymen `Model.stl` kann ein 3D-Generator benannte Oberflächen-Patches ausgeben (`GeometryResult.Patches["wall"]`, `["inlet"]`, `["outlet_1"]`, ...), die aus `ShapeKernel`/`PicoGK`-Booleans (Fluidvolumen minus Einlass-/Auslass-Kappen) erzeugt werden, plus einen **Innenpunkt (`FluidSeedPoint`)** für `snappyHexMesh` (`locationInMesh`)!
  - Dadurch entfallen manuelles Patch-Labeln und fragile Koordinaten-Heuristiken komplett — genau das Prinzip, das auch LEAP 71 für die Automatisierung nutzt.

### Entscheidung 3: Hybrid-Optimierer-Bridge (`IExternalOptimizer` via JSON Ask/Tell)
* **Abgestimmt (21.11.2026):**
  - **Standard (Zero-Dependency):** Der bereinigte C#-`Bayes` (Phase 2) bleibt immer eingebaut und funktioniert ohne Python.
  - **Optionales Power-Backend (`"OptimizationAlgorithm": "ExternalPython"` bzw. `"BoTorch"` / `"Pymoo"`):**
    - Schlankes, sprachunabhängiges **Ask/Tell-JSON-Protokoll** über STDIN/STDOUT (oder temporäre JSON-Dateien pro Iteration — null Netzwerk-Overhead, 100 % reproduzierbar, voll journalisierbar!):
      - C# schreibt `opt_request.json` (Parameterraum, Objectives, Constraints, bisherige gültige/ungültige/gescheiterte Punkte, Seed, gewünschte Batch-Größe, optional Fidelity-Level).
      - Python-Worker (`scripts/optimizers/botorch_worker.py` / `pymoo_worker.py` im Conda-Env `opt-py`) rechnet die nächsten Kandidaten (z. B. **BoTorch `qLogEI` / `Constrained qLogNEI` / Multi-Objective `qNEHVI` / Multi-Fidelity `qMFKG`** oder **pymoo `NSGA-II`**) und liefert `opt_response.json` (Kandidaten + Modell-Vorhersagen + ARD-Sensitivitäten + Pareto-Ranking).
    - **Automatischer Fallback & Preflight:** Fehlt das Conda-Env, meldet `Preflight`/`doctor` exakt den 1-Zeilen-Installationsbefehl (oder fällt auf Wunsch klar dokumentiert auf C#-Bayes zurück).
    - **Architektur-Brücke zu Phase 5 (LLM):** Weil das Protokoll komplett datei-/JSON-basiert und sauber typisiert ist, kann derselbe Datenvertrag später in Phase 5 direkt als Diagnostik- & Steuerungs-API für den LLM-Agenten dienen!

---

## 3. Arbeitspakete (Checkliste)

### AP 3.1: `LEAP71_ShapeKernel` einbinden & Multi-Patch-Geometrie-Vertrag bauen — ✔ Abgeschlossen (22.11.2026)
- [x] `LEAP71_ShapeKernel` sauber einbinden (gepinnte Vendored-Kopie Commit `09545ba15eacb64fb12a5707056dbe95b605e52d` unter `third_party/ShapeKernel/` als `Leap71.ShapeKernel.csproj`, in `FluidForge.sln`/`FluidForge.csproj`/`scripts/sim.ps1` eingebunden, `THIRD_PARTY_NOTICES.md` aktualisiert).
- [x] `GeometryResult` um `Dictionary<string, string> SurfacePatches` (z. B. `inlet`, `outlet`, `wall`), `Vector3? FluidSeedPoint` und `MeshUnit` erweitern (100 % abwärtskompatibel; `MeshRequest` um optionales `Geometry`, `WorkflowController` um `Patch_*`-Artifacts & `StlWriter` um Multi-Solid-ASCII-STL erweitert; pure-Core `MultiPatchMeshPartitioner`).
- [x] Hilfs-Bausteine unter `Kernels/PicoGk/InternalFlow/` bereitstellen (`FlowPort`, `InternalFlowExportResult`, `MultiPatchFluidExporter`, `ShapeKernelPipeBuilder`): wasserdichtes Fluidvolumen mit plan getrimmten Einlass-/Auslass-Kappen aus `ShapeKernel`-Rohren/Krümmern + Export als kombiniertes STL, Einzel-Patch-STLs und Multi-Solid-ASCII-STL.
- [x] Unit- & PicoGK-Integrationstest (`MultiPatchGeometryTests.cs` + `ArchitectureBoundaryTests.cs` `Leap71`-Verbot in `Core/`): Echter 90°-Rohrkrümmer aus `ShapeKernel` exportiert wasserdichte `inlet.stl`, `outlet.stl`, `wall.stl` + gültigen `FluidSeedPoint` im Inneren (`58.024` Dreiecke, `0` offene/`0` non-manifold Kanten).

### AP 3.2: OpenFOAM-Anbindung für `Internal3D` (`Solvers/OpenFoam/`) — ✔ Abgeschlossen & auf `BIC_12` verifiziert (22.11.2026)
- [x] **Preset-gekapselte Konfiguration mit vollem Profi-Override (`OpenFoamOptions.cs`):** Im Standardfall nur physikalische Kernparameter in Projekt-JSONs (`Fluid`, `InletCondition`, `InletVelocity`/`InletVolumetricFlowRate`, `OutletPressure`, `TurbulenceModel` Default `kOmegaSST`, `Preset`); **alle** relevanten Meshing-/Solver-/Konvergenz-Details plus `CustomDictionaryOverrides` & `CustomTemplateDirectory` vollständig typgeprüft über `solvers/openfoam.json` & `schemas/openfoam.schema.json` überschreibbar.
- [x] **Template-basierter Case-Generator (`OpenFoamCaseGenerator.cs`):** Erzeugt komplette ESI-`v2506`-OpenFOAM-Fallstruktur (`0/`, `constant/triSurface/geometry.stl` in SI-Metern, `constant/transportProperties`, `constant/turbulenceProperties`, `system/blockMeshDict`, `system/surfaceFeatureExtractDict`, `system/snappyHexMeshDict`, `system/meshDict` für `cfMesh`, `system/fvSchemes`, `system/fvSolution`, `system/controlDict`, `system/decomposeParDict`) inkl. FunctionObjects für `PressureDrop`, `PressureDropStatic`, `MassBalanceError`, `FlowImbalance`, `yPlus`, `forceCoeffs` und ParaView (`case.foam` + optional `foamToVTK`).
- [x] **Mesher & Solver Stages (`OpenFoamSnappyMesher.cs`, `OpenFoamSimpleSolver.cs`, `OpenFoamProcessRunner.cs`, `OpenFoamMeshQualityParser.cs`, `OpenFoamPostProcessor.cs`, `OpenFoamConvergenceJudge.cs`):** Strikt Conda/MPICH-isolierte Ausführung mit `/usr/lib/openfoam/openfoam2506/etc/bashrc` & System-OpenMPI (`/usr/bin/mpirun`), `SnappyHexMesh` + optionales `CfMesh`-Fallback, harte deterministische Prüftore (`checkMesh`, Non-Orthogonality, Skewness, Layer-Coverage-Check, `MassBalanceError <= 1 %`, Residuen- & Trailing-Window-Drift).
- [x] **`External3D`-Profil auf OpenFOAM vorbereitet + Vorlage & Beispielprojekt:** `External3D:*`-Presets um `solvers/openfoam.json`-Overlay (inkl. Symmetrie & `forceCoeffs`, 100 % abwärtskompatibel für SU2/`MantaAuv`) ergänzt; `OpenFoamProjectDefinition`, `_TemplateInternal3D` (inkl. `scripts/new-project.ps1 -Template Internal3D`) und `PipeBend90` (≤ 15 Keys JSON) angelegt.
- [x] **Server- & Lokal-Setup (`setup_server.sh`, `sim-runner.sh`, `sim.ps1 doctor`):** System-Erkennung für `/usr/lib/openfoam/openfoam2506` (ESI `v2506`) + Rootless-Conda-Fallback (`openfoam`) + optionales `opt-py`-Env in `setup_server.sh`; isolierte OpenFOAM- & OpenMPI-Prüfung in `sim-runner.sh doctor` (`PipeBend90` & `MantaAuv` beide `VALIDATION SUCCEEDED`).
- [x] **Echter Integrationstest (`OpenFoamIntegrationTests.cs`) LIVE auf `BIC_12` bestanden (2/2 in 15,89 s):** `StraightPipe` laminar (`12.755` Zellen, `Δp = 1.2613 Pa`, `MassBalanceError = 0.0000 %`) und `PipeBend90` mit echtem `ShapeKernel` + `kOmegaSST` + `mpirun -np 4` (`17.299` Zellen, `LayerCoverage = 97.0 %`, `Δp = 138.93 Pa`, `MassBalanceError = 0.0000 %`, `YPlus_Med = 12.68`).

### AP 3.3: Hybrid-Optimierer-Bridge (`IExternalOptimizer` + Python BoTorch / pymoo) — ✔ Abgeschlossen & auf `BIC_12` verifiziert (23.11.2026)
- [x] **Ask/Tell-JSON-Vertrag spezifizieren (`Core/Optimization/ExternalOptimizerProtocol.cs` + `ParetoFrontCalculator.cs`):** Versioniertes JSON-Schema (`ProtocolVersion = "1.0"`) für Request/Response, exakte nicht-dominierte Sortierung, 2D-Hypervolume & `pareto_front.json`/STL-Export (100 % pure `Core/`).
- [x] **`ExternalProcessOptimizer.cs` (`IOptimizationAlgorithm`, `IOptimizationPreflight`) + `SimulationConfig.ExternalOptimizer` + `OptimizationAlgorithmFactory`:** Deterministischer Subprozess-Aufruf (`--request`/`--response`), Timeout, Fehlerprotokollierung, automatischer Fallback auf C#-`Bayes` (bzw. Fail-Fast wenn deaktiviert) und Registrierung von `"BoTorch"`, `"Pymoo"`, `"ExternalPython"`.
- [x] **Python-Worker implementieren (`scripts/optimizers/`):** `botorch_worker.py` (`qLogEI`/Constrained `qLogNEI`, Constrained `qNEHVI`, Multi-Fidelity, ARD-Wichtigkeiten, `--check`), `pymoo_worker.py` (`NSGA-II`/`NSGA-III` mit Constraint-Handling), `environment_opt.yml` (`opt-py` bereits auf `BIC_12` installiert).
- [x] **Pareto-Auswertung & Plots (`scripts/plot_pareto.py`):** Headless-`Agg` Pareto-Front- & Hypervolume-Plots + `--export-stls`-Export; Unit-Tests & `[PythonOptFact]`-Integrationstests in `tests/FluidForge.Tests/ExternalOptimizerTests.cs` (`972/972` Unit-Tests lokal grün, Code bereits auf `BIC_12` deployed & gebaut).
- [x] **Live-Ausführung `Category=PythonOpt` auf `BIC_12`:** Echter 2-Ziel-Constrained-Pareto-Test mit `~/miniconda/envs/opt-py/bin/python` (`BoTorch qNEHVI`, `pymoo NSGA2`, `plot_pareto.py`) auf `BIC_12` erfolgreich (`3/3 Passed, 0 Skipped, 0 Failed` in ~29 s); fehlendes `import botorch` in `scripts/optimizers/botorch_worker.py` behoben.

### AP 3.4: Multi-Fidelity-Grundlage (Grob-zu-Fein-Vorfilter) — ✔ Abgeschlossen (23.11.2026)
- [x] **Zweistufiges Fidelity-Konzept verankern (`MultiFidelityConfig`, `FidelityLevels`, `MultiFidelitySelector`, `WorkflowController`, `RunJournal`, `ExternalProcessOptimizer`, `OpenFoamCaseGenerator`):** `SimulationConfig.MultiFidelity` (`Mode: "Off" | "TwoStageFilter"`, `CoarseMeshFactor`, `CoarseMaxIterations`, `PromoteTopK`, `PromoteTopFraction`), `FidelityLevel` (`Coarse`/`High`/`Verification`) auf `ModelRecord`, in `results.csv`, `RunJournal` & Ask/Tell-JSON (`Fidelity` + `FidelityLevel` + `AvailableFidelities`), Two-Stage-Promotion inkl. Pareto-Präferenz, High-Fidelity-Vorrang in `SelectBestRecord`/`ChampionFineVerifier`/`AutoStop`, OpenFOAM-`controlDict.endTime`-Cap & 7 neue Unit-Tests (`MultiFidelityTests.cs`).
- [x] **JSON-Schemas, englische Doku & `HandbookCoverageTests`:** Alle 6 Schemas via `--export-schemas schemas` aktualisiert (`MultiFidelity`, `ExternalOptimizer`, `openfoam.schema.json`), `docs/` (`config-reference.md`, `physics-presets.md`, `creating-a-project.md`, `server-workflow.md`) & `README.md` auf Phase-3-Stand gebracht, `HandbookCoverageTests` um `OpenFoamOptions`, `ExternalOptimizerConfig` & `MultiFidelityConfig` erweitert.

---

## 4. Verifikations- & Abnahmekriterien

1. Ein neues `Internal3D`-Projekt mit `LEAP71_ShapeKernel` + OpenFOAM benötigt **<= 20 Zeilen JSON** — kein einziger OpenFOAM-Dictionary-Key muss im Projekt stehen.
2. `checkMesh` + Massenbilanz (`< 1 %`) + Konvergenzrichter lehnen numerisch kaputte OpenFOAM-Läufe automatisch und zuverlässig ab.
3. Hybrid-Bridge-Test: Synthetisches 2-Ziel-Constrained-Problem liefert reproduzierbar eine saubere **Pareto-Front** über `BoTorch` (`qNEHVI`), während derselbe C#-Core ohne Python weiter sauber mit dem eingebauten C#-`Bayes` (Single-Objective + Constraints) läuft.
4. `setup_server.sh` + `sim.ps1 doctor` richten OpenFOAM und das optionale Python-Optimierer-Env auf einem frischen Linux-Server ohne `sudo` reproduzierbar ein.

---

## 5. Handover-Hinweis für Umsetzungs-Agenten (`/delegate`)
- OpenFOAM-Integration **strikt in `Solvers/OpenFoam/`** kapseln — `Core/` darf kein einziges OpenFOAM-Detail kennen!
- Starte bei AP 3.2 zwingend mit dem einfachsten möglichen physikalischen Fall (**gerades Rohr → 90°-Rohrkrümmer**), bevor komplexe Verzweigungen gerechnet werden.
- Prüfe vor dem Conda-OpenFOAM-Setup auf `BIC_12` die genaue MPI-Verträglichkeit (`conda-forge` `openfoam` bringt sein eigenes MPI mit — getrenntes Conda-Env wie bei `mpich` verhindert Konflikte mit SU2!).

---

## 6. Umsetzungsprotokoll, Entscheidungen & Erkenntnisse (Checkpoint-Log)

### Checkpoint 1 (`AP 3.1`, 22.11.2026) — ✔ Abgeschlossen
- **Entscheidungen:**
  - `LEAP71_ShapeKernel` als gepinnte Vendored-Source-Kopie (Commit `09545ba15eacb64fb12a5707056dbe95b605e52d`, Apache 2.0) unter `third_party/ShapeKernel/` als separates Projekt `Leap71.ShapeKernel.csproj` eingebunden (isoliertes `<Nullable>disable</Nullable>` + `<NoWarn>CS8632;CS0618</NoWarn>`, sodass `FluidForge.csproj` strikt bei 0 Warnungen bleibt). `scripts/sim.ps1` um `third_party` in `$DeployRoots` & `$prune` erweitert.
  - Pure-Core-Trennung beibehalten: Dreiecks-Partitionierung und Möller–Trumbore-Point-in-Mesh-Prüfung liegen unter `Core/Utilities/MultiPatchMeshPartitioner.cs`; `PicoGK`/`Leap71`-Zugriffe bleiben strikt in `Kernels/PicoGk/InternalFlow/` (über `ArchitectureBoundaryTests` abgesichert).
  - `BIC_12` `opt-py` Conda-Env (`python=3.11`, `torch 2.15.0+cpu`, `botorch 0.18.1`, `gpytorch 1.15.2`, `pymoo 0.6.2`, `matplotlib`, `scipy`, `numpy`) erfolgreich parallel in `tmux` angelegt und import-verifiziert.
- **Erkenntnisse & Fixes:**
  - PicoGK Marching Cubes erzeugt auf achsenparallelen Schnittflächen vereinzelt kollabierte Null-Flächen-Dreiecke (`A == B`), die als Schein-Non-Manifold-Kanten auffallen; `MultiPatchFluidExporter.Export` filtert diese topologie-neutral vor der Wasserdichtheitsprüfung heraus.
  - Supervisor-Review-Fix: Doppeltes `Voxels.Dispose()` bei `trimPorts == true` in `MultiPatchFluidExporter.Export` eliminiert.
- **Nächster Schritt:** Checkpoint 2 (`AP 3.2a` — OpenFOAM C#-Kern unter `Solvers/OpenFoam/`, `SimulationPresets`, `OpenFoamProjectDefinition`, `_TemplateInternal3D` & `PipeBend90`).

### Checkpoint 2 (`AP 3.2a`, 22.11.2026) — ✔ Abgeschlossen
- **Entscheidungen:**
  - Alle OpenFOAM-Typen strikt unter `src/FluidForge/Solvers/OpenFoam/` (`OpenFoamOptions`, `OpenFoamProcessRunner`, `OpenFoamCaseGenerator`, `OpenFoamMeshQualityParser`, `OpenFoamSnappyMesher`, `OpenFoamPostProcessor`, `OpenFoamConvergenceJudge`, `OpenFoamSimpleSolver`) gekapselt; `ArchitectureBoundaryTests` erzwingt `Core/`-Freiheit von `OpenFoam`.
  - MPI-Isolation in `OpenFoamProcessRunner`: entfernt alle `miniconda`/`anaconda`/`conda`/`mpich`-Segmente aus `PATH` und `LD_LIBRARY_PATH`, lädt `/usr/lib/openfoam/openfoam2506/etc/bashrc` mit `set +u`/`set -u` und startet parallele Läufe strikt über System-OpenMPI (`/usr/bin/mpirun`).
  - SI-Meter-Skalierung erfolgt direkt beim Schreiben von `constant/triSurface/geometry.stl` (Multi-Solid-ASCII-STL) & `locationInMesh`, sodass OpenFOAM ohne externes `surfaceTransformPoints` konsistent in Metern arbeitet.
  - `External3D:*`-Presets bleiben 100 % SU2-abwärtskompatibel (`MantaAuv`, `_Template3D`) und tragen zusätzlich das `solvers/openfoam.json`-Overlay; Projekt-Basisklasse (`CfdProjectDefinition` vs. `OpenFoamProjectDefinition`) wählt das 3D-Backend.
  - Neue Vorlage `_TemplateInternal3D` (10 Keys) + Beispielprojekt `PipeBend90` (11 Keys) + `schemas/openfoam.schema.json` erstellt.
- **Verifikation:** `dotnet build FluidForge.sln --nologo -warnaserror` (**0 Warnungen, 0 Fehler**), `957/957` Unit-Tests grün, `--print-config PipeBend90` & `--dump-preset Internal3D:StandardRans` verifiziert.
- **Nächster Schritt:** Checkpoint 3 (`AP 3.2b` — `sim-runner.sh`/`sim.ps1 doctor`/`setup_server.sh` um OpenFOAM ESI `v2506` erweitern, Deploy auf `BIC_12` und echte `OpenFoamIntegrationTests` mit geradem Rohr & 90°-Krümmer real auf `BIC_12` verifizieren).

### Checkpoint 3 (`AP 3.2b`, 22.11.2026) — ✔ Abgeschlossen
- **Live-Verifikation auf `BIC_12` (`openfoam2506` + Open MPI `4.1.6` + `opt-py`):**
  - `sim.ps1 doctor -Project PipeBend90` & `-Project MantaAuv`: beide **`VALIDATION SUCCEEDED`**.
  - `PipeBend90 --smoke-test` unter `xvfb-run`: Exit 0 (**`PASS 6/6 probes, 5/5 parameter checks`**).
  - `OpenFoamIntegrationTests` (2/2 in 15,89 s):
    - `StraightPipe` (laminar, `np=1`): `MeshCells=12.755`, `NonOrthoMax=25,79°`, `62` Iter., `PressureDrop=1.2613 Pa`, `MassBalanceError=0.0000 %`.
    - `PipeBend90` (`ShapeKernel`, `kOmegaSST`, `mpirun -np 4`): `MeshCells=17.299`, `NonOrthoMax=60,22°`, `LayerCoverage=97,0 %`, `97` Iter., `PressureDrop=138.93 Pa` ($K \approx 0.28$), `MassBalanceError=0.0000 %`, `YPlus_Med=12.68`, `YPlus_Max=19.04`.
- **Reale `openfoam2506`-Stolpersteine gefunden & sofort behoben:**
  1. `snappyHexMeshDict`: per-Region `patchInfo` muss unter `refinementSurfaces/geometry/regions/<patch>/patchInfo` verschachtelt sein; `maxBoundarySkewness` auf `options.MaxSkewness` statt `20` gesetzt.
  2. Paralleles `decomposePar` für `snappyHexMesh -parallel` scheitert, wenn `0/` bereits existiert (da `background`-Patch vom `blockMesh` nicht in `0/` steht) → `OpenFoamSnappyMesher` stasht `0/` während des parallelen Meshing-Blocks sauber in `_0_meshing_backup` und stellt es im `finally` wieder her.
  3. `openfoam2506` schreibt die Layer-Coverage-Tabelle 6-spaltig (`patch faces target mesh thickness [m] [%]`) → `OpenFoamMeshQualityParser` entsprechend erweitert.
  4. `openfoam2506` gibt beim Start `trapFpe: Floating point exception trapping enabled (FOAM_SIGFPE).` aus → `ContainsFatalOrCrashLine` ignoriert diesen Banner; `ResolveEffectiveTailCount` begrenzt das Drift-Fenster bei schnell konvergierenden Läufen auf `N/3`.
- **Verifikation:** Lokal `dotnet build -warnaserror` (0 Warnungen, 0 Fehler), `958/958` Unit-Tests grün.
- **Nächster Schritt:** Checkpoint 4 (`AP 3.3` — Hybrid-Optimierer-Bridge `ExternalOptimizerProtocol`, `ExternalProcessOptimizer`, `botorch_worker.py`, `pymoo_worker.py`, `environment_opt.yml`, `plot_pareto.py` & echter 2-Ziel-Constrained-Pareto-Test auf `BIC_12`).

### Checkpoint 4a (`AP 3.3` Implementierung + lokale Verifikation, 22./23.11.2026) — ✔ Abgeschlossen (Pause um `00:03:38 CET`)
- **Warum der Nachtlauf gestoppt hat:** Kein Code-Fehler, kein Crash und kein VPN-Abbruch — um **`00:03:20 CET`** startete `hydrogen-ap33` den `sim.ps1 deploy -Project PipeBend90 -Force` als Hintergrund-Task (`task-114`), der auch erfolgreich durchlief (`0 Warning(s), 0 Error(s)` auf `BIC_12` in 3,62 s). Der Swarm-Worker beendete um **`00:03:38 CET`** seinen Turn im Zustand `waiting`, wodurch das Background-Task-Ende ihn nicht automatisch reaktivierte und der Coordinator auf `swarm_wait` weiter schlief.
- **Bereits umgesetzt & lokal verifiziert (committed):**
  - `src/FluidForge/Core/Optimization/ExternalOptimizerProtocol.cs` & `ParetoFrontCalculator.cs`
  - `src/FluidForge/Core/Algorithms/ExternalProcessOptimizer.cs`, `OptimizationAlgorithmFactory.cs`, `SimulationConfig.cs` (`ExternalOptimizerConfig`), `schemas/simulation.schema.json`, `docs/config-reference.md` (erste Ergänzung für `ExternalOptimizer`)
  - `scripts/optimizers/botorch_worker.py`, `scripts/optimizers/pymoo_worker.py`, `scripts/optimizers/environment_opt.yml`, `scripts/plot_pareto.py`
  - `tests/FluidForge.Tests/ExternalOptimizerTests.cs` (Unit-Tests + `[PythonOptFact]`-Integrationstests für `BoTorch qNEHVI`, `pymoo NSGA2`, `plot_pareto.py`)
  - **Verifikation:** `dotnet build FluidForge.sln --nologo -warnaserror` (**0 Warnungen, 0 Fehler**), `dotnet test --filter "Category!=Integration"` (**972/972 Unit-Tests bestanden**), Deploy & Build auf `BIC_12` bereits grün (`0 Warning(s), 0 Error(s)`).

### Checkpoint 4b (`AP 3.3` Live-Verifikation auf `BIC_12`, 23.11.2026) — ✔ Abgeschlossen
- **Live-Verifikation auf `BIC_12` (`~/miniconda/envs/opt-py/bin/python`, `torch 2.15.0+cpu`, `botorch 0.18.1`, `pymoo 0.6.2`):**
  - Erster Lauf deckte fehlendes `import botorch` in `scripts/optimizers/botorch_worker.py` auf (`botorch.__version__` warf `NameError` bei `--check`).
  - Nach Ergänzung von `import botorch`: `dotnet test tests/FluidForge.Tests/FluidForge.Tests.csproj --filter "Category=PythonOpt"` real auf `BIC_12` bestanden (**`Passed: 3, Skipped: 0, Failed: 0`** in ~29 s — `BoTorch_qNEHVI_2Obj_Constrained_Pareto_Live`, `Pymoo_NSGA2_2Obj_Constrained_Pareto_Live`, `PlotPareto_Generates_Plots_And_Exports_Stls_Live`).
  - Temporärer Testordner `~/FluidForge_opt_test` auf `BIC_12` vollständig aufgeräumt; aktueller Stand via `sim.ps1 deploy -Project PipeBend90 -Force` deployed (`0 Warning(s), 0 Error(s)`).
  - **Lokale Verifikation:** `dotnet build FluidForge.sln --nologo -warnaserror` (**0 Warnungen, 0 Fehler**), `972/972` Unit-Tests bestanden.

### Checkpoint 5 (`AP 3.4` Multi-Fidelity + Docs/Schemas + Gesamtabnahme, 23.11.2026) — ✔ Abgeschlossen
- **Entscheidungen & Umsetzung (`AP 3.4` Multi-Fidelity-Vorfilter):**
  - `SimulationConfig.MultiFidelity` (`MultiFidelityConfig`: `Mode = "Off" | "TwoStageFilter"`, `CoarseMeshFactor = 2.0f`, `CoarseMaxIterations = 500`, `PromoteTopK = 2`, `PromoteTopFraction = 0.25f`) + `WithMaxIterationsCap(...)` hinzugefügt.
  - `FidelityLevels` (`Coarse`, `High`, `Verification`, `ToNumeric`, `IsCoarse`) und `ModelRecord.FidelityLevel` eingeführt; vollständig durchgereicht in `results.csv` (Spalte `FidelityLevel` nach `FailedStage`), `RunJournal` (Persistenz & Replay-Prüfung gegen Fidelity-Kollisionen) sowie Ask/Tell-JSON (`ExternalObservation.Fidelity` & `FidelityLevel`, `MultiFidelity`, `AvailableFidelities`).
  - `MultiFidelitySelector` (pure `Core/Pipeline/`) validiert Konfiguration upfront und rankt Stage-1-Kandidaten deterministisch (Feasible > Infeasible, bei Multi-Objective Pareto-Vorrang, dann Fitness/Variant).
  - `WorkflowController` führt im Modus `TwoStageFilter` zunächst Stage 1 (Coarse-Faktor, Tag `"Coarse"`, Iterations-Cap für SU2 & OpenFOAM über `MeshRequest.MaxSolverIterations` → `OpenFoamCaseGenerator.WriteControlDict`) aus und promoviert die Top-Kandidaten auf volle Fidelity (Tag `"Fine"`, `FidelityLevel = "High"`); `SelectBestRecord`, `ChampionFineVerifier`, `AutoStop` und `ExternalProcessOptimizer`-Pareto-Export bevorzugen konsistent Non-Coarse-Ergebnisse.
  - `tests/FluidForge.Tests/MultiFidelityTests.cs` (7 Unit-Tests) & aktualisierte `SurrogateDataTests.cs`-Headerprüfung ergänzt.
- **Schemas, englische Doku & Coverage:**
  - `--export-schemas schemas` aktualisierte alle 6 JSON-Schemas (`simulation.schema.json` inkl. `MultiFidelity` & `ExternalOptimizer`, `openfoam.schema.json`, etc.).
  - `HandbookCoverageTests.SettingTypes()` um `OpenFoamOptions` (58 Keys), `ExternalOptimizerConfig` (6 Keys) und `MultiFidelityConfig` (5 Keys) erweitert.
  - `docs/config-reference.md`, `docs/physics-presets.md`, `docs/creating-a-project.md`, `docs/server-workflow.md` und `README.md` (persönliches `## Preface` unverändert erhalten) auf vollständigen Phase-3-Stand gebracht.
- **Finale Gesamtabnahme (lokal & `BIC_12`):**
  - Lokal: `dotnet build FluidForge.sln --nologo -warnaserror` (**0 Warnungen, 0 Fehler**), `dotnet test --filter "Category!=Integration"` (**984/984 bestanden, 0 fehlgeschlagen, 0 übersprungen**), `PipeBend90 --print-config` geprüft.
  - Remote `BIC_12`: `sim.ps1 deploy -Project PipeBend90 -Force` (**0 Warnungen, 0 Fehler**, 246 Dateien); `sim.ps1 doctor -Project PipeBend90` & `sim.ps1 doctor -Project MantaAuv` beide **`VALIDATION SUCCEEDED -- ready to start.`** (OpenFOAM `v2506` + isoliertes OpenMPI + `opt-py`).
- **Handover:** Phase 3 ist vollständig abgeschlossen; nächster Schritt laut Roadmap ist **Phase 4** ([`phase4_validierung_benchmarks_und_release.md`](phase4_validierung_benchmarks_und_release.md)).
