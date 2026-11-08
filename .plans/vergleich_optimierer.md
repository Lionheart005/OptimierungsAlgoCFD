# Vergleichslauf Rsm vs. Evolution vs. Bayes (AP 2.7 / TODO-60)

> **Stand: 08.11.2026**, Server BIC_12, Projekt `Naca2D`. Rohdaten lokal unter
> `Serverergebnisse/Vergleich_Rsm/`, `Vergleich_Evolution/`, `Vergleich_Bayes/` (erster Bayes-Lauf) und
> `Serverergebnisse/Vergleich_Seeds/` (vier weitere Läufe, jeweils mit `Simulation_Results.csv`, `run-info.json`, `simulation.log`).

## Kurzfazit

**Bei diesem kleinen Budget (5 × 5 = 25 Rechnungen, 4 Parameter) ist Bayes deutlich besser als Evolution, aber nicht besser als Rsm.**
Das belegt weder die Überlegenheit noch die Unterlegenheit des Bayes-Optimierers: 10 der 25 Rechnungen sind bei Bayes
noch Versuchsplan, und Rsm besteht bei so wenigen Rechnungen praktisch aus Zufallssuche, die in vier Dimensionen
mit 25 Punkten schon erstaunlich gut ist. Ob das Modell seinen Vorteil ausspielt, zeigt erst ein größeres Budget
(Empfehlung unten). **`Rsm` bleibt Standard.**

## Aufbau

| | |
| --- | --- |
| Projekt | `Naca2D` (4 freie Parameter: `MaxCamber`, `CamberPosition`, `MaxThickness`, `AngleOfAttackDeg`), Fitness = Gleitzahl CL/CD |
| Budget | 5 Iterationen × 5 Varianten = 25 Rechnungen je Lauf, Feinprüfung `Final` (nur Champion) |
| Netz | fest `MeshRefinementFactor = 2.0` (Ergebnis der Auto-Kalibrierung vom 08.11.2026), `MeshCalibration.Mode = Manual`. Alle Läufe haben damit **dieselbe Physik**; die absoluten Gleitzahlen sind wegen des groben Netzes höher als auf dem feinen Netz (Referenzlauf: Feinprüfung ca. 5–15 % darunter) und nur untereinander vergleichbar |
| Seeds | Seed 1 = `20261108`, Seed 2 = `424242`; je Seed alle Verfahren mit **demselben** Seed |
| Bayes | Standardwerte, Modus `Auto` → `Metrics` (Drag und Lift einzeln), 10 Startrechnungen (`max(5, 2·4+2)`) |
| Code-Stand | Bayes mit Batch-Abstoßung (siehe unten). Der erste Bayes-Lauf von Seed 1 (ohne) ist als „v1" mit aufgeführt |

## Ergebnis: beste Fitness nach n Rechnungen

**Seed 1 (20261108)**

| Verfahren | 5 | 10 | 15 | 20 | 25 | fehlgeschlagen | Rechenzeit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Rsm | 43,22 | 43,22 | 54,48 | 54,48 | **55,14** | 3 | 5,7 min |
| Evolution | 0,68 | 18,02 | 19,41 | 19,44 | 21,00 | 2 | 4,6 min |
| Bayes v1 (ohne Batch-Abstoßung) | 28,39 | 28,39 | 47,41 | 47,41 | 47,41 | 3 | 5,7 min |
| Bayes | 28,39 | 28,39 | 47,41 | 47,62 | 50,24 | 6 | 5,8 min |

**Seed 2 (424242)**

| Verfahren | 5 | 10 | 15 | 20 | 25 | fehlgeschlagen | Rechenzeit |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Rsm | 42,05 | 42,05 | 42,05 | 42,05 | 42,05 | 6 | 10,7 min |
| Evolution | 13,96 | 32,63 | 32,63 | 39,51 | **42,77** | 0 | 4,0 min |
| Bayes | 20,95 | 39,55 | 42,27 | 42,99 | **42,99** | 6 | 5,8 min |

**Mittel über beide Seeds**

| Verfahren | 5 | 10 | 15 | 20 | 25 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Rsm | 42,64 | 42,64 | 48,26 | 48,26 | **48,59** |
| Bayes | 24,67 | 33,97 | 44,84 | 45,30 | 46,62 |
| Evolution | 7,32 | 25,33 | 26,02 | 29,48 | 31,89 |

## Einordnung

* **Bayes schlägt Evolution klar** (46,6 gegen 31,9 im Mittel). Evolution startet beim NACA 0012 mit kleiner
  Streuung und kommt in 25 Rechnungen kaum aus dessen Umgebung heraus.
* **Rsm liegt im Mittel knapp vor Bayes** (48,6 gegen 46,6), bei Seed 2 liegen alle drei innerhalb von 2 %
  (42,05 / 42,77 / 42,99). Bei zwei Seeds und 25 Rechnungen ist das ein Unterschied im Rauschen. Rsm hat bei 4
  Parametern erst ab 17 gelungenen Rechnungen ein Modell (`((k+1)(k+2))/2 + 2`); bis dahin und in diesem Budget
  ist es im Kern Zufallssuche, und die ist bei 4 Dimensionen und 25 Punkten nicht schlecht.
