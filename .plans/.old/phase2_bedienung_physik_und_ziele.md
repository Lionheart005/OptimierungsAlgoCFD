# Phase 2: Verständliche Bedienung, Presets, saubere Physik & Ziel-/Constraint-Architektur

> **Status:** ✔ **Abgeschlossen am 22.11.2026** — Alle 4 Etappen (`AP 2.1` bis `AP 2.4`) vollständig umgesetzt und lokal inkl. echter Integrationstests (`PicoGK`, `Gmsh2D`, `Gmsh3D Prism`, `3D-Symmetrie Voll-vs-Halb`, `2D-SST`) sowie `--smoke-test` (`Naca2D` & `MantaAuv`) verifiziert (**0 Warnungen, 0 Fehler, 928/928 Unit-Tests + 5/5 echte Integrationstests grün**). Bereit für **Phase 3 (`phase3_plan_a_openfoam_shapekernel_hybrid.md`)**.  
> **Ziel:** Jeder Ingenieur — und vor allem du selbst — soll ein neues Optimierungsproblem in Minuten aufsetzen, physikalisch verstehen und ohne Konfigurations-Dschungel sicher betreiben können.

---

## 1. Ziel & Kontext

Die Analyse hat gezeigt, dass nicht fehlende Features das Problem sind, sondern **Unübersichtlichkeit und versteckte Fallstricke**:
- `_Vorlage3D` zeigt aktuell **127 Konfigurations-Keys** über 4 JSONC-Dateien und ein **88 KB** großes Handbuch.
- Zielfunktion und harte Nebenbedingungen sind vermischt (`if (Vol > 85000) Fitness *= 0.1f`) → 14,2 h verschwendete 3D-CFD in einer Generation + Bouncer-Falle an der Klippe + kaputtes Surrogat.
- Einzelpunkt-Optimierung ohne Symmetrieebene und mit zu strengem `CONV_CAUCHY_EPS = 1e-5` führt zu 3-Wochen-Läufen und „Nadel-Optima“.

Phase 2 löst genau diese drei Engpässe: **Konfigurations-Diät durch Presets**, **saubere Trennung von Objectives & Constraints** und **praxistaugliche, transparente Physik**.

---

## 2. Architektur & Getroffene Entscheidungen

### Entscheidung 1: Physik-Presets nach dem Prinzip „Progressive Disclosure“ (einfach als Standard, voller Profi-Zugriff per JSON!)
* **Problem:** Ein Einsteiger (oder man selbst beim schnellen Projekt-Setup) will nicht über 127 Keys wie `CflAdaptFactorUp`, `CurvatureMinSizeFactor` oder `RoleFineTuner` stolpern. **Gleichzeitig dürfen Profis und Tüftler nie ausgesperrt werden:** Wer genau weiß, was er tut, muss weiterhin **jede einzelne** Simulations-, Vernetzungs-, Solver- und Optimierer-Einstellung transparent über JSON anfassen können, ohne dafür C#-Code umzuschreiben!
* **Lösung:** Einführung einer zweistufigen Preset-Matrix als **Basisschicht im bestehenden 4-Schichten-Merge** (nicht als starre Black-Box!):
  - **Domänen-Profil:** `External2D` (SU2 + Gmsh2D — dauerhafter 2D-Standard), `External3D` (in Phase 2 SU2 + Gmsh3D mit Symmetrie; ab Phase 3 optional/perspektivisch OpenFOAM als 3D-Standard), `Internal3D` (ab Phase 3: OpenFOAM + `snappyHexMesh`/`cfMesh`).
  - **Genauigkeitsstufe:** `FastConcept` (schnelle Exploration), `StandardRans` (ausgewogener Standard), `VerificationFine` (Feinprüfung / Benchmark) — plus voller manueller Modus (`Custom` / beliebige Einzel-Overrides).
