# Phase 4.3a: Grundlegende Überarbeitung des Vaskulären Routing-Algorithmus (`v4` — Geodätischer BOT/GCO-Gefäßbaum mit Krümmungs- & Druckgleichheits-Garantie)

> **Status (26.11.2026):** **Geplant (`AP 4.3a`)** — startet nach Abschluss der laufenden Physik-Benchmarks (`AP 4.2b`) und **vor** dem großen `BIC_12`-Showcase-Optimierungslauf (`AP 4.3b`).  
> **Auslöser:** Kritische visuelle & code-seitige Auswertung der 4 `v3`-Preview-Morphologien ([`Anmerkungen.txt`](Anmerkungen.txt), `results/StatorCoolingRing_v3_preview/overview_4_morphologies.png`).  
> **Architektur-Leitplanke:** **Strikter Core-Feature-Freeze!** Umsetzung zunächst sauber innerhalb `src/projects/StatorCoolingRing/` & `tests/FluidForge.Tests/` (ohne Änderungen an `src/FluidForge/Core/` oder `src/FluidForge/Solvers/`), aber bereits mit sauberer `ISdfKeepIn` / `ISdfObstacle`-Schnittstelle für die direkte Extraktion nach `FluidForge.Kernels.Vascular` in [`AP 4.4`](phase4_5_vascular_routing_core_engine.md).  
> **Gewinner-Regel für Showcase & Preset:** Nach Fertigstellung wird `v4` objektiv (geometrisch, visuell, Meshing & kurzer CFD-Check) gegen die bestehende `v3`-Baseline verglichen. **Die klar beste Version** wird im großen `BIC_12`-Showcase (`AP 4.3b`) gerechnet **und** anschließend in [`AP 4.4`](phase4_5_vascular_routing_core_engine.md) als universelle Kern-Engine (`FluidForge.Kernels.Vascular`) und Projekt-Preset (`_TemplateVascular3D`) ins Hauptprogramm übernommen!

---

## 1. Diagnose: Warum `v3` (FDEB) grundlegend überarbeitet wird

Die `v3`-Basis ([`phase4_stator_cooling_ring_v3_emergent_vascular.md`](phase4_stator_cooling_ring_v3_emergent_vascular.md)) hat bewiesen, dass die **äußere Infrastruktur** hervorragend funktioniert: analytische 3D-SDFs, frame-invarianter 45°-DfAM-Teardrop, `MIN_ROTATION`-Sweeps, lokale Wandstärken, Rippenmatrix, `voxOverOffset` + scharfkantiger SW-24-Sechskant, Pre-CFD-Constraints und OpenFOAM-`MultiPatchFluidExporter`. **Diese ~65–70 % bleiben vollständig erhalten.**

Das **Routing-Herzstück (`Phase A–C`)** basiert jedoch auf *Force-Directed Edge Bundling* (FDEB — ursprünglich für 2D-Netzwerkvisualisierung entwickelt) auf **12 losen, unabhängigen Polylinien ohne Baum-Gedächtnis**. Daraus folgen systematisch alle 8 Kritikpunkte aus [`Anmerkungen.txt`](Anmerkungen.txt):

