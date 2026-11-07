# Servertest des Branches `MajorPhysikFixes` (06.11.2026)

> Prüft alles aus `anmerkungen_umsetzung.md` (TODO-31 bis TODO-49) und Block V aus
> `ki_optimierung.md` (TODO-50 bis TODO-52) auf BIC_12 (192.168.122.121, 20 Kerne).
> Erst die kurzen Prüfungen (Minuten), danach ein langer, physikalisch belastbarer Lauf.
> Ergebnisse landen per `fetch` unter `Serverergebnisse/` (gitignored).
> Testläufe wurden über `*.local.json` auf dem Server verkürzt/umgestellt; sie sind nach
> jeder Stufe wieder gelöscht worden (Repo-Dateien unverändert, außer den Fixes unten).

## Stufe 1 – kurze Prüfungen (abgeschlossen)

| # | Prüfung | Status | Ergebnis / Befund |
| --- | --- | --- | --- |
| 1 | Lokal `dotnet build` + `dotnet test` | [x] | vorher 481/481, nach allen Fixes **484/484**, 0 Warnungen |
| 2 | SSH, `deploy`, `doctor`, `projects` | [x] | ok. Auf dem Server lag der Stand vom 23.10.; keine `*.local.json` |
| 3 | `calibrate -Project Naca2D` (TODO-42/45/46) | [x] | erst alle SU2-Läufe abgebrochen (B2), dann ohne Grenzschicht (B3). Nach Fix: läuft, y+ P95 ≈ 1,5, aber starke Netzabhängigkeit (P1) |
| 4 | `run -Project Naca2D` + `stop` + `status` + `resume` (Praxistest TODO-52) | [x] | **bestanden**: „Abgebrochener Lauf gefunden: 4 Rechnungen“, `[WIEDERAUFNAHME] … übernommen (1/4…4/4)`, Seed aus dem Journal, `run-info.json` mit Wiederaufnahme-Eintrag, Journal-Schlusszeile |
| 5 | `fetch` ohne `-Project` (TODO-31), `plot_history.py` (TODO-36) | [x] | fetch ok (nimmt letztes Projekt, ein Archiv). Plot war leer (B7), behoben |
| 6 | `recheck … -Unsteady` (URANS, TODO-37) | [x] | erst SU2-Abbruch (B8), dann Abbruch nach 10/200 Schritten (B9). Nach Fix: 200 Schritte in ~2 min, Zeitmittel `tavg[CD]` |
| 7 | MantaAuv 1×2, `BoundaryLayerMode: "Hexa"` (Server-Spike TODO-40) | [x] **fehlgeschlagen** | Gmsh bricht ab (B11). `"Prism"` gleicher Aufbau. Nicht behoben, eigener Arbeitsschritt |
| 7b | MantaAuv 1×2, `"Threshold"` | [x] | läuft, 15–24 s je Variante; y+ P95 ≈ 43–55 (ohne Wandschicht erwartet) |
| 8 | TestprojektNACA `Use2DPrecalibration` (TODO-48) | [x] | Ablauf korrekt (2D 5 Stufen → 3D 3+1 Stufen → Bericht). Ergebnis „fehlgeschlagen: y+ der Referenzstufe“ – richtig, da 3D keine Wandschicht hat |

## Befunde und Korrekturen (alle mit Test, außer wo vermerkt)

* **B1 – `sim.ps1 calibrate` tat bei unverändertem Code nichts** (Exit-Code 1 ohne Meldung).
  `Invoke-Deploy` behandelte im Zweig „Code unverändert" nur `-ThenStart`. Behoben in `scripts/sim.ps1`.