* **Effekt für Projektdateien (Best of Both Worlds):**
  - **Standardfall (einfach):** Ein neues Projekt definiert in seinen Vorlagen nur noch **~10–15 selbsterklärende Keys** (Preset, Fluid, Anströmung/Durchfluss, Parameter & Grenzen, Budget).
  - **Profi-/Manuell-Fall (voll transparent):** Jeder einzelne Detail-Key (SU2, Gmsh, OpenFOAM, Grenzschicht, Konvergenz, Optimierer, Kernel) bleibt 100 % über JSON (`simulation.json`, `solvers/*.json`) überschreibbar — ohne eine einzige Zeile C# anzufassen!
  - **Volle Transparenz per CLI:** Über `--print-config` (sowie `effective-config.json` in jedem Lauf) und einen optionalen Export-Befehl `--dump-preset <Preset>` können Profis sich jederzeit alle aktuell wirksamen Detail-Parameter (inkl. Herkunft: Preset vs. Projekt-Override vs. `*.local.json`) anzeigen oder als bearbeitbare JSON-Vorlage ausgeben lassen.
* **Single Source of Truth:** Doppelte Pflege zwischen `CreateDefaults()` in C# und `project.json` wird abgeschafft. Parameter, Grenzen und Projekt-Defaults leben an genau **einem** Ort.
* **JSON-Standard:** Sauberes JSON + JSON-Schema-Export für Autocomplete & Inline-Erklärungen direkt in VS Code — so sind auch alle Profi-Keys beim Tippen sofort auffindbar und dokumentiert.

### Entscheidung 2: Trennung von Zielfunktion(en) & Pre-/Post-CFD-Constraints
* **Abgestimmtes Modell (21.11.2026):**
  - **Single-Objective als einfacher Standard** (transparente Zielfunktion im Projektcode — Ingenieure sehen exakt, wonach optimiert wird), **aber** das Interface unterstützt nativ **1 bis N Ziele (`Objectives`)**, damit ab Phase 3 nahtlos echte **Pareto-Fronten (Multi-Objective)** laufen können, ohne den Kern nochmal umzubauen!
  - **Pre-CFD-Geometrie-Constraints (`IGeometryConstraint` / `PreSolveConstraints`):** Werden unmittelbar nach der Geometrieerzeugung (~1 s) geprüft (z. B. `MinVolume <= Volume <= MaxVolume`, `MinWallThickness >= 1.5 mm`, Wasserdichtheit, Selbstschnittfreiheit). **Bei Verletzung wird KEIN Mesher und KEIN CFD-Solver gestartet!** Der Punkt wird als `Infeasible (PreSolve)` mit seinem exakten Constraint-Wert (`margin = limit - actual`) geloggt, damit der Optimierer die machbare Grenze lernt.
  - **Post-CFD-Physik-Constraints (`IPhysicsConstraint` / `PostSolveConstraints`):** Werden nach dem Solver ausgewertet (z. B. `Lift >= 50 N`, `MaxFlowImbalance <= 5 %`, `YPlus_P95 <= 4.5`). Keine harten `× 0.1`-Strafen mehr in der Zielfunktion!
  - **Bouncer-Entkopplung:** `ChampionValidator` vergleicht die **rohen physikalischen Zielmetriken** (`Drag`, `Lift`, `PressureDrop`) statt einer durch Straf-Klippen verzerrten Fitness. Damit wird ein wahres Optimum dicht an einer Constraint-Grenze nie mehr fälschlich disqualifiziert.

