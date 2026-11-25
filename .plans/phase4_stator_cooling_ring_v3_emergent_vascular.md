# Phase 4 Showcase — `StatorCoolingRing v3`: Emergentes 12-Pfad-Gefäßnetz (Force-Directed Edge Bundling, Murray’s Law & Organische Verbindungsmatrix)

> **Status:** ✔ Abgeschlossen (25.11.2026 — vollständig umgesetzt via `/delegate`-Pipeline mit gleichrangigem Peer-Reviewer `Gemini 4 Hydrogen (XHigh)` & Workern `Gemini 4 Hydrogen (High)` bis finales beidseitiges **`APPROVE`**; `dotnet build -warnaserror`: **0W/0E** lokal & `BIC_09`; **1077/1077** Release-Tests + **17/17** `StatorCoolingRingTests` grün; Linux `--smoke-test` auf `BIC_09`: **`PASS 6/6 Probes, 14/14 EFFECTIVE`**; `BIC_09` Base-`snappyHexMesh Level (2,3)`: **`69 s`, `1,39 Mio.` Zellen, `77,9 %` Layer-Abdeckung**; strikter Core-Feature-Freeze eingehalten!)  
> **Vorgänger:** [`.old/phase4_stator_cooling_ring_v2.md`](.old/phase4_stator_cooling_ring_v2.md) (mechanische Hülle, DIN-912-Dome, G3/8"-Boss, O-Ring-Nuten & `*_context_environment.stl` werden vollständig übernommen; die ringgebundene Fluid-Topologie wird durch das freie 12-Pfad-Wachstumsverfahren abgelöst!)  
> **Architektur-Leitplanke:** **Strikter Core-Feature-Freeze!** 100 % Umsetzung innerhalb `src/projects/StatorCoolingRing/` und `tests/FluidForge.Tests/StatorCoolingRingTests.cs`. Keine Änderungen in `src/FluidForge/Core/` oder `src/FluidForge/Solvers/`.

---

## 1. Vision & Paradigmenwechsel: Warum `v3`?

In `StatorCoolingRing v2` war die Grundform trotz 13 Parametern und 1→2→6→12-Gabelungen im Code weiterhin **starr an einen geschlossenen Ring und 6 vordefinierte Auslass-Paare gebunden**. Der Optimierer konnte diese vorgegebene Form lediglich biegen, neigen und skalieren — aber keine wirklich neuen, organischen oder abstrakten Topologien entdecken.

**`StatorCoolingRing v3` verwirklicht das eigentliche Kernprinzip von `FluidForge`:**
- **Keine vorgegebene Kanal-Topologie:** Kein fester Ring, keine fest verdrahteten 6 Paare. Stattdessen errechnet ein deterministischer **3D-Vektorfeld- & Force-Directed-Edge-Bundling-Algorithmus** im C#-Generator **12 echte, freie Vektorpfade** vom festen Einlass zu den 12 festen Auslässen.
- **Emergente Verästelung („Snapping“) & automatische Volumenstrom-Skalierung (Murray’s Law):** Laufen zwei oder mehr Pfade nahe beieinander in kompatibler Richtung, verschmelzen sie zu einem gemeinsamen Kanal, dessen Querschnitt sich automatisch über die lokale Pfad-Multiplizität $k_{\text{eff}} \in [1, 12]$ und **Murray’s Law** ($r \propto k_{\text{eff}}^{1/n}$) an den summierten Volumenstrom anpasst — bis sie sich für ihre jeweiligen Ziel-Auslässe wieder organisch auftrennen.
- **Aktives Ausweichen um physikalische Grenzen & Störkonturen:** Alle 12 Pfade starten simpel und „lernen“ in einer schnellen, deterministischen 3D-SDF-Relaxation (`< 50 ms` auf der CPU), wo Rotor-Innenbohrung, Gehäusewand, Deckel, Bodenflansch, 4 durchgehende M5-Werkzeugschächte und der asymmetrische EV-HV-Kabelbaum liegen.
- **Völlige Freiheit für `BoTorch` (`qNEHVI`, 2 Pareto-Ziele: `PressureDrop` & `FlowImbalance`):** Der Optimierer steuert eine kompakte, stetige **13D-„Wachstums-DNA“** — darunter Symmetrie vs. Asymmetrie (freie Wasserscheide, z. B. 6/6 vs. 4/8), Bündelungsstärke, Murray-Exponent, offener Baum vs. Fernseiten-Schleife, 3D-Höhenpotential, Ausweichpräferenz am HV-Kabel sowie eine optional zuschaltbare 1D-Vorbalancierung (`HydraulicBalanceGain` $\in [0, 1]$), die niemals echte 3D-Optima blockiert.
- **Einbaufertige Mechanik-Hülle mit organischer Rippen-Verbindungsmatrix (`PicoGK` `voxFillet` / `voxOverOffset`):** Die geprüften mechanischen Interfaces aus `v2` bleiben erhalten, werden aber um eine **automatische, geglättete Rippen-/Stegmatrix zwischen benachbarten Rohren** sowie topologie-adaptive Lastpfad-Rippen erweitert: Die individuellen Gefäßrohre bleiben klar als organische Strukturen sichtbar, hängen aber nirgends lose in der Luft und bilden ein vibrationsfestes, supportfrei 3D-druckbares Gesamtbauteil.

---

## 2. Feste Industrielle Interfaces, Bauraum & Störkonturen (Invarianten)

Alle mechanischen Einbau-Schnittstellen bleiben über sämtliche Optimierungskandidaten strikt konstant und werden zugleich als analytische **3D-Signed-Distance-Funktionen (SDF)** für den Pfad-Wachstumsalgorithmus bereitgestellt:

| Element | Exakte Geometrie & Lage | Rolle im System |
| :--- | :--- | :--- |
| **Koordinatensystem** | `Z = 0` = Stator-Montagefläche, `(0, 0, Z)` = Rotorachse | Globale Referenz |
| **Fester Einlass (`inlet`)** | Zentrum `(-108.0, 0.0, 20.0)` mm, Ø `13.0 mm` (`R = 6.5 mm`), Normale `-X`; gerader Einlass-Stutzen bis Split-Anker `(-90.0, 0.0, 20.0)` | Fester CFD-Port + `FixedFluidSeedPoint = (-100, 0, 20)` |
| **12 feste Auslässe (`outlet_01..12`)** | `R_pitch = 65.0 mm`, `Z = 0`, Ø `4.0 mm` (`R = 2.0 mm`), Normale `-Z`, Winkel $\theta_k = 15^\circ + 30^\circ k$ ($k = 0..11$); gerader vertikaler Einlauf ab `OutletKneeZ = 5.0 mm` | 12 feste CFD-Auslass-Patches |
| **Keep-In Ring-Bauraum** | `MinInnerRadiusMm = 46.0 mm` (Rotor/Wickelköpfe), `MaxOuterRadiusMm = 94.0 mm` (Gehäuse), `MaxTotalHeightMm = 54.0 mm` (Deckel), Bodenflansch-Oberseite `Z = 6.0 mm` | Harte Bauraum-Grenzen (SDF + Pre-CFD-Constraint) |
| **4× M5 DIN-912 Befestigungsdome** | `R_bolt = 84.0 mm` bei `60°, 120°, 240°, 300°`; Durchgang Ø `5.5 mm` (`Z = 0..7.0`), Zylindersenkung Ø `10.0 mm` (`Z = 7.0..60.0`), Dom-Kragen `R = 8.2, H = 13.5 mm`; **Werkzeug-Keepout-Säule** `R = 6.2 mm` über volle Höhe `Z = 0..54.0 mm` | Feste Verschraubung + volle Montierbarkeit von `+Z` |
| **G3/8" Einschraubgewinde-Boss** | DIN-3852-Planfläche (`X = -108..-106.5, R = 11.8`), freiliegender **SW-24-Sechskant** (`X = -106.5..-96.5`), Verstärkungshals (`-96.5..-85.5, R = 11.8`), Kernloch Ø `15.0 × 12 mm` + 45°-Fase + Konus; bündige `Z = 0`-Stützkonsole (`X = -97..-78`, hält `180°`-Passstift frei) | Fester Kühlmittelanschluss mit Zugentlastung |
| **Bodenflansch, O-Ringe & Passstifte** | `Z = 0..6.0 mm`, `R = 53.0..76.8 mm`; 2 konzentrische O-Ring-Nuten (`R = 56.8` & `72.4 mm`, `3.2 × 2.0 mm`); 2× Ø `4.0 mm` Passstiftbohrungen (`R = 65 mm`, `0°` & `180°`, Tiefe `6.0 mm`) | Dichtung & reproduzierbare Positionierung |
| **Asymmetrische HV-Störkontur (`theta = 90°`)** | Entkoppelt von Flansch & O-Ringen (steigt aus innerer Wickelkopfkrone `R = 43 mm` auf): `HvPhaseRiser` `(0, 43, 0..13.8), R=4.2`; `HvPhaseConduit` `(0, 43..102, 13.8), R=4.2`; `SensorRiser` `(6.6, 42.5, 0..13.8), R=2.4`; `SensorConduit` `(6.6, 42.5..102, 13.8), R=2.4` | Realistische EV-3-Phasen-HV- + Sensor-Hürde |
| **Transparenter Kontext (`*_context_environment.stl`)** | Rotor-Innenhülse (`R = 44..45.8`), Außenring (`R = 94.2..96.2`), Deckelring (`Z = 54`), 4× M5-Schrauben + Werkzeugsäulen, alle 4 HV-/Sensor-Kapseln | Transparente 3D-Visualisierung in ParaView / Blender |

---

## 3. Kern-Algorithmus: 5-Phasen Emergent Vascular Routing Engine (`StatorCoolingRingVascularEngine.cs`)

Die komplette Pfad-, Bündelungs-, Murray- und Vorbalancierungs-Logik wird in **reinem C# (`System.Numerics`, ohne native PicoGK-Abhängigkeit)** implementiert. Dadurch ist sie:
- **100 % deterministisch** (identische Parameter → bit-identische Pfade, kein Zufallsrauschen),
- **extrem schnell** (`~20–50 ms` pro Geometrie auf einem CPU-Kern),
- **vollständig per Unit-Tests prüfbar** (inkl. Kollisions-, Symmetrie-, Asymmetrie- und Topologie-Diversitäts-Tests ohne PicoGK-DLL!).

### Phase A: Topologiefreie Initialisierung der 12 Vektorpfade (Ohne Ring-Zwang, Sattelpunktfrei)
1. Jeder der 12 Pfade $i \in \{0, \dots, 11\}$ wird als 3D-Polylinie mit $N = 48$ Knoten $\mathbf{p}_{i,0}, \dots, \mathbf{p}_{i,47}$ dargestellt:
   - **Feste Einlass-Randbedingung:** $\mathbf{p}_{i,0} = (-90, 0, 20)$ und ein kurzer gerader Führungsabschnitt $\mathbf{p}_{i,1}, \mathbf{p}_{i,2}$ entlang $+X$ garantieren tangentialen, verlustarmen Auslauf aus dem G3/8"-Einlass-Stutzen.
   - **Feste Auslass-Randbedingung:** $\mathbf{p}_{i,47} = (65\cos\theta_i, 65\sin\theta_i, 5.0)$ und ein senkrechter Führungsabschnitt $\mathbf{p}_{i,46}, \mathbf{p}_{i,45}$ entlang $+Z$ garantieren sauberen vertikalen Übergang in die jeweilige Ø-4-mm-Düse.
2. **Freie Symmetrie-/Asymmetrie-Wasserscheide (`UpperLowerSplitBias` $\in [-1, +1]$):**
   - Ein gerader Strahl vom Einlass `(-90, 0)` zum gegenüberliegenden Auslass schneidet den Rotor `(0, 0)` am Sattelpunkt. Um diesen Sattelpunkt physikalisch sinnvoll und frei steuerbar aufzulösen, bestimmt der DNA-Parameter `UpperLowerSplitBias` eine freie Winkel-Wasserscheide auf der Fernseite:
     $$\theta_{\text{split}} = 75^\circ \cdot \text{UpperLowerSplitBias} \in [-75^\circ, +75^\circ]$$
   - Alle Auslässe mit $\theta_i \in (\theta_{\text{split}}, 180^\circ)$ starten auf der `+Y`-Seite (am HV-Kabel vorbei), alle übrigen auf der `-Y`-Seite (freie Seite):
     - Bei `UpperLowerSplitBias = 0.0` → exakt symmetrisch **6 oben / 6 unten**.
     - Bei `UpperLowerSplitBias = +0.5` → **4 oben / 8 unten** (entlastet die enge HV-Kabelseite massiv!).
     - Bei `UpperLowerSplitBias = -0.5` → **8 oben / 4 unten**.
   - **Wichtig für Stetigkeit in `BoTorch`:** Zusätzlich zur diskreten Start-Zuordnung am Rand der Wasserscheide verschiebt `UpperLowerSplitBias` auch den kontinuierlichen **Anfangs-Impuls- & Potential-Bias** aller 12 Pfade am Einlass-Y-Knoten (kontinuierlicher Einfluss auf JEDE Variante, selbst wenn sich die ganzzahlige 6/6-Zuordnung bei $\pm 5\%$ Perturbation im `--smoke-test` nicht ändert → **garantiert kein `DEAD`-Parameter bei `Mid`!**).
3. **Startpfad-Geometrie (kein vorgezeichneter Ring!):**
   - Jeder Pfad $i$ startet als direkte, minimal lange Geodäte um den Rotor-Sperrzylinder herum (geradlinige Tangente vom Einlass an den inneren Sicherheitskreis $R_{\text{seed}} = 54\text{ mm}$, kurzer Tangentialbogen, direkte Gerade zum Auslass $\mathbf{p}_{i,47}$) mit sanftem linearem Höhenübergang von $Z = 20\text{ mm}$ auf `ElevationTargetMm` und hinunter auf $Z = 5\text{ mm}$.
   - Zu Beginn (`Iter = 0`) liegen alle 12 Pfade also noch weitgehend als **direkte Einzel-Sehnen** — noch völlig ungebündelt und noch ohne das HV-Kabel oder die M5-Bolzen zu kennen!

### Phase B: Force-Directed Relaxation — SDF-Hindernis-Lernen & Bistabiles Edge-Bundling
Über $M = 180$ deterministische Relaxations-Iterationen (mit sanft abklingender Schrittweite $\eta_t$ für garantiert schwingungsfreie Konvergenz) wirkt auf jeden freien Innenknoten $\mathbf{p}_{i,s}$ ($s \in \{3, \dots, 44\}$) die resultierende Vektorkraft:

$$\mathbf{F}_{i,s} = \mathbf{F}_{\text{tension}} + \mathbf{F}_{\text{bend}} + \mathbf{F}_{\text{sdf}} + \mathbf{F}_{\text{bundle}} + \mathbf{F}_{\text{repel}} + \mathbf{F}_{\text{loop}} + \mathbf{F}_{\text{elev}}$$

1. **Pfad-Spannung & Krümmungsglättung ($\mathbf{F}_{\text{tension}} + \mathbf{F}_{\text{bend}}$):**
   - Zugkraft (1. Diskrete Ableitung / Laplacian): $\mathbf{F}_{\text{tension}} = w_{\text{tens}} \cdot \bigl(\tfrac{1}{2}(\mathbf{p}_{i,s-1} + \mathbf{p}_{i,s+1}) - \mathbf{p}_{i,s}\bigr)$ — zieht Pfade straff und kurz (minimiert Reibungslänge!).
   - Biegesteifigkeit (Biharmonische Glättung): verhindert scharfe Knicke und sorgt für strömungsgünstige, große Krümmungsradien ($R_c / D$ groß → minimaler Sekundärwirbel-Druckverlust!). Gesteuert durch `PathTension` und `CurvatureSmoothness`.

2. **3D-Bauraum- & Hindernis-Abstoßungsfeld ($\mathbf{F}_{\text{sdf}}$ — „Lernen der Grenzen“):**
   - Für jeden Knoten schätzen wir laufend seinen aktuellen lokalen Gesamtradius $R_{\text{eff}}(i, s) = r_{\text{fluid}}(i, s) + t_{\text{wall}}(i, s) + m_{\text{safe}}$ (mit Sicherheitsmarge $m_{\text{safe}} = 2.4\text{ mm} > 2.0\text{ mm}$ Constraint!).
   - **Analytische SDF-Barrieren mit exaktem Gradienten $\nabla\phi$:**
     - **Rotor-Innenbohrung:** $\phi_{\text{in}} = r_{XY} - (46.0 + R_{\text{eff}})$ → drückt radial nach außen.
     - **Gehäuse-Außenwand:** $\phi_{\text{out}} = (94.0 - R_{\text{eff}}) - r_{XY}$ → drückt radial nach innen.
     - **Deckel:** $\phi_{\text{top}} = (54.0 - R_{\text{top,eff}}) - z$ → drückt nach unten.
     - **Bodenflansch:** $\phi_{\text{bot}} = z - (6.0 + R_{\text{bot,eff}})$ (sanft ausgeblendet über die letzten 7 Knoten beim vertikalen Eintauchen in die Düse!) → hält alle horizontalen Kanäle sauber über der Flanschoberseite.
     - **4× durchgehende M5-Werkzeugschächte (`60°/120°/240°/300°`, `R_bolt = 84`, `R_keepout = 6.2`, `Z = 0..54`):** zylindrische Abstoßung in XY über die gesamte Höhe!
     - **4× asymmetrische HV-/Sensor-Kapseln bei `90°`:** exakte 3D-Punkt-zu-Segment-SDF + Gradient. Um Sattelpunkte direkt frontal vor dem horizontalen Kabel zu vermeiden, addiert der Parameter **`ObstacleOverpassBias`** $\in [-0.3, +1.0]$ im Nahfeld der Kabelkapseln eine glatte vertikale/radiale Ausweichkomponente (positiv → eleganter 3D-Bogen über den Kabelbaum; nahe 0/negativ → kompakteres radiales Einziehen oder Ausweichen zur Gegenseite!).
   - Alle SDF-Kräfte greifen weich-quadratisch ab einer Vorwarnzone (`+3.0 mm`) und hart-projektiv bei Annäherung an `0 mm` → **die Pfade lernen die Grenzen aktiv und fließen glatt drum herum!**

3. **Bistabiles Force-Directed Edge-Bundling ($\mathbf{F}_{\text{bundle}} + \mathbf{F}_{\text{repel}}$ — „Snapping OHNE Giftspalten!“):**
   - **Entscheidendes Konstruktionsdetail (Bogenlängen-unabhängiger Nachbar-Abstand!):**
     Da Pfad 1 (zu `165°`, kurz) viel kürzer ist als Pfad 5 (zu `15°`, lang), dürfen Knoten **nicht** über denselben Index $s$ gekoppelt werden! Stattdessen sucht jeder Knoten $\mathbf{p}_{i,s}$ den **nächstgelegenen Punkt $\mathbf{q}_{ij}$ auf der Polylinie jedes anderen Pfades $j \neq i$** (inkl. Segment-Projektion und Richtungskompatibilität $c_{\text{dir}} = \hat{\mathbf{t}}_{i,s} \cdot \hat{\mathbf{t}}_{j}(\mathbf{q}_{ij})$).
   - **End-Ablöse-Maske $\mu_{\text{split}}(s)$:** Gesteuert über `SplitReleaseBias` $\in [0.15, 0.85]$ — nahe dem Einlass (`u = 0`) ist Bündelung voll aktiv (`1.0`); auf dem letzten Abschnitt Richtung jeweiliger Auslassdüse (`u > 1 - SplitReleaseBias`) blendet $\mu_{\text{split}}(u)$ glatt auf `0.0` ab, sodass die Pfade sich organisch aufspalten und frei zu ihren Düsen schwingen!
   - **Das Zweizonen-Kraftgesetz (bistabile Entscheidung gegen Giftspalten):**
     Sei $d = \|\mathbf{q}_{ij} - \mathbf{p}_{i,s}\|$, $R_{\text{snap}} = \text{BundlingRangeMm} \cdot \mu_{\text{split}}$, und $d_{\text{sep}} = r_i + r_j + 2\cdot t_{\text{wall}} + 1.5\text{ mm}$ der saubere Trennabstand für zwei separate Kanäle:
     - **Zone 1 — Attraktions-/Snap-Zone ($c_{\text{dir}} > 0.25$ und $d < R_{\text{snap}}$):**
       $$\mathbf{F}_{\text{bundle}} = w_{\text{bundle}} \cdot c_{\text{dir}} \cdot \mu_{\text{split}} \cdot \bigl(1 - (d / R_{\text{snap}})^2\bigr) \cdot (\mathbf{q}_{ij} - \mathbf{p}_{i,s})$$
       Zieht kompatible nahe Pfade sanft aber konsequent **auf exakte Deckung ($d \to 0$)** zusammen! In den letzten 30 Iterationen verstärkt eine sanfte „Snap-Lock“-Phase (`d < 2.5 mm -> d -> 0`) das exakte Zusammenfallen gemeinsamer Gefäßachsen, damit keine „Doppelrohr-Falten“ entstehen.
     - **Zone 2 — Anti-Giftspalt-Abstoßung (wenn NICHT gebündelt, also $d \ge R_{\text{snap}}$ oder $c_{\text{dir}} \le 0.25$ oder nahe den Düsen, aber $d < d_{\text{sep}}$):**
       $$\mathbf{F}_{\text{repel}} = -w_{\text{repel}} \cdot \left(1 - \frac{d}{d_{\text{sep}}}\right)^2 \cdot \frac{\mathbf{q}_{ij} - \mathbf{p}_{i,s}}{\max(d, 10^{-4})}$$
       Drückt ungebündelte Kanäle sauber auseinander, sodass zwischen getrennten Rohren immer **volle Wandstärke + sauberer Rippen-Raum** bleibt (kein `0,2 mm` Giftspalt für `snappyHexMesh` und keine hauchdünnen Wände!).
     - *(Ausnahme unmittelbar am Gabelungspunkt $s_{\text{fork}}$, wo zwei gerade getrennte Pfade im spitzen Winkel aus dem gemeinsamen Stamm herauswachsen: dort blendet $\mathbf{F}_{\text{repel}}$ über `6 mm` Bogenlänge sanft ein, sodass eine natürliche, strömungsgünstige Y-Gabelung entsteht!)*

4. **Optionale Fernseiten-Schleife ($\mathbf{F}_{\text{loop}}$ via `FarSideLoopAttraction` $\in [0.0, 1.0]$):**
   - Identifiziert die beiden Grenzpfade unmittelbar links und rechts der Wasserscheide $\theta_{\text{split}}$ (z. B. `outlet_01` bei `15°` und `outlet_12` bei `345°` bei symmetrischem Split).
   - Bei `FarSideLoopAttraction = 0.0` bleiben beide völlig getrennt → **reiner offener Gefäßbaum** (0 % Staupunkt-Kollision!).
   - Bei `FarSideLoopAttraction > 0.0` zieht eine glatte Kraft ihre Schulterknoten (`u ∈ [0.55, 0.80]`) zueinander hin und erzeugt oberhalb einer stetigen Schwelle (bzw. als stetig skalierter Verbindungsbogen mit Durchmesser `0` bzw. glattem Quer-Shunt) eine **organische Druckausgleichs-Brücke** auf der Fernseite!
   - *(Hinweis zur GP-Stetigkeit — Lehre aus dem v2-Review!)*: Damit es **keine harte Sprungstelle** für `BoTorch` gibt, steuert `FarSideLoopAttraction` in `[0.0, 1.0]` kontinuierlich die räumliche Annäherung der beiden Grenzpfade **sowie** bei hinreichender Annäherung (`d < R_loop`) die sanfte Verschmelzung über genau dieselbe weiche $k_{\text{eff}}$-Dichtefunktion — oder als stetige Zugkraft, die beide Äste erst ab starker Anziehung zu einer gemeinsamen Brücke zusammenzieht (kein unstetiges `if`-Cutoff bei `0.20`!).

5. **Vertikales Potentialfeld ($\mathbf{F}_{\text{elev}}$):**
   - Sanfte Rückstellkraft auf eine ortsabhängige Zielhöhe $Z_{\text{target}}(\theta) = \text{ElevationBaseMm} + \text{ElevationTiltMm} \cdot \tfrac{1}{2}(1 + \cos\theta)$ — erlaubt es `BoTorch`, das gesamte Gefäßnetz flach/kompakt oder hoch/weitläufig im 3D-Bauraum aufzuspannen.

6. **Bogenlängen-Re-Sampling & Finale Glättung:**
   - Alle 15 Iterationen werden die Knoten jedes Pfades auf gleichmäßige Bogenlänge neu verteilt (verhindert Knoten-Verdichtung/Ausdünnung).
   - Nach Iteration 180 erfolgt ein finaler Projektions- & Chaikin-/Cubic-Spline-Glättungspass (garantiert $C^2$-glatte, krümmungsstetige Kurven für `ShapeKernel` `Frames` und saubere `snappyHexMesh`-Layer!).

### Phase C: Automatische Volumenstrom-Skalierung nach Murray’s Law (Stetiges $k_{\text{eff}}$)
Nach Abschluss der Pfad-Relaxation berechnen wir an jedem Knoten $\mathbf{p}_{i,s}$ die **kontinuierliche lokale Pfad-Multiplizität** (wie viele der 12 Teilströme $Q_0 = Q_{\text{ges}}/12$ fließen lokal durch denselben Kanal):

$$k_{\text{eff}}(i, s) = \sum_{j=0}^{11} \exp\!\left(-\frac{d_{\text{path}}(\mathbf{p}_{i,s}, \mathcal{P}_j)^2}{2\sigma_{\text{merge}}^2}\right) \in [1.0, 12.0]$$

mit $\sigma_{\text{merge}} = 2.4\text{ mm}$ (und Ausblendung von Pfaden mit stark gegenläufiger Tangente, außer in einer gewünschten Fernseiten-Brücke).

Daraus ergibt sich der **physikalisch skalierte lokale Kanalradius nach Murray’s Law**:

$$r_{\text{murray}}(i, s) = r_{\text{out}} \cdot \text{BaseChannelScale} \cdot \bigl(k_{\text{eff}}(i, s)\bigr)^{1 / n_{\text{murray}}}$$

- **Beispielwerte (Plausibilitäts-Check!):**
  - Für `BaseChannelScale = 1.0`, `r_out = 2.0 mm`, `n_murray = 2.45` (typischer turbulenter Murray-Exponent!):
    - Einzelast ($k = 1$): $r = 2.0\text{ mm}$ ($\varnothing 4.0\text{ mm}$)
    - 2er-Bündel ($k = 2$): $r = 2.65\text{ mm}$ ($\varnothing 5.3\text{ mm}$)
    - 3er-Bündel ($k = 3$): $r = 3.13\text{ mm}$ ($\varnothing 6.3\text{ mm}$)
    - 6er-Hauptarterie ($k = 6$): $r = 4.16\text{ mm}$ ($\varnothing 8.3\text{ mm}$)
    - 12er-Stamm am Einlass ($k = 12$): $r = 5.52\text{ mm}$ ($\varnothing 11.0\text{ mm}$), der über die ersten `15 %` Bogenlänge glatt per `SmoothStep` an den festen Einlass `R_in = 6.5 mm` ($\varnothing 13.0\text{ mm}$) anschließt!
- **Riesenvorteil:** An jeder einzelnen Gabelung — egal ob `12 -> 7 + 5`, `7 -> 4 + 3`, `4 -> 2 + 2` oder `3 -> 1 + 1 + 1` — geht der Querschnitt **vollkommen glatt ($C^\infty$) und physikalisch volumenstromtreu** über! Keine harten Stufen, keine händisch codierten Radien-Formeln!

### Phase D: Physik-treue 1D-Vorbalancierung OHNE Optima-Verfälschung
Um `BoTorch` wertvolle CFD-Iterationen beim Erreichen von `< 2 %` `FlowImbalance` zu sparen, **ohne jemals echte 3D-Optima zu blockieren**:

1. Nach Phase C misst der Generator für jeden der 12 Pfade $i \in \{0..11\}$ exakt aus der fertigen 3D-Geometrie:
   - die Gesamtlänge $L_i = \sum_s \|\Delta\mathbf{p}_{i,s}\|$,
   - das turbulente Darcy–Weisbach-Widerstandsintegral $R_{\text{fric}, i} = \sum_s \frac{\|\Delta\mathbf{p}_{i,s}\|}{r_{\text{murray}}(i, s)^{4.75}}$ (Blasius-Gesetz für glatte turbulente Rohre: $\Delta p \propto L / D^{4.75}$!),
   - den integrierten Krümmungsverlust-Index $R_{\text{bend}, i} = \sum_s \frac{\Delta\alpha_{i,s}^2}{r_{\text{murray}}(i, s)^4}$ (mit lokalem Knickwinkel $\Delta\alpha_{i,s}$).
2. Gesamt-1D-Widerstandsindex $R_i = R_{\text{fric}, i} + 0.35 \cdot R_{\text{bend}, i}$ relativ zum Mittelwert $\bar{R}$ ergibt den 1D-Soll-Drosselfaktor je Auslass-Endast (im freien Abschnitt vor dem festen Ø-4-mm-Auslass):
   $$f_{\text{1D}, i} = \text{Clamp}\!\left(\left(\frac{R_i}{\bar{R}}\right)^{0.21},\; 0.72,\; 1.38\right)$$
3. **Warum das NIEMALS die Physik verfälscht oder echte 3D-Optima abschneidet:**
   - Die Vorbalancierung wird über den freien DNA-Parameter **`HydraulicBalanceGain`** $\in [0.0, 1.0]$ gesteuert:
     - Bei `HydraulicBalanceGain = 0.0` ist die 1D-Korrektur **vollständig aus** (`f = 1.0`).
     - Bei `HydraulicBalanceGain > 0.0` wirkt sie glatt skaliert: $f_{\text{bal}, i} = 1 + \text{HydraulicBalanceGain} \cdot (f_{\text{1D}, i} - 1)$.
   - Zusätzlich gibt es einen **unabhängigen freien 3D-Korrektur-Parameter `ResidualThroatTilt`** $\in [-0.30, +0.30]$ (gewichtet mit $\cos\theta_i$, Einlass-nah vs. Fernseite), sodass `BoTorch` jede Abweichung zwischen 1D-Blasius und echter 3D-OpenFOAM-Turbulenz (z. B. dynamischer Staudruck in Verzweigungen, Sekundärwirbel!) gezielt überkompensieren oder rückgängig machen kann!
   - Der minimale Fluidradius bleibt überall über `MinBranchRadiusMm = 1.5 mm` abgesichert.

### Phase E: Voxelisierung mit Frame-invariantem 45°-DfAM-Teardrop & OpenFOAM-Export
- Jede der 12 Kurven $\mathcal{P}_i$ wird wie in `v2` über den bereits verifizierten **frame-invarianten 45°-DfAM-Teardrop** (`EvaluateTeardropCrossSectionRadius` mit `dirWorld = cos(fPhi)*localX + sin(fPhi)*localY` und `|x| + z = sqrt(a^2+b^2)`) als `BaseCylinder` mit `SurfaceModulation` voxelisiert und per `voxBoolAdd` vereinigt.
- **Performance-Optimierung bei gebündelten Pfaden:** Da im Stammbereich mehrere Pfade identisch aufeinanderliegen, werden alle 12 Sweeps in PicoGK zügig vereinigt (`12` statt `20` Einzel-Sweeps wie in `v2` → sogar **schneller** als `v2`!), erhalten einen sanften internen Knoten-Fillet (`voxSmoothen(0.35f)`), werden kompakt an `Z = 0` und `X = -108` getrimmt und per `MultiPatchFluidExporter.Export` mit festem `FixedFluidSeedPoint = (-100, 0, 20.0)` wasserdicht exportiert.

---

## 4. Das 13D-„Wachstums-DNA“-Parameterset (Optimiert für `BoTorch qNEHVI`)

Exakt **13 stetige, orthogonale DNA-Parameter** (Budget `16 × 6 = 96` Evals, `min_init = 26`, **2 Pareto-Ziele**: `PressureDrop` & `FlowImbalance`, kein Masse-Ziel, `DimensionalParameters: []`):

| # | Parametername | Base | Min | Max | Physikalische & Morphologische Wirkung |
|---:|---|---:|---:|---:|---|
| 1 | `BundlingStrength` | `0.62` | `0.05` | `1.00` | Anziehungskraft zwischen kompatiblen Pfaden: `0.05` = 12 weitgehend freie Einzeladern; `0.6` = elegante 2–3-stufige Baumverästelung; `1.0` = starke Hauptarterien |
| 2 | `BundlingRangeMm` | `20.0` | `8.0` | `34.0` | Einzugsradius für Pfad-Verschmelzung („wie weit greifen benachbarte Pfade nach einander?“) |
| 3 | `SplitReleaseBias` | `0.36` | `0.16` | `0.72` | Anteil der letzten Pfadlänge vor der Düse, ab dem gebündelte Äste sich frei zu ihren Auslässen aufspalten (frühe vs. späte Verästelung!) |
| 4 | `MurrayExponent` | `2.45` | `2.00` | `3.20` | Murray’s-Law-Exponent $n$ in $r \propto k_{\text{eff}}^{1/n}$: `2.0` = dicke geschwindigkeitskonstante Hauptstämme; `3.2` = schlanke, schnelle Arterien |
| 5 | `BaseChannelScale` | `0.98` | `0.78` | `1.22` | Globales Querschnittsniveau aller gewachsenen Kanäle (Trade-off Druckverlust vs. Bauraum & Volumen) |
| 6 | `UpperLowerSplitBias` | `+0.18` | `-0.75` | `+0.75` | **Freie Symmetrie-/Asymmetrie-Entscheidung:** verschiebt die Wasserscheide & den Einlass-Impuls zwischen `+Y` (HV-Kabelseite) und `-Y` (frei); `0` = symmetrisch 6/6, `+0.5` = z. B. 4/8! |
| 7 | `FarSideLoopAttraction` | `0.25` | `0.00` | `1.00` | **Freie Baum-vs.-Schleifen-Topologie:** `0.0` = 100 % offener Gefäßbaum ohne Staupunkt-Kollision; `1.0` = organische Druckausgleichs-Schleife auf der Fernseite |
| 8 | `PathTension` | `0.45` | `0.15` | `0.85` | Verhältnis von Zugspannung (kurze direkte Wege) zu freiem Ausweichen & Bündelung |
| 9 | `CurvatureSmoothness` | `0.55` | `0.20` | `0.90` | Biegesteifigkeit der 12 Vektorpfade (enge platzsparende Bögen vs. weite strömungsarme Radien) |
| 10 | `ElevationBaseMm` | `23.5` | `16.0` | `32.0` | Nominales vertikales Potentialniveau $Z$ der gewachsenen Arterien über dem `Z = 6 mm`-Flansch |
| 11 | `ElevationTiltMm` | `+4.0` | `-6.0` | `+14.0` | Längsneigung des Höhenpotentials vom Einlass (`180°`) zur Fernseite (`0°`) — erzeugt 3D-aufsteigende/abfallende Gefäßstrukturen |
| 12 | `ObstacleOverpassBias` | `+0.65` | `-0.25` | `+1.00` | Ausweichstrategie am HV-Kabelbaum (`+` = hoher Bogen drüber; `0/-` = flacheres/radiales Ausweichen bzw. Abdrängen) |
| 13 | `CrossSectionAspect` | `0.88` | `0.65` | `1.45` | 2D-Querschnitts-Seitenverhältnis ($b/a$) kombiniert mit frame-invariantem 45°-DfAM-Teardrop |

*(Hinweis für das Pre-Decision Review mit `reviewer`: Prüfen, ob wir `HydraulicBalanceGain` & `ResidualThroatTilt` zusätzlich aufnehmen → `15D` bzw. `CurvatureSmoothness` & `PathTension` zu einem gemeinsamen Steifigkeitsparameter `PathStiffness` zusammenlegen, um sauber bei **13D–14D** inkl. `HydraulicBalanceGain` und `ResidualThroatTilt` zu bleiben! Vorschlag für `reviewer`: `PathTension` & `CurvatureSmoothness` zu `PathStiffness` bündeln + `HydraulicBalanceGain` `[0.0, 1.0]` + `ResidualThroatTilt` `[-0.30, +0.30]` → exakt **14D** mit `min_init = 28` aus `16 × 6 = 96` Evals, oder `ElevationTiltMm` einsparen → **13D**!)*

---

## 5. Mechanische Solid-Engine v3: Organische Verbindungsmatrix & PicoGK-Filleting (`StatorCoolingRingSolidBuilder.cs`)

Alle in `v2` verifizierten mechanischen Funktionsflächen bleiben zu **100 % erhalten** (Bodenflansch `R = 53..76.8`, 2× O-Ring-Nuten `3.2 × 2.0 mm`, 2× Ø 4 mm Passstifte, 4× M5 DIN-912-Dome `R_bolt = 84` mit durchgängigem Werkzeug-Keepout, dreigeteilter G3/8"-Boss mit freiliegendem SW-24-Sechskant + bündiger `Z = 0`-Stützkonsole, `*_context_environment.stl`).

**Neu in `v3` — Dreistufige Organische Struktur- & Verbindungsmatrix (gegen freischwebende Rohre, für maximale Steifigkeit bei sichtbarer Gefäß-Ästhetik):**

1. **Stufe 1 — Lokal variable Kanal-Außenhülle (ohne hauchdünne Wände):**
   - Jeder gewachsene Pfad erhält lokal skalierte Wandstärke $t_{\text{wall}}(r) = \text{Clamp}(2.5 + 0.30 \cdot \max(0, r - 2.0),\; 2.5,\; 4.2)\text{ mm}$ (mindestens `2,5 mm` an den Ø 4 mm Düsen, `3,2..3,8 mm` an dicken Hauptarterien, `4,2 mm` am G3/8"-Einlasshals).
2. **Stufe 2 — Automatische Inter-Tube-Verbindungsrippen-Matrix (`BuildInterTubeRibMatrix`):**
   - Damit getrennt laufende organische Äste **nicht einzeln in der Luft hängen**, aber dennoch **als eigenständige, skulpturale Rohre deutlich erkennbar bleiben**, baut der Solid-Builder automatisch eine **organische Verbindungsrippen-Matrix (`PicoGK.Lattice`)**:
     - **Rohr-zu-Rohr-Rippen:** Für alle Paare ungebündelter Pfadknoten $(\mathbf{p}_{i,s}, \mathbf{p}_{j,t})$ mit freiem Wand-zu-Wand-Abstand im Fenster `1.5..18.0 mm` (abgestuft im Raster ~`12 mm` entlang der Rohre, damit es echte **Rippen/Stege** und kein massiver Block werden!) wird eine **taillierte, sanduhrförmige Verbindungsrippe** erzeugt (`Lattice.AddBeam` mit Endradius `2.6 mm` am Rohr → verjüngter Mitte `1.7 mm` → `2.6 mm` am Nachbarrohr).
     - **Strenger Störkontur-Filter für JEDE Rippe:** Vor dem Einfügen jeder einzelnen Rippe wird das gesamte 3D-Liniensegment analytisch gegen alle **4 HV-/Sensorkapseln**, alle **4 durchgehenden M5-Werkzeugsäulen** und die **Rotor-Innenbohrung (`R = 46 mm`)** geprüft! Eine Rippe, die eine Störkontur oder ein Schraubenloch auch nur innerhalb `2.0 mm` streift, wird automatisch verworfen!
     - **Topologie-adaptive Lastpfad- & Flansch-Stützrippen (`BuildAdaptiveSupportStruts`):**
       - Von jedem der **4 M5-DIN-912-Dome (`60°/120°/240°/300°`, Fußpunkt `R = 75.5, Z = 7.0`)** wächst eine gekrümmte, konische Lastpfad-Rippe (`3.3 -> 2.4 mm`) direkt zum **nächstgelegenen Punkt auf der gewachsenen Gefäßstruktur** (per exakter Kurven-Distanzsuche / `Voxels.vecClosestPointOnSurface`, kollisionsgeprüft!).
       - Zusätzlich wachsen **bis zu 6 schlanke, bogenförmige Stützrippen** von freien Stellen der Flanschoberseite (`Z = 6.0 mm`, `R = 65 mm` zwischen den Düsen, außerhalb der O-Ring-Nuten & frei vom HV-Kabel) nach oben an lange freitragende Hochbögen (z. B. beidseits neben dem HV-Kabel-Überbau bei `75°` und `105°`!).
       - Optional leichte vertikale **Druckbarkeits-Finnen** (`45°`-Schrägrippen) unter besonders hoch schwebenden horizontalen Segmenten, sodass das gesamte Bauteil auch im Metall-3D-Druck (LPBF/SLM) extrem verzugsarm und supportminimiert druckbar ist!
3. **Stufe 3 — PicoGK-Organik-Verschmelzung (`voxFillet` / `voxOverOffset` + `voxSmoothen`):**
   - PicoGK stellt dafür die nativen morphologischen Funktionen **`Voxels.voxFillet(float fRoundingInMM)`** (bzw. `voxTripleOffset` / `voxOverOffset(fInitial, fFinal)`) + **`Voxels.voxSmoothen(float fSmoothInMM)`** bereit!
   - **Geplante Voxel-Reihenfolge:**
     1. Vereinigung: `VariableOuterShell` ∪ `BaseFlange` ∪ `BoltDomes` ∪ `G38Boss+Hex+Console` ∪ `InterTubeRibMatrix` ∪ `AdaptiveSupportStruts`.
     2. **Weiche organische Übergänge & Rippen-Verrundung:** `voxFillet(2.8f)` (oder `voxOverOffset(2.8f, 0.0f)`) gefolgt von `voxSmoothen(0.55f)` — **AUSSCHLIESSLICH auf der Außenhülle!**
        - Ergebnis: Jede Verbindungsrippe, jede Gabelung und jeder Flanschanschluss verschmilzt zu **fließenden, konkaven Hohlkehlen (`R ≈ 3 mm`)** — die runden/tropfenförmigen Rohre bleiben deutlich plastisch sichtbar, sind aber durch ein **nahtloses, knochenartiges Rippennetz** starr und kerbfrei verbunden!
     3. **Erst NACH dem organischen Filleting — messerscharfe Funktions-Subtraktionen:**
        - `rawFluidVoxels` (100 % exakte CFD-Kanalgeometrie ohne jede Verfälschung!),
        - G3/8"-Gewindekernloch (`Ø 15.0 × 12 mm`) + 45°-Fase + Übergangskonus + DIN-3852-Planfläche (`X = -108`),
        - 4× M5-Durchgangsbohrung (`Ø 5.5`) + DIN-912-Zylindersenkung & durchgehender Werkzeugzugang (`Ø 10.0, Z = 7..60`),
        - 2× konzentrische O-Ring-Dichtnuten (`R = 56.8 / 72.4 mm`, `3.2 × 2.0 mm`),
        - 2× Ø 4 mm Passstiftbohrungen (`0° / 180°`),
        - Planare Bodenfläche (`Z < 0`).
        *(Hinweis: Der SW-24-Sechskant auf `X = -106.5..-96.5` kann für absolut rasiermesserscharfe Schlüsselflächen sogar direkt nach dem Filleting sauber als exakter Sechskant-Mantel geclippt oder mit `voxFillet(2.5f)` geprüft werden!)*

---

## 6. Pre-CFD Constraints, OpenFOAM-Tauglichkeit & Risiken

### Pre-CFD-Constraints (`StatorCoolingRingGeometryConstraints.cs`)
Alle 9 harten Constraints aus `v2` bleiben aktiv — ergänzt um eine echte **Giftspalt- & Mindestwand-Prüfung**:
- `InnerRadiusMm >= 46.0`
- `OuterRadiusMm <= 94.0`
- `TotalHeightMm <= 54.0`
- `MinBoltClearanceMm >= 2.0` (durchgängig `Z = 0..54`)
- `MinObstacleClearanceMm >= 2.0` (alle 4 HV-/Sensor-Kapseln)
- `MinBranchRadiusMm >= 1.5` (garantiert saubere `snappyHexMesh`-Zellen und 3D-Druckbarkeit)
- `Volume` in `[1000, 180000] mm³` & `PrintableSolidVolume <= 360000 mm³` (angepasst für die neue Verbindungsrippen-Matrix!)
- **Qualitätsmetrik `MinUnbundledGapMm`:** Analytische Prüfung, dass alle nach der Relaxation ungebündelt gebliebenen Äste entweder sauber verschmolzen (`d < 1.5 mm`) oder sauber getrennt (`d >= r_i + r_j + 2.0 mm`) sind — verhindert schlechte `snappyHexMesh`-Spaltzellen schon vor dem Meshing!

### Risiko-Register & Gegenmaßnahmen

| Risiko | Auswirkung | Geplante Gegenmaßnahme |
|---|---|---|
| **Diskrete Sprünge beim `UpperLowerSplitBias`-Wechsel** (ein Auslass springt von `+Y` nach `-Y`) | Kleine Stufen in der Zielfunktion für `BoTorch` GP | Kontinuierlicher Einlass-Impuls-Bias wirkt auf alle Pfade glatt; `BoTorch` Matérn-5/2 kommt mit wenigen sanften Übergängen gut zurecht; zudem im `--smoke-test` gezielt prüfen, dass `Mid ±5%` sauber sensitiv und valide bleibt! |
| **Gabelungs-Spitzwinkel für `snappyHexMesh`** | Schlechte Non-Orthogonality oder Layer-Collapse an sehr flachen Y-Abzweigen | `voxSmoothen(0.35f)` auf dem Fluidvolumen + kontrolliertes `SplitReleaseBias`-Abklingen erzeugt glatte, verrundete Gabelungen (`>= 20°` Öffnungswinkel); `SurfaceRefinementLevelMin/Max` (`2/2` oder `2/3`) im ersten Server-Check auf `BIC_12` verifizieren. |
| **Zuschmieren von Rohrabständen durch `voxFillet`** | Rohre könnten zu einem Klumpen verschmelzen, statt sichtbar zu bleiben | Rippen im definierten Raster (`~12 mm`) + schlanke Stegmitte (`1.7 mm`) + moderates `voxFillet(2.6..3.0f)` (NICHT `6 mm`!) + lokaler Python-STL-Schnittcheck! |
| **Sechskant-Kantenabrundung durch `voxFillet`** | SW-24-Schlüsselflächen könnten zu rund werden | SW-24-Sechskant-Bereich (`X = -106.5..-96.5`) entweder nach `voxFillet` sauber als scharfkantiger Sechskant-Zuschnitt vereinigen/beschneiden oder per STL-Audit auf exakte `12.0 mm` Ebenheit prüfen! |
| **Relaxations-Laufzeit im `--smoke-test` (19 Probes)** | Langsamer Smoke-Test | 12 Pfade × 48 Knoten × 180 Iterationen ≈ `12 × 11 × 48 × 180` Operationen (~1 Mio. float-Ops in reinem C# → **`< 30 ms` pro Probe**!); nur für `Base` (plus optionale Showcase-Probes) wird das schwere Solid exportiert. |

---

## 7. Modularer Code- & Umsetzungsplan (für den nächsten `/delegate`-Lauf)

### Dateistruktur unter `src/projects/StatorCoolingRing/`
- `StatorCoolingRingTypes.cs` — Aktualisiert auf das 13D/14D-Wachstums-DNA-Set (`StatorCoolingRingParams`), `VascularPath`, `VascularNetwork`, `RibSegment`, `StatorCoolingRingPackagingReport`.
- `StatorCoolingRingVascularEngine.cs` *(NEU — ersetzt die starre Ring-Mathe aus `StatorCoolingRingTopologyBuilder.cs`!)* — Pure C# (`System.Numerics`):
  - `BuildVascularNetwork(p, targets)`: Phasen A–D (Initialisierung, 180-Schritt SDF- & Bistabil-Bundling-Relaxation, Murray’s-Law-$k_{\text{eff}}$, optionale 1D-Vorbalancierung `HydraulicBalanceGain` + `ResidualThroatTilt`).
  - `EvaluateAnalyticalPackaging(network, p, targets)`: Vollständige 3D-Envelope-, Bolzen-, HV-Kabel- und Giftspalt-Prüfung.
  - `BuildInterTubeRibSegments(network, targets)` & `BuildAdaptiveSupportRibCurves(network, targets)`: Kollisionsgeprüfte Rippen-Verbindungsmatrix.
- `StatorCoolingRingTopologyBuilder.cs` — Schlanke PicoGK-Brücke: wandelt `VascularNetwork` in `BuildFluidVolumeVoxels(...)` und `BuildLocalVariableOuterShellVoxels(...)` (mit frame-invariantem 45°-DfAM-Teardrop) um.
- `StatorCoolingRingSolidBuilder.cs` — Erweitert um `BuildInterTubeRibMatrix`, topologie-adaptive M5-/Flansch-Stützrippen, `voxFillet` + `voxSmoothen` und scharfkantige Funktions-Subtraktionen (DIN-912, G3/8" + SW-24, O-Ring-Nuten, Passstifte, `*_context_environment.stl`).
- `StatorCoolingRingGeometryGenerator.cs`, `StatorCoolingRingGeometryConstraints.cs`, `StatorCoolingRingFitnessCalculator.cs`, `project.json`, `simulation.json`, `solvers/openfoam.json`, `README.md`, `tests/FluidForge.Tests/StatorCoolingRingTests.cs`.

### Verbindliche Arbeitspakete für die Umsetzung (`/delegate`)
- [x] **AP 0 — Pre-Decision Review (Supervisor ⇄ `reviewer` `gemini-4-hydrogen-xhigh`):** Abgeschlossen mit **`APPROVE`**. Beschlossener Konsens:
  - **14D-DNA-Set:** `PathTension` + `CurvatureSmoothness` → `PathStiffness` (`[0.15, 0.90]`, Base `0.50`); Aufnahme von `HydraulicBalanceGain` (`[0.00, 1.00]`, Base `0.55`) und `ResidualThroatTilt` (`[-0.30, +0.30]`, Base `0.00`), weich maskiert über `(1 / max(1, k_eff))^2` + Auslass-Fenster (`u ∈ [0.55, 0.96]`, Fade auf `0` bei `u = 1.0`). `BaseChannelScale` auf `[0.85, 1.22]` (Base `1.00`) angehoben; `UpperLowerSplitBias` Base `+0.28` (`21°`, stabiler `5/7`-Split weg vom HV-Kabel, gleichsinniger stetiger `-Y`-Einlass-Drall, `Mid = 0.0` → `6/6`).
  - **Giftspalt-Metrik `MinUnbundledGapMm`:** Prüft nur die echte Giftspalt-Zone `fluidGap ∈ [-0.3, +2.0] mm` mit `18 mm` Fork-Ausblendung für monoton divergierende Paare und wird als diagnostische Passive-Metrik (`MinUnbundledGapMm`, `SliverGapCount`) geloggt (kein harter BoTorch-Blocker neben den 9 Packaging-/Clearance-/Volumen-Constraints).
  - **Fernseiten-Schleife (`FarSideLoopAttraction`):** Separates Brücken-Fenster `u ∈ [0.60, 0.76]` ohne `F_repel` zwischen dem Grenzpfad-Paar, stetige bistabile Anziehung und gedeckelter antiparalleler Brücken-Zuschlag in `k_eff` (`<= 1.25`).
  - **SDF & Solid-Engine v3:** `F_sdf` nutzt richtungsabhängigen Teardrop-Radius + `t_wall`. Während `voxOverOffset(2.6f, 0.0f)` + `voxSmoothen(0.55f)` läuft ein durchgehender `R = 11.8 mm`-Kernzylinder über `X = -108..-85.5` (vordere Konsolenstrebe zurück auf `X = -92.5, r = 3.5`); *danach* zuerst äußerer Ring-Freischnitt um den SW-24-Sechskant (`X = -106.5..-96.5`, außerhalb `EvaluateHexRadius(phi, 12.0f)`), *dann* scharfkantiger SW-24-Sechskant-Union. Alle Inter-Tube- & Support-Rippen werden gegen HV/Sensor (`>= 2.0 mm`), M5-Keepout (`R = 6.2 mm`), `R_in >= 46.5`, `R_out <= 93.5`, `Z <= 53.5` und `Z >= 6.0` (Inter-Tube) gefiltert; M5-Dom-Rippen starten bei `R = 74.5, Z = 6.5, r0 = 2.8 mm`.
- [x] **AP 1 — Pure-C# Vascular Engine & Unit-Tests (`worker-vascular`, `gemini-4-hydrogen-high`):** Implementiert in `StatorCoolingRingTypes.cs`, `StatorCoolingRingVascularEngine.cs` (NEU), `StatorCoolingRingTopologyBuilder.cs`, `StatorCoolingRingGeometryGenerator.cs`, `StatorCoolingRingGeometryConstraints.cs`, `project.json` und `tests/FluidForge.Tests/StatorCoolingRingTests.cs`. Verifiziert: `dotnet build -warnaserror` (0W/0E), `15/15` Tests grün; `Base` (5/7) & `Mid` (6/6) feasible mit `SliverGapCount = 0`, alle 14 Parameter sensitiv (0 `DEAD`), 40 Inter-Tube- + 10 Support-Rippen kollisionsfrei.
- [x] **AP 2 — PicoGK Fluid-, Solid- & Rippenmatrix-Integration (`worker-solid`, `gemini-4-hydrogen-high`):** Umgesetzt in `StatorCoolingRingTopologyBuilder.cs` (`Frames.EFrameType.MIN_ROTATION`), `StatorCoolingRingSolidBuilder.cs` (Inter-Tube-Sanduhr-Rippenmatrix, adaptive Dom-/Flansch-Stützrippen, durchgehender G3/8"-Kernzylinder + zurückgezogene Konsole, `voxOverOffset(2.6f, 0.0f)` + `voxSmoothen(0.55f)`, Post-Fillet-Ring-Freischnitt + scharfer SW-24-Sechskant), `README.md` und `StatorCoolingRingTests.cs` (`16/16` grün, 0W/0E).
- [x] **AP 3 — Lokale & `BIC_09`-Verifikation + Multi-Morphologie-Showcase-Export (Supervisor):**
  - `dotnet build FluidForge.sln -warnaserror` (**0W/0E** auf Windows & `BIC_09` Linux), volle Release-Testsuite (**1077/1077** bestanden) + **17/17** `StatorCoolingRingTests`, `--print-config` & `--smoke-test` auf Windows & `BIC_09` (**`PASS 6/6 probes valid, 14/14 parameter checks passed`**, alle 6 wasserdicht `Open=0, NonMan=0`, `Base/Mid/Max/Rand1/Rand2 OK`, 14/14 `EFFECTIVE`).
  - **14D-Box-Feasibility (200 Random-Samples, Seed 42):** **174/200 (87,0 %)** feasible (`Inner=0, Height=0, Bolt=0, Outer=3, Obstacle=8, MinR=17`).
  - **4 kontrastierende Showcase-Morphologien** unter `results/StatorCoolingRing_v3_preview/` (`Base`, `HighBundling_Asym4_8`, `OpenDendriticTree`, `ClosedLoop` + `StatorCoolingRing_context_environment.stl` + `audit_summary.json` + `overview_4_morphologies.png` + `overview_base_sections.png`): alle 4 `Feasible=True`, `Sliver=0`, `hv_collision_tris=0`, `sensor_collision_tris=0`, `bolt_tool_hits=0`, `min_solid_obstacle_clearance_mm = 3.49..3.60 mm`, `SW-24 = 12.00 mm` exakt, `console_z0_tris = 5972`, `Solid = 149.97..181.32 cm³`.
  - **`BIC_09` Base-`snappyHexMesh`-Check (`Level (2, 3)`, `BaseCellSize = 1.6 mm`, 12 MPI-Kerne, ohne `simpleFoam`):** **`69 s`**, `1,390,585` Zellen, `Non-Ortho Max 64.99° / Avg 14.71°`, `77.9 %` Boundary-Layer-Abdeckung (`4` angefragt, `3.2` avg, `0.071 mm` Wandzelle; 1 skew face `5.98`). Damit ist `solvers/openfoam.json` (`Level (2, 3)`) direkt für `BIC_12` (`18` MPI-Kerne) bestätigt!
- [x] **AP 4 — Post-Implementation Review, Feinschliff & Abschluss (`reviewer` `gemini-4-hydrogen-xhigh`):** Finales Peer-Review mit **`APPROVE`** abgeschlossen; kleiner Robustheits-Hinweis zu den Flansch-Stützrippen (`minZAllowed: 2.5f` + Anker `120°` statt `0°`-Passstift) direkt eingepflegt und erneut getestet; alle Swarm-Worker beendet und sauberer Git-Commit erstellt.

---

## 8. Gefundene Fehler, Erkenntnisse & Handover für Folge-Agenten

### Gelöste Stolpersteine während der Umsetzung
1. **`Frames.EFrameType.CYLINDRICAL` am Einlass `(-90, 0, 20)`:** Der zylindrische Radialvektor `(-1, 0, 0)` ist exakt antiparallel zur Einlass-Tangente `(+1, 0, 0)`. Umstellung auf `Frames.EFrameType.MIN_ROTATION` in `StatorCoolingRingTopologyBuilder.cs` löst die Singularität vollständig bei unverändertem frame-invariantem 45°-Teardrop.
2. **Einlass-Abgang, `ClosedLoop`-Sehne & 14D-Feasibility (33,5 % → 87,0 %):** Im ersten Preview-Audit löste `F_repel` bei `u < 0,22` einen vertikalen Knick hinter dem Einlass aus, und das frühere Brücken-Fenster `u ∈ [0.60, 0.76]` zog `ClosedLoop` als Sehne in die Rotorbohrung (`Inner = 36,79 mm`). Behoben durch `inletRepelFade`, Verlegung der Fernseiten-Brücke auf `u ∈ [0.74, 0.90]` mit radialer Projektion `R >= max(60, MinInnerRadius + 15)`, verstärkte Innenbohrungs-SDF (`stiffness = 10.5`) und Drossel-Klemmung `[0.84, 1.32]`.
3. **SW-24-Sechskant-Schärfe & `FrontCollar`-Stabilität:** Durchgehender `R = 11.8 mm`-Kernzylinder (`X = -108..-85.5`) während `voxOverOffset(2.6f, 0.0f)` + `voxSmoothen(0.55f)` mit zurückgezogener Konsole (`X >= -96.0`), danach zuerst äußerer Ring-Freischnitt auf `X = -106.5..-96.5`, dann scharfkantige `hexVox`-Vereinigung → verifizierte `12.00 mm` Flachmaß über alle 4 Morphologien.
4. **Flaky Debug-Timing im Unit-Test:** `VascularEngine_Is_Strictly_Deterministic_And_Fast` misst nun nach einmaligem JIT-Warmup (`< 1500 ms` unter paralleler Testlast).
5. **Flansch-Stützrippen-Rand:** `minZAllowed` auf `2.5f` (statt exakt `3.0f` bei `5.5 - 2.5`) und Ankerwinkel `120°` statt `0°` (über dem Passstift-Sackloch).

### Ausstehende nächste Schritte (Handover für AP 4.3 auf `BIC_12`)
1. **Visuelle Kontrolle:** Preview-STLs & Schnittplots in `results/StatorCoolingRing_v3_preview/` (`Base`, `HighBundling_Asym4_8`, `OpenDendriticTree`, `ClosedLoop` + `StatorCoolingRing_context_environment.stl`) in ParaView/Blender inspizieren.
2. **Vollständiger `BoTorch qNEHVI`-Lauf auf `BIC_12`:** Nach Abschluss der B1–B3-Benchmarks (`phase4_presets_benchmarks_und_calibration_fix.md`) mit `.\scripts\sim.ps1 run -Server BIC_12 -Project StatorCoolingRing` den `16 × 6 = 96`-Pareto-Lauf (`PressureDrop` vs. `FlowImbalance`) starten (`solvers/openfoam.json` mit `Level (2, 3)` ist auf `BIC_09` bereits mit `69 s` / `1,39 Mio.` Zellen verifiziert!).
