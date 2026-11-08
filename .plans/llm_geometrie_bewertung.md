# AP 2.8: Architektur-Bewertung für LLM-gestützte Geometriegenerierung

> **Stand: 08.11.2026.** Reine Bewertung, **kein Umsetzungsauftrag**. Ausgangsfrage aus
> `Anmerkungen.txt` (Z. 36): Ist ein „physics-informed" LLM machbar, das C#-Code für `Planar2D` bzw.
> PicoGK schreibt, im Stil von Leap71? Dieses Dokument beantwortet das für **dieses** Framework und
> ersetzt die kurze Notiz „Idee 5" in `ki_optimierung.md`.

---

## 1. Kurzantwort

**Machbar, sinnvoll als äußere, langsame Schleife mit Mensch-Freigabe. Nicht als Ersatz für den
Optimierer.**

| Aufgabe | Wer ist am besten geeignet | Warum |
| --- | --- | --- |
| **Zahlen feinabstimmen** (Länge, Radius, Anstellwinkel …) | Bayes-Optimierer (Meilenstein 2) | Braucht wenige Rechnungen, kennt seine Unsicherheit, ist reproduzierbar und rechnet in Sekunden. Ein LLM wäre hier teurer, langsamer und schlechter. |
| **Struktur ändern** (neue Flosse, Teil weglassen, anderer Querschnittsverlauf, neuer Parameter) | LLM als „Geometrie-Entwickler" | Das ist Programmieren, nicht Zahlensuchen. Die Parametrisierung ist die Grenze jedes Optimierers: Er findet nur, was der Generator ausdrücken kann. |
| **Physik rechnen** | SU2 (später CalculiX) | Das LLM *erfindet keine Physik*. „Physics-informed" heißt hier: Das LLM bekommt die **gemessenen** Ergebnisse (Kennwerte, Parameter-Wichtigkeit) als Rückmeldung, nicht, dass es Strömung im Kopf rechnet. |
| **Bewertung festlegen** | Der Mensch | Siehe Abschnitt 4: Fitness und Entwurfsvorgabe darf das LLM nie ändern. |

---

## 2. Warum das gut zur bestehenden Architektur passt

Fast alles, was eine LLM-Runde braucht, gibt es schon:

| Baustein | Vorhanden | Rolle in der LLM-Runde |
| --- | --- | --- |
| Ein Projekt ist ein **Ordner** (`src/projects/<Name>/`), den die `ProjectRegistry` von selbst findet | ja | Jede Runde ist ein **neuer Projektordner** (`MantaAuv_E02`). Nichts Laufendes wird verändert, und alte Runden bleiben vergleichbar. |
| `new-project.ps1` kopiert eine Vorlage und ersetzt den Namen | ja | Startet die Runde: Kopie des besten bisherigen Projekts. |
| Geometrie = eine Klasse hinter `IGeometryGenerator`, Zahlen in `project.json` | ja | Der Änderungsumfang des LLM ist klein und klar abgegrenzt: **Generator und `project.json` des neuen Ordners**. |
| `dotnet build` und `dotnet test` (637 Tests, u. a. `TemplateProjectTests`, `ProjectsOnDiskTests`) | ja | Erstes, automatisches Prüftor: Kompiliert es? Hält es die Projektregeln ein? |
| Fail-fast bei fehlenden Grenzen und Metriken (`ParameterSpace`, `EnsureRequiredMetrics`) | ja | Fängt kaputte Parametrisierungen **vor** der ersten teuren Rechnung ab. |
| Bayes-Optimierer mit Parameter-Wichtigkeit (`Bayes_Verlauf.csv`) | ja (neu) | Lernt eine **neue Parametrisierung in Sekunden von null** und liefert dem LLM zugleich die Rückmeldung, welche Parameter überhaupt wirken. |
| Fahrtenschreiber `run-info.json` und `Quellstand/` | ja | Jede Runde ist später nachvollziehbar: Welcher Code hat welches Ergebnis erzeugt? |

---

## 3. Vorgeschlagene Architektur

```text
 Mensch                      LLM-Agent                      Deterministische Prüftore
 ──────                      ─────────                      ─────────────────────────
 Entwurfsvorgabe  ─────────► liest Vorgabe, letzte Ergebnisse
 (fix, nur Mensch            (Simulation_Results.csv,
  ändert sie)                 Bayes_Verlauf.csv) und den
                              aktuellen Generator
                                     │
                                     ▼
                              legt Projektordner <Name>_E02
                              an (new-project.ps1) und
                              ändert Generator + project.json
                                     │
                                     ▼
                                                            Tor 1: dotnet build, dotnet test
                                                            Tor 2: Geometrie-Rauchtest (siehe unten)
                                                            Tor 3: Hash-Prüfung: Fitness und
                                                                   Entwurfsvorgabe unverändert
                                     │
                                     ▼
 Freigabe  ◄──────────────── Zusammenfassung: Was wurde
 (Mensch, jede Runde)         geändert, warum, Prüfergebnisse
        │
        ▼
 kurzer Bayes-Lauf auf dem Server (sim.ps1 run -Project <Name>_E02)
        │
        ▼
 Ergebnisse ──► nächste Runde (oder Abbruch)
```