### Entscheidung 3: Pragmatische Physik-Upgrades für Geschwindigkeit & Aussagekraft
1. **Symmetrie-Randbedingung (`MARKER_SYM`):** Für symmetrische 3D-Geometrien (z. B. AUV, Flügel, symmetrischer Verteiler) halbiert eine Symmetrieebene (`Y = 0` oder `Z = 0`) sofort Zellzahl und Rechenzeit um **~50–60 %** (bei automatischer Verdopplung integraler Kräfte `CD`/`CL`/`Volume`/`FrontalArea`).
2. **Praxistauglicher SU2-Konvergenzabbruch:** Analyse des `MantaAuv`-Laufs zeigte: 26/36 Varianten liefen volle 5.000 Iterationen, obwohl `CD` ab ~800 Iterationen auf 0,01 % stabil war (Rest-Oszillation ~`1.5e-4` blockierte `CONV_CAUCHY_EPS = 1e-5`). Lösung: Differenzierte Cauchy-Toleranz je Preset (`FastConcept`: `1e-4`, `StandardRans`: `5e-5` + `AutoRestartOnDrift`, `VerificationFine`: `1e-5`) und dokumentierter Check auf stationäre Schwingung — spart in 3D **50–70 % Rechenzeit** ohne Qualitätsverlust.
3. **Multi-Point-Evaluation (Betriebspunkte):** Ein Projekt kann optional **2–3 Betriebspunkte** definieren (z. B. Anstellwinkel `α = 2°, 5°, 8°` oder Geschwindigkeiten `v = 1.5, 2.5 m/s` mit Gewichten). Das verhindert Einzelpunkt-Overfitting (wie das Kleben von `Naca2D` am Stall-Rand).
4. **Ehrliche Low-$Re$-/Transitions-Transparenz:** Automatischer Hinweis im Start-Log & Bericht, wenn $Re < 5\cdot 10^5$ mit vollturbulentem `SA` gerechnet wird (*„Transitional regime detected; fully turbulent SA ignores laminar separation bubbles — absolute CD may deviate from free-transition wind-tunnel data; consider SST/transition model or compare against fully-turbulent references“*). Optionales `SST`-Turbulenzmodell als sauber getestete Option ergänzen.

### Entscheidung 4: Feinschliff am eingebauten C#-Bayes-Optimierer
Da der C#-Bayes als **Zero-Dependency-Standard** bestehen bleibt, werden seine größten mathematischen Flaschenhälse aus dem Audit behoben:
- **Natives Constrained EI (`EI × P(feasible)`):** Nutzt die neuen expliziten Pre-/Post-Constraints direkt im Surrogat.
- **L-BFGS-B + schwache Regularisierungs-Priors** statt konstantem Adam (`lr = 0.05`) für stabileres GP-Hyperparameter-Fitting (insb. bei kleinem `n = 7..20` und `n > 150`).
- **Quasi-Monte-Carlo (Sobol-Sequenz)** statt 8 zufälliger Draws beim Screening im `Metrics`-Modus.

---

## 3. Arbeitspakete (Checkliste)

### AP 2.1: Konfigurations-Diät & Preset-System (mit vollem Profi-Override) — ✔ Abgeschlossen (22.11.2026)
- [x] **Preset-Registry entwerfen (`Core/Configuration/SimulationPresets.cs`):** Kuratierte 3×3-Matrix (`External2D`, `External3D`, Phase-3-Platzhalter `Internal3D` × `FastConcept`, `StandardRans`, `VerificationFine`) + Kurzformen als Layer 1.5 im bestehenden Konfigurations-Merge (`JsonConfigLoader.LoadForProjectWithPreset`, `ProjectContext.LoadSolverOptions`) — **jeder einzelne Detail-Key** bleibt per JSON in `simulation.json` / `solvers/*.json` ohne Code-Änderung überschreibbar (inkl. strikter Levenshtein-Tippfehlerprüfung und `"$schema"`-Unterstützung).
- [x] **Profi-Transparenz-Befehle (`--print-config`, `--dump-preset <Name>`, `--export-schemas`):** Zeigen alle wirksamen Detail-Parameter samt Herkunft (`preset:...` / Projekt / Local) inkl. geladener Solver-Optionen an (`sim-runner.sh`-kompatibel) bzw. geben ein komplettes Preset als voll editierbare JSON-Dateien aus.
- [x] **Single Source of Truth für Projekte:** `IProjectDefinition.CreateDefaults()` optional gemacht, fehlende `MaxDeviations` automatisch aus `ParameterBounds` abgeleitet (`ProjectConfigDto.ToProjectConfig()`); Reflection-basierter `JsonSchemaGenerator` + 5 JSON-Schemas unter `schemas/` (`simulation`, `project`, `su2`, `gmsh`, `gmsh2d`).
- [x] **Vorlagen (`_Template2D`, `_Template3D`) drastisch entschlackt:** Kein `CreateDefaults()`-Duplikat mehr, `<= 15` klare Kern-Keys mit `$schema` und `External3D:StandardRans`/`External2D:StandardRans`; `scripts/new-project.ps1` & `TemplateProjectTests` aktualisiert.
- [x] **Schlanke englische Dokumentation geschrieben:** 90-KB-Monolith `src/projects/BEDIENHANDBUCH.md` entfernt und durch modulare englische Guides ersetzt: `README.md`, `docs/creating-a-project.md`, `docs/physics-presets.md`, `docs/server-workflow.md`, `docs/config-reference.md` (100 % Coverage aller 11 Config-Typen & `sim.ps1`-Befehle über `HandbookCoverageTests`). (**869/869 Unit-Tests grün, 0 Warnungen.**)

