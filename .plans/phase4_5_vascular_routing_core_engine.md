# Phase 4.4 (ehem. 4.5): Generalisierte Emergente Gefäßnetz-Engine (`FluidForge.Kernels.Vascular`) & `_TemplateVascular3D`

> **Status:** Geplant als **`AP 4.4`** direkt **nach** dem `StatorCoolingRing`-Showcase-Lauf (`AP 4.3b`) und **vor** Gesamtauswertung, LaTeX-Bericht & Community-Release (`AP 4.5`) — bildet zugleich das ideale Fundament für Phase 5 / LLM-Code-Evolution!  
> **Auslöser:** Der im Phase-4-Showcase [`StatorCoolingRing v3`](phase4_stator_cooling_ring_v3_emergent_vascular.md) entwickelte, rein C#-basierte 5-Phasen-Algorithmus für topologiefreie 3D-Gefäßnetze (Force-Directed Edge Bundling, 3D-SDF-Hindernis-Lernen, Murray’s Law, maskierte 1D-Vorbalancierung & organische Rippenmatrix) hat sich optisch, geometrisch (`88,5 %` 14D-Box-Feasibility, `0` Kollisionen) und CFD-seitig (`69 s` `snappyHexMesh`, `77,9 %` Boundary-Layer-Abdeckung) als derart stark erwiesen, dass er als **wiederverwendbares Kernmodul + Projekt-Preset/Vorlage** für beliebige 3D-Verteiler-, Kühlkanal- und Manifold-Probleme in `FluidForge` bereitgestellt werden soll.  
> **Architektur-Leitplanke:** **`src/FluidForge/Core/` bleibt strikt physik- und geometrieneutral!** Die generalisierte Engine lebt sauber schichtgetrennt unter `src/FluidForge/Kernels/Vascular/` (reines C#, 0 native Abhängigkeiten) und `src/FluidForge/Kernels/PicoGk/InternalFlow/` (PicoGK-/ShapeKernel-Voxel-Brücke), flankiert von einer neuen Projektvorlage `src/projects/_TemplateVascular3D/` inkl. `new-project.ps1`-Option.

---

## 1. Ziel & Nutzen für Nutzer (und Phase 5)

Aktuell steckt die gesamte emergente Gefäßnetz-Logik (~1.750 Zeilen in `StatorCoolingRingVascularEngine.cs`) fest verdrahtet in einem einzelnen Projekt (`StatorCoolingRing`) mit fixem 1→12-Ring-Layout. Wer einen **1→8 Batterie-Kühlverteiler**, einen **2→6 Wärmetauscher-Sammler**, eine **4→1 Raketendüsen-Einspritzbrücke** oder einen **Freiform-Kühlblock um beliebige Störkonturen** optimieren möchte, müsste heute den gesamten Algorithmus kopieren und umschreiben.

**Zielbild Phase 4.5:**
- Ein externer Ingenieur kann mit `.\scripts\new-project.ps1 -Template Vascular3D -Name MyManifold` in **< 10 Minuten** ein eigenes, topologiefreies 3D-Verteiler-/Kühlkanalprojekt aufsetzen — indem er nur noch **Ports (Ein-/Auslässe)**, **Bauraum-Grenzen (Keep-In)** und **Störkonturen (Keep-Out)** deklariert.
- **Volle Optimierer-Freiheit ohne Boilerplate:** Ein fertiges DNA-Preset (`VascularDnaPreset`) liefert direkt die erprobten, stetigen Optimierungsparameter (Bündelung, Murray-Exponent, Steifigkeit, Asymmetrie/Wasserscheide, Schleifen-Option, 3D-Höhenpotential, 1D-Vorbalancierung, 45°-DfAM-Teardrop) — Nutzer können gezielt Teilmengen aktivieren oder festpinnen.
- **Synergie mit Phase 5 (LLM-Code-Evolution):** Statt 1.700 Zeilen Geometrie-Code zu mutieren, kann ein Phase-5-LLM später kompakte deklarative `VascularProblemSpec`-Konfigurationen, neue Kraftgesetze oder hybride SDF-Strukturen in ~40 Zeilen evolvieren — mit 100 % deterministischem, unit-testbarem Verifikations-Harness.

---

## 2. Architekturentwurf: Wo gehört es hin? (3-Schichten-Integration)

```text
  src/FluidForge/Core/                         <-- bleibt 100 % physik-/geometrieneutral (Architektur-Test!)
  
  src/FluidForge/Kernels/Vascular/             <-- SCHICHT 1: Pure-C# Vascular Routing Engine (0 PicoGK-Deps!)
    ├── VascularProblemSpec.cs                 (Ports: M Einlässe × N Auslässe, Flow-Gewichte, Routing-Modus)
    ├── VascularSdfPrimitives.cs               (ISdfKeepIn / ISdfObstacle: Ring, Box, Zylinder, Kapsel, Ebene)
    ├── VascularDnaOptions.cs                  (Stetige Wachstums-DNA + Preset-Factory für project.json)
    ├── EmergentVascularRouter.cs              (5-Phasen-Solver: Init, Relaxation, Murray, 1D-Balance)
    ├── TeardropCrossSection.cs                (Frame-invarianter 45°-DfAM-Teardrop + lokale Wandstärke)
    ├── VascularPackagingEvaluator.cs          (Analytische Envelope-, Störkontur- & Giftspalt-Prüfung)
    └── VascularRibMatrixBuilder.cs            (Kollisionsgeprüfte Sanduhr-Rippen & adaptive Stützstreben)

  src/FluidForge/Kernels/PicoGk/InternalFlow/  <-- SCHICHT 2: PicoGK / ShapeKernel Voxel-Brücke
    ├── VascularVoxelBuilder.cs                (MIN_ROTATION-Sweeps, Fluid- & Variable-Wall-Shell-Voxels)
    └── VascularSolidHelpers.cs                (Lattice-Rippen, voxOverOffset+voxSmoothen, Clean-STL-Export)
    (nutzt direkt die bestehenden FlowPort.cs & MultiPatchFluidExporter.cs!)

  src/projects/_TemplateVascular3D/            <-- SCHICHT 3: Sofort nutzbare Vorlage & Preset
    ├── project.json                           (Standard-Vascular-DNA-Parameter + Targets)
    ├── simulation.json                        (Preset: Internal3D:StandardRans + BoTorch qNEHVI)
    ├── solvers/openfoam.json                  (Multi-Outlet PressureDrop + FlowImbalance)
    └── VascularTemplateProject.cs / Generator (Deklaratives Minimalbeispiel in ~60 Zeilen!)
```

### Warum genau diese Trennung?
1. **`Core/` bleibt sauber:** Keine Verletzung des Phase-1-Architekturtests (`Core/` darf weder `PicoGK` noch domänenspezifische Geometrie kennen).
2. **Blitzschnelle Pure-C#-Tests ohne native DLL:** Alles in `FluidForge.Kernels.Vascular` nutzt ausschließlich `System.Numerics` — voll testbar in CI, auf Windows/Linux/macOS und im analytischen Pre-CFD-Constraint-Check (`< 50–250 ms` pro Variante).
3. **Nahtlose Brücke zu bestehendem `InternalFlow`:** `FlowPort` und `MultiPatchFluidExporter` liegen bereits exakt in `src/FluidForge/Kernels/PicoGk/InternalFlow/` — `VascularVoxelBuilder` dockt dort ohne jede Duplikation an.
4. **Optional deklaratives JSON-Format (`vascular.json`) für „Zero-Code“-Standardfälle:** Nutzer können einfache Multi-Port-Manifolds (z. B. 1→6 Box-Kühler oder 1→8 Ring-Verteiler) optional sogar rein über JSON definieren, während Fortgeschrittene wie `StatorCoolingRing` beliebige eigene C#-Solid-Features (Flansche, Gewinde-Bosse, O-Ring-Nuten) um das `VascularNetwork` herum bauen.

---

## 3. Kernfunktionen & Generalisierungen (von `StatorCoolingRing v3` → universell)

| Baustein aus `StatorCoolingRing v3` | Generalisierung in `FluidForge.Kernels.Vascular` |
|---|---|
| Fixer 1 Einlass + 12 Ring-Auslässe | Beliebige **`M >= 1` Einlässe** und **`N >= 1` Auslässe** (`VascularPortSpec`: Position, Richtung, Radius, gerader Stutzen/Knie, optionales Soll-Flussverhältnis `TargetFlowWeight`, optionale Paar-Zuordnung oder freie M×N-Verteilung) |
| Fest codierter Ring (`R_in = 46..94`, `Z = 6..54`) | Komponierbare **Keep-In-Bauraum-Domänen** (`AnnularRingKeepIn`, `AxisAlignedBoxKeepIn`, `CylinderKeepIn`, beliebige analytische `ISdfKeepIn`) |
| Fest codierte 4 M5-Bolzen + 4 HV-Kapseln + SW-24-Ebene | Beliebige Liste aus **Keep-Out-Hindernissen** (`CapsuleObstacle`, `CylinderObstacle`, `HalfSpacePlaneObstacle`, `BoxObstacle`, `ISdfObstacle`) mit individueller Sicherheitsmarge, Name (für Diagnose/Constraints) und optionalem Ausweich-Bias (`BypassAxis`) |
| Ring-Wasserscheide `UpperLowerSplitBias` & `FarSideLoopAttraction` | **Routing-Modi (`VascularTopologyMode`)**: `AnnularAroundCore` (mit freier Wasserscheide + optionaler Fernseiten-Schleife wie Stator), `OpenFreeSpace` (Box-/Quader-Verteiler ohne zentralen Kern), `MultiInletMerge` (M→N-Sammler/Mischer) |
| 14D-Wachstums-DNA | **Stufenweises DNA-System (`VascularDnaProfile`)**: `Compact` (6–8 Kernparameter für schnelle Konzepte mit C#-`Bayes`), `Standard` (10–12 Parameter), `Full3D` (14 Parameter inkl. Asymmetrie, Schleife, 3D-Tilt & 1D-Vorbalancierung für `BoTorch qNEHVI`) — jeder Parameter über `project.json` aktivierbar oder fixierbar |
| 1D-Blasius-Vorbalancierung & `ResidualThroatTilt` | Vollständig port-allgemein: gewichtet jeden Pfad gegen seinen Soll-Volumenstrom `TargetFlowWeight` und maskiert Drosselung sauber mit `(1 / max(1, k_eff))^2` auf den freien Endast; bei `M > 1` Einlässen analog auf den jeweiligen Zweig |
| Inter-Tube-Sanduhr-Rippen + Dom-/Flansch-Stützen | Generischer **`VascularRibMatrixBuilder`**: erzeugt kollisionsgeprüfte Inter-Tube-Verbindungsrippen sowie konfigurierbare `SupportAnchorSpec[]` (beliebige Flansch-/Dom-Fußpunkte mit Zielrichtung & Radiusverlauf), alle automatisch gegen sämtliche `ISdfObstacle` & `ISdfKeepIn` gefiltert |
| `EvaluateTeardropCrossSectionRadius` & `MIN_ROTATION`-Sweep | Zentral in `TeardropCrossSection.cs` + `VascularVoxelBuilder.cs`: optional `Circular`, `Elliptical`, oder `SelfSupportingTeardrop45` (konfigurierbare Druckrichtung `BuildDirection`, Standard `+Z`!) |

---

## 4. Arbeitspakete (Checkliste für `/delegate` in Phase 4.5)

- [ ] **AP 4.5.1 — Pre-Decision Review & API-Design (`Supervisor ⇄ reviewer` `gemini-4-hydrogen-xhigh`):**
  - Exakte Schnittstellen für `VascularProblemSpec`, `ISdfKeepIn` / `ISdfObstacle`, `VascularDnaOptions`, `VascularTopologyMode` und `VascularVoxelBuilder` festlegen.
  - Prüfen, ob `VascularConstraintsHelper` direkt fertige `ConstraintEvaluation`-Listen aus `VascularPackagingReport` generieren kann (reduziert Boilerplate in Projekten drastisch!).
  - Architektur-Test verifizieren (`Core/` bleibt 100 % frei von `Kernels.Vascular`- und `PicoGK`-Referenzen).
- [ ] **AP 4.5.2 — Pure-C# Engine `FluidForge.Kernels.Vascular` (`hydrogen-worker`, `high`):**
  - Extraktion & Generalisierung der 5-Phasen-Engine, SDF-Primitive, `TeardropCrossSection` (mit konfigurierbarer `BuildDirection`), `VascularPackagingEvaluator` und `VascularRibMatrixBuilder` nach `src/FluidForge/Kernels/Vascular/`.
  - Umfassende Pure-C#-Unit-Tests (`tests/FluidForge.Tests/VascularKernelTests.cs`):
    - Ring-Domäne (1→12 mit zentralem Kern, Wasserscheide & Fernseiten-Schleife),
    - Quader-/Box-Domäne (1→6 Kühlplatte mit zylindrischen Schraub-Keepouts, `OpenFreeSpace`),
    - Multi-Inlet-Sammler (2→4 / 4→1 Mischer),
    - Determinismus, Kollisionsfreiheit aller Hindernisse & Rippen, `BuildDirection`-Invarianz des 45°-Teardrops, `MinUnbundledGapMm`/Sliver-Prüfung.
- [ ] **AP 4.5.3 — PicoGK-Brücke `VascularVoxelBuilder` & `VascularSolidHelpers` (`hydrogen-worker`, `high`):**
  - Implementierung unter `src/FluidForge/Kernels/PicoGk/InternalFlow/VascularVoxelBuilder.cs` & `VascularSolidHelpers.cs` (`Frames.EFrameType.MIN_ROTATION`, Fluid- & Variable-Wall-Shell-Sweeps, Lattice-Rippenmatrix, `voxOverOffset` + `voxSmoothen`, wasserdichter STL-Export).
- [ ] **AP 4.5.4 — Dogfooding-Refaktorierung `StatorCoolingRing` (`hydrogen-worker`, `high`):**
  - `StatorCoolingRing` nutzt direkt `FluidForge.Kernels.Vascular` + `VascularVoxelBuilder` statt eigener 1.750-Zeilen-Engine; projektspezifisch bleiben nur noch die mechanischen Stator-Schnittstellen (G3/8" + SW-24-Boss, 4× M5-DIN-912-Dome, O-Ring-Nuten, Passstifte, HV-Kapseln, `*_context_environment.stl`).
  - **Harte Regressions-Abnahme:** Alle 18 bestehenden `StatorCoolingRingTests`, `FEASIBILITY-200 >= 85 %`, `--smoke-test` (`6/6`, `14/14 EFFECTIVE`) und alle 4 Showcase-Morphologien (`hex_fluid_encroachment_tris = 0`, `HV = 0`, `Bolt = 0`) bleiben vollständig grün!
- [ ] **AP 4.5.5 — Projektvorlage `_TemplateVascular3D`, `new-project.ps1` & Nutzer-Doku (`hydrogen-worker`, `medium`):**
  - Neue fertige Vorlage `src/projects/_TemplateVascular3D/` (z. B. 1→6 Kompakt-Kühlverteiler in einem Quader-Bauraum mit 2 Schraub-Keepouts — demonstriert bewusst einen **anderen** Anwendungsfall als den Stator-Ring!).
  - Integration in `scripts/new-project.ps1` (`-Template Vascular3D` neben `2D`, `3D`, `Internal3D`).
  - Kompakte englische Anleitung unter `docs/vascular-routing.md` + Verlinkung in `docs/creating-a-project.md` und `README.md`.
- [ ] **AP 4.5.6 — Verifikation, Post-Implementation Review (`APPROVE`) & `BIC_09`-Smoke-Test:**
  - `dotnet build FluidForge.sln -warnaserror` (0W/0E), volle Testsuite grün, `--smoke-test` auf `StatorCoolingRing` und einem frisch per `new-project.ps1 -Template Vascular3D` generierten Testprojekt (lokal & kurz auf `BIC_09`).

---

## 5. Abnahmekriterien für Phase 4.5

1. **`Core/`-Isolation 100 % intakt:** Bestehende Architektur-Tests bestätigen, dass `src/FluidForge/Core/` keinerlei Abhängigkeit auf `Vascular` oder `PicoGK` hat.
2. **Mindestens 2 grundlegend verschiedene Domänen verifiziert:** Sowohl `StatorCoolingRing` (`AnnularAroundCore`, 1→12) als auch `_TemplateVascular3D` (`OpenFreeSpace`, 1→6 Box-Verteiler) laufen über exakt dieselbe Kern-Engine (`FluidForge.Kernels.Vascular` + `VascularVoxelBuilder`) mit `Open=0, NonMan=0` und `0` Hindernis-Kollisionen.
3. **Zero-Regression auf `StatorCoolingRing`:** Alle 18 `StatorCoolingRingTests` und alle 4 Showcase-Morphologien bleiben nach der Dogfooding-Umstellung geometrisch & analytisch voll valide.
4. **First-10-Minutes-Erlebnis:** Mit `.\scripts\new-project.ps1 -Template Vascular3D -Name DemoManifold` entsteht ein direkt kompilierbares, per `--smoke-test` prüfbares und auf OpenFOAM/BoTorch lauffähiges 3D-Verteilerprojekt.