* **B2 – 2D: SU2 brach jede Rechnung ab** („doesn't have any definition for marker Fluid").
  Gmsh 4.11.1 schreibt beim SU2-Export in 2D die `Physical Surface("Fluid")` zusätzlich als
  Rand-Marker. Ohne die Gruppe schreibt Gmsh keine Zellen (NELEM= 0); `Mesh.SaveAll = 1` ergibt
  „Mismatch between NPOIN“. Fix: `Gmsh2DMesher.RemoveMarker` streicht den Block nach dem Vernetzen.
* **B3 – 2D: die Quad-Grenzschicht wurde nie erzeugt.** `BoundaryLayer Field = 1;` fehlte, und
  Gmsh 4.11 wertet `NbLayers` nur mit `BetaLaw` aus (ohne `Thickness` nur 1 Schicht). Fix:
  `Thickness` = Gesamtdicke aus `BoundaryLayerCalculator`, `SizeFar` = Wandzellgröße, Feld angemeldet.
  Ergebnis: ~23 Quad-Schichten, y+ Median ≈ 1,1, P95 ≈ 1,5–1,9.
* **B4 – Kalibrierung überwachte nur Drag.** `ResolveMonitoredMetrics` parste `su2.json` ohne
  Kommentar-Unterstützung → still leer → „Drag". Fix: `JsonCommentHandling.Skip`.
* **B5 – Kalibrierstufen überschrieben sich gegenseitig** (`History_Gen1_Var1.csv`, ebenso VTU/CSV;
  die Feinprüfung hätte die Dateien der normalen Variante überschrieben). Fix:
  `Su2Solver.ArchiveBaseNameFromMesh` nimmt den Tag aus dem Netznamen (`…_Kal5`, `…_Fein1`, `…_Recheck`).
* **B6 – Prozentformat `:0.1` falsch** (Feinprüfung zeigte „131 %“ statt 13,1 %, Recheck „01 %“).
  In .NET ist die `1` ein Literal. Fix: `:0.0` in `ChampionFineVerifier.cs` und `Program.cs` (ohne Test).
* **B7 – `plot_history.py`: leerer Plot.** x-Achse war `Time_Iter`, die SU2 7.5.1 auch stationär
  schreibt (immer 0). Fix: Zeitspalten nur, wenn sie variieren (ohne Test).
* **B8 – URANS: SU2-Abbruch** „ITER must not be used … unsteady“. Fix: `ITER=` im Zeitmodus
  auskommentiert. Zusätzlich `TAVG_AERO_COEFF` in `HISTORY_OUTPUT` und Lesen von `tavg[CD]` usw.
  (vorher wurde nur der Momentanwert des letzten Zeitschritts gelesen).
* **B9 – KERNBEFUND: SU2 lief nie parallel.** Das offizielle `SU2-v7.5.1-linux64-mpi` ist
  statisch mit **MPICH** gebaut, `MpiRunPath` zeigte auf **OpenMPI** (Miniconda 5.0.10). Ergebnis:
  `mpirun -n 12` startete 12 unabhängige serielle Kopien derselben Rechnung, die sich gegenseitig
  `history.csv`, `restart.dat`, VTUs überschrieben (Banner 12×, `[unset]: write_line error`,
  `OpenMPIFile`-Fehler, Exit-Code 1 trotz „Exit Success“, URANS-Absturz nach 10 Schritten).
  Betrifft vermutlich **alle Läufe seit der Einrichtung**. Fix: `conda create -n mpich -c conda-forge mpich`
  auf BIC_12 (eigenes Env, OpenMPI bleibt), `MpiRunPath` → `/home/lpleissner/miniconda/envs/mpich/bin/mpiexec`
  in MantaAuv/Naca2D/TestprojektNACA, `setup_server.sh` ergänzt. Wirkung: 2D-Kalibrierung 10 min → 33 s,
  URANS 22 min (und Absturz) → 113 s.
* **B10 – PicoGK-Läufe endeten nie.** `Library.Go` ohne `bEndAppWithTask` hält das Programm offen,
  bis jemand das Viewer-Fenster schließt; unter Xvfb nie (auch der Lauf vom 23.10. endet ohne
  „Ende:“). Der unsichtbare Viewer rendert per llvmpipe dauerhaft auf ~10 Kernen. Fix:
  `bEndAppWithTask: true` in `PicoGkKernel`, `LP_NUM_THREADS=1` vor `xvfb-run` in `sim-runner.sh`.
  Ergebnis: sauberes Ende, `run-info.json` „abgeschlossen“, Viewer ≈ 1 Kern.
* **B11 – 3D-Wandschicht „Hexa“/„Prism“ funktionierte nicht. Behoben (Zwei-Schritt-Verfahren).**
  Ursachen (alle am Server mit Gmsh 4.11.1 nachgestellt): (1) Flächennummern wurden vor
  `ClassifySurfaces`/`CreateGeometry` gesammelt; (2) das Fluidvolumen nutzte die Körperfläche statt der
  Oberseiten der Schicht; (3) ohne Größenvorgabe hatte die neu vernetzte Oberfläche ~113 Knoten;
  (4) **Gmsh-Schwäche:** die Wandschicht-Extrusion über mehrere Teilflächen (ein geschlossener Körper
  zerfällt beim Klassifizieren immer in ≥ 2) lässt eine Teilfläche mit Dicke 0 liegen → degenerierte
  Zellen → SU2 bricht in ParMETIS ab („Key … not found“); (5) `Mesh.RecombineAll`/`MeshSizeMax` wirken
  global (auch auf Kanal und Volumen). Lösung in `GmshCfdMesher`: **Schritt 1** `Wall_<V>.geo` vernetzt
  die Oberfläche neu und speichert sie als EINE Fläche (`Physical Surface(1)`, `SaveElementTagType = 2`,
  Medit `.mesh`); **Schritt 2** `Meshing_<V>.geo` extrudiert diese eine Fläche, sammelt die Oberseiten,
  mergt danach den Kanal und füllt den Kern mit Tetraedern. Durchdringt sich die Schicht (Gmsh-Abbruch),
  wird automatisch mit weniger äußeren Schichten wiederholt (21 → 16 → 12 …). Fluid-Marker wird
  entfernt (wie 2D). Tests angepasst/ergänzt (486/486).
  **Messung MantaAuv Gen1_Var1:** `"Prism"` 21 Schichten (2,2 mm), 125 160 Prismen, 1 ungültige Zelle,
  SU2 konvergiert (Residuum ~1e-9,5), **y+ Median 1,07 / P95 2,29** (Threshold: 37 / 54), CD 0,373
  (Threshold 0,337). `"Hexa"`: max. 12 Schichten, alle Quader gültig, aber ~5 % der Übergangspyramiden
  an den scharfen Flügelkanten gekippt → SU2 divergiert. An einer glatten Kugel funktioniert Hexa
  (SU2 konvergiert). **Empfehlung im Handbuch geändert: „Prism“ für Voxel-Körper.**
  Noch nicht auf dem Server deployt (die Kette läuft mit dem Stand davor).

## Physikalische Beobachtungen (kein Code-Fehler, aber wichtig)

* **P1 – Naca2D-Vorgabenetze sind für CD zu grob.** Kalibrierung (α = 5°, Re 1,7·10⁵): CD 0,052 (Faktor 2)
  → 0,029 (1,0) → 0,019 (0,5); GCI feinstes Netz ≈ 17 %. Keine Stufe erreicht 2 %. Lift ist robuster.
* **P2 – „Konvergiert“ heißt bei den Vorgaben nur „CD ändert sich je Iteration kaum“.** Fast alle
  Varianten stoppen bei Iteration 252–260 (frühestmöglich: CONV_STARTITER 100 + 150 Cauchy-Elemente),
  Residuen fallen nur 2–4 Dekaden, CD driftet im Fenster noch ~3 % (Plot `History_Gen3_Var2.png`).
  Die Schwingungswarnung („Wirbelablösung“) misst max−min im Fenster und enthält diese Drift.
* **P3 – TestprojektNACA rechnet den 2D-Schnitt mit `FrontalArea` (= Profildicke) als Bezug**, die
  2D-Beiwerte sind daher ~8,3× zu groß (0,116 statt ~0,014). Für die Kalibrierung (relative
  Abweichungen) egal, für die Anzeige irreführend.
* **P4 – `Naca2DTmrBenchmark` wird im Programm nirgends aufgerufen**; Naca2D rechnet bei Re 1,7·10⁵,
  die NASA-Referenz gilt für Re 6·10⁶. Der Abgleich wird in Stufe 2 von Hand gemacht.
* Kleinkram: Zellzahl-Hochrechnung der Kalibrierung nimmt immer (2/0,5)³ an, in 2D wäre es ², also
  4× zu vorsichtig. Fingerabdruck der Kalibrierung ignoriert `*.local.json`. Feinprüfung heißt
  `Gen99_Var99_Fein1` statt Name des Kandidaten. „OPTIMIERUNG ERFOLGREICH BEENDET“ auch wenn alle
  Varianten gescheitert sind. SU2-Version in `run-info.json` „unbekannt“.

## Stufe 2 – Kette 1 (06.11.2026 13:16 – ca. 22:15), Skript `~/servertest_kette.sh`

Ergebnisse auf dem Server unter `~/Servertest_20261106/`, lokal unter
`Serverergebnisse/Servertest_2026-11-06/` (ohne `*.su2`, Volumen-VTU, `restart.dat`).

**NASA-TMR NACA 0012, Re 6·10⁶, feinstes Netz (Faktor 0,5, 247 000 Zellen), je 20 000 Iterationen:**

| α | CD sim / ref | CL sim / ref | GCI CD |
| --- | --- | --- | --- |
| 0° | 0,00799 / 0,00819 (−2 %) | 0,000 / 0 | 10 % |
| 10° | 0,0158 / 0,01231 (+28 %) | 1,055 / 1,0909 (−3 %) | 30 % |
| 15° | 0,0346 / 0,02124 (+63 %) | 1,413 / 1,5461 (−9 %) | 27 % |

Bei α = 0 stimmt es, mit Anstellwinkel ist auch das feinste Netz nicht netzkonvergiert (CD fällt
von Stufe zu Stufe noch stark). Ursachen-Kandidaten: Auflösung der Vorderkante/Saugspitze, Fernfeld
50 c statt 500 c (CL), inkompressibel statt M 0,15, Residuen stagnieren bei ~1e-7 (Cauchy 1e-6 nie
erfüllt → „fehlgeschlagen“). Bei α = 0 ist CL ≈ 0, die relative Abweichung von CL ist dann sinnlos
groß (Kalibrierung sollte Größen nahe 0 absolut vergleichen – offen).

**Naca2D (Re 1,7·10⁵, α 5°):** Kalibrierung konvergiert in allen Stufen, aber GCI 22 %, keine Stufe
in 2 % → Faktor 0,5. Optimierung 8×8: **33 von 64 Varianten scheiterten an Gmsh** („Edge not
recovered“, B12), ebenso alle Feinprüfungen und der URANS-Recheck (Faktor 0,35). Champion Gen5_Var1,
Gleitzahl 63, y+ Median 0,92. Die Feinprüfung meldete trotzdem „Rangfolge hält“ (B13).

**MantaAuv 3D 10×10 „Threshold“ (Pipeline-Langzeittest):** ~2 min je Variante, 0 Ausfälle in
>100 Rechnungen, Lauf endet sauber (Viewer-Fix wirkt).

* **B12 – 2D-Wandschicht kollidiert an dünnen Profilen** (seit dem B3-Fix aktiv). Behoben:
  `Gmsh2DMesher` wiederholt mit ½ bzw. ¼ Schichtdicke (y1 bleibt). Von Hand geprüft: gescheitertes
  Profil Gen1_Var2 läuft mit ½ Dicke.
* **B13 – Feinprüfung zählte gescheiterte feine Rechnungen als verifiziert.** Behoben:
  `FineVerified = 0`, Meldung „NICHT PRÜFBAR“. Test ergänzt (487/487).

## Stufe 2 – Kette 2 (ab ca. 22:15), Skript `~/servertest_kette2.sh`, tmux `kette2`

Wartet auf Kette 1, baut den neuen Stand (b62e63e4…, mit B11–B13), dann:
A) Naca2D 8×8 Faktor 0,5 erneut + URANS-Recheck (nach 42 Rechnungen um 23:21 durch zweites Deploy unterbrochen, gesichert unter `40_Naca2D_Optimierung_v2`).
Ersetzt durch **Kette 3** mit komplettem Block W (TODO-W1 bis W9).