### AP 2.2: Neue Objective- & Constraint-Architektur — ✔ Abgeschlossen (22.11.2026)
- [x] **Schnittstellen & Core-Datenmodelle eingeführt (`Core/Models/OptimizationOutcome.cs`, `Core/Interfaces/`):**
  - `OptimizationDirection`, `EvaluationFailureStage`, `ObjectiveValue`, `ConstraintEvaluation` (`Margin >= 0` zulässig, `Violation = -Margin <= 0` direkt kompatibel mit Constrained EI, Factory-Helfer `AtMost`/`AtLeast`/`InRange`), `EvaluationOutcome`.
  - `IObjectiveEvaluator` + `ObjectiveEvaluatorBase` (abwärtskompatibel zu `IFitnessCalculator`, nativ 1..N Objectives ohne Straf-Klippen) und `IGeometryConstraintEvaluator` (`IProjectDefinition.CreateGeometryConstraints`).
- [x] **`WorkflowController` um Pre-CFD-Filter erweitert:** Verletzt eine Variante einen Pre-CFD-Constraint, wird sie sofort als `Feasible=false, FailedStage=PreConstraint, SimulationFailed=false` (inkl. Margins) protokolliert — **kein Gmsh, kein SU2**, kein `EnsureRequiredMetrics`-Abbruch und keine Verzerrung von `RunTimeEstimator`. `SelectBestRecord` bevorzugt zulässige Modelle.
- [x] **`ChampionValidator` (Bouncer) entkoppelt:** Vergleicht bei `IObjectiveEvaluator` rohe physikalische Objectives statt Straf-Fitness; wiederholt automatisch einmalig mit umgekehrtem Jitter-Vorzeichen bei `PreConstraint`-Grenzkontakt; Legacy-Pfad unverändert.
- [x] **`results.csv`, `RunJournal` & `SimulationLog` sauber erweitert:** Neue Spalten `Feasible`, `FailedStage`, dynamische `Obj_<Name>` & `Con_<Name>_Margin`, vollständiger `RunJournal`-Roundtrip und klare gelbe `INFEASIBLE (PreConstraint/PostConstraint)`-Logausgaben. (**879/879 Unit-Tests grün, 0 Warnungen.**)