| Kritikpunkt (`Anmerkungen.txt`) | Nachgewiesene Ursache in `v3` (`StatorCoolingRingVascularEngine.cs`) | Lösung in `v4` („by construction“) |
| :--- | :--- | :--- |
| **1 & 2. Re-Merging & X-Kreuzungen** | Zustandslose lokale Snap-Entscheidung je Knoten (`inSnapZone`) + blindes `voxBoolAdd` aller 12 Einzelrohre | **Expliziter gerichteter Baum-Graph $T=(V,E)$:** Einmal getrennte Äste gehören verschiedenen Teilbäumen an und können sich nie wieder vereinigen; harte Kante-gegen-Kante-Abstandsprojektion verhindert Kreuzungen |
| **3. Zu steile, abrupte Trennwinkel** | Binäre Kraft-Klippe (`rawAttr >= 0.18 / 0.28`) + sofortige volle Abstoßung `dSep ≈ 13 mm` über nur `5 mm` Y-Fade | Glatte Y-Bifurkationen aus dem **BOT-Kräftegleichgewicht**, explizit begrenzt auf ein strömungs- und druckbares Fenster (`Min/MaxBifurcationAngleDeg`, z. B. `25°–65°`) |
| **4. 90°-Abzweige im `HighBundling`** | Klassisches FDEB-Artefakt: Hauptstrang hält Seitenpfad zu lange magnetisch fest und stößt ihn am Auslass senkrecht ab | Im BOT-Baum wandert jeder Gabelungsknoten automatisch stromaufwärts ins physikalische Gleichgewicht → weiche, spitze Y-Abzweige statt 90°-T-Stücken |
| **5. Selbstkreuzung & Hin-/Zurücklaufen** | `if (j == i) continue;` (kein Selbst-Check); `ResampleInteriorArcLength` zementiert Falten | Jede Baumkante ist kurz, gerichtet, krümmungsbegrenzt und gegen Selbstkollision geprüft (`SelfIntersectionCount == 0` hart getestet) |
| **6. Snappen bei falschen Richtungen** | `cDir > 0.25` erlaubt Snappen bis `75.5°` Kreuzungswinkel; `FarSideLoop` zieht sogar `180°`-gegenläufige Pfade zusammen | Kein blindes Magnet-Snapping mehr! Optionaler Ring-Schluss existiert nur als **genau eine explizite, krümmungsbegrenzte Brückenkante** |
| **7. Kürzeste Wege vs. bewusste Verlängerung für Druckausgleich** | `InitializeGeodesicInterior` kennt keine Hindernisse; lokale Zugfeder `fTens` ist 20–100× schwächer als SDF/Repel und zieht Bäuche in 180 Schritten nicht straff | **Stufe 1:** echte 3D-Geodäten um alle Hindernisse als Basis (lange Pfade bleiben maximal kurz!). **Stufe 2:** gezielter `PathBalanceWeight`-Term $\beta \in [0, 1]$ erlaubt **bewusste** topologische/geometrische Wegverlängerung **nur** auf zu kurzen Ästen für Druckgleichheit! |
| **8. 120°-Knicke mit Mini-Radius** | Kein minimales Krümmungsradius-Limit ($R_c/D$); schwache Biharmonic verliert gegen SDF/Snap; harter Sturzflug in fixierte Endknoten (`Z = 5..9.8 mm`) | **Harte PBD-Krümmungsprojektion** ($R_c \ge \lambda \cdot D_{\text{local}}$, z. B. $\ge 1.5\,D$) + geplanter 90°-Landebogen vor jeder vertikalen Düse |

---

## 2. Wissenschaftliche Basis der `v4`-Architektur

Der neue Kern kombiniert vier etablierte Forschungsprinzipien zu einem schnellen, deterministischen Pure-C#-Verfahren (`< 100 ms` pro Variante, 0 native Abhängigkeiten):

1. **Branched Optimal Transport (BOT) / Gilbert–Steiner-Netzwerke** *(Gilbert 1967; Xia 2003; Lippmann, Sanmartín & Hamprecht, NeurIPS 2022)*:
   - Minimiert auf einem expliziten Baum $T = (V, E)$ das subadditive Funktional $\sum_{e \in E} Q_e^\alpha L(e)$ mit einem einzelnen physikalischen Bündelungsexponenten $\alpha \in (0, 1)$.
   - Erzeugt automatisch Grad-3-Y-Gabelungen mit optimalen Öffnungswinkeln $\cos(\theta_1+\theta_2) = (Q_0^{2\alpha} - Q_1^{2\alpha} - Q_2^{2\alpha}) / (2 Q_1^\alpha Q_2^\alpha)$.
2. **Global Constructive Optimization (GCO) & Iso-Pressure-Bäume** *(Schreiner & Buxbaum 1993; Georg, Preusser & Hahn 2010)*:
   - Glatte lokale Topologie-Anpassungen (NNI — *Nearest-Neighbor Interchange* / Edge-Collapse bei Kantenlänge $\to 0$) verhindern lokale Topologie-Fallen ohne Zufalls-Sprünge.
   - Bewusste Erweiterung um einen **Druckgleichheits-Term** ($\beta \cdot \operatorname{Var}(\Delta p_{1\text{D}, i})$), sodass kurze Pfade bei Bedarf gezielt länger geführt werden dürfen.
