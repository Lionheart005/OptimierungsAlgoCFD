# Block W – Physik-Feinschliff (vor dem KI-Plan)

> **Stand: 06.11.2026.** Folgt auf den Servertest (`servertest_2026-11-06.md`) und kommt **vor**
> `ki_optimierung.md` (TODO-53 ff.). Damit die KI-Nummern nicht schon wieder verschoben werden,
> heißen die Schritte hier **TODO-W1 bis TODO-W10**.
> Quelle: deine Nachricht vom 06.11.2026 abends (Punkte 1–6 der offenen Liste) und die
> Messungen aus dem Servertest.
> **Status (07.11.2026):** **TODO-W1 bis W10 sind vollständig abgeschlossen (557/557 Tests grün).**
> Server-Validierung Kette 3 (`~/servertest_kette3.sh`) lief am 06./07.11.2026 erfolgreich durch; die Folgefixes B14–B17 und Architektur-Kritikpunkte KP-1 bis KP-7 sind umgesetzt und auf BIC_12 bereitgestellt.

---

## Fortschritt (für die Übergabe)

| Schritt | Inhalt | Größe | Status |
| --- | --- | --- | --- |
| TODO-W1 | Kalibrierung/Feinprüfung: Abweichung relativ **plus** absolute Untergrenze (Werte nahe 0) | klein | [x] |
| TODO-W2 | 2D-Schnitt mit Sehne als Bezug, SU2-Version aus dem Banner | klein | [x] |
| TODO-W3 | Konvergenz neu: „iterativer Fehler“ statt nackter Cauchy-Schwelle, Ergebnis = Fenstermittel | mittel | [x] |
| TODO-W4 | Profil **Kontrollrechnung**: Kalibrierung, Feinprüfung, Recheck rechnen streng und lange | mittel | [x] |
| TODO-W5 | Adaptive Kalibrierung: weiter verfeinern (0,35 / 0,25 …), bis die Toleranz hält | mittel | [x] |
| TODO-W6 | **Detailbox** um und hinter dem Modell (2D und 3D), Vorder-/Hinterkante feiner | mittel | [x] |
| TODO-W7 | URANS startet aus der stationären Lösung | mittel | [x] Neustart am Server geprüft |
| TODO-W8 | Projektvorgaben übernehmen (Naca2D-Netz, MantaAuv „Prism“ + großer Kanal) | klein | [x] Werte nach W10 nachjustiert |
| TODO-W9 | Kleinkram aus dem Servertest | klein | [x] |
| TODO-W10 | Server-Validierung: NASA-TMR α 0/10/15, Naca2D, MantaAuv | Server | [x] Kette 3 abgeschlossen (07.11.2026) + B14–B17 umgesetzt |

---

## Ausgangslage (Messungen 06.11.2026)

* **NASA-TMR NACA 0012, Re 6·10⁶, feinstes Netz (Faktor 0,5):** α 0°: CD −2 %; α 10°: CD +28 %,
  CL −3 %; α 15°: CD +63 %, CL −9 %. CD fällt mit jeder Stufe noch deutlich → nicht netzkonvergiert.
* **Konvergenz:** Mit den Vorgaben (500 Iter., Cauchy 1e-4 über 150) stoppt SU2 meist bei
  Iteration ~250, obwohl CD im Fenster noch ~3 % driftet. Mit strengen Werten (Cauchy 1e-6) wird
  das Kriterium nie erreicht, weil die Residuen bei ~1e-7 stagnieren (CD pendelt dann nur noch ±0,5 %).
* **Kalibrierung bei α = 0:** CL ≈ 0 → relative Abweichung „1 727 %“, Kalibrierung „fehlgeschlagen“,
  obwohl CL in jeder Stufe praktisch 0 ist.
* **URANS** startet aus dem Freistrom, CL ist nach 200 Schritten noch im Anlauf.
* **TestprojektNACA**: 2D-Schnitt mit `FrontalArea` (= Profildicke) als Bezug → Beiwerte ×8,3.
* **SU2-Version** in `run-info.json` „unbekannt“.
* Physik-Fixes aus dem Servertest (B1–B13) sind umgesetzt, aber **nicht committet**.

---

## Entscheidungen

### Von dir (06.11.2026)

