# Phase 3 (Plan A): `LEAP71_ShapeKernel`, OpenFOAM (`Internal3D`) & Hybrid-Optimierer-Bridge (`BoTorch`/`pymoo`)

> **Status:** Geplant (Herzstück der strategischen Neuausrichtung auf Plan A)  
> **Vision:** `PicoGK` + `LEAP71_ShapeKernel` auf ihrem wahren Heimfeld einsetzen — komplexe, 3D-druckbare **interne Fluidik** (Verteiler, Kanäle, Mischer, Düsen, Wärmetauscher) mit OpenFOAM + robuster Multi-Objective-/Multi-Fidelity-Optimierung — **ohne** Parameter-Chaos!

---

## 1. Ziel & Kontext

Mit Phasen 1 und 2 steht ein sauberer, verständlicher, englischsprachiger Kern. In Phase 3 heben wir das Framework funktional auf das nächste Level und erschließen unsere klare Open-Source-Nische:
1. **`LEAP71_ShapeKernel`** als erstklassigen parametrischen 3D-Geometrie-Baukasten anbinden (weg von rohen `Lattice.AddBeam`-Bastelformen hin zu echten parametrischen Rohren, Spline-Kanälen, Flanschen, Gyroids, Wandstärken und benannten Funktionsflächen).
2. **OpenFOAM als primären 3D-Solver für interne Strömungen (`Internal3D`)** anbinden — streng gekapselt hinter Presets, sodass die Bedienung simpel bleibt und keine 100 neuen OpenFOAM-Knöpfe in den Projektdateien auftauchen.
3. **Hybrid-Optimierer-Bridge (`IExternalOptimizer` via JSON Ask/Tell)** ergänzen: Der eingebaute C#-Bayes bleibt Zero-Dependency-Standard; optional schaltet man ein Python-Conda-Env (`BoTorch/Ax`, `pymoo`) dazu — für echte **Pareto-Fronten (`qNEHVI`, `NSGA-II`)**, **Constrained BO** und **Multi-Fidelity-BO**. Gleichzeitig schafft diese Schnittstelle das Fundament für Phase 5 (LLM-Anbindung).

---

## 2. Architektur & Getroffene Entscheidungen

### Entscheidung 1: Klare Rollenverteilung SU2 vs. OpenFOAM (gegen Parameter-Wildwuchs!)
| Domänen-Preset | Mesher | Solver | Einsatzgebiet |
| :--- | :--- | :--- | :--- |
| **`External2D`** | Gmsh 2D (O-Typ Quad-BL) | **SU2** (`INC_RANS`) | 2D-Tragflächen, Profile, NASA-TMR-Validierung (ultraschnell, 2–3 % Genauigkeit) |
| **`External3D`** | Gmsh 3D (Prism-BL + Symmetrie) | **SU2** (`INC_RANS`) | 3D-Außenumströmungen (AUVs, Rümpfe, Flügel, DARPA SUBOFF) |
| **`Internal3D`** *(NEU!)* | **OpenFOAM `snappyHexMesh` (oder `cfMesh`)** | **OpenFOAM `simpleFoam`** *(später optional `chtMultiRegionSimpleFoam`)* | **Interne 3D-Druck-Fluidik:** Rohrkrümmer, Verteiler (Manifolds), statische Mischer, Kühlkanäle, Düsen, Ventile |

* **Warum OpenFOAM + `snappyHexMesh`/`cfMesh` für `Internal3D`?**
  - `snappyHexMesh`/`cfMesh` erzeugen hex-dominante Netze mit automatischen Grenzschicht-Layern direkt auf komplexen `PicoGK`-Voxel-STLs — deutlich robuster gegenüber komplexen inneren Verzweigungen als Gmsh-Tetraeder-Remeshing (`ClassifySurfaces`).
  - OpenFOAM unterstützt nativ **beliebig viele benannte Randflächen-Patches** (`inlet`, `outlet_1..N`, `wall`, `symmetry`) aus getrennten STL-Dateien oder Multi-Solid-STL.
  - Druckverlust ($\Delta p = p_{\text{in}} - p_{\text{out}}$), Massenbilanz-Fehler ($\left|\dot{m}_{\text{in}} - \sum \dot{m}_{\text{out}}\right| / \dot{m}_{\text{in}}$), Auslass-Gleichverteilung und `yPlus` sind Standard-FunctionObjects in OpenFOAM!
* **Wie verhindern wir Parameter-Angst?**
  - Ein `Internal3D`-Projekt konfiguriert in seiner JSON **nur physikalische Randbedingungen** (z. B. `Fluid`, `InletVelocity` oder `VolumetricFlowRate`, `OutletPressure`, `TurbulenceModel: kOmegaSST`, `Preset: StandardRans`).
  - Sämtliche OpenFOAM-Dictionaries (`system/controlDict`, `fvSchemes`, `fvSolution`, `snappyHexMeshDict`, `0/U`, `0/p`, `0/k`, `0/omega`) werden **deterministisch aus getesteten Templates im Preset generiert** — kein manuelles Editieren von OpenFOAM-Textdateien!
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

