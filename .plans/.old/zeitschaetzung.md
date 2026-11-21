# Bessere Zeitschätzung (und früher Grenz-Hinweis)

> **Stand: 20.11.2026** — **Umgesetzt und lokal getestet (AP-1 bis AP-6), noch nicht auf dem Server.**
> Build ohne Warnungen, alle Tests grün. Beim Deploy gilt wie immer: erst, wenn kein Lauf mehr rechnet.
> **Anlass:** Lauf `MantaAuv` (BIC_12, Start 13.11.2026). Das Log meldete nach Gen4_Var7
> „Geschätzte Restzeit: 17:34:24“, tatsächlich waren es noch rund **13 Tage** plus Feinprüfung.
> **Wichtig:** Erst umsetzen bzw. deployen, wenn auf dem Server kein Lauf mehr rechnet
> (ein Deploy stoppt laufende Läufe).

---

## 1. Was heute falsch ist (gemessen am Lauf `MantaAuv`)

Die Schätzung steht in `WorkflowController.UpdateTimer` (Rechnung) und
`SimulationLog.PrintVariantFooter` (Ausgabe).

| # | Fehler | Auswirkung im Lauf |
|---|---|---|
| F1 | Format `hh:mm:ss` lässt die **Tage weg**. | „17:34:24“ statt ≈ 12 T 17 h. Die Anzeige springt scheinbar zufällig (09:01 → 03:38 → 15:18), weil sie alle 24 h überläuft. |
| F2 | Mittelwert über **alle** bisherigen Varianten. | Die ersten Varianten waren klein (5 min), ab Iteration 3 dauert jede ≈ 4,6 h. Mittel 3,3 h statt 4,6 h, die Schätzung ist **≈ 30 % zu kurz**. |
| F3 | Die **Nachprüfung des Champions** (`Var99`, eine volle Rechnung je Iteration) fehlt. | ≈ 4,5–5 h je Iteration fehlen, bei 10 Iterationen ≈ 2 Tage. |
| F4 | Die **Feinprüfung am Ende** (`ChampionVerification`, `FinalTopN`) fehlt. | Top 3 auf feinerem Netz (≈ 2,3 × Zellen) mit strengem Profil (bis 20.000 statt 5.000 SU2-Iterationen): bis zu ≈ 40 h je Variante, also **bis zu ≈ 5 Tage**. |
| F5 | Nur eine Dauer, **kein Datum**. | Für eine Erinnerung muss man selbst rechnen. |

---

## 2. Arbeitspakete

Alles in C#, keine neuen Abhängigkeiten, kein neuer Pflicht-Schlüssel in den JSON-Dateien.

### AP-1 Format mit Tagen (klein, zuerst)
- [x] Neue Hilfsfunktion `SimulationLog.FormatDuration(TimeSpan)`, z. B. `12 T 17 h`, unter einem Tag `17 h 34 min`, unter einer Stunde `34 min`.
- [x] `PrintVariantFooter` nutzt sie. Die Laufzeit der Variante ebenfalls lesbar ausgeben (`4 h 49 min` statt `17321.5s`).

### AP-2 Schätzer als eigene, testbare Klasse
- [x] `Core/Pipeline/RunTimeEstimator.cs`, reine Rechnung ohne Solver, damit sie per Unit-Test prüfbar ist.
- [x] **Gleitendes Fenster:** Die Dauer je Variante ist der Mittelwert der **letzten 10 gerechneten** Varianten (weniger als 10 vorhanden → alle). Gescheiterte Varianten (wenige Sekunden) bleiben außen vor, sonst ziehen sie den Schnitt nach unten.
  *Abweichung vom ersten Entwurf:* Aus dem Journal übernommene Varianten (`--resume`) zählen **mit ihrer ursprünglichen `DurationSeconds`** mit (die bleibt seit TODO-51 erhalten). Sonst gäbe es nach einem `resume` erst nach einer neu gerechneten Variante (Stunden) eine Prognose.
- [x] **Restliche Rechnungen** = offene Varianten + eine Champion-Nachprüfung je noch nicht abgeschlossener Iteration (inklusive der laufenden).
- [x] **Feinprüfung am Ende getrennt** ausweisen (`Mode` = `Final`/`FinalTopN`, bei `EachIteration` je Iteration):
  Dauer ≈ Anzahl × Varianten-Dauer × Zellfaktor × (`ControlRuns.Su2MaxIterations` / `Su2MaxIterations`).
  Zellfaktor: `√2 ^ (RefinementSteps × Exponent)` mit Exponent 2,4 (3D, gemessen am MantaAuv: Kalibrierstufe 0,71 → 0,50 brachte Faktor 2,28 statt 2,83) bzw. 2,0 (2D).
  *Abweichung vom ersten Entwurf:* Kein Auslesen der Kalibrierung. Die gemessene Konstante ist einfacher und braucht keine Kalibrierdaten (die es bei `Mode = "Manual"` gar nicht gibt).
  Das ist eine **Obergrenze**, weil das strenge Profil früher konvergieren kann; so wird sie auch beschriftet.
