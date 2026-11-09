# Automatisches Ende der Optimierung (AutoStop)

> Stand: 09.11.2026 — Auftrag des Nutzers, umgesetzt in derselben Session.

## Entscheidungen (mit dem Nutzer abgestimmt)

1. **Generationen/Iterationen und Varianten bleiben**, wie sie sind. Kein Umbau auf fortlaufende Simulationsnummern.
2. **Die Laufdauer wird weiter wie bisher angegeben** (`MaxIterations` × `VariantsPerIteration`). Sie ist mit
   AutoStop die **Obergrenze**; das automatische Ende kann den Lauf nur früher beenden, nie verlängern.
3. **Zwei Einstellungen plus Schalter:**
   ```jsonc
   "AutoStop": {
     "Mode": "Off",          // "On" | "Off" (Standard: Off, Verhalten exakt wie bisher)
     "MinImprovement": 0.01, // 0.01 = 1 %: Mindestverbesserung des Bestwerts ...
     "WindowVariants": 20    // ... innerhalb der letzten so vielen Varianten
   }
   ```
4. **Regel:** Nach jeder abgeschlossenen Iteration (nie mitten in einer Runde, damit Generationen vollständig bleiben):
   - `vorher` = bester Fitnesswert aller Varianten **vor** den letzten `WindowVariants` Varianten,
   - `jetzt` = bester Fitnesswert aller Varianten bis jetzt,
   - Verbesserung = (`jetzt` − `vorher`) / |`vorher`|.
   - Ist die Verbesserung kleiner als `MinImprovement`, endet die Optimierung. Danach laufen wie gewohnt die finale
     Feinprüfung und die Schlussmeldung.
5. **Kein Abbruch**, solange es noch kein „vorher“ gibt (weniger als `WindowVariants` + 1 Varianten) oder der Bestwert
   von „vorher“ praktisch 0 ist (relative Verbesserung nicht bestimmbar).
6. Fehlgeschlagene Varianten zählen im Fenster mit (sie haben Zeit gekostet), liefern aber keinen Bestwert.
7. Falsche Werte (unbekannter Mode, negative Schwelle, Fenster < 1) brechen den Lauf **vor** der ersten Rechnung ab.

## Hinweise für Nutzer (stehen auch im BEDIENHANDBUCH)

- `WindowVariants` mindestens so groß wählen wie der Versuchsplan am Anfang (Bayes: `max(Varianten, 2 × Parameter + 2)`),
  sonst kann der Lauf enden, bevor das Ersatzmodell richtig gearbeitet hat. Bewährt: 2 Iterationen (= 2 × `VariantsPerIteration`).
- `MinImprovement` nicht unter die Genauigkeit einer Einzelrechnung legen (Toleranzen der Netzkalibrierung/Feinprüfung).
- Die Restzeit-Schätzung im Log rechnet weiter mit der Obergrenze, ist also „höchstens“.
- Neue Einstellung ändert den Konfigurations-Fingerabdruck: Läufe, die mit einem älteren Stand gestartet wurden,
  lassen sich mit dem neuen Stand nicht fortsetzen (betrifft nur abgebrochene Altläufe).

## Schritte

- [x] `AutoStopConfig` in `Core/Models/SimulationConfig.cs` (`SimulationConfig.AutoStop`)
- [x] Reine Entscheidungslogik `Core/Pipeline/AutoStop.cs` (ohne Pipeline testbar)
- [x] Einbau in `WorkflowController.RunOptimization` (Prüfung beim Start, Entscheidung nach jeder Iteration, Log-Zeile)
- [x] Kommentierter Block in `simulation.json` von Naca2D, MantaAuv, `_Vorlage2D`, `_Vorlage3D` (überall `"Off"`)
- [x] Abschnitt im `src/projects/BEDIENHANDBUCH.md`
- [x] Unit-Tests (`tests/OptimierungsAlgoCFD.Tests/AutoStopTests.cs`) + Build + kompletter Testlauf

## Nachtrag 09.11.2026: Vorlagen standardmäßig an

- [x] `_Vorlage2D` und `_Vorlage3D`: `"Mode": "On"`, `MinImprovement` 0.01, `WindowVariants` 10 (= 2 Runden zu 5 Varianten;
  deckt den Bayes-Versuchsplan ab: 2D 6, 3D 8 Rechnungen; frühestes Ende nach Runde 3 von 5).
- [x] Code-Standard bleibt `"Off"`, damit bestehende Projekte (Naca2D, MantaAuv) unverändert laufen. Die Vorlagentests
  führen AutoStop deshalb als bewusste Abweichung (wie `MaxIterations`/`VariantsPerIteration`).

## Offen / später

- Auf dem Server einmal mit `"Mode": "On"` laufen lassen und prüfen, ob die Abbruchstelle plausibel ist.