### AP 3.1: `LEAP71_ShapeKernel` einbinden & Multi-Patch-Geometrie-Vertrag bauen
- [ ] `LEAP71_ShapeKernel` sauber einbinden (gepinnte Version / Submodule unter `third_party/ShapeKernel/` oder projektkompatibel, Bau auf Windows + Linux prüfen, `THIRD_PARTY_NOTICES.md` aktualisieren).
- [ ] `GeometryResult` um `Dictionary<string, string> SurfacePatches` (z. B. `inlet`, `outlet`, `wall`), `Vector3? FluidSeedPoint` und `MeshUnit` erweitern (100 % abwärtskompatibel für bestehende Ein-STL-Projekte).
- [ ] Hilfs-Bausteine unter `Kernels/PicoGk/InternalFlow/` bereitstellen: Erzeugung eines wasserdichten Fluid-Volumens mit sauber abgetrennten Einlass-/Auslass-Kappen aus `ShapeKernel`-Rohren/Kanälen (`BasePipe`, `BaseBox`, Spline-Frames) + Export als Multi-Solid-STL / Einzel-Patch-STLs.
- [ ] Unit- & PicoGK-Integrationstest: Einfacher 90°-Rohrkrümmer aus `ShapeKernel` exportiert valide `inlet.stl`, `outlet.stl`, `wall.stl` + gültigen `FluidSeedPoint` im Inneren.

### AP 3.2: OpenFOAM-Anbindung für `Internal3D` (`Solvers/OpenFoam/`)
- [ ] **Preset-gekapselte Konfiguration (`OpenFoamOptions.cs`):** Nur physikalische Kernparameter in Projekt-JSONs (`Fluid`, `InletBoundary`, `OutletBoundary`, `TurbulenceModel` Default `kOmegaSST`, `Preset`); Experten-Overrides optional.
- [ ] **Template-basierter Case-Generator (`OpenFoamCaseGenerator.cs`):** Erzeugt komplette OpenFOAM-Fallstruktur (`0/`, `constant/triSurface/`, `constant/transportProperties`, `constant/turbulenceProperties`, `system/blockMeshDict`, `system/snappyHexMeshDict`, `system/fvSchemes`, `system/fvSolution`, `system/controlDict`, `system/decomposeParDict`) inkl. FunctionObjects für:
  - Totaldruck-/Statikdruck-Mittelwerte an `inlet` & jedem `outlet_*` (`PressureDrop`),
  - Massen-/Volumenströme je Ein-/Auslass (`MassBalanceError`, `FlowImbalance`),
  - `yPlus` (`YPlus_Med`, `YPlus_P95`, `YPlus_Max`),
  - ParaView-Export (`foamToVTK` oder natives `.foam`-File).
- [ ] **Mesher & Solver Stages (`OpenFoamSnappyMesher.cs`, `OpenFoamSimpleSolver.cs`):**
  - Parallele Ausführung (`decomposePar` → `mpirun snappyHexMesh -parallel` → `checkMesh` → `mpirun simpleFoam -parallel` → `reconstructPar`).
  - Harte deterministische Prüftore: `checkMesh`-Qualitätsprüfung (non-orthogonality, skewness, negative volumes), **Massenbilanz-Check** (`MassBalanceError < 1 %` als Pflicht-Verifikationskriterium!), Residuen- & Druckverlust-Konvergenzrichter (`OpenFoamConvergenceJudge`).
- [ ] **Server- & Lokal-Setup (`setup_server.sh`, `sim.ps1 doctor`):**
  - Rootless-Installation von OpenFOAM in `setup_server.sh` (via `conda-forge` `openfoam` Env oder Apptainer-Support) und Prüfung in `sim.ps1 doctor`.
- [ ] **Echter Integrationstest (`OpenFoamIntegrationTests.cs`):** Kleines gerades Rohr (Hagen-Poiseuille / Darcy-Weisbach) rechnet real durch und prüft Massenbilanz + plausiblen Druckverlust.

### AP 3.3: Hybrid-Optimierer-Bridge (`IExternalOptimizer` + Python BoTorch / pymoo)
- [ ] **Ask/Tell-JSON-Vertrag spezifizieren (`Core/Optimization/ExternalOptimizerProtocol.cs`):** Versioniertes JSON-Schema für Request (Bounds, Objectives, Constraints, History inkl. `InfeasiblePreSolve`/`Failed`, BatchSize, Fidelity, Seed) und Response (Kandidaten, Vorhersagen + Unsicherheit, ARD-Wichtigkeit, Pareto-Maske).
- [ ] **`ExternalProcessOptimizer.cs` (`IOptimizationAlgorithm`):** Ruft den konfigurierten Python-Worker deterministisch als Subprozess mit sauberem Timeout, Fehlerprotokollierung und optionalem Fallback auf C#-Bayes auf.
- [ ] **Python-Worker implementieren (`scripts/optimizers/`):**
  - `botorch_worker.py`: Unterstützt Single-Objective **Constrained `qLogEI`**, Multi-Objective **Constrained `qNEHVI`** (Pareto-Front!) und vorbereitetes **Multi-Fidelity** (z. B. `FastConcept` vs. `StandardRans`).
  - `pymoo_worker.py`: Alternative für diskrete/gemischte oder hochdimensionale Pareto-Probleme (`NSGA-II` / `NSGA-III`).
  - `environment_opt.yml` + automatische Conda-Env-Einrichtung in `setup_server.sh` & Prüfung in `sim.ps1 doctor`.
- [ ] **Pareto-Auswertung & Plots (`scripts/plot_pareto.py`):** Automatische Berechnung und Visualisierung der nicht-dominierten Pareto-Front + Hypervolume-Fortschritt über Generationen (inkl. Export aller Pareto-optimalen STLs!).

### AP 3.4: Multi-Fidelity-Grundlage (Grob-zu-Fein-Vorfilter)
- [ ] **Zweistufiges Fidelity-Konzept verankern:** Ein Optimierer (insb. `BoTorch` oder ein einfacher Two-Stage-Filter) kann Varianten zunächst auf `FastConcept` (grob, Minuten) prüfen und nur vielversprechende Kandidaten auf `StandardRans` / `VerificationFine` hochstufen — die entscheidende Voraussetzung, um 3D-Läufe von Wochen auf Stunden/Tage zu drücken!

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