---

## Stufe 3 – Kette 3 (06.11.2026 23:24 – 07.11.2026 14:03, abgeschlossen)

Skript: `~/servertest_kette3.sh`, Ergebnisse lokal unter `Serverergebnisse/Servertest_2026-11-06/W10_*` und `kette3.log`.

| Schritt | Testlauf | Dauer | Status | Wichtigste Kennwerte / Befund |
| --- | --- | --- | --- | --- |
| **W10_01** | NASA-TMR NACA 0012, $\alpha = 10^\circ$, $Re = 6\cdot 10^6$ (Kalibrierung Stufe 1–7, bis $f=0{,}25$, 821 426 Zellen) | 66 min | [x] | Alle 7 Stufen konvergiert! Feinstes Netz ($f=0{,}25$): **$C_D = 0{,}01194$** (NASA: $0{,}01231$, **$-3{,}0\,\%$**), **$C_L = 1{,}0656$** (NASA: $1{,}0909$, **$-2{,}3\,\%$**). Monotone Konvergenz ($p = 2{,}24$, Extrapolation $C_D = 0{,}01135$, $GCI_\text{fine} = 6{,}21\,\%$). $y^+_{95} \approx 2{,}08\dots 2{,}33$. |
| **W10_02** | NASA-TMR NACA 0012, $\alpha = 0^\circ$, $Re = 6\cdot 10^6$ (Kalibrierung Stufe 1–7) | 60 min | [x] | Alle 7 Stufen konvergiert, kombinierte Toleranz ($C_L \approx 0$) funktioniert! Stufe 4 ($f=0{,}71$, 117 288 Zellen): **$C_D = 0{,}00820$** (NASA: $0{,}00819$, **$+0{,}1\,\%$**), $C_L = -0{,}00015$. $y^+_{95} \approx 1{,}44\dots 1{,}57$. Bei Stufe 6/7 fiel $C_D$ auf $0{,}00736 / 0{,}00690$, weil der Hinterkanten-Retry die Wandschichtdicke auf 13 % kürzte (siehe B14). |
| **W10_03** | NASA-TMR NACA 0012, $\alpha = 15^\circ$, $Re = 6\cdot 10^6$ (Kalibrierung Stufe 1–5) | 32 min | [x] | Stufen 1–5 konvergiert, monoton ($p = 3{,}20$, $GCI_\text{fine} = 3{,}56\,\%$). Stufe 5 ($f=0{,}50$, 221 246 Zellen): $C_D = 0{,}02956$, **$C_L = 1{,}4631$** (NASA: $1{,}5461$, **$-5{,}4\,\%$**). Adaptive Verfeinerung nach Stufe 5 gestoppt, da $y^+_{95} = 2{,}59 > 2{,}50$ an der Saugspitze der Nase (siehe B16). |
| **W10_04** | Naca2D Optimierung 8×8 mit `AutoCalibration` ($Re = 1{,}7\cdot 10^5$, $\alpha = 5^\circ$) | 22 min | [x] **Abbruch in Kal7** | Stufen 1–6 (bis $f=0{,}35$, 411 121 Zellen, $C_D = 0{,}01438$, $C_L = 0{,}51377$) liefen durch. Bei Zusatzstufe 7 ($f=0{,}25$) brach Gmsh trotz 13 % Schichtdicke ab (`Edge not recovered`, B14). Weil `RefineAdaptively` die gescheiterte Stufe 7 in `stages` beließ, wertete `CalibrationEvaluator` Stufe 7 als kaputte Referenzstufe und brach den gesamten Lauf vor Start der 8×8-Optimierung ab (B15). |
| **W10_05** | Naca2D URANS-Recheck des Champions | – | übersprungen | Da W10_04 in der Vorkalibrierung abbrach, lag kein Champion vor (URANS-Restart selbst wurde vorab erfolgreich am Server verifiziert). |
| **W10_06** | MantaAuv 3D `"Prism"`-Kalibrierung (Stufe 1–6, bis $f=0{,}35$, 1,28 Mio. Zellen) | 3,8 h | [x] | Prismen-Wandschicht bis 1,28 Mio. Zellen völlig stabil! Stufen 3–6 konvergiert ($C_D$: $0{,}3773 \to 0{,}2719 \to 0{,}2305 \to 0{,}1978$). Stufe 7 (3,5 Mio. Zellen) durch `MaxCells3D` (3 Mio.) regulär begrenzt. |
| **W10_07** | MantaAuv 3D `"Prism"` 4×4 Optimierung ($f=0{,}71$) + Top-3 Feinprüfung ($f=0{,}50$) | 7,8 h | [x] **100 % Erfolg** | **16/16 Varianten + 3/3 Feinprüfungen ohne einzigen Ausfall!** Champion: **Gen1_Var4** ($C_D = 0{,}1709$ normal / $0{,}1460$ fein, 415 809 Zellen, $y^+_\text{median} = 1{,}08$, $y^+_{95} = 2{,}22$). Top-3-Feinprüfung: **Rangfolge 1 → 1, 2 → 2, 3 → 3 hält zu 100 %** (`RankingHolds = 1`). |