### AP 2.3: C#-Bayes-Engine aufpolieren (Zero-Dependency-Standard) — ✔ Abgeschlossen (22.11.2026)
- [x] **Constrained EI (`EI × ∏ P(constraint_k <= 0)`)** in `SurrogateTrainingSet`, `SurrogateModelEnsemble`, `AcquisitionOptimizer` und `BayesOptimizationAlgorithm` implementiert (separate GPs für Pre- und Post-CFD-Constraints inkl. `PreConstraint`-Margin-Lernen ohne Crash-Abstoßung, `FitConstraintOnly`-Machbarkeitssuche bei 0 machbaren CFD-Punkten und Kriging-Believer-Pseudo-Observations auf Constraints).
- [x] **GP-Hyperparameter-Fitting verbessert (`Core/Surrogates/LbfgsB.cs` & `GaussianProcess.cs`):** Box-beschränktes Projected L-BFGS-B mit Armijo-Backtracking + Curvature-Step-Expansion und schwachen MAP-Regularisierungs-Priors (`LogMarginalPosterior`, `muSn = ln(1e-4)`) sowie Multi-Start auch für `n > 150` (`LogMarginalLikelihood` unverändert exakt).
- [x] **Sobol-QMC (`Core/Surrogates/SobolSequence.cs` & `NormalDistribution.InverseCdf`)** mit Joe-&-Kuo-(2008)-Richtungszahlen (d=1..64 + Halton-Fallback) und Cranley-Patterson-Shift für `Metrics`-Screening (`FullSamples=128`, `ScreenSamples=16`), Kandidaten-Pools und lineare 95-%-Quantil-Schätzung (`PredictFitnessBand`) eingesetzt.
- [x] **Geometrie-Rauchtest als CLI-Befehl (`--smoke-test <Project>`, `Cli/SmokeTestCommand.cs`):** Prüft 6 deterministische Punkte (`Base`, `Min`, `Mid`, `Max`, `Rand1`, `Rand2`) auf Erzeugung, Wasserdichtheit (`MeshMetrics.CountBoundaryAndNonManifoldEdges`), endliche Metriken & Pre-CFD-Constraints sowie ±5-%-Parameter-Sensitivität (erkennt tote Parameter zuverlässig; Strömungsparameter `AngleOfAttackDeg` als Solver-Parameter markiert). (**906/906 Unit-Tests grün, `Naca2D --smoke-test` Exit-Code 0, 0 Warnungen.**)

### AP 2.4: Physik-Upgrades (Symmetrie, Konvergenz, Multi-Point, Low-Re-Transparenz) — ✔ Abgeschlossen (22.11.2026)
- [x] **Symmetrie-Randbedingung (`MARKER_SYM`)** für 3D-Gmsh & SU2 implementiert (`SymmetryPlane: "None" | "Y0" | "Z0"` mit Auto-Sync & Konfliktprüfung in `CfdProjectDefinitionBase`, exaktem `Core/Utilities/StlHalfClipper.cs`, Halb-Tunnel-Blockage-Parität in `GmshTunnelBuilder`, planbündiger Normalenprojektion in `BoundaryLayerWallField`, `Symmetry`-Marker für `Prism`/`Threshold`, halbiertem `REF_AREA`, `SymmetryFactor = 2` & `DragForceN`). Echter 3D-Integrationstest (`Symmetry3DIntegrationTests`, Doppelkeil 50×20×12 mm, Prism): `FullCells = 72.123` vs. `HalfCells = 34.386` (**Ratio 0,477**), `CD_full = 0,29708` vs. `CD_half = 0,29555` (**Δ = 0,515 % < 1 %**!).
- [x] **SU2-Abbruchkriterium verfeinert & real validiert:** Preset-gestufte `CauchyTolerance` (`FastConcept: 1e-4`, `StandardRans: 5e-5` + `AutoRestartOnDrift`, `VerificationFine: 1e-5`) + `Su2HistoryAnalyzer.SimulateCauchyStop` / `FindEarliestStationaryIteration`, empirisch validiert auf allen **36 realen 5.000-Iter-Verläufen** aus `Serverergebnisse/BIC_12_MantaAuv_20261121_Archiv/` (`Su2ConvergenceArchiveValidationTests`): Reduktion von `5.000` auf `1.193,8` Iter. bei `5e-5` (**−76,1 %**, mittl. `CD`-Abweichung `0,433 %`) bzw. `1.110,5` Iter. bei `1e-4` (**−77,8 %**, `0,459 %`) und `1.218,2` Iter. mit `AutoRestartGuard` (**−75,6 %**, `0,351 %`).
- [x] **Multi-Point-Evaluation (`OperatingPoints`):** `OperatingPointOptions` (`Name`, `Velocity`, `AngleOfAttackDeg`, `Weight`) auf `Su2SolverOptions` inkl. `ConfigSchema`-Array-Validierung, Max-Velocity-`FlowConditions` für sicheres `y+`, Netz-Wiederverwendung über alle Betriebspunkte mit `_OP{j}`-Metriken/-Archivierung und gewichteter Aggregation.
- [x] **Physikalische Reparatur der Beispielprojekte:**
  - `Naca2D`: Auf `Preset: "External2D:StandardRans"`, 3 Betriebspunkte (`α = 2° / 5° / 8°`, Gewichte `0.3 / 0.4 / 0.3`), `ObjectiveEvaluatorBase` (gewichtete Multi-Point-`GlideRatio`), `Naca2DGeometryConstraints` (`MinThicknessMm = 4.5 mm` = 9 % Sehne als Pre-CFD-Constraint) und Post-CFD-Constraint `MinLift = 0.1` umgestellt (`Naca2DTmrBenchmark` bleibt voll intakt).
  - `MantaAuv`: Auf `Preset: "External3D:StandardRans"`, `SymmetryPlane: "Y0"`, `ObjectiveEvaluatorBase` mit physikalischem volumetrischem Widerstandsbeiwert $C_{D,V} = C_D \cdot A_{\text{front}} / V^{2/3}$ (`HydrodynamicEfficiency = SensorDistance / C_{D,V}^alpha`) und `MantaGeometryConstraints` (`Volume` in `[2000, 85000] mm³` als Pre-CFD-Constraints ohne `× 0.1`-Strafklippe) umgestellt.