**Die Schleife ist langsam und eng geführt:** wenige Runden, jede mit Freigabe. Das LLM ändert die
**Struktur**, der Bayes-Optimierer die **Zahlen**, SU2 rechnet die **Physik**.

### Tor 2: der Geometrie-Rauchtest

Billig, ohne Strömungsrechnung, in Sekunden. Er prüft, was ein LLM-Entwurf typischerweise falsch macht:

| Prüfung | Wie | Vorhanden |
| --- | --- | --- |
| Geometrie entsteht überhaupt, ohne Ausnahme, für Startwert **und** die Grenzwerte jedes Parameters | Generator mit Min-/Max-/Mittelwerten aufrufen | `IGeometryGenerator`, `ParameterBounds` |
| Körper ist **dicht** (wasserdicht, eine zusammenhängende Hülle) | Netzprüfung auf offene Kanten; in 2D: Kontur geschlossen, ohne Selbstüberschneidung | teilweise (`MeshMetrics`, `MeshMetrics2D`, Knickpunkt-Schutz B14 in 2D) |
| **Volumen bzw. Fläche im erlaubten Bereich** | Kennwerte aus dem Generator gegen `OptimizationTargets` | ja (Kennwerte), Schwellen je Projekt |
| **Mindestwandstärke** | Voxel: Erosion/Offset und Rest prüfen; 2D: Abstand der Konturen | neu (PicoGK-`Offset` existiert) |
| **Keine zu scharfe Krümmung** (Netzbarkeit, Knicke) | Konturkrümmung gegen Grenze; in 2D besteht der Knickschutz bereits | teilweise |
| **Die Parameter wirken wirklich** | Jeden Parameter um 5 % ändern, ein Kennwert (Fläche/Volumen) muss sich ändern. Sonst ist der Parameter tot, und der Bayes-Optimierer würde Rechenzeit daran verschwenden. | neu, einfach |

Der Rauchtest ist **auch ohne LLM nützlich**: Er prüft jede neue Parametrisierung, die ein Mensch
schreibt, bevor ein Lauf stundenlang rechnet.

---

## 4. Leitplanken (nicht verhandelbar)

1. **Das LLM darf die Bewertung nicht ändern.** Fitness-Formel (`*FitnessCalculator.cs`),
   `OptimizationTargets` und die Entwurfsvorgabe sind für den Agenten schreibgeschützt. Sonst optimiert
   es die Note statt des Bauteils (Reward Hacking, siehe `src/projects/README.md`). Technisch: Hash-Prüfung
   dieser Dateien als Tor 3, die Runde wird sonst verworfen.
2. **Jede Runde gibt der Mensch frei.** Keine autonomen Nachtläufe mit API-Anbindung, kein Programm,
   das sich zur Laufzeit selbst umschreibt (so auch in `ki_optimierung.md` festgelegt).
3. **Nur neue Projektordner, nie Änderungen an laufenden.** Alte Runden bleiben unverändert und
   vergleichbar; `Quellstand/` hält fest, was gerechnet wurde.
4. **Vergleichbarkeit:** Runden werden nur verglichen, wenn Fitness-Formel, Physik-Einstellungen und
   Netzstufe gleich sind (die Trainingsdaten-Regel F13 und `MeshRefinementFactor` stützen das).
5. **Ein LLM-Entwurf ist unbewiesen, bis er gerechnet und gegengeprüft ist.** Der Champion einer
   LLM-Runde durchläuft wie jeder andere die Feinprüfung und den Türsteher.

---

## 5. Zwei Wege, den Code zu übersetzen

| | **A: Projektordner + `dotnet build`** (empfohlen) | **B: Roslyn zur Laufzeit** (`Microsoft.CodeAnalysis.CSharp`) |
| --- | --- | --- |
| Idee | LLM schreibt Dateien in einen Projektordner, das normale Build- und Testsystem prüft sie. | Das Programm übersetzt Quelltext im Speicher und lädt ihn. |
| Aufwand | **fast keiner**, alles existiert | neue Abhängigkeit (mehrere MB NuGet), eigener Lade- und Fehlercode |
| Nachvollziehbarkeit | Dateien, Git, `Quellstand/` | Quelltext müsste extra archiviert werden |
| Sicherheit | Läuft nur, was der Mensch freigibt. Gefährlicher Code wird im Review sichtbar. | .NET kann geladenen Code **nicht** sicher einsperren (`AssemblyLoadContext` isoliert Versionen, nicht Rechte). Echte Isolation bräuchte einen eigenen Prozess mit eingeschränkten Rechten. |
| Passt zum Stil des Projekts | ja (R3: keine neue Abhängigkeit, Ordnername = Projektname) | nur bei vollautonomem Betrieb nötig, der ausdrücklich nicht gewollt ist |