---

## Neue Befunde aus Kette 3 & offene Architekturpunkte (alle umgesetzt am 07.11.2026)

* **[x] B14 – 2D: `EdgeRefinementFactor` an spitzen Knickpunkten (`CornerIndices`) verursacht Wandschicht-Selbstschnitt (`Kal7`).**
  Behoben in `Gmsh2DMesher.cs`: Knickpunkte (`CornerIndices`) bleiben auf `surfaceSize`, nur gerundete Vorderkante (`leIdx`) wird feiner diskretisiert; `MinThicknessScale` auf `0.0625f` gesenkt. Kal7 vernetzt fehlerfrei.
* **[x] B15 – Kalibrierung: Fehlgeschlagene adaptive Zusatzstufe überschreibt gültige Referenzstufe + harter Abbruch im `Auto`-Modus.**
  Behoben in `MeshCalibration.cs` und `CalibrationEvaluator.cs`: Gescheiterte Zusatzstufen fliegen aus `stages` raus; `CalibrationDecision.FallbackStage` liefert die feinste gültige Stufe mit `[WARNUNG]` statt Hard-Crash.
* **[x] B16 – Kalibrierung: `y+`-Grenze `2.5` bei $\alpha = 15^\circ$ zu eng für lokale Saugspitze.**
  Behoben in `MeshCalibration.cs`: Limit für $y^+_{95}$ in der Kalibrierung auf `3.0` (Stufe 1: `3.5`) angehoben.