| # | Frage | Entscheidung |
| --- | --- | --- |
| EW1 | Wie genau sollen Kontrollrechnungen sein? | **Physikalisch belastbar, auch wenn sie Stunden dauern** (es sind wenige). |
| EW2 | Wer legt die zulässige Abweichung fest? | **Die Kalibrierung entscheidet selbst**, auch für die Konvergenz. |
| EW3 | Werte nahe 0 | Abweichung auf die Größe des Werts normieren; ist der Wert klein, sind große relative Abweichungen in Ordnung. |
| EW4 | Anstellwinkel | **Nicht anders rechnen.** Stattdessen eine **sehr feine Box um und hinter dem Modell** (Modellgröße), damit Nachlauf und Wirbel stimmen. |
| EW5 | Wandschicht | Prismen sind in Ordnung, wenn die Werte belastbar sind; **Hexa und Prism bleiben wählbar.** |
| EW6 | Bezugsfläche, SU2-Version, URANS | beheben |

### Eigene Festlegungen (Begründung jeweils kurz)

| # | Festlegung | Warum |
| --- | --- | --- |
| FW1 | **Kombinierte Toleranz** wie bei `numpy.isclose`: `|Δ| ≤ rtol·|q_ref| + atol_q`. Angezeigt wird die **normierte Abweichung** `|Δ| / (rtol·|q_ref| + atol_q)`; ≤ 1 heißt „in Ordnung“. | Genau deine Idee (EW3): Bei großen Werten zählt die relative Abweichung, bei Werten nahe 0 die absolute. Eine Zahl, die man ohne Nachdenken lesen kann. |
| FW2 | `atol` je Kennwert, Vorgaben: **Lift 0,005**, **Drag 0,0001** (1 „drag count“), Momente 0,001, sonst 0. Einstellbar unter `MeshCalibration.AbsoluteTolerances`. | 0,005 ist genau das Kriterium, das die NASA-TMR-Prüfung (`Naca2DTmrBenchmark`) bei α = 0 nutzt. 1 drag count ist die übliche Auflösungsgrenze im Flugzeugbau. |
| FW3 | **Iterativer Fehler** = Unterschied der Mittelwerte von erster und zweiter Hälfte des Konvergenzfensters (mit derselben Normierung wie FW1). „Konvergiert“, wenn er **≤ 1/10 der Kalibrier-Toleranz** ist (Kontrollrechnungen; normale Varianten 1/4). Ergebnis ist der **Fenstermittelwert**, nicht die letzte Iteration. | Trennt echtes Weiterlaufen (Drift) von harmlosem Pendeln. Die Faustregel „Iterationsfehler höchstens ein Zehntel des Diskretisierungsfehlers“ (Celik et al. 2008; Eça & Hoekstra empfehlen noch strenger, 100–1000-fach) sorgt dafür, dass die Kalibrierung Netzeffekte misst und keine Konvergenzeffekte (EW2). Residuen allein sind kein Maß: Sie stagnieren bei SU2-INC mit Limiter bei ~1e-7, obwohl CD längst steht. |
| FW4 | **Zwei Profile:** normale Varianten (zügig) und **Kontrollrechnungen** (Kalibrierung, Feinprüfung, Recheck). Der Türsteher (Variante 99) rechnet bewusst mit dem normalen Profil, weil er mit normalen Varianten verglichen wird. | EW1. Die vielen Optimierungsvarianten bleiben schnell, die wenigen Kontrollrechnungen werden genau. |
| FW5 | Kalibrierung verfeinert **adaptiv** weiter (Faktor 0,5 → 0,35 → 0,25 …), solange die Toleranz nicht hält und die hochgerechnete Zellzahl unter der Grenze bleibt (2D: 2 Mio., 3D: wie bisher 50 Mio.). Zusätzlich Richardson-Extrapolation als Schätzung des „netzunabhängigen“ Werts im Bericht. | Bei α 10/15 war 0,5 nachweislich zu grob. So entscheidet die Kalibrierung selbst, wie fein es sein muss (EW2), statt dass jemand Stufen vorgibt. |
| FW6 | **Detailbox** (EW4): von 0,1 L vor bis **1,0 L hinter** dem Modell, quer Modellhöhe + 0,25 L je Seite; Zellgröße 2D L/200, 3D = Wandzellgröße × 1,5; skaliert mit dem Netzfaktor (die Kalibrierung prüft sie also mit). Die bestehende lange Nachlauf-Box (3 L, gröber) bleibt. Dazu in 2D eine **feinere Wand an Vorder- und Hinterkante** (krümmungsabhängig). | Wirbel und Nachlauf werden in Modellnähe aufgelöst. Die Vorderkante ist bei Anstellwinkel die empfindlichste Stelle (Saugspitze) – das ist keine Sonderbehandlung des Winkels (EW4), sondern gilt immer. |
| FW7 | **URANS-Recheck:** erst stationär (Kontrollprofil), dann URANS **mit Neustart aus diesem Feld** (`RESTART_SOL= YES`; die Lösung wird als `restart_00000.dat` und `restart_00001.dat` bereitgestellt, `RESTART_ITER= 2`). Zeitschritte so viele, dass **≥ 20 Perioden** gerechnet und die **letzten 10** gemittelt werden. Der Bericht stellt stationär und zeitgemittelt gegenüber. | Kein Anlauf aus dem Freistrom mehr; die Mittelung beginnt erst, wenn die Strömung eingeschwungen ist. |
| FW8 | Bezugsgröße im 2D-Schnitt immer die **Sehne**, egal was das 3D-Projekt einträgt. SU2-Version aus dem **Banner der ersten Rechnung** (die Konsolenausgabe wird künftig immer mitgelesen). | Kleine, eindeutige Fehler. |