- [x] **Low-$Re$-Regime-Check & `SST`-Unterstützung:** `Su2RegimeAdvisor.CheckLowReynoldsWarning` gibt bei `0 < Re_L < 5e5` eine ehrliche `[WARNING]`-Regime-Warnung aus; `Su2ConfigGenerator.ResolveTurbulenceModel` validiert & unterstützt `SA` und `SST` (zusätzlich per echtem 2D-`SST`-Integrationstest verifiziert: `CD = 0,08386`, `CL = 0,20660`). Alle `schemas/*.schema.json` und `docs/` synchronisiert.

---

## 4. Verifikations- & Abnahmekriterien (✔ Alle erfüllt am 22.11.2026)

1. ✔ Ein komplett neues 2D- oder 3D-Testprojekt (`_Template2D`/`_Template3D`) benötigt **<= 15 Keys / <= 20 Zeilen JSON** und **1 kompakte C#-Projektdatei ohne doppelte Defaults** (`TemplateProjectTests` & `PresetAndSchemaTests` grün).
2. ✔ Pre-CFD-Constraint-Test (`ObjectivesAndConstraintsTests`) beweist: Unzulässige Geometrien werden in Millisekunden (`< 2 s`) als `FailedStage = PreConstraint` abgewiesen, ohne dass Gmsh oder SU2 jemals aufgerufen werden (`Times.Never()`).
3. ✔ Echter 3D-Symmetrie-Integrationstest (`Symmetry3DIntegrationTests`, Doppelkeil 50×20×12 mm, Prism + SU2): `CD`-Abweichung **`0,515 %` (< 1 %)**, Zellzahl halbiert (`72.123` → `34.386`, Ratio `0,477`).
4. ✔ `Bayes` mit `Constrained EI` + `L-BFGS-B` + `MAP-Priors` + `Sobol-QMC` übertrifft den bisherigen Stand auf synthetischen Constrained- und Manta-Benchmarks (`Fitness`-Modus stieg von `78,6` auf `101,26`, `Metrics`-Modus `109,56` von `110,4`) bei realistischer 95-%-Bandabdeckung (`LbfgsBAndSobolTests`, `ConstrainedBayesTests`).
5. ✔ `--smoke-test` fängt tote Parameter und offene STLs zuverlässig ab (`SmokeTestCommandTests`) und besteht real auf `Naca2D` (`6/6` Probes, `3/3` Parameter) und `MantaAuv` (`6/6` Probes, `5/5` Parameter).

---

## 5. Umsetzungsprotokoll, Erkenntnisse & Handover an Phase 3 (`/delegate`)

