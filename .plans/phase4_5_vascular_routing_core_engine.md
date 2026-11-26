# Phase 4.4 (ehem. 4.5): Generalisierte Emergente Gefäßnetz-Engine (`FluidForge.Kernels.Vascular`) & `_TemplateVascular3D`

> **Status:** Geplant als **`AP 4.4`** direkt **nach** der Algorithmus-Überarbeitung (`AP 4.3a`) und dem `StatorCoolingRing`-Showcase-Lauf (`AP 4.3b`) sowie **vor** Gesamtauswertung, LaTeX-Bericht & Community-Release (`AP 4.5`) — bildet zugleich das ideale Fundament für Phase 5 / LLM-Code-Evolution!  
> **Verbindliche Versions-Regel („Showcase-Gewinner = Preset-Basis“):** Als Kern-Algorithmus für `FluidForge.Kernels.Vascular` und das Projekt-Preset `_TemplateVascular3D` wird **zwingend genau die beste, am Ende im `StatorCoolingRing`-Showcase (`AP 4.3b`) tatsächlich verwendete und CFD-verifizierte Version** übernommen! Zielarchitektur ist die in [`phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md`](phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md) geplante **`v4`-Engine (Geodätischer BOT/GCO-Gefäßbaum mit PBD-Krümmungsprojektion & bewusster hydraulischer Wegverlängerung)**; die ältere `v3`-FDEB-Variante dient nur als historische A/B-Baseline und wird **nicht** ins Hauptprogramm übernommen, sobald `v4` den A/B-Vergleich gewinnt.  
> **Architektur-Leitplanke:** **`src/FluidForge/Core/` bleibt strikt physik- und geometrieneutral!** Die generalisierte Engine lebt sauber schichtgetrennt unter `src/FluidForge/Kernels/Vascular/` (reines C#, 0 native Abhängigkeiten) und `src/FluidForge/Kernels/PicoGk/InternalFlow/` (PicoGK-/ShapeKernel-Voxel-Brücke), flankiert von einer neuen Projektvorlage `src/projects/_TemplateVascular3D/` inkl. `new-project.ps1`-Option.

---

## 1. Ziel & Nutzen für Nutzer (und Phase 5)

Aktuell steckt die gesamte vaskuläre Routing-Logik projektspezifisch in `StatorCoolingRing` (`StatorCoolingRingVascularEngine.cs`, nach `AP 4.3a` in der gekürten finalen Showcase-Version). Wer einen **1→8 Batterie-Kühlverteiler**, einen **2→6 Wärmetauscher-Sammler**, eine **4→1 Raketendüsen-Einspritzbrücke** oder einen **Freiform-Kühlblock um beliebige Störkonturen** optimieren möchte, müsste heute den gesamten Algorithmus kopieren und umschreiben.

**Zielbild Phase 4.4 (ehem. 4.5):**
- **Exakt die bewährte Showcase-Gewinner-Logik als Standard:** Keine doppelte Code-Pflege und kein veralteter Prototyp im Preset — `_TemplateVascular3D` und `StatorCoolingRing` laufen über **exakt denselben** finalen Routing-Kern aus `AP 4.3a/b` (Ziel: `v4` Geodätischer BOT/GCO-Gefäßbaum mit garantierter Baumstruktur, krümmungsbegrenzten 3D-Kanten, Hindernis-Umfahrung und optionaler Druckgleichheits-Wegverlängerung).
- Ein externer Ingenieur kann mit `.\scripts\new-project.ps1 -Template Vascular3D -Name MyManifold` in **< 10 Minuten** ein eigenes, topologiefreies 3D-Verteiler-/Kühlkanalprojekt aufsetzen — indem er nur noch **Ports (Ein-/Auslässe)**, **Bauraum-Grenzen (Keep-In)** und **Störkonturen (Keep-Out)** deklariert.
- **Volle Optimierer-Freiheit ohne Boilerplate:** Ein fertiges DNA-Preset (`VascularDnaPreset`) liefert direkt die im Showcase erprobten, stetigen Optimierungsparameter (z. B. BOT-Exponent $\alpha$, Ziel-Gabelungswinkel, Mindest-Krümmungsradius $R_c/D$, Druckgleichheits-Wegverlängerung $\beta$, Murray-Exponent, Asymmetrie/Wasserscheide, optionale Ausgleichsbrücke, 3D-Höhenpotential, 1D-Vorbalancierung, 45°-DfAM-Teardrop) — Nutzer können gezielt Teilmengen aktivieren oder festpinnen.
- **Synergie mit Phase 5 (LLM-Code-Evolution):** Statt 1.000+ Zeilen Geometrie-Code zu mutieren, kann ein Phase-5-LLM später kompakte deklarative `VascularProblemSpec`-Konfigurationen, neue Kostenfunktionen oder hybride SDF-Strukturen in ~40 Zeilen evolvieren — mit 100 % deterministischem, unit-testbarem Verifikations-Harness.

---

## 2. Architekturentwurf: Wo gehört es hin? (3-Schichten-Integration)

```text
  src/FluidForge/Core/                         <-- bleibt 100 % physik-/geometrieneutral (Architektur-Test!)
  
  src/FluidForge/Kernels/Vascular/             <-- SCHICHT 1: Pure-C# Vascular Routing Engine (0 PicoGK-Deps!)
    ├── VascularProblemSpec.cs                 (Ports: M Einlässe × N Auslässe, Flow-Gewichte, Routing-Modus)
    ├── VascularSdfPrimitives.cs               (ISdfKeepIn / ISdfObstacle: Ring, Box, Zylinder, Kapsel, Ebene)
    ├── VascularDnaOptions.cs                  (Stetige Wachstums-DNA + Preset-Factory für project.json)
    ├── VascularRouterEngine.cs                (Gekürter Showcase-Gewinner-Solver, Ziel: v4 Geodäten + BOT/GCO-Baum + PBD-Krümmung + Murray + 1D-Balance)
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

## 3. Kernfunktionen & Generalisierungen (von gekürter `StatorCoolingRing`-Showcase-Version → universell)

> **Hinweis:** Die konkrete innere Solver-Implementierung (Ziel: `v4` Geodätischer BOT/GCO-Baum aus [`phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md`](phase4_3a_vascular_algorithm_v4_bot_tree_redesign.md)) wird 1:1 aus der finalen, im Showcase-Lauf (`AP 4.3b`) verwendeten Gewinner-Version extrahiert und von allen Stator-Spezifika befreit.

| Baustein aus `StatorCoolingRing` (Showcase-Gewinner) | Generalisierung in `FluidForge.Kernels.Vascular` |
|---|---|
| Fixer 1 Einlass + 12 Ring-Auslässe | Beliebige **`M >= 1` Einlässe** und **`N >= 1` Auslässe** (`VascularPortSpec`: Position, Richtung, Radius, gerader Stutzen/Landebogen, optionales Soll-Flussverhältnis `TargetFlowWeight`, optionale Paar-Zuordnung oder freie M×N-Verteilung) |
| Fest codierter Ring (`R_in = 46..94`, `Z = 6..54`) | Komponierbare **Keep-In-Bauraum-Domänen** (`AnnularRingKeepIn`, `AxisAlignedBoxKeepIn`, `CylinderKeepIn`, beliebige analytische `ISdfKeepIn`) |
| Fest codierte 4 M5-Bolzen + 4 HV-Kapseln + SW-24-Ebene | Beliebige Liste aus **Keep-Out-Hindernissen** (`CapsuleObstacle`, `CylinderObstacle`, `HalfSpacePlaneObstacle`, `BoxObstacle`, `ISdfObstacle`) mit individueller Sicherheitsmarge, Name (für Diagnose/Constraints) und allgemeinem 3D-Geodäten-/SDF-Ausweichverhalten |
| Ring-Wasserscheide `UpperLowerSplitBias` & optionale Fernseiten-Brücke | **Routing-Modi (`VascularTopologyMode`)**: `AnnularAroundCore` (mit freier Wasserscheide + optionaler einzelner Ausgleichsbrücke wie Stator), `OpenFreeSpace` (Box-/Quader-Verteiler ohne zentralen Kern), `MultiInletMerge` (M→N-Sammler/Mischer) |
| Gekürte Showcase-Wachstums-DNA (`v4`-Standard: BOT-$\alpha$, Ziel-Y-Winkel, $R_c/D$, Wegverlängerung $\beta$, Murray, 1D-Balance) | **Stufenweises DNA-System (`VascularDnaProfile`)**: `Compact` (6–8 Kernparameter für schnelle Konzepte mit C#-`Bayes`), `Standard` (10–12 Parameter), `Full3D` (volle Showcase-DNA für `BoTorch qNEHVI`) — jeder Parameter über `project.json` aktivierbar oder fixierbar |
| Baum-exakte 1D-Hydraulik (Blasius + Krümmer/Abzweig) & `ResidualThroatTilt` | Vollständig port-allgemein: gewichtet jeden Auslass gegen `TargetFlowWeight`, kombiniert optionale geometrische Wegverlängerung (`PathBalanceWeight`) mit maskierter Endast-Drosselung; bei `M > 1` Einlässen analog auf dem jeweiligen Teilbaum |
| Inter-Tube-Sanduhr-Rippen + Dom-/Flansch-Stützen | Generischer **`VascularRibMatrixBuilder`**: erzeugt kollisionsgeprüfte Inter-Tube-Verbindungsrippen sowie konfigurierbare `SupportAnchorSpec[]` (beliebige Flansch-/Dom-Fußpunkte mit Zielrichtung & Radiusverlauf), alle automatisch gegen sämtliche `ISdfObstacle` & `ISdfKeepIn` gefiltert |
| `EvaluateTeardropCrossSectionRadius` & Einmal-pro-Kante-`MIN_ROTATION`-Sweep | Zentral in `TeardropCrossSection.cs` + `VascularVoxelBuilder.cs`: optional `Circular`, `Elliptical`, oder `SelfSupportingTeardrop45` (konfigurierbare Druckrichtung `BuildDirection`, Standard `+Z`!) |

---

## 4. Arbeitspakete (Checkliste für `/delegate` in Phase 4.4 / ehem. 4.5)

- [ ] **AP 4.4.1 — Pre-Decision Review & API-Design (`Supervisor ⇄ reviewer` `gemini-4-hydrogen-xhigh`):**
  - Verifizieren, welche konkrete Showcase-Version aus `AP 4.3a/b` gekürt wurde (Ziel: `v4` Geodätischer BOT/GCO-Baum) und exakt diese als einzige Quelle für `FluidForge.Kernels.Vascular` festschreiben.
  - Exakte Schnittstellen für `VascularProblemSpec`, `ISdfKeepIn` / `ISdfObstacle`, `VascularDnaOptions`, `VascularTopologyMode` und `VascularVoxelBuilder` festlegen.
  - Prüfen, ob `VascularConstraintsHelper` direkt fertige `ConstraintEvaluation`-Listen aus `VascularPackagingReport` generieren kann (reduziert Boilerplate in Projekten drastisch!).
  - Architektur-Test verifizieren (`Core/` bleibt 100 % frei von `Kernels.Vascular`- und `PicoGK`-Referenzen).
- [ ] **AP 4.4.2 — Pure-C# Engine `FluidForge.Kernels.Vascular` (`hydrogen-worker`, `high`):**
  - 1:1-Extraktion & Generalisierung der **finalen, im Showcase (`AP 4.3a/b`) gekürten Routing-Engine** (Ziel: `v4` Geodätischer BOT/GCO-Baum + PBD-Krümmungs-/Abstands-Projektion + optionale Druckgleichheits-Wegverlängerung), SDF-Primitive, `TeardropCrossSection` (mit konfigurierbarer `BuildDirection`), `VascularPackagingEvaluator` (inkl. Krümmungs-, Selbstkreuzungs- & Y-Winkel-Metriken) und `VascularRibMatrixBuilder` nach `src/FluidForge/Kernels/Vascular/`.
  - Umfassende Pure-C#-Unit-Tests (`tests/FluidForge.Tests/VascularKernelTests.cs`):
    - Ring-Domäne (1→12 mit zentralem Kern, Wasserscheide & optionaler Fernseiten-Brücke),
    - Quader-/Box-Domäne (1→6 Kühlplatte mit zylindrischen Schraub-Keepouts, `OpenFreeSpace`),
    - Multi-Inlet-Sammler (2→4 / 4→1 Mischer),
    - Determinismus, Kollisionsfreiheit aller Hindernisse & Rippen, `BuildDirection`-Invarianz des 45°-Teardrops, `SelfIntersectionCount == 0`, `MinBendRadiusRatio`, `MinUnbundledGapMm`/Sliver-Prüfung.
- [ ] **AP 4.4.3 — PicoGK-Brücke `VascularVoxelBuilder` & `VascularSolidHelpers` (`hydrogen-worker`, `high`):**
  - Implementierung unter `src/FluidForge/Kernels/PicoGk/InternalFlow/VascularVoxelBuilder.cs` & `VascularSolidHelpers.cs` (Einmal-pro-Kante-`Frames.EFrameType.MIN_ROTATION`-Sweeps, Fluid- & Variable-Wall-Shell-Voxels, Lattice-Rippenmatrix, `voxOverOffset` + `voxSmoothen`, wasserdichter STL-Export).
- [ ] **AP 4.4.4 — Dogfooding-Refaktorierung `StatorCoolingRing` (`hydrogen-worker`, `high`):**
  - `StatorCoolingRing` nutzt direkt `FluidForge.Kernels.Vascular` + `VascularVoxelBuilder` statt eigener Projektimplementierung; projektspezifisch bleiben nur noch die mechanischen Stator-Schnittstellen (G3/8" + SW-24-Boss, 4× M5-DIN-912-Dome, O-Ring-Nuten, Passstifte, HV-Kapseln, `*_context_environment.stl`).
  - **Harte Regressions-Abnahme:** Exakte Reproduktion der finalen Showcase-Ergebnisse aus `AP 4.3a/b`: alle `StatorCoolingRingTests`, `FEASIBILITY-200 >= 85 %`, `--smoke-test` (`6/6`, alle Parameter `EFFECTIVE`) und alle Showcase-Morphologien (`hex_fluid_encroachment_tris = 0`, `HV = 0`, `Bolt = 0`) bleiben vollständig grün!
- [ ] **AP 4.4.5 — Projektvorlage `_TemplateVascular3D`, `new-project.ps1` & Nutzer-Doku (`hydrogen-worker`, `medium`):**
  - Neue fertige Vorlage `src/projects/_TemplateVascular3D/` auf Basis derselben Showcase-Gewinner-Engine (z. B. 1→6 Kompakt-Kühlverteiler in einem Quader-Bauraum mit 2 Schraub-Keepouts — demonstriert bewusst einen **anderen** Anwendungsfall als den Stator-Ring!).
  - Integration in `scripts/new-project.ps1` (`-Template Vascular3D` neben `2D`, `3D`, `Internal3D`).
  - Kompakte englische Anleitung unter `docs/vascular-routing.md` + Verlinkung in `docs/creating-a-project.md` und `README.md`.
- [ ] **AP 4.4.6 — Verifikation, Post-Implementation Review (`APPROVE`) & `BIC_09`-Smoke-Test:**
  - `dotnet build FluidForge.sln -warnaserror` (0W/0E), volle Testsuite grün, `--smoke-test` auf `StatorCoolingRing` und einem frisch per `new-project.ps1 -Template Vascular3D` generierten Testprojekt (lokal & kurz auf `BIC_09`).

---

## 5. Abnahmekriterien für Phase 4.4 (ehem. 4.5)

1. **Exakte Showcase-Gewinner-Parität:** `FluidForge.Kernels.Vascular` und `_TemplateVascular3D` nutzen nachweislich **exakt die finale, beste Version** des Algorithmus, die auch im `StatorCoolingRing`-Showcase (`AP 4.3b`) eingesetzt wurde — kein veralteter `v3`-Abzweig!
2. **`Core/`-Isolation 100 % intakt:** Bestehende Architektur-Tests bestätigen, dass `src/FluidForge/Core/` keinerlei Abhängigkeit auf `Vascular` oder `PicoGK` hat.
3. **Mindestens 2 grundlegend verschiedene Domänen verifiziert:** Sowohl `StatorCoolingRing` (`AnnularAroundCore`, 1→12) als auch `_TemplateVascular3D` (`OpenFreeSpace`, 1→6 Box-Verteiler) laufen über exakt dieselbe Kern-Engine (`FluidForge.Kernels.Vascular` + `VascularVoxelBuilder`) mit `Open=0, NonMan=0` und `0` Hindernis-Kollisionen.
4. **Zero-Regression auf `StatorCoolingRing`:** Alle `StatorCoolingRingTests` und alle finalen Showcase-Morphologien bleiben nach der Dogfooding-Umstellung geometrisch & analytisch voll valide.
5. **First-10-Minutes-Erlebnis:** Mit `.\scripts\new-project.ps1 -Template Vascular3D -Name DemoManifold` entsteht ein direkt kompilierbares, per `--smoke-test` prüfbares und auf OpenFOAM/BoTorch lauffähiges 3D-Verteilerprojekt.
