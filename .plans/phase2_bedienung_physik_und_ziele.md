# Phase 2: Verständliche Bedienung, Presets, saubere Physik & Ziel-/Constraint-Architektur

> **Status:** Geplant (baut auf dem bereinigten Kern aus Phase 1 auf)  
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

### AP 2.1: Konfigurations-Diät & Preset-System (mit vollem Profi-Override)
- [ ] **Preset-Registry entwerfen (`Core/Configuration/SimulationPresets.cs`):** Kuratierte Kombinationen aus Domäne (`External2D`, `External3D`, Platzhalter `Internal3D`) × Genauigkeit (`FastConcept`, `StandardRans`, `VerificationFine`) als reguläre Schicht im bestehenden Konfigurations-Merge — **jeder einzelne Detail-Key** bleibt per JSON in `simulation.json` / `solvers/*.json` ohne Code-Änderung überschreibbar (inkl. strikter Levenshtein-Tippfehlerprüfung).
- [ ] **Profi-Transparenz-Befehle (`--print-config`, `--dump-preset <Name>`):** Zeigen alle wirksamen Detail-Parameter samt Herkunft (`Preset` / `Project` / `Local`) an bzw. schreiben ein komplettes Preset als voll editierbare JSON-Datei zum Experimentieren heraus.
- [ ] **Single Source of Truth für Projekte:** Redundanz zwischen `CreateDefaults()` und `project.json` beseitigen; JSON-Schema generieren/hinterlegen für Autocomplete aller Standard- und Experten-Keys im Editor.
- [ ] **Vorlagen (`_Template2D`, `_Template3D`) drastisch entschlacken:** Standardmäßig nur **~15 klare Kern-Keys** zeigen; alle Detail-Parameter bleiben voll funktionsfähig und sind sauber in `docs/config-reference.md` + JSON-Schema dokumentiert.
- [ ] **Schlanke englische Dokumentation schreiben:** Ersetze das 88-KB-Monolith-Handbuch durch klare, kurze Themen-Guides: `README.md` (5-Minuten-Quickstart), `docs/creating-a-project.md`, `docs/physics-presets.md`, `docs/server-workflow.md`, `docs/config-reference.md` (vollständige Referenz aller Experten-JSON-Keys, auto-geprüft via `HandbookCoverageTests`).

### AP 2.2: Neue Objective- & Constraint-Architektur
- [ ] **Schnittstellen einführen (`Core/Interfaces/`):**
  - `IObjectiveEvaluator` (ersetzt bzw. erweitert `IFitnessCalculator` abwärtskompatibel): liefert benannte `ObjectiveValue`s (`Minimize`/`Maximize`) + `PostSolveConstraint`s (`Name`, `ActualValue`, `Limit`, `IsSatisfied`, `Margin`).
  - `IGeometryConstraintEvaluator` (oder `PreSolveConstraints` direkt am Projekt): prüft `GeometryResult` (Volumen, Wandstärke, Dichtheit, Bounding-Box, Abstände) **vor** dem Vernetzen.
- [ ] **`WorkflowController` um Pre-CFD-Filter erweitern:** Verletzt eine Variante einen Pre-CFD-Constraint, wird sie in < 2 s als `InfeasiblePreSolve` protokolliert (inkl. Constraint-Margin für den Optimierer) — **kein Gmsh, kein SU2!**
- [ ] **`ChampionValidator` (Bouncer) entkoppeln:** Vergleicht rohe Zielgrößen statt Straf-Fitness; berücksichtigt Machbarkeit sauber getrennt.
- [ ] **`Simulation_Results.csv` & Journal sauber erweitern:** Klare Spalten `Feasible`, `FailedStage` (`None`/`Geometry`/`PreConstraint`/`Mesh`/`Solver`/`PostConstraint`), einzelne Objectives und Constraint-Margins.