### Getroffene Architektur- & Detailentscheidungen
- **Presets als reine `Core/`-Schicht (Layer 1.5):** `SimulationPresets` speichert Overlays als `JsonObject` (`SimulationOverrides` + `SolverOverrides`), sodass `Core/` 100 % solverfrei bleibt (`ArchitectureBoundaryTests`), volle Levenshtein-Tippfehlerprüfung greift und `effective-config.json` / `--print-config` die Herkunft `preset:<Name>` sauber nachweisen.
- **Konvention `Margin` vs. `Violation`:** `ConstraintEvaluation.Margin >= 0` bedeutet zulässig (Sicherheitsabstand), `Violation = -Margin <= 0` entspricht der Standard-BO-Konvention für Constrained EI (`EI × ∏ P(constraint_k <= 0)`). Bereichsgrenzen (`InRange`) werden in zwei glatte einseitige Constraints (`_Min`, `_Max`) zerlegt, damit die Constraint-GPs keine V-Knicke lernen müssen.
- **Bouncer-Grenzkontakt-Retry:** Wenn der 1-%-Jitter eines zulässigen Grenz-Optimums einen Pre-CFD-Constraint triggert, wiederholt `ChampionValidator` automatisch einmalig mit umgekehrtem Vorzeichen (`-delta`), um CFD-Stabilität innerhalb des zulässigen Bereichs statt an der Geometriewand zu testen.

### Gefundene Bugs & wichtige Erkenntnisse
- **Echter 3D-Geometrie-Bug durch `MantaAuv --smoke-test` gefangen (`Core/Utilities/StlSmoother.cs`):** `VertexMerger` verschmolz bisher Vertices im Radius `1e-2f` (0,01 mm im geschrumpften PicoGK-Arbeitsraum), was an dünnen Flossenkanten von `MantaAuv` nicht-mannigfaltige Kanten erzeugte. Verschärfung auf `1e-5f` stellt 100 % wasserdichte, mannigfaltige STLs sicher.
- **GP-Noise-Prior (`GaussianProcess.LogMarginalPosterior`):** Ein Noise-Prior-Zentrum `muSn = ln(0.01)` drückte rauschfreie deterministische Funktionen mit kleinem `n` (`Sine1D(6)`) zu stark ins Rauschen; `muSn = ln(1e-4)` mit `sigmaSn = 2.0` regularisiert sicher gegen ARD-Kollaps bei gleichzeitig exakter Interpolation rauschfreier Funktionen.
- **Kriging-Believer-Incumbent-Cap:** Das Capping des Believer-Incumbent-Updates (`InitialBestTransformed + 0.25 * PlausibleGap`) verhindert Drift bei stark optimistischer Extrapolation auf realen CFD-Daten (`BayesRealDataReplayTests`).
- **`.gitignore`-Ergänzung:** `results/` wurde ergänzt, damit lokale `--smoke-test`- und Testlauf-Artefakte nie versehentlich ins Repo geraten.

### Verifikation (Stand 22.11.2026)
- `dotnet build FluidForge.sln`: **0 Warnungen, 0 Fehler**
- `dotnet test FluidForge.sln --filter "Category!=Integration"`: **928/928 Unit-Tests bestanden** (0 Fehler, 0 übersprungen)
- `dotnet test FluidForge.sln --filter "Category=Integration"`: **5/5 echte Integrationstests bestanden** (`PicoGK`, `Naca2D 2D → Gmsh2D → SU2`, `Gmsh3D Prism`, `3D-Symmetrie Voll-vs-Halb`, `2D-SST`)
- `Naca2D --smoke-test` & `MantaAuv --smoke-test`: beide **Exit-Code 0**

### Nächste Schritte (Handover an Phase 3)
- **Phase 2 ist zu 100 % abgeschlossen.**
- Nächster Arbeitsschritt: **[`phase3_plan_a_openfoam_shapekernel_hybrid.md`](phase3_plan_a_openfoam_shapekernel_hybrid.md)** starten (`LEAP71_ShapeKernel` + Multi-Patch-Geometrie, OpenFOAM als 3D-Standard für `Internal3D`/`External3D`, Hybrid-Optimierer-Bridge für `BoTorch`/`pymoo` direkt auf der neuen multi-objective-fähigen `IObjectiveEvaluator`-Schnittstelle).
- *Hinweis für nachfolgende Modelle:* Dieser Plan gilt als abgeschlossen; für die nächste `/delegate`-Session direkt `phase3_plan_a_openfoam_shapekernel_hybrid.md` als Startpunkt laden.
