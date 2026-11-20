# Bessere Zeitschätzung (und früher Grenz-Hinweis)

> **Stand: 20.11.2026** — Plan, noch nichts umgesetzt.
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
- [ ] Neue Hilfsfunktion `SimulationLog.FormatDuration(TimeSpan)`, z. B. `12 T 17 h`, unter einem Tag `17 h 34 min`, unter einer Stunde `34 min`.
- [ ] `PrintVariantFooter` nutzt sie. Die Laufzeit der Variante ebenfalls lesbar ausgeben (`4 h 49 min` statt `17321.5s`).

### AP-2 Schätzer als eigene, testbare Klasse
- [ ] `Core/Pipeline/RunTimeEstimator.cs`, reine Rechnung ohne Solver, damit sie per Unit-Test prüfbar ist.
- [ ] **Gleitendes Fenster:** Die Dauer je Variante ist der Mittelwert der **letzten 10 live gerechneten** Varianten (aus dem Journal übernommene zählen nicht mit; weniger als 10 vorhanden → alle). Gescheiterte Varianten (wenige Sekunden) bleiben außen vor, sonst ziehen sie den Schnitt nach unten.
- [ ] **Restliche Rechnungen** = offene Varianten + eine Champion-Nachprüfung je noch nicht abgeschlossener Iteration (inklusive der laufenden).
- [ ] **Feinprüfung am Ende getrennt** ausweisen (`Mode` = `Final`/`FinalTopN`, bei `EachIteration` je Iteration):
  Dauer ≈ Anzahl × Varianten-Dauer × Zellfaktor × (`ControlRuns.Su2MaxIterations` / `Su2MaxIterations`).
  Zellfaktor aus der Kalibrierung (gemessene Zellzahl der feineren Stufe / Stufe des gewählten Faktors), ohne Kalibrierung ersatzweise 2,8 (3D) bzw. 2,0 (2D) je `RefinementSteps`.
  Das ist eine **Obergrenze**, weil das strenge Profil früher konvergieren kann; so wird sie auch beschriftet.
- [ ] AutoStop an: Die Schätzung bleibt die Obergrenze (`MaxIterations` × `VariantsPerIteration`), der Text sagt „höchstens“.

### AP-3 Datum statt nur Dauer
- [ ] Fußzeile je Variante, z. B.:
  `└─ ⏱ 4 h 49 min | Rest ≈ 12 T 17 h → Optimierung fertig ca. Do 03.12. 22:00, danach Feinprüfung bis zu ≈ 5 T`
- [ ] In der Zusammenfassung jeder Iteration eine Zeile:
  `Nächste Iteration fertig ca. Mo 23.11. 09:00`.
  Das ist genau die Angabe, die man braucht, um einen Lauf von Hand nach Iteration N zu stoppen.
- [ ] Ortszeit des Servers, Wochentag und Datum auf Deutsch.

### AP-4 `sim.ps1 status` zeigt die Schätzung
- [ ] `status` gibt zusätzlich die letzte Prognose-Zeile aus dem Log aus (die letzte Zeile mit „fertig ca.“), damit man nicht im Log suchen muss. Nur lesen, kein neuer Server-Befehl nötig.

### AP-5 Grenz-Hinweis schon je Iteration (Nebenbei, klein)
- [ ] `BoundaryHint` meldet heute **nur ganz am Ende** (`FinishOptimization`), also erst nach der Feinprüfung, und **gar nicht**, wenn der Lauf von Hand gestoppt wird.
- [ ] Zusätzlich in der Zusammenfassung jeder Iteration ausgeben, wenn der Champion an einer Grenze liegt (gleiche Klasse, gleiche 1-%-Regel, eine kurze Zeile). Die Grenzen werden weiterhin **nie** automatisch erweitert.

### AP-6 Tests und Doku
- [ ] Unit-Tests für `FormatDuration` (unter 1 h, Stunden, mehr als 1 Tag) und `RunTimeEstimator` (Fenster, gescheiterte Varianten, Nachprüfung je Iteration, Feinprüfung, AutoStop). Die Tests prüfen nur die Rechnung, keine Einstellungswerte der Projekte.
- [ ] `BEDIENHANDBUCH.md`: kurzer Absatz „Wie die Restzeit geschätzt wird“ (was drin ist, was nicht, warum die Feinprüfung eine Obergrenze ist).
- [ ] `dotnet test OptimierungsAlgoCFD.sln` grün.

---

## 3. Bewusst nicht im Umfang

- Eine Vorhersage aus Zellzahl × Iterationen je Variante. Die wäre genauer, die Zellzahl steht aber erst nach dem Vernetzen fest, und das gleitende Fenster reicht hier: Bayes bleibt nach den ersten Iterationen in einem Bereich mit ähnlich großen Netzen.
- Eine Prognose-Datei auf dem Server (z. B. in `run-info.json`). Erst, wenn AP-4 nicht reicht.

## 4. Fehler / Erkenntnisse

- (noch keine)