---

## Arbeitsschritte

### TODO-W1 – Kombinierte Toleranz (klein)
* `CalibrationEvaluator`, `ChampionFineVerifier.ComputeMaxRelativeDeviation`, Recheck-Ausgabe in
  `Program`: eine gemeinsame Hilfsfunktion `MetricTolerance.NormalizedDeviation(q, qRef, rtol, atol)`.
* Neuer Schlüssel `MeshCalibration.AbsoluteTolerances` (Wörterbuch Kennwert → atol, Vorgaben FW2).
* Berichte (`calibration.csv`, `Verifikation.csv`, Konsole): Spalte „normiert“ (≤ 1 = ok) zusätzlich zur
  relativen Abweichung.
* Tests: α = 0-Fall (CL 0,0001 vs. −0,0005 → ok), großer Wert (CL 1,0 vs. 1,05 bei rtol 2 % → nicht ok).

### TODO-W2 – Bezugsfläche 2D-Schnitt, SU2-Version (klein)
* 2D-Stufe der Vorkalibrierung (`Program`, `controller2D`): Solver-Optionen mit `ReferenceAreaMetric = "Chord"`.
* `Su2Solver`: Konsolenausgabe immer puffern (nur die ersten ~50 Zeilen), Version per Regex aus dem
  Banner; `RunInfoWriter` trägt sie nach der ersten Rechnung nach.
* Tests: Banner-Parser; 2D-Optionen nutzen „Chord“.

### TODO-W3 – Konvergenz neu (mittel)
* `Su2HistoryAnalyzer`: `IterativeError` je Kennwert (FW3), `Oscillation` getrennt davon (Amplitude um den
  Trend, nicht max − min inkl. Drift). Ergebnis = Fenstermittel (`ResultAveraging` neue Vorgabe
  `"CauchyWindowMean"`).
* SU2 stoppt per Cauchy über **alle** `ResultMetrics` (`CONV_FIELD= (DRAG, LIFT)`), `CONV_STARTITER`
  = Fensterlänge statt fest 100. Danach prüft das Programm den iterativen Fehler; erfüllt er die Grenze
  nicht, gilt die Rechnung als „nicht konvergiert“ (wie heute, mit klarer Meldung).
* Neue Vorgaben für normale Varianten: `Su2MaxIterations` 3000, Fenster 300, Grenze 0,5 %.
* Schwingungswarnung nur noch bei echter Oszillation.
* Tests mit synthetischen Verläufen: Drift, Pendeln ohne Drift, Pendeln mit Drift.

### TODO-W4 – Profil Kontrollrechnung (mittel)
* Neuer Abschnitt `ControlRuns` in `simulation.json`: `Su2MaxIterations` (Vorgabe 20 000),
  `ConvergenceWindow` (500), Grenze für den iterativen Fehler = `MeshCalibration.Tolerance / 5` (FW3).