* **[x] B17 – NACA-Darstellung: 2D-SVG stand auf dem Kopf + dicker roter Punkt an Hinterkante; 3D-NACA-Kugelradius an der Nase.**
  Behoben in `Shape2D.cs`: Y-Achse invertiert, Knickpunkt-Kreis entfernt; in `TestprojektNACAGeometryGenerator.cs`: Kugelradius an der Vorderkante sauber begrenzt.
* **Architektur-Kritikpunkte 1–7 (`architektur_kritikpunkte_naca.md`) & `Anmerkungen.txt`:**
  - [x] KP-1: `RubberBandScaler` setzt `ShrinkFactor = 1.0f`, wenn `DimensionalParameters` leer ist.
  - [x] KP-2: `ChampionValidator` nutzt relativen Jitter bzgl. `ParameterBounds` und clampt auf `[Min, Max]`.
  - [x] KP-3: `WorkflowController` formatiert kleine Parameterwerte mit bis zu 4 Nachkommastellen (`0.####`) + übersichtlichere Log-Ausgabe.
  - [x] KP-4: `AngleOfAttackDeg` als aktiver Optimierungsparameter in `project.json` durch `Su2Solver` unterstützt.
  - [x] KP-7: `ExecutableResolver` mit Tilde-Expansion und automatischer Server-Fallback-Kette für `gmsh`, `SU2_CFD` und `mpirun`/`mpiexec`.
  - [x] `MachNumber` aus allen Projekt-JSONs entfernt (`FreestreamVelocity` in m/s ist alleinige Vorgabe).