### AP 2.3: C#-Bayes-Engine aufpolieren (Zero-Dependency-Standard)
- [ ] **Constrained EI (`EI × ∏ P(constraint_k <= 0)`)** in `AcquisitionOptimizer` implementieren (separate GPs für Constraints inkl. Pre-CFD-Geometrie-Constraints!).
- [ ] **GP-Hyperparameter-Fitting verbessern:** L-BFGS-B (mit sauberem Multi-Start auch für `n > 150`) + leichte schwach-informative Priors gegen ARD-Kollaps bei kleinem `n`.
- [ ] **Sobol-QMC** für das `Metrics`-Screening & die Quantil-Schätzung (`PredictFitnessBand`) einsetzen.
- [ ] **Geometrie-Rauchtest als CLI-Befehl (`--smoke-test <Project>`):** Testet den Generator automatisch an `Min` / `Mid` / `Max` / Zufallspunkten auf Kompilierbarkeit, Wasserdichtheit, sinnvolle Metriken und Parameter-Wirkung (±5 % Sensitivität — auch als Basis für Phase 5!).

### AP 2.4: Physik-Upgrades (Symmetrie, Konvergenz, Multi-Point, Low-Re-Transparenz)
- [ ] **Symmetrie-Randbedingung (`MARKER_SYM`)** für 3D-Gmsh & SU2 implementieren (konfigurierbar z. B. `"SymmetryPlane": "Y0" | "Z0" | "None"`), inkl. korrekter Flächen-/Volumen-/Kraft-Verdopplung und Unit-/Integrationstest (Halbmodell vs. Vollmodell muss identischen `CD` liefern!).
- [ ] **SU2-Abbruchkriterium verfeinern:** Preset-gestufte `CauchyTolerance` + `AutoRestartOnDrift` + optionaler gleitender Stationaritäts-Check, validiert gegen die archivierten 3D-Verläufe aus `BIC_12_MantaAuv_20261121_Archiv/`.
- [ ] **Multi-Point-Evaluation (`OperatingPoints`):** Projekt kann 1..k Betriebspunkte (`Velocity`, `AngleOfAttackDeg`, `Weight`) definieren; Solver rechnet alle Punkte sauber durch und aggregiert gewichtete Zielgrößen.
- [ ] **Physikalische Reparatur der Beispielprojekte:**
  - `Naca2D`: Umstellen auf 2–3 Betriebspunkte + realistische Mindestdicke als Constraint (löst den künstlichen `MaxCamber=8, MaxThickness=6`-Grenzkontakt).
  - `MantaAuv` (falls behalten): Umstellen auf physikalische Widerstandskraft $F_D$ bzw. volumetrischen Widerstandsbeiwert $C_{D,V} = C_D \cdot A_{\text{front}} / V^{2/3}$, Volumen als Pre-CFD-Constraint, Symmetrieebene `Y0` aktivieren.
- [ ] **Low-$Re$- & Regime-Check im Preflight:** Automatische Reynolds-Zahl-Berechnung und ehrliche Regime-Warnung beim Laufstart.

---

## 4. Verifikations- & Abnahmekriterien

1. Ein komplett neues 2D- oder 3D-Testprojekt benötigt **<= 20 Zeilen JSON** und **1 kompakte C#-Projektdatei** (ohne doppelte Defaults).
2. Pre-CFD-Constraint-Test beweist: Eine unzulässige Geometrie (`Volume > Max`) wird in < 2 Sekunden abgewiesen, ohne dass Gmsh oder SU2 aufgerufen werden.
3. Symmetrie-Test (3D-Vollmodell vs. `Y0`-Halbmodell): `CD`-Abweichung `< 1 %`, Zellzahl & Laufzeit ca. halbiert.
4. `Bayes` mit `Constrained EI` + L-BFGS-B + Sobol übertrifft auf synthetischen Constrained-Benchmarks und auf `Naca2D` (Multi-Point) den bisherigen Stand reproduzierbar; 95-%-Bandabdeckung liegt realistisch bei ~85–95 %.
5. `--smoke-test` fängt kaputte/wirkungslose Parameter vor jedem teuren Serverlauf zuverlässig ab.

---

## 5. Handover-Hinweis für Umsetzungs-Agenten (`/delegate`)
- Halte die `IObjectiveEvaluator`-Schnittstelle unbedingt **multi-objective-fähig** (Liste von Objectives), auch wenn der eingebaute C#-Bayes zunächst 1 Objective (+ beliebig viele Constraints) verarbeitet — Phase 3 dockt hier direkt `BoTorch`/`pymoo` für Pareto-Fronten an!
- `HandbookCoverageTests` auf die neue, modulare `docs/`-Struktur umstellen, statt wieder ein 90-KB-Einzelhandbuch zu erzwingen.