* `WorkflowController`/`Su2Solver`: Tags `Kal*`, `Fein*`, `Recheck` (und die 2D-Vorkalibrierung) nutzen das
  Profil; Variante 99 (Türsteher) nicht (FW4).
* Kalibrierung: eine Stufe zählt nur, wenn ihr iterativer Fehler die Grenze einhält; sonst Meldung
  „Stufe nicht auskonvergiert – mehr Iterationen nötig“ statt stummer Wertung.
* Tests: Profilwahl je Tag; Kalibrierung verwirft nicht auskonvergierte Stufe.

### TODO-W5 – Adaptive Kalibrierung (mittel)
* `MeshCalibration.Run` (2D und 3D): nach Stufe 5 (0,5) weiter mit 0,35, 0,25 …, solange die Toleranz
  (W1) zur nächstfeineren Stufe nicht hält und die Zellgrenze (FW5) nicht überschritten wird.
* Richardson-Extrapolation aus den drei feinsten Stufen im Bericht (`q_∞`, Ordnung p, GCI).
* Neuer Schlüssel `MeshCalibration.MaxCells2D` (Vorgabe 2 000 000).
* Tests: künstliche Folge, die erst bei 0,25 hält.

### TODO-W6 – Detailbox und feinere Kanten (mittel)
* `Gmsh2DMesherOptions` und `GmshMesherOptions`: `DetailBox` (an/aus, Vorgabe **an**),
  `DetailBoxLengthFactor` (1,0), `DetailBoxWidthFactor` (0,25), `DetailBoxCellSizeFactor`
  (2D 0,005 · L; 3D 1,5 × Wandzellgröße). Als zusätzliches `Box`-Feld im `Min`-Feld.
* 2D: Punktgrößen an Vorder- und Hinterkante feiner (krümmungsabhängig, `EdgeRefinementFactor` 0,25).
* Handbuch: neue Schlüssel (der Handbuch-Test verlangt das).
* Tests: .geo enthält die Box mit den richtigen Grenzen; Skalierung mit dem Netzfaktor.

### TODO-W7 – URANS aus stationärer Lösung (mittel)
* `Program.RunRecheck` bei `--unsteady`: 1. stationär (Kontrollprofil), 2. `restart.dat` →
  `restart_00000.dat` und `restart_00001.dat`, 3. URANS mit `RESTART_SOL= YES`, `RESTART_ITER= 2`.
* `TimeIterations` aus der Periodenzahl (Vorgabe 20 Perioden, `StepsPerPeriod` 50), Mittelung über die
  letzten 10 Perioden (`WINDOW_START_ITER`).
* Bericht: stationär vs. zeitgemittelt, Schwingungsamplitude, erkannte Frequenz (Strouhal) aus dem CL-Verlauf.
* **Erst prüfen** (Server, 10 min): ob SU2 7.5.1 INC so aus einer stationären Lösung neu startet.
* Tests: cfg-Schlüssel; Dateien werden bereitgestellt.

### TODO-W8 – Projektvorgaben (klein)
* Naca2D `gmsh.json`: Wand c/250, Nahfeld und Nachlauf c/100 (wie im Servertest). `simulation.json`:
  Werte aus W3/W4 statt 500 Iterationen.
* MantaAuv `gmsh.json`: `BoundaryLayerMode: "Prism"`, Kanal 1200 mm, `TunnelCenterX` 300, `WakeRefinement`.
  Wird nach den Ergebnissen von Kette 2 (Prism-Kalibrierung) endgültig festgelegt.
* Achtung: MantaAuv-Ergebnisse vor/nach dieser Änderung sind nicht vergleichbar (Kommentar in der JSON).

### TODO-W9 – Kleinkram (klein)
* Zellzahl-Hochrechnung der Kalibrierung in 2D quadratisch statt kubisch.
* Fingerabdruck der Kalibrierung berücksichtigt `*.local.json`.
* Feinprüfung benennt Dateien nach dem Kandidaten (`Gen5_Var1_Fein1` statt `Gen99_Var99_Fein1`).
* „OPTIMIERUNG ERFOLGREICH BEENDET“ nur, wenn mindestens eine Variante gültig war.

### TODO-W10 – Server-Validierung [x] Abgeschlossen (07.11.2026)
1. NASA-TMR α 0/10/15 mit adaptiver Kalibrierung, Detailbox, Kontrollprofil. **Ziel:** CL innerhalb 2 %,
   CD innerhalb 5–10 % der Referenz (der Rest geht auf Fernfeld 50 c und inkompressibel statt M 0,15).