* **Das Modell arbeitet wie vorgesehen.** In `Bayes_Verlauf.csv` stehen für beide Seeds `AngleOfAttackDeg` als wichtigster
  Parameter (Relevanz 1,0, die anderen unter 0,3), was physikalisch stimmt (Anstellwinkel dominiert die Gleitzahl).
  Die Treffsicherheit steigt mit den Daten: Anteil der Varianten im 95-%-Band bei Seed 1 von 0,25 über 1,0, bei Seed 2
  von 0,50 über 1,0; mittlere Abweichung zwischen Vorhersage und Ergebnis von 77 % auf 22 % bzw. von 23 % auf 16 %.
* **Der Modellaufwand fällt nicht ins Gewicht:** Bayes braucht 5,8 min gegen 5,7 min bei Rsm (Seed 1). Die Zeit
  steckt in den CFD-Rechnungen.
* **Erster Bayes-Lauf (v1) fand Schwächen, die behoben wurden:** (1) Vier Vorschläge einer Runde lagen fast aufeinander
  (Camber 8 / Lage 75 / Dicke 6, Anstellwinkel 3,2° bis 5,1°). Seither stoßen sich die Vorschläge einer Runde ab
  (`ki_optimierung.md`, Abschnitt „Umsetzungsstand KI-Teil"). (2) Das Modell blieb in einer Ecke des Suchraums
  (Lage 75 %, Dicke 6 %) hängen, weil es mit 12 bis 17 Punkten in vier Dimensionen `CamberPosition` und `MaxThickness`
  für fast wirkungslos hielt. Rsm fand das bessere Gebiet (Lage ca. 44 %, Dicke ca. 9 bis 10 %). Das ist eine Folge der wenigen Daten, kein Fehler;
  bei mehr Daten korrigiert sich das Modell.

## Einschränkungen

* **Nur zwei Seeds, 25 Rechnungen, 4 Parameter.** Streuung zwischen Seeds ist größer als der Abstand zwischen Rsm und Bayes
  (auf synthetischen Testfunktionen genauso, siehe Kommentare in `BayesOptimizationAlgorithmTests.cs`).
* **Grobes Netz (Faktor 2,0).** Die Landschaft hat Abrisskanten (Anstellwinkel über etwa 12 bis 15°, instationäre Strömung,
  bis zu 6 von 25 Rechnungen als fehlgeschlagen gewertet); das erschwert Modelle, die glatte Funktionen annehmen.
* **Naca2D ist der leichte Fall.** Es rechnet je Variante nur Sekunden und hat 4 Parameter. Der Nutzen eines Ersatzmodells
  wächst mit der Rechenzeit je Variante und der Parameterzahl; der eigentliche Einsatzfall ist `MantaAuv`
  (5 Parameter, 3D, teure Rechnung, Fitness mit Sprungstelle, Modus `Metrics`). Der ist hier **nicht** gemessen.

## Empfehlung und offene Entscheidung

1. **Standard bleibt `Rsm`.** Dieser Lauf rechtfertigt keinen Wechsel.
2. **Aussagekräftiger Vergleich:** dasselbe auf `Naca2D` mit **10 × 10** (100 Rechnungen, je Verfahren etwa 15 bis 25 min bei
   Faktor 2,0) und drei Seeds. Dann hat Bayes 90 modellgeführte Rechnungen gegenüber 10, und die Auswertung
   zeigt, ob das Modell die Landschaft besser nutzt. Aufwand etwa 2 bis 3 Stunden Serverzeit.
3. **Der echte Test ist `MantaAuv` mit `"Bayes"`** (Modus `Metrics`). Wird `MantaAuv` dauerhaft auf `Bayes` umgestellt, muss der Test
   `Repository_Simulation_Json_Selects_Rsm` (`OptimizationAlgorithmFactoryTests.cs`) bewusst angepasst werden.
4. **Entscheidung für dich:** `DefaultAlgorithm` (und ggf. MantaAuv) erst nach Punkt 2 oder 3 ändern.

## Praxisfunde beim Servertest

* `fetch` holte `Bayes_Verlauf.csv` nicht mit. Ist behoben (`std_items` in `scripts/sim-runner.sh`).
* Ein neuer Lauf leert `Analyseergebnisse/Verlaeufe/` nicht: die `History_*`-Dateien früherer Läufe bleiben liegen und werden mit geholt
  (im Ordner liegen so auch Dateien des 10×10-Referenzlaufs). Das war schon vorher so und ist nicht behoben; wer die Verläufe
  eines Laufs auswertet, sollte nur Dateien nehmen, die zu `Simulation_Results.csv` passen.
* `sim.ps1 run` meldet nach dem Start eine `tmux`-Bibliothekswarnung als `FEHLER:`, obwohl der Lauf startet. Das ist ein
  Anzeigeproblem der Meldung, kein Fehler des Laufs.