3. **Turbulentes Murray’s Law & Idelchik-Hydraulik** *(Uylings 1977: $n \approx 7/3 \approx 2.33$; Idelchik Kap. 6 & 7)*:
   - Exakter Volumenstrom $Q_e$ je Baumkante statt Gauß-Näherung $k_{\text{eff}}$; Abzweig- und Krümmerverluste fließen direkt in die 1D-Widerstands- und Balance-Bewertung ein.
4. **Geodätische 3D-Hindernisnavigation + Position-Based Curvature Projection (PBD)**:
   - Trennt **Baum-Topologie** von **biegsamer 3D-Kurvengeometrie im kontinuierlichen SDF-Raum**: Globale Umfahrung komplexer Hindernisse (Kabelbäume, Bolzen, konkave Gehäuse, später beliebige CAD-Keep-Outs) per schnellem 3D-SDF-Grid-Dijkstra/Fast-Marching; anschließend gitterfreie kontinuierliche Entspannung mit harten Projektionen für SDF-Abstand, Kante-gegen-Kante-Abstand und Mindestbiegeradius $R_c \ge \lambda \cdot D$.

---

## 3. Technische Spezifikation der 5-Phasen-`v4`-Engine

### Datenstruktur (Pure C#, `System.Numerics`, direkt kompatibel zu `AP 4.4`)

```text
VascularTree (expliziter Graph statt 12 loser Polylinien)
├── Nodes : IReadOnlyList<VascularTreeNode>
│     Id, Kind (InletRoot | Bifurcation | OutletTerminal), Position, Tangent,
│     ParentEdgeId, ChildEdgeIds (i. d. R. 2 bei Bifurcation)
├── Edges : IReadOnlyList<VascularTreeEdge>
│     Id, ParentNodeId, ChildNodeId, FlowWeight Q_e, MurrayRadiusMm r_e,
│     Points (glatte 3D-Kurve, 8..20 Knoten je nach Länge), Radii,
│     IsBridgeEdge (false; höchstens 1 explizite Ausgleichsbrücke)
└── OutletRoutes : IReadOnlyList<VascularOutletRoute>
      Aus dem Baum abgeleitete Einlass→Auslass-Ketten für 1D-Hydraulik,
      Diagnostik, Packaging-Audit & Abwärtskompatibilität zu Solid/Rippen
```

- **Entscheidender Voxel-Vorteil:** `StatorCoolingRingTopologyBuilder` sweept **jede Baumkante $e \in E$ genau EINMAL** mit ihrem exakten Radiusverlauf (glatt verrundeter Übergang an Bifurkationsknoten) — **keine 12-fach überlagerten Doppelrohre**, sauberere Zwickel, geringere Voxel-Artefakte!

### Phase 1 — Analytisches 3D-SDF-Feld & Globale Hindernis-Geodäten
1. **Saubere SDF-Schnittstelle (vorbereitet für `AP 4.4`):**
   - `EvaluateKeepInClearance(pos, dirRadius)` (Ring: Rotor `R >= 46`, Außenwand `R <= 94`, Deckel `Z <= 54`, Flansch `Z >= 6` mit lokalem Düsen-Eintauchtrichter).
   - `EvaluateObstacleClearance(pos, dirRadius)` (4× M5-Werkzeugschächte `R = 6.2`, 4× HV-/Sensor-Kapseln, SW-24-Sechskant-Ebene `X >= -93.5`).