2. Naca2D-Optimierung 8×8 (normales Profil) + Feinprüfung Top-3 + URANS-Recheck (Kontrollprofil).
3. MantaAuv „Prism“: Kalibrierung + kurzer Lauf.
*Ergebnis:* Kette 3 am Server BIC_12 abgeschlossen. Folgeerkenntnisse B14–B17 (Gmsh Hinterkanten-Knickpunkt, Kalibrierungs-Fallback, y+-Limit, 2D-SVG/3D-NACA) sowie Architekturpunkte KP-1 bis KP-7 wurden vollständig behoben und verifiziert.

---

## Reihenfolge

```text
W1, W2, W9  (klein, unabhängig)        → sofort
W3 → W4 → W5                            (bauen aufeinander auf)
W6                                       (unabhängig, braucht nur W1 für den Test)
W7                                       (braucht W4; vorher 10-min-Servertest)
W8                                       (nach Kette 2 und W6)
W10                                      (zum Schluss)
```

Aufwand: Code etwa 2–3 Arbeitstage, dazu die Serverläufe (W10).

---

## Risiken

| Risiko | Gegenmittel |
| --- | --- |
| Kontrollrechnungen werden sehr lang (20 000 Iter. × feine Netze) | Abbruch per Cauchy, sobald der iterative Fehler die Grenze hält; Zellgrenze in W5 |
| SU2 INC startet URANS nicht aus einer stationären Lösung | Erst Servertest (W7); notfalls lange Anlaufphase mit verworfener erster Hälfte (wie heute, aber 20 Perioden) |
| Detailbox macht normale Varianten deutlich teurer | Zellgröße skaliert mit dem Netzfaktor; die Kalibrierung zeigt, ob sie sich lohnt; abschaltbar |
| `atol`-Vorgaben passen nicht zu einem neuen Kennwert | Vorgabe 0 (= rein relativ wie heute) für unbekannte Kennwerte; Handbuch erklärt es |
| Änderungen an Konvergenz/Ergebnis verschieben alle Kennwerte | Alte und neue Läufe nicht direkt vergleichen; steht im Handbuch und im Logkopf |

---

## Offene Fragen

Keine zwingenden. Falls du eine Vorgabe anders willst, sag es vor W1:
* `atol`-Werte (Lift 0,005, Drag 0,0001)?
* Detailbox 1 L hinter dem Modell – oder länger (z. B. 2 L)?
* Kontrollrechnungen bis 20 000 Iterationen?

---

## Umsetzungsstand und Abweichungen vom Plan (06.11.2026)

**Recherche vor dem Start** (Literatur und SU2-Doku): Iterativer Fehler höchstens 1/10 des Diskretisierungsfehlers
(Celik et al. 2008), strenger bei Eça & Hoekstra; SU2-eigenes Cauchy-Kriterium auf Iterationsdifferenzen und
Residuen sind kein verlässliches Maß; die NASA-TMR-Gitter haben **500 c** Fernfeld, die Auflösung an der Hinterkante
ist der größte Einfluss; ein Neustart des URANS aus einer stationären Lösung braucht zwei Zeitstände.