**Empfehlung: Weg A.** Roslyn lohnt erst, wenn ein vollautonomer Betrieb gewollt wäre. Dann aber
nur in einem eigenen Prozess mit eingeschränkten Rechten und mit denselben Prüftoren.

---

## 6. Einordnung gegenüber der klassischen parametrischen Optimierung

* **Für topologische und strukturelle Neukonzeption sehr attraktiv.** Ein Mensch müsste jede neue
  Parametrisierung selbst programmieren; das LLM kann Varianten davon in Minuten entwerfen.
* **Für numerische Feinoptimierung ist der Bayes-Ansatz klar überlegen:** reproduzierbar, mit
  Unsicherheitsmaß, mit gemessener Effizienz (siehe `vergleich_optimierer.md`).
* **Das Ergebnis eines LLM-Entwurfs ist die *Parametrisierung*, nicht die Lösung.** Erst der
  Bayes-Lauf auf dieser Parametrisierung sagt, ob sie besser ist als die vorige. Zwei Parametrisierungen
  sind nur dann fair verglichen, wenn beide mit demselben Budget optimiert wurden.
* **Die Rückmeldung an das LLM ist der eigentliche „Physics-informed"-Teil:** `Simulation_Results.csv`
  (Kennwerte je Variante), `Bayes_Verlauf.csv` (welche Parameter wirken, wie gut das Modell trifft)
  und die Plots aus `scripts/`. Mit diesen Daten kann es begründen, *warum* es eine Struktur ändert.

**Zu Leap71:** Deren Ansatz (Formfindung als Code, mit PicoGK als Kernel) ist die Grundlage dieses
Frameworks. Wie dort LLMs eingesetzt werden, wurde hier nicht geprüft; diese Bewertung stützt sich
allein auf die Architektur dieses Repositories.

---

## 7. Risiken

| Risiko | Gegenmittel |
| --- | --- |
| LLM erzeugt Geometrie, die kompiliert, aber unsinnig oder undicht ist. | Tor 2 (Rauchtest), vor jeder Rechnung. |
| Tote oder redundante Parameter blähen den Suchraum auf. | Rauchtest „Parameter wirkt", Parameter-Wichtigkeit aus `Bayes_Verlauf.csv` als Rückmeldung. |
| Reward Hacking (die Bewertung wird „umgangen"). | Leitplanke 1 und Hash-Prüfung. Harte Grenzen in `OptimizationTargets`, die der Mensch setzt. |
| Zu viele Runden, Kosten und Rechenzeit laufen weg. | Jede Runde braucht Freigabe; kurzer Bayes-Lauf mit festem Budget; Abbruchregel („nach 3 Runden ohne Verbesserung"). |
| Schlechte Vergleichbarkeit zwischen Runden. | Gleiche Fitness, gleiche Physik, gleiches Budget; `Quellstand/` und `run-info.json` je Runde. |
| Ein LLM-Entwurf überfordert den Netzgenerator (dünne Stellen, Knicke). | Mindestwandstärke und Krümmung im Rauchtest; fehlgeschlagene Netze meidet der Bayes-Optimierer ohnehin (Abstoßung). |

---

## 8. Stufenplan (falls es gewollt ist)

| Stufe | Inhalt | Aufwand | Nutzen auch ohne LLM |
| --- | --- | --- | --- |
| **0** | Manuell: Claude Code (oder ein anderes LLM) ändert in einer Sitzung einen Projektordner, der Mensch prüft und startet den Lauf. | keiner | — |
| **1** | **Geometrie-Rauchtest** als Befehl (`--smoke-test <Projekt>`): Generator an Min/Mitte/Max, Dichtheit, Volumen im Bereich, „Parameter wirkt". | klein bis mittel (reines C#) | **ja**: prüft jede neue Parametrisierung |
| **2** | Ein Claude-Code-Skill `/entwurfsrunde` führt die Schleife aus Abschnitt 3 mit Freigabe je Runde und Hash-Prüfung durch. | klein (ein Skill, keine neue Software) | — |
| **3** | Vollautonomer Betrieb mit Roslyn/API. | groß | **nicht empfohlen** |

**Empfehlung:** Stufe 1 lohnt sich unabhängig davon. Ob Stufe 2 folgt, hängt davon ab, ob die
Parametrisierung wirklich der Engpass ist. Das zeigt der Vergleichslauf in `vergleich_optimierer.md`
mit der Parameter-Wichtigkeit (viele wirkungslose Parameter oder eine stark verfehlte Form wären
Hinweise).

---

## 9. Nicht Teil dieser Bewertung

Eine API-Anbindung, ein Training oder Fine-Tuning eines eigenen Modells, ein PINN als Ersatz für SU2
(die Andockstellen dafür stehen in `ki_optimierung.md`, Abschnitt „PINN eines Partners") und jede
Änderung am Framework. Die Bewertung hat nichts am Code verändert.