- [x] AutoStop an: Die Schätzung bleibt die Obergrenze (`MaxIterations` × `VariantsPerIteration`), der Text sagt „höchstens“.

### AP-3 Datum statt nur Dauer
- [x] Fußzeile je Variante, z. B.:
  `└─ ⏱ 4 h 49 min | Rest ≈ 12 T 17 h → Optimierung fertig ca. Do 03.12. 22:00, danach Feinprüfung bis zu ≈ 5 T`
- [x] In der Zusammenfassung jeder Iteration eine Zeile:
  `Nächste Iteration fertig ca. Mo 23.11. 09:00`.
  Das ist genau die Angabe, die man braucht, um einen Lauf von Hand nach Iteration N zu stoppen.
- [x] Ortszeit des Servers, Wochentag und Datum auf Deutsch.

### AP-4 `sim.ps1 status` zeigt die Schätzung
- [x] `status` gibt zusätzlich die letzte Prognose aus dem Log aus („--- Prognose (aus dem Log) ---“), damit man nicht im Log suchen muss. Nur lesen.
  *Umsetzung:* in `scripts/sim-runner.sh` (Funktion `status`), nicht in `sim.ps1`. Dort liegt schon die Ausgabe der letzten Logzeilen. Wirkt erst nach dem nächsten Deploy; `bash -n` ist sauber, der `grep`-Block wurde mit einer Beispieldatei unter `set -euo pipefail` ausprobiert.

### AP-5 Grenz-Hinweis schon je Iteration (Nebenbei, klein)
- [x] `BoundaryHint` meldet heute **nur ganz am Ende** (`FinishOptimization`), also erst nach der Feinprüfung, und **gar nicht**, wenn der Lauf von Hand gestoppt wird.
- [x] Zusätzlich in der Zusammenfassung jeder Iteration ausgeben, wenn der Champion an einer Grenze liegt (gleiche Klasse, gleiche 1-%-Regel, eine kurze Zeile). Die Grenzen werden weiterhin **nie** automatisch erweitert.

### AP-6 Tests und Doku
- [x] Tests in `tests/OptimierungsAlgoCFD.Tests/RunTimeEstimatorTests.cs`: `FormatDuration` (auch Rundung an den Stufen), `FormatDateTime`, `RunTimeEstimator` (Fenster, ungültige Werte, Nachprüfung je Iteration, Vorschau auf die nächste Iteration, Ende, Feinprüfung, `EachIteration`, 2D/3D, AutoStop), die Log-Zeilen und drei Tests, die den echten `WorkflowController`-Ablauf prüfen (Prognose in jeder Fußzeile, `[GRENZE]` je Iteration). Die Tests setzen ihre Werte selbst und prüfen keine Projekt-Einstellungen.
- [x] `BEDIENHANDBUCH.md`: neuer Abschnitt 6.1a „Wie lange dauert der Lauf noch?“, Zeile `status` in der Befehlstabelle angepasst.
- [x] `dotnet test OptimierungsAlgoCFD.sln` grün, Build ohne Warnungen.

---

## 3. Bewusst nicht im Umfang

- Eine Vorhersage aus Zellzahl × Iterationen je Variante. Die wäre genauer, die Zellzahl steht aber erst nach dem Vernetzen fest, und das gleitende Fenster reicht hier: Bayes bleibt nach den ersten Iterationen in einem Bereich mit ähnlich großen Netzen.
- Eine Prognose-Datei auf dem Server (z. B. in `run-info.json`). Erst, wenn AP-4 nicht reicht.

## 4. Fehler / Erkenntnisse

- **Zellwachstum:** Die erste Idee (`√2³ ≈ 2,8` je Stufe) war zu hoch. `MeshCalibration.EstimateNextCells` nennt denselben Grund: Wandschicht und Oberfläche wachsen nur quadratisch. Gemessen am MantaAuv 2,28 je Stufe, als Potenz von √2 also ≈ 2,4.
- **Feinprüfung bleibt eine Obergrenze:** Sie nimmt an, dass die Varianten das normale Iterationslimit ausschöpfen (beim MantaAuv ab Iteration 3 immer der Fall). Konvergieren Varianten früher, ist die Angabe zu hoch, nie zu niedrig.
- **Offen / bewusst nicht gemacht:** Netzkalibrierung am Laufanfang ist nicht in der Prognose (läuft vor der ersten Variante). Prüfung der Champion-Nachprüfung (`Var99`) mit eigener Dauer statt „eine Variante lang“: Sie dauerte am MantaAuv gleich lang, deshalb nicht nötig.
- **Server:** Wirkt erst nach einem Deploy. Beim laufenden MantaAuv-Lauf (Commit `c2ebdf2`) bleibt das alte Log-Format bis zum nächsten `run`.