| Schritt | Umgesetzt als | Abweichung vom Plan |
| --- | --- | --- |
| W1 | `MetricTolerance` (kombinierte Toleranz), `CalibrationEvaluator`/`ChampionFineVerifier` nutzen sie, Berichte zeigen die **normierte** Abweichung (≤ 1 = ok), `MeshCalibration.AbsoluteTolerances` | Richardson-Extrapolation zusätzlich im Bericht |
| W2 | 2D-Schnitt mit `ReferenceAreaMetric = "Chord"`; SU2-Version aus dem Banner (`Release 7.5.1`) in `run-info.json` | – |
| W3 | Drift-Prüfung im Endfenster (`Su2ConvergenceJudge`, `Su2HistoryAnalyzer.WindowDrift/TailMeans/OscillationAmplitude`), `ResultAveraging "TailMean"`, `IterativeToleranceFraction` | Bei Endfenster < 20 Zeilen gilt die alte Regel (bestehende Tests); `CONV_FIELD` bleibt ein Feld (Mehrfachfelder nicht geprüft); `CONV_STARTITER` unverändert |
| W4 | `ControlRuns`-Profil (`SimulationConfig.WithControlRunProfile`), erkannt am Tag `Kal…`/`Fein…`/`Recheck…`; Variante 99 bewusst nicht | Feinprüfungs-Tag nennt den Kandidaten (`Fein1_Gen5_Var1`) |
| W5 | `MeshCalibration.RefineAdaptively` (0.35, 0.25, 0.18, 0.13), Zellgrenzen `MaxCells2D/3D`, Zellhochrechnung nach Dimension | Stoppt auch bei nicht konvergierter feinster Stufe oder y+-Verletzung (feiner hilft dann nicht) |
| W6 | 2D: Detailbox, Kantenverfeinerung, Fernfeld 500 c mit Wachstumsstrecke; 3D: optionale Detailbox | **Kantenverfeinerung über Punktgrößen und geteilte Kontur, nicht über Felder** (siehe unten). Detailbox in 3D Standard aus |
| W7 | Zweistufiger Recheck: stationär → `restart_00000/00001.dat` → URANS mit `RESTART_SOL`; 20 Perioden, Mittel der letzten 10 | `TIME_ITER` ist ein absoluter Endindex; `OUTPUT_WRT_FREQ` begrenzt die Restart-Dateien; Historie heißt `history_00002.csv`; Strouhal-Zahl nicht umgesetzt |
| W8 | Naca2D: Auto-Kalibrierung, Top-3-Feinprüfung, 3000 Iterationen, `TailMean`, `TreatUnconvergedAsFailed`, feineres Grundnetz; MantaAuv: Prism, Kanal 1200 mm, Nachlauf-Box, `ControlRuns` 8000 Iterationen, `MaxCells3D` 3 Mio. | Werte nach W10 nachjustieren |
| W9 | Zellhochrechnung 2D, Fingerabdruck mit `*.local.json`, Feinprüfung nennt den Kandidaten, „Optimierung erfolgreich“ nur bei gültigen Varianten | Recheck nimmt den tatsächlichen Netzfaktor der Variante aus der CSV (nach Auto-Kalibrierung stand dort nur 1.0) |

**Gmsh-Erkenntnisse (4.11.1, am Server geprüft), wichtig für Wartung:**
* In 2D scheitert die Quad-Wandschicht bei dünnen Profilen an der Hinterkante („Edge not recovered“). Wiederholung mit
  ½, ¼, ⅛ der Schichtdicke fängt das ab (1 von 42 Varianten brauchte alle Stufen bis ¼ und scheiterte, daher ⅛).
* **Distanz-, Ball- und Krümmungsfelder mit Zellen unter der Wandzellgröße lassen Gmsh bei aktivem Grenzschichtfeld
  endlos rechnen.** Punktgrößen an den Kanten funktionieren, wirken aber auf die ganze Kurve; deshalb wird die Kontur
  an Vorder-/Hinterkante geteilt (Teilungspunkte 0,1 L entfernt).
* Detailbox-Feld ist unproblematisch (+2 000 Knoten bei 55 000).
* 3D: siehe Befund B11 im Servertest-Protokoll.

**SU2-Erkenntnisse:** Das Restart-Paar `restart_00000/00001.dat` + `RESTART_ITER= 2` + `SOLUTION_FILENAME= restart.dat`
funktioniert; der erste Zeitschritt startet bei dem stationären Wert (CL 1,136 gegen 1,140).

**Fehler von mir (06.11.2026 23:21):** Ein Deploy in ein zweites Server-Verzeichnis stoppte die laufende Kette 2, weil
`sim-runner.sh running/stop` benutzerweit arbeiten. Folge: Naca2D-Optimierung v2 nach 42 Rechnungen unterbrochen
(gesichert unter `~/Servertest_20261106/40_Naca2D_Optimierung_v2`); Merkzettel in den Notizen. Kette 3 startet auf
leerem Server.

**Kette 3 (TODO-W10):** TMR α 10 → 0 → 15 (Kalibrierung, bis 16 h je Schritt), Naca2D 8×8 mit Auto-Kalibrierung,
URANS-Recheck des Champions, MantaAuv Prism Kalibrierung + 4×4. Geschätzt 25–40 h. Auswertung nach Ende; Ergebnisse
liegen unter `~/Servertest_20261106/W10_*`.