2. **Schnelles 3D-Geodäten-Grid (`~3.5–4.0 mm` Voxel):**
   - Einmalige Auswertung des freien Bauraums im Bounding-Box-Volumen (`~15.000` aktive Zellen im Stator-Ring → `< 10 ms` in reinem C#!).
   - Zellen mit $\phi_{\text{clear}}(\mathbf{x}) < r_{\text{req}} + t_{\text{wall}} + m_{\text{safe}}$ sind gesperrt; Zellen in der Nähe von Hindernissen erhalten ein weiches **Korridor-Clearance-Potential**, sodass sich Routen automatisch elegant durch die Mitte freier Kanäle schlängeln.
   - Allgemeine 3D-Anisotropie über `ObstacleOverpassBias` (gewichtet `+Z`-Schritte vs. laterale XY-Schritte sanft im Hindernis-Nahfeld — **100 % ohne HV-Kabel-Sonderabfragen!**).
   - Virtuelle Wasserscheiden-Barriere bei $\theta_{\text{split}} = 75^\circ \cdot \text{UpperLowerSplitBias}$ trennt den `+Y`- und `-Y`-Halbraum sauber und stetig.
3. **Ausgabe:** Global kollisionsfreie, hindernisumschlängelnde geodätische Routen und Distanzen für alle 12 Auslässe (inkl. geplanter 90°-Landezone oberhalb jeder vertikalen Auslassdüse bei `Z_knee + R_land` statt abruptem Fall auf `Z = 9.8 mm`).

### Phase 2 — Expliziter BOT/GCO-Gefäßbaum & Bewusste Druckgleichheits-Verlängerung
1. **Deterministische Initial-Baumtopologie:**
   - Auf beiden Seiten der Wasserscheide werden die Auslässe geordnet (entlang geodätischem Fortschritt / Winkel bzw. agglomerativem Geodäten-Clustering — allgemein für beliebige 3D-Port-Anordnungen!) zu einem binären Baum $T_0$ verbunden.
   - Jeder interne Knoten $v$ startet auf dem geodätisch zulässigen Schwerpunkt zwischen Eltern- und Kindknoten; jede Kante kennt sofort ihren exakten Volumenstrom $Q_e = \sum_{l \in \text{subtree}(e)} w_l$ und ihren exakten **Murray-Radius**:
     $$r_e = r_{\text{out}} \cdot \text{BaseChannelScale} \cdot Q_e^{1 / n_{\text{murray}}}$$
2. **Kombinierte Optimierung der Bifurkationsknoten (BOT + Druckgleichheit):**
   - Minimierung von:
     $$J(T) = \sum_{e \in E} Q_e^{\alpha} \cdot L_{\text{geo}}(e) \;+\; \beta \cdot \sum_{i=1}^{N} \left(\frac{\Delta p_{1\text{D}, i}(T) - \overline{\Delta p}_{1\text{D}}}{\overline{\Delta p}_{1\text{D}}}\right)^2$$
     gesteuert über `BranchingAlpha` ($\alpha$) und `PathBalanceWeight` ($\beta$).
   - **Konsequenz von $\alpha$:** Kleines $\alpha$ zieht Verzweigungsknoten weit Richtung Auslässe (starke Hauptarterien); großes $\alpha \to 0.95$ zieht Verzweigungsknoten Richtung Einlass (kurze, direkte Einzeläste).
   - **Konsequenz von $\beta$ (deine Anforderung aus Punkt 7!):** Bei $\beta = 0$ gehen alle Pfade den kürzesten BOT-Weg. Bei $\beta > 0$ wandern die Verzweigungsknoten **nur der zu kurzen Äste** bewusst weiter nach vorne/außen (um Widerstandslänge zu gewinnen), während die langen Pfade maximal kurz bleiben!
   - **Engpass-Bewusstsein:** Da $r_e$ exakt bekannt ist, drückt die SDF-Projektion einen zu dicken Stammknoten automatisch *vor* ein enges Hindernis (z. B. HV-Kabel) oder in eine freie Höhe/Seite — der Baum spaltet sich bei Bedarf *vor* der Engstelle in zwei schlankere Äste auf.
3. **Glatte GCO-/NNI-Topologie-Verfeinerung:**
   - Wenn eine interne Kante zwischen zwei Bifurkationsknoten unter $L_{\text{collapse}}$ (z. B. `4.0 mm`) schrumpft, prüft der Solver deterministisch die beiden lokalen *Nearest-Neighbor-Interchange*-Varianten (NNI) und übernimmt nur bei echter Verbesserung von $J(T)$ (mit kleiner Hysterese gegen Flattern → glatt für `BoTorch`!).

### Phase 3 — Gitterfreie 3D-Kanten-Relaxation mit harten PBD-Projektionen
Jede Baumkante $e = (u \to v)$ wird entlang ihrer kollisionsfreien Geodäte als glatte Polylinie/Spline initialisiert (mit weichem `90°`-Bogen-Einlauf oberhalb jeder Auslassdüse) und in einem deterministischen **Position-Based-Dynamics (PBD)**-Zyklus (ca. `60–90` Durchläufe) **im kontinuierlichen 3D-Raum** ohne Rasterstufen geglättet:

1. **Optionale gezielte Bogen-Verlängerung (nur bei $\beta > 0$):**
   Falls ein Endast nach Phase 2 trotz Druckgleichheits-Verschiebung und Querschnittsgrenze (`MinBranchRadiusMm`) noch signifikant unter $\overline{\Delta p}_{1\text{D}}$ liegt, erhält genau diese Endkante einen sanften, im freien Bauraum gerichteten lateralen/3D-Auslenkungsimpuls proportional zum Widerstandsdefizit — hart begrenzt durch alle nachfolgenden Projektionen (nie Knicke, nie Kollisionen!).
2. **Gabelungswinkel- & $G^1$-Tangenten-Projektion:**
   - Am Einlass: tangential entlang `+X`; am Auslass: vertikal `-Z`.
   - An jeder Bifurkation $v$: beide Kindkanten starten tangential aus der Eltern-Tangente mit kontrolliertem Aufspreizwinkel im Korridor `[\theta_min, \theta_max]` (z. B. **Gesamtöffnungswinkel `25°–65°`**, gesteuert über `TargetBranchAngleDeg`) → **keine 90°-T-Abzweige und keine papierdünnen Messerkanten!**
3. **Harte Krümmungsradius-Projektion ($R_c \ge \lambda_{\text{bend}} \cdot D_e$):**
   - Für jedes innere Knotentriplett $(\mathbf{p}_{k-1}, \mathbf{p}_k, \mathbf{p}_{k+1})$ jeder Kante wird der lokale Umkreisradius $R_c$ direkt berechnet.
   - Unterschreitet $R_c < R_{\min} = \text{MinBendRadiusRatio} \cdot (2\,r_e)$ (z. B. $1.5\,D_e$), werden die drei Knoten direkt geometrisch auf den Kreis mit Radius $R_{\min}$ entspannt!
   - **Garantie:** 120°-Haarnadelknicke und selbstfaltende Kammern sind mathematisch ausgeschlossen.
4. **Harte 3D-SDF-Projektion (mit richtungsabhängigem 45°-Teardrop + Wandstärke):**
   - Projiziert jeden Kantenpunkt exakt außerhalb aller Keep-Outs und innerhalb aller Keep-Ins (inkl. M5-Bolzen, HV-/Sensor-Kapseln, SW-24-Sechskant, Rotor, Gehäuse, Deckel, Flansch).
5. **Harte Kante-gegen-Kante- & Selbstkollisions-Projektion:**
   - Alle nicht-verwandten Kantenpaare sowie nicht-benachbarte Segmente derselben Kante ($|k - m| \ge 3$) werden hart auf Mindestabstand $d \ge r_i + r_j + t_i + t_j + m_{\text{gap}}$ getrennt (Ausblendung ausschließlich im definierten Y-Zwickel zweier Geschwisterkanten mit dem selben Elternknoten!).
   - **Garantie:** 0 Selbstkreuzungen, 0 X-Kreuzungen, 0 ungewollte Re-Merges, 0 Giftspalten.
6. **Sanfte Kanten-Straffung + Bogenlängen-Ausgleich:**
   - Hält alle nicht-verlängerten Kanten straff und kurz (direktes elastisches Straffen auf Kantenebene — da jede Baumkante nur ~8–16 Knoten hat statt 48, konvergiert die globale Straffung um den Faktor **10–20× schneller** als in `v3`!).
7. **Optionale explizite Fernseiten-Brücke (`FarSideLoopStrength` $\in [0, 1]$):**
   - Bei `0.0`: 100 % reiner, schleifenfreier Gefäßbaum.
   - Bei `> 0.0`: Genau **eine** explizite, SDF- und krümmungsgeprüfte Ausgleichskante verbindet die beiden äußersten Grenz-Äste jenseits der Wasserscheide mit stetig skaliertem Durchmesser — niemals durch unkontrolliertes Zusammensnappen gegenläufiger Pfade.

### Phase 4 — Exakte Baum-1D-Hydraulik & Vorbalancierung
- Nutzt direkt die echte Baum-Topologie $T = (V, E)$ mit realem $Q_e$, realem Krümmer-Verlustbeiwert aus lokalem $R_c / D$ (Idelchik Kap. 6) und realem Abzweig-Verlustbeiwert aus lokalem Y-Winkel $\theta$ (Idelchik Kap. 7)!
- Steuert die freie Endast-Drosselung wie bewährt über `HydraulicBalanceGain` $\in [0, 1]$ und `ResidualThroatTilt` $\in [-0.30, +0.30]$ (weich eingeblendet nur auf freien Endkanten ohne Stamm-Verfälschung, `r >= MinBranchRadiusMm`).

### Phase 5 — Voxelisierung, Mechanik-Solid & Erweiterte Qualitäts-Metriken
- **Voxelisierung:** Einmal-pro-Kante-Sweep (`MIN_ROTATION` + 45°-Teardrop) + glatte Knoten-Übergänge + leichtes `voxSmoothen(0.35f)`.
- **Mechanik-Solid:** 100 % unveränderte Übernahme von Bodenflansch, O-Ring-Nuten, Passstiften, M5-DIN-912-Domen, G3/8"-Boss + SW-24-Sechskant-Freischnitt, Inter-Tube- & Support-Rippenmatrix und `*_context_environment.stl`.
- **Neue analytische Qualitäts-Metriken (im `PackagingReport` & Unit-Tests):**
  - `SelfIntersectionCount` (hart `== 0`)
  - `UnintendedCrossOrMergeCount` (hart `== 0`)
  - `MinBendRadiusRatio` ($\min R_c / D$ über alle Kanten)
  - `MinBifurcationAngleDeg` / `MaxBifurcationAngleDeg` (Verifikation des Y-Winkel-Korridors)
  - plus alle 9 bestehenden Packaging-, Clearance-, Volumen- und `SliverGapCount`-Metriken!

---

## 4. Bereinigtes, physikalisch orthogonales `v4`-DNA-Parameterset (14D)

| # | Parametername | Base | Min | Max | Physikalische Wirkung (ersetzt `v3`-FDEB-Knöpfe sauber) |
|---:|---|---:|---:|---:|---|
| 1 | `BranchingAlpha` | `0.68` | `0.22` | `0.95` | **BOT-Exponent $\alpha$:** `0.95` = kurze direkte Einzeläste; `0.68` = Murray-/Uylings-Optimalbaum; `0.22` = stark gebündelte Hauptarterien (ersetzt 3 konkurrierende `v3`-Knöpfe!) |
| 2 | `TargetBranchAngleDeg` | `38.0` | `24.0` | `62.0` | Ziel-Öffnungswinkel der Y-Gabelungen (schlanke verlustarme Y-Abzweige vs. kompakte frühe Trennung; verhindert 90°-Artefakte & Messerkanten) |
| 3 | `MinBendRadiusRatio` | `1.65` | `1.20` | `2.60` | Physikalisches Krümmungslimit $R_c / D$ (ersetzt abstraktes `PathStiffness`; schließt 120°-Knicke hart aus) |
| 4 | `PathBalanceWeight` | `0.25` | `0.00` | `1.00` | **NEU (Punkt 7!):** `0` = rein kürzeste BOT-Wege; `> 0` = gezielte Wegverlängerung kurzer Äste für gleichmäßigen Druckverlust |
| 5 | `MurrayExponent` | `2.35` | `2.00` | `3.20` | Murray’s-Law-Exponent $n$ auf exaktem $Q_e$ (`2.33` = turbulentes Uylings-Optimum, `3.0` = laminar) |
| 6 | `BaseChannelScale` | `1.00` | `0.85` | `1.22` | Globales Querschnittsniveau aller gewachsenen Kanten |
| 7 | `UpperLowerSplitBias` | `+0.28` | `-0.75` | `+0.75` | Freie Wasserscheide `+Y` (HV-Kabel) vs. `-Y` (frei), z. B. 6/6, 5/7, 4/8 |
| 8 | `FarSideLoopStrength` | `0.00` | `0.00` | `1.00` | `0.0` = reiner Gefäßbaum; `> 0` = eine einzelne, explizite, krümmungsbegrenzte Fernseiten-Ausgleichsbrücke |
| 9 | `ElevationBaseMm` | `23.5` | `16.0` | `32.0` | Nominales Höhenniveau $Z$ des Gefäßbaums im 3D-Bauraum |
| 10 | `ElevationTiltMm` | `+4.0` | `-6.0` | `+14.0` | Längsneigung des Höhenpotentials vom Einlass zur Fernseite |
| 11 | `ObstacleOverpassBias` | `+0.60` | `-0.40` | `+1.00` | Allgemeine 3D-Ausweichpräferenz im Geodäten-Feld (`+` = eher vertikal drüber; `0/-` = eher lateral herum) |
| 12 | `CrossSectionAspect` | `0.88` | `0.65` | `1.45` | Seitenverhältnis ($b/a$) des frame-invarianten 45°-DfAM-Teardrops |
| 13 | `HydraulicBalanceGain` | `0.55` | `0.00` | `1.00` | Stärke der 1D-Querschnitts-Vorbalancierung auf den freien Endästen |
| 14 | `ResidualThroatTilt` | `0.00` | `-0.30` | `+0.30` | Freie 3D-Korrektur-Neigung gegen 1D-vs-3D-Turbulenzabweichungen |

*(Hinweis: Im Pre-Decision Review prüfen wir zusätzlich, ob `FarSideLoopStrength` als 14. Parameter bleibt oder ob wir durch Weglassen / Fixieren auf `0` auf noch knackigere **13D** gehen — für `BoTorch qNEHVI` mit 96 Evals ist 13D–14D optimal.)*

---

## 5. Arbeitspakete & Objektives A/B-Entscheidungstor (`AP 4.3a`)

- [ ] **AP 4.3a.0 — Pre-Decision Review (`Supervisor ⇄ reviewer` `gemini-4-hydrogen-xhigh`):**
  - Exakte Datenstrukturen (`VascularTree`, `ISdfKeepIn`/`ISdfObstacle`-Vorbereitung für `AP 4.4`), Grid-Auflösung (`3.5` vs. `4.0 mm`), PBD-Projektionsreihenfolge, GCO/NNI-Hysterese (GP-Stetigkeit für `BoTorch`!), `PathBalanceWeight`-Formel und finales 13D/14D-DNA-Set abstimmen.
- [ ] **AP 4.3a.1 — Pure-C# `v4`-Engine & Harte Invarianten-Tests (`hydrogen-worker`, `high`):**
  - Implementierung der 5-Phasen-`v4`-Engine (Geodäten-Grid, BOT/GCO-Baum, PBD-Krümmungs-/SDF-/Abstands-Projektion, Baum-1D-Hydraulik, erweiterter `PackagingReport`) — sauber getrennt von PicoGK, deterministisch, `< 100 ms`.
  - Strenge Unit-Tests in `tests/FluidForge.Tests/StatorCoolingRingTests.cs`:
    - `SelfIntersectionCount == 0`, `UnintendedCrossOrMergeCount == 0`, `SliverGapCount == 0` über alle Showcase-Morphologien & 200-Sample-Box!
    - `MinBendRadiusRatio >= 1.15` (keine 120°-Knicke!), Y-Abzweigwinkel im Zielfenster (keine 90°-HighBundling-Artefakte!),
    - Nachweis von `PathBalanceWeight`: bei `0.0` kürzeste Wege, bei `0.8` gezielte Verlängerung kurzer Äste mit sinkender 1D-Druckverlust-Varianz,
    - Nachweis der Stetigkeit (keine `DEAD`-Parameter, keine chaotischen Sprünge bei `Mid ±5 %`),
    - `FEASIBILITY-200 >= 85 %` bei `0` Hindernis-/Bolzen-/Hex-Kollisionen.
- [ ] **AP 4.3a.2 — PicoGK-Einmal-pro-Kante-Sweep & Solid-Integration (`hydrogen-worker`, `high`):**
  - `StatorCoolingRingTopologyBuilder` auf sauberes Einmal-pro-Kante-Sweeping inkl. Knoten-Übergangsblends umstellen; `StatorCoolingRingSolidBuilder` (Rippenmatrix, `voxOverOffset`, scharfer SW-24-Sechskant) anbinden und verifizieren (`Open=0, NonMan=0`, `hex_fluid_encroachment_tris = 0`, `HV = 0`, `Bolt = 0`).
- [ ] **AP 4.3a.3 — Multi-Morphologie-Preview & Objektiver A/B-Vergleich (`v4` vs. `v3`):**
  - Generierung von 4–5 kontrastierenden `v4`-Morphologien (`Base`, `HighBundling_Asym4_8`, `DirectShortPaths_AlphaHigh`, `BalancedLongShort_BetaHigh`, optional `ClosedLoop`) unter `results/StatorCoolingRing_v4_preview/` inkl. `overview_4_morphologies.png`, Schnittplots und vollständigem STL-Audit.
  - **Visuelle Kontrolle mit dem Nutzer:** Direkter Side-by-Side-Vergleich `v4` vs. `v3` gegen alle 8 Punkte aus [`Anmerkungen.txt`](Anmerkungen.txt).
  - **Kurzer CFD- & Meshing-A/B-Check auf `BIC_09`/`BIC_12`:** Base-Variante von `v4` vs. `v3` mit identischem OpenFOAM-Setup (`snappyHexMesh` Qualität/Layer-Abdeckung + `simpleFoam` `PressureDrop` & `FlowImbalance`).
- [ ] **AP 4.3a.4 — Gewinner-Entscheidung & Post-Implementation Review (`APPROVE`):**
  - Formale Festlegung der **finalen Showcase-Version** (Ziel: `v4` BOT/GCO-Baum) für den großen `BIC_12`-Pareto-Lauf (`AP 4.3b`) **und** als verbindliche Code-Basis für die Generalisierung ins Hauptprogramm ([`AP 4.4 / phase4_5_vascular_routing_core_engine.md`](phase4_5_vascular_routing_core_engine.md)).

---

## 6. Abnahmekriterien für `AP 4.3a`

1. **Alle 8 Kritikpunkte aus [`Anmerkungen.txt`](Anmerkungen.txt) nachweislich behoben:** 0 ungewollte Re-Merges/X-Kreuzungen, 0 Selbstkreuzungen, keine 90°-Abzweig-Artefakte bei starker Bündelung, keine 120°-Mini-Radius-Knicke, saubere Y-Gabelungswinkel, echte kürzeste Wege bei `PathBalanceWeight = 0` sowie kontrollierte Wegverlängerung kurzer Äste bei `PathBalanceWeight > 0`, und elegantes 3D-Schlängeln um HV-Kabel & M5-Bolzen.
2. **Starke Optimierbarkeit & Geometrie-Qualität:** `0W/0E`, alle Unit-Tests grün, `--smoke-test` (`6/6` wasserdicht, alle DNA-Parameter `EFFECTIVE`), `FEASIBILITY-200 >= 85 %`, `SliverGapCount = 0`, `HV/Bolt/Hex-Kollisionen = 0`.
3. **Nachweisbarer Fortschritt im A/B-Check (`v4` vs. `v3`):** Visuell klar organischer/plausibler, sauberes `snappyHexMesh` (mindestens ebenbürtige Non-Orthogonality & Layer-Abdeckung) sowie überzeugende CFD-Basiswerte (`PressureDrop`, `FlowImbalance`).
4. **Generalisierungs-Reife für `AP 4.4`:** Keine Stator-Spezialhacks im Routing-Kern — die gesamte Logik arbeitet rein auf Ports + `ISdfKeepIn` + `ISdfObstacle`, sodass sie in `AP 4.4` 1:1 als `FluidForge.Kernels.Vascular` & `_TemplateVascular3D` ins Hauptprogramm übernommen werden kann.
