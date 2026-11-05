# KI-gestützte Optimierung: Bayes-Optimierer mit Ersatzmodellen

> **Stand: 05.11.2026.** Nachfolgeplan zu `anmerkungen_umsetzung.md` (letzter Schritt dort: TODO-49).
> Quelle ist der Block „Erweiterungen → KI" aus `.plans/Anmerkungen.txt` und die Beratung vom
> 05.11.2026. Die Nummerierung geht hier mit **TODO-50** weiter, Entscheidungen mit **E13**,
> Festlegungen mit **F9**.
>
> **Status:** Noch nichts umgesetzt. FEM ist zurückgestellt, siehe `.plans/fem_ausblick.md`.
>
> **Branch:** Vorschlag ist ein neuer Branch `KiOptimierung`, abgezweigt, sobald `MajorPhysikFixes`
> auf dem Server geprüft ist. Die Code-Schritte TODO-50 bis TODO-56 brauchen **keinen Server**,
> alles wird mit Tests geprüft. Nur TODO-57 ist ein Lauf auf dem Server.

---

## Fortschritt (für die Übergabe)

`[x]` erledigt, `[~]` teilweise erledigt (der Rest steht im jeweiligen Abschnitt), `[ ]` offen.

| Block | Schritt | Inhalt | Wo | Status |
| --- | --- | --- | --- | --- |
| G | TODO-50 | Markierung „disqualifiziert", Parameterraum, Regel für Trainingsdaten | lokal | [ ] |
| G | TODO-51 | Mathematischer Kern: Gauß-Prozess, Cholesky, Latin Hypercube | lokal | [ ] |
| H | TODO-52 | `BayesOptimizationAlgorithm`, Modus „Fitness" (**Idee 1**) | lokal | [ ] |
| H | TODO-53 | Modus „Metrics": ein Ersatzmodell je Kennwert (**Idee 2**) | lokal | [ ] |
| H | TODO-54 | Viele Parameter, lange Läufe: Vertrauensbereich und Deckel für die Lernmenge | lokal | [ ] |
| H | TODO-55 | Transparenz: Vorhersage gegen Ergebnis, Modellgüte, Parameter-Wichtigkeit | lokal | [ ] |
| I | TODO-56 | Bedienhandbuch, READMEs, Kommentarköpfe der JSON-Dateien | lokal | [ ] |
| I | TODO-57 | Vergleichslauf Rsm, Evolution und Bayes auf dem Server | **Server (du)** | [ ] |

**Verifikationsstand vor Beginn:** laut `anmerkungen_umsetzung.md` 443/443 Tests grün,
0 Warnungen. Vor TODO-50 einmal `dotnet test Automatisierung.sln` laufen lassen und die Zahl hier
eintragen.

---

## Ziel

**Bis zu einem guten Ergebnis sollen deutlich weniger teure CFD-Rechnungen nötig sein.** Dabei gilt:

| # | Anforderung (von dir) | Was das konkret heißt |
| --- | --- | --- |
| R1 | Die Optimierung bleibt problemunabhängig. | Der Optimierer sieht nur Parameternamen, Grenzen, Kennwerte und Fitness. Er kennt keine Physik und kein Projekt, wie heute. |
| R2 | Kein Servercluster, keine GPU | Das Modell lernt auf der CPU in Sekunden, in jedem Lauf neu, ohne Vortraining. |
| R3 | Alles in C# | Keine Python- oder ML-Bibliothek und kein neues NuGet-Paket. |
| R4 | 5–20 Parameter, nach oben offen | Bis etwa 10 Parameter arbeitet das Modell global. Darüber schaltet sich automatisch ein Vertrauensbereich zu (TODO-54). |
| R5 | Bedienbar ohne IT-/KI-Kenntnisse | Ein Schalter: `"OptimizationAlgorithm": "Bayes"`. Alles andere hat Vorgaben, die meist passen. |
| R6 | Alles bleibt, wie es ist | `Rsm` bleibt Standard. `Rsm` und `Evolution` rechnen **unverändert**, und jedes Projekt läuft ohne Änderung weiter. |

Die physikalischen Korrekturen aus dem letzten Plan werden die einzelne CFD-Rechnung voraussichtlich
deutlich verlängern: von „ein paar Minuten" auf ein Vielfaches. Gemessen ist das noch nicht. Gerade
deshalb lohnt der Bayes-Optimierer: Jede eingesparte Variante spart dann richtig Zeit.

---

## Die Idee in Alltagssprache

**Idee 1: eine Landkarte mit Nebel.** Das bisherige RSM baut aus den gerechneten Varianten eine
„Landkarte" der Fitness und sucht darauf. Diese Karte ist ein gewichteter Mittelwert der bekannten
Punkte. Sie kann deshalb **nie einen Ort vorhersagen, der besser ist als der beste schon gerechnete**,
und sie weiß nicht, wo sie sich nicht auskennt. Der Gauß-Prozess (auch „Kriging") zeichnet zusätzlich
**Nebel** ein, nämlich die Unsicherheit. Die Auswahlregel *Expected Improvement* fragt dann:
„Wo ist die Chance am größten, den bisherigen Besten zu schlagen?" Das kann ein Ort nahe am Besten
sein (verfeinern) oder ein Ort im dichten Nebel (erkunden). Diese Abwägung trifft das Verfahren
selbst und rechnerisch begründet, nicht über feste Prozentsätze wie `RsmExploitationRatio`.

**Idee 2: Kennwerte einzeln schätzen statt die Fitness als Ganzes.** Die Manta-Fitness ist
`Volume × SensorDistance / Drag³`, und außerhalb des Volumenbereichs wird sie mal 0,1 genommen. Sie
springt also. Sprünge kann kein Ersatzmodell gut lernen. `Drag`, `Volume` und `SensorDistance` für
sich verlaufen dagegen glatt. Deshalb bekommt jeder Kennwert sein eigenes Modell. Für einen Kandidaten
werden die Kennwerte geschätzt, und daraus rechnet **deine eigene Fitness-Formel des Projekts** die
Fitness. Strafen und Grenzen wirken damit automatisch richtig. Der Kern muss die Formel dafür nicht
kennen.

**Was gleich bleibt:** Jeder Vorschlag wird echt gerechnet, der Türsteher (Bouncer) prüft weiter, und
die Feinprüfung der Gewinner (TODO-43) bleibt. Das Modell entscheidet nur, **welche** Varianten
gerechnet werden, und ersetzt keine einzige echte Rechnung im Ergebnis.

---

## Ausgangslage (geprüft am 05.11.2026)

| # | Befund | Beleg | Bedeutung |
| --- | --- | --- | --- |
| 1 | **Das RSM ist ein gewichteter Mittelwert (IDW).** Es kann nichts Besseres vorhersagen als den besten Stützpunkt, und es hat kein Maß für Unsicherheit. | `Core/Algorithms/RsmOptimizationAlgorithm.cs:129-162` | Das ist der Kern von Idee 1. |
| 2 | **Das RSM übernimmt Rauschen als Wahrheit.** Liegt ein Kandidat auf einem Stützpunkt, gilt dessen Fitness exakt. | `RsmOptimizationAlgorithm.cs:150` | CFD-Ergebnisse rauschen (Netz, Konvergenz). Ein GP mit gelerntem Rauschterm glättet das. |
| 3 | **Disqualifizierte Varianten tragen nur die Fitness 0,0001, ohne Kennzeichen.** | `Core/Pipeline/ChampionValidator.cs:80`, `:85` | Ein lernendes Modell hielte eine gute, aber wackelige Variante für schlecht. Es braucht eine Markierung (TODO-50). Das RSM nutzt die Werte heute mit (`RsmOptimizationAlgorithm.cs:80`). Das bleibt so (R6). |
| 4 | **Die Fitness-Formeln haben Sprünge.** | `src/projects/MantaAuv/MantaFitnessCalculator.cs:105` (×0,1-Strafe) | Das ist der Grund für Idee 2. |
| 5 | **Jedes Projekt deklariert schon, welche Kennwerte seine Fitness braucht.** Manta: `Drag`, `Volume`, `SensorDistance`. Naca2D: `Drag`, `Lift`. | `Core/Interfaces/IFitnessCalculator.cs:31` (`RequiredMetrics`) | Genau diese Liste braucht Idee 2. **Kein Projekt muss Code ändern.** |
| 6 | **Fitness-Rechner schreiben auch in den Datensatz.** | `src/projects/Naca2D/Naca2DFitnessCalculator.cs:84` (`GlideRatio`) | Geschätzte Kandidaten bekommen deshalb Wegwerf-Datensätze, die nie in die History kommen. |
| 7 | **Die History ist ein sauberer Lerndatensatz auf einer Netzstufe.** Feinprüfungen ändern weder Auswahl noch History (Regel F7). Kontrollrechnungen laufen als Variante 99 ohne History-Eintrag. Jeder Datensatz trägt seine Netzstufe. | `ChampionFineVerifier.cs:76`, `WorkflowController.cs:15`, `:104`, `:273` (`MeshRefinementFactor`) | Die Netzstufe je Datensatz ist auch die spätere Andockstelle für Idee 3. |
| 8 | **Die Schnittstelle passt ohne Umbau.** Das RSM nutzt schon das Muster „Vorschläge in `PrepareNextIteration` vorausberechnen, in `GenerateParameters` ausgeben". | `Core/Interfaces/IOptimizationAlgorithm.cs`, `RsmOptimizationAlgorithm.cs` (`_nextVirtualCandidates`) | Der `WorkflowController` bleibt unverändert. |
| 9 | **`EvaluateAndSelectBest` wird auch für einzelne Feinprüf-Datensätze aufgerufen.** | `WorkflowController.cs`, die Lambdas an `ChampionFineVerifier.VerifyIterationWinner` / `VerifyFinal` | Diese Methode darf außer der Fitness keinen Zustand ändern. |
| 10 | **Verschachtelte Konfigurationsabschnitte werden streng geprüft**, und das Handbuch muss jeden Schlüssel erklären. | `Core/Configuration/ConfigSchema.cs` (rekursiv), `tests/.../HandbookCoverageTests.cs:41` (`SettingTypes`) | Der neue Abschnitt `Bayes` braucht Handbucheinträge, und sein Typ muss in `SettingTypes` stehen. |
| 11 | **Ein Test verlangt, dass MantaAuv `Rsm` nutzt.** | `tests/.../OptimizationAlgorithmFactoryTests.cs:67` | Wird Manta später umgestellt, muss dieser Test bewusst mitgeändert werden. |
| 12 | **Es gibt keine Bibliothek für lineare Algebra im Projekt.** | `src/Automatisierung_v2/Automatisierung_v2.csproj` (nur Clipper2, PicoGK) | Siehe F10. |
| 13 | **Naca2D hat 3 Parameter, rechnet in 2D und braucht kein PicoGK.** | `src/projects/Naca2D/project.json:16-20` | Ideal als schneller Vergleichslauf (TODO-57). |

---

## Entscheidungen

### Mit dir abgestimmt (05.11.2026)

| # | Frage | Entscheidung |
| --- | --- | --- |
| **E13** | FEM jetzt? | **Nein, zurückgestellt.** Was aufgefallen ist und worauf man achten sollte, steht in `.plans/fem_ausblick.md`. |
| **E14** | Welche KI-Ideen werden umgesetzt? | **Idee 1** (Bayes-Optimierer) und **Idee 2** (Ersatzmodell je Kennwert), konkret in diesem Plan. |
| **E15** | Idee 3, mehrstufige Genauigkeit | **Nur als Gedankenspiel**, siehe „Ausblick". Kein TODO. |
| **E16** | Idee 5, LLM schreibt den Geometriecode | **Nur als Notiz**, siehe „Ausblick". Kein TODO. Eine LLM-Anbindung gibt es vorerst nicht. |
| **E17** | PINN | **Ganz raus.** Nur beschrieben, wo das Modell eines Partners später andocken könnte. |
| **E18** | Sprache | **Alles in C#**, solange es keine großen Probleme macht. |
| **E19** | Parameterzahl | **5–20, nach oben offen.** |
| **E20** | Kleine Architekturfragen | **Entscheidet der Planer**, siehe Festlegungen. Leitlinie: flexibel, robust und vielfältig wie heute. |

Zur Rechenzeit einer CFD-Variante: bisher wenige Minuten, nach den Physik-Korrekturen noch nicht
gemessen. Die Planung geht deshalb von **teuren** Rechnungen aus.

### Eigene Festlegungen (Architektur)

| # | Festlegung | Begründung |
| --- | --- | --- |
| **F9** | Neuer Name `"Bayes"` in `OptimizationAlgorithm`. **Standard bleibt `"Rsm"`.** `Rsm` und `Evolution` werden nicht angefasst. Einzige Ausnahme ist die neue CSV-Spalte aus TODO-50, die rein informativ ist. | R6. Wer nichts ändert, merkt nichts. Ob `Bayes` Standard wird, entscheidest du nach TODO-57. |
| **F10** | **Eigene kleine lineare Algebra** (Cholesky-Zerlegung, Dreieckslöser, etwa 100 Zeilen) statt eines NuGet-Pakets | Mehr braucht ein GP nicht. Es kommt keine neue Abhängigkeit auf den Server, und alles ist vollständig getestet und lesbar. MathNet.Numerics bliebe der Ausweg, falls später mehr nötig wird. |
| **F11** | Schnittstelle **`ISurrogateModel`** in `Core/Surrogates/`. Der Gauß-Prozess ist die erste Umsetzung, und der Optimierer kennt nur die Schnittstelle. | Andere Modelle lassen sich einstecken, auch das eines Partners (siehe Ausblick), ohne den Optimierer anzufassen. |
| **F12** | **Lernen pro Lauf aus der eigenen History.** Kein Vortraining, kein Warmstart aus alten Läufen. | R1 und R2. Die alten Ergebnisse stammen außerdem aus der ungenaueren Physik und würden das Modell vergiften. |
| **F13** | **Eine zentrale Regel für Trainingsdaten** (`SurrogateTrainingSet`): nicht fehlgeschlagen, nicht disqualifiziert, endliche Werte, dieselbe Netzstufe wie der Lauf | Eine Stelle statt verstreuter Filter. Die Netzstufe ist die Andockstelle für Idee 3. |
| **F14** | **Parameterraum normiert auf [0, 1]** über `ParameterBounds`. Eine fehlende Grenze bricht **vor der ersten Rechnung** mit klarer Meldung ab. `Min == Max` gilt als fester Parameter und wird nicht modelliert. `MaxDeviations` wirkt bei `Bayes` nicht. | Das ist die Fail-fast-Linie des Projekts (TODO-16, TODO-26). Feste Parameter machen das Modell sonst numerisch instabil. |
| **F15** | **Modell:** GP mit Matérn-5/2-Kern, **eine Längenskala je Parameter** (ARD) und gelerntem Rauschen. Die Hyperparameter werden über die Log-Marginal-Likelihood mit analytischem Gradienten bestimmt (Adam, mehrere Starts). | Das ist der Standard in der Bayes-Optimierung. Die Längenskalen liefern nebenbei die **Parameter-Wichtigkeit** (TODO-55). |
| **F16** | **Auswahlregel Expected Improvement.** Im Modus „Metrics" wird sie per Monte-Carlo über die Projekt-Fitness berechnet. | EI hat keine Stellschraube, die ein Nutzer verstehen müsste. |
| **F17** | **Mehrere Vorschläge je Iteration:** `VariantsPerIteration` Stück per *Kriging Believer*. Ab 4 Varianten ist einer davon ein reiner **Erkunder** (größte Unsicherheit, im ganzen Raum). Iteration 1, Variante 1 = Startwerte, wie bei Rsm und Evolution. | Der Erkunder ist eine billige Versicherung gegen ein selbstsicheres, falsches Modell. Er entspricht der „Entdecker"-Rolle des evolutionären Algorithmus. |
| **F18** | **Die Fitness des Projekts bleibt die einzige Wahrheit.** Im Modus „Metrics" ruft der Optimierer den `IFitnessCalculator` des Projekts mit Wegwerf-Datensätzen auf. Der Kern kennt keine Formel und keinen Metriknamen. | R1. Neue Projekte bekommen Idee 2 ohne eine Zeile zusätzlichen Code. |
| **F19** | **Zustand ändert sich nur in `PrepareNextIteration`.** `EvaluateAndSelectBest` setzt nur Fitness und Vorhersagespalten. | Befund 9. |
| **F20** | Intern wird in `double` gerechnet, an der Schnittstelle bleibt es `float`. Der Zufall ist injizierbar (`Random? random = null`), wie bei Rsm und Evolution (TODO-19). | Numerische Stabilität. Reproduzierbare Tests. |
| **F21** | **Viele Parameter:** Ab 10 frei veränderlichen Parametern schaltet sich automatisch ein **Vertrauensbereich** zu, und die Lernmenge ist gedeckelt (`MaxTrainingPoints`). | R4. Der Rechenaufwand des Modells bleibt bei vielen Parametern und langen Läufen begrenzt. |
| **F22** | **Das Modell bricht nie den Lauf ab.** Scheitert es numerisch, wird gewarnt und diese Iteration mit Latin-Hypercube-Punkten gefüllt. In Tests gibt es feste Seeds und **keine Zeit-Grenzwerte**. | Robustheit vor Eleganz. Die wackeligen Tests aus TODO-33 sollen sich nicht wiederholen. |

### Konfiguration, wie sie am Ende aussieht

```jsonc
// --- Optimierungsverfahren: "Rsm", "Evolution" oder "Bayes" ---
"OptimizationAlgorithm": "Bayes",

// --- Nur für "Bayes". Alles optional, die Vorgaben passen meist. ---
"Bayes": {
  "SurrogateTarget": "Auto",   // "Auto" | "Fitness" | "Metrics" (Auto = Metrics, wenn die Fitness RequiredMetrics nennt)
  "InitialSamples": 0,         // 0 = automatisch: max(VariantsPerIteration, 2 × freie Parameter + 2)
  "CandidatePool": 5000,       // so viele Kandidaten prüft das Modell je Vorschlag auf seiner Landkarte
  "TrustRegion": "Auto",       // "Auto" (ab 10 freien Parametern) | "On" | "Off"
  "MaxTrainingPoints": 400     // Deckel für die Lernmenge, hält die Rechenzeit des Modells klein
}
```

Neue Klasse `BayesConfig` in `Core/Models/SimulationConfig.cs`, eingehängt als Property `Bayes`, so
wie es `MeshCalibrationConfig` schon vormacht.

---

## Arbeitsschritte

### Block G — Grundlagen

#### TODO-50 — Markierung „disqualifiziert", Parameterraum, Regel für Trainingsdaten

**Ziel:** Das Modell bekommt saubere Lerndaten, und fehlende Grenzen fallen vor der ersten Rechnung
auf.

**Dateien:**
* `Core/Models/ModelRecord.cs`: neues `public bool Disqualified { get; set; }`
* `Core/Pipeline/ChampionValidator.cs:80`, `:85`: zusätzlich `candidate.Disqualified = true`.
  Die Fitness 0,0001 bleibt, so dass Rsm und Evolution unverändert rechnen.
* `Core/Models/SimulationContext.cs:138`: CSV-Spalte `Disqualified` (0/1) **hinten** anhängen, nach
  `SimulationFailed`. `scripts/Plot_RSM.py` liest über Spaltennamen, das ist geprüft. Tests, die den
  exakten Kopf erwarten, werden nachgezogen.
* neu `Core/Surrogates/ParameterSpace.cs`
  * Aufgebaut aus `context.CurrentBaseParameters` (Reihenfolge der Schlüssel = Reihenfolge der
    Dimensionen) und `context.Project.ParameterBounds`.
  * `FreeNames` (Min < Max), `FixedValues` (Min == Max).
  * `double[] ToUnit(Dictionary<string, float>)` und `Dictionary<string, float> FromUnit(double[])`.
    `FromUnit` füllt die festen Parameter auf und begrenzt die Werte auf den erlaubten Bereich.
  * `Validate()`: Fehlt eine Grenze, oder ist Min > Max, gibt es eine `InvalidOperationException`.
    Die Meldung nennt den Parameter und `src/projects/<Name>/project.json → "ParameterBounds"`.
    Liegen die Startwerte außerhalb der Grenzen, wird gewarnt und begrenzt.
* neu `Core/Surrogates/SurrogateTrainingSet.cs`
  * `Build(SimulationContext, ParameterSpace)` liefert die nutzbaren Datensätze samt normierter
    Eingaben und dazu die **ausgeschlossenen Punkte** (fehlgeschlagen oder disqualifiziert), die für
    die Abstoßung in TODO-52 gebraucht werden.
  * Regel: `!SimulationFailed && !Disqualified && float.IsFinite(Fitness)`, Variante ≠ 99
    (vorsorglich), und `PassiveParameters["MeshRefinementFactor"]` gleich
    `context.EffectiveMeshRefinementFactor` (Toleranz 1e-6). Datensätze ohne diesen Schlüssel werden
    angenommen, das betrifft ältere Daten und Tests.

**Tests:** Der Validator setzt das Flag in beiden Zweigen (`ChampionValidatorTests` erweitern). Die
CSV-Spalte ist vorhanden. Jede Filterregel hat einen eigenen Fall. Die Normierung übersteht den
Hin- und Rückweg. Die Meldung bei fehlender Grenze nennt den Parameter. Ein fester Parameter bleibt
fest.

**Fertig, wenn:** Alle Tests grün sind und die bestehenden Rsm- und EA-Tests **unverändert** grün
sind.

#### TODO-51 — Mathematischer Kern: Gauß-Prozess

**Ziel:** Ein schlanker, vollständig getesteter GP ohne fremde Bibliothek.

**Dateien (alle neu, `Core/Surrogates/`):**
* `ISurrogateModel.cs`
  ```csharp
  public interface ISurrogateModel
  {
      void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs);   // ersetzt alle Daten
      (double Mean, double Variance) Predict(double[] input);                      // latente Varianz, ohne Rauschen
      void AddPseudoObservation(double[] input, double output);                    // für Batch-Vorschläge, ohne Neu-Lernen
      IReadOnlyList<double>? ParameterRelevance { get; }                           // 0..1 je Dimension, null = unbekannt
  }
  ```
* `DenseCholesky.cs`: Zerlegung mit Jitter-Leiter (1e-8, ×10 bis 1e-4, danach
  `SurrogateFitException`), Vorwärts- und Rückwärtseinsetzen, Log-Determinante.
* `GaussianProcess.cs` (implementiert `ISurrogateModel`)
  * Die Ausgaben werden intern standardisiert (Mittelwert 0, Streuung 1). Bei konstanten Ausgaben
    wird Streuung 1 gesetzt, die Vorhersage ist dann konstant.
  * Matérn 5/2 mit ARD: `k = σ² (1 + √5 r + 5/3 r²) exp(−√5 r)`, `r² = Σ ((x_i − x'_i) / ℓ_i)²`.
  * Gradient: `∂k/∂ln ℓ_i = 5/3 σ² (1 + √5 r) exp(−√5 r) · (Δ_i / ℓ_i)²`,
    `∂k/∂ln σ² = k`, `∂K/∂ln σ_n² = σ_n² I`.
  * Log-Marginal-Likelihood `−½ yᵀα − Σ ln L_ii − n/2 ln 2π`, Gradient
    `½ tr((ααᵀ − K⁻¹) ∂K/∂θ)`.
  * Hyperparameter im Log-Raum, begrenzt auf ℓ ∈ [0,01; 20], σ² ∈ [0,05; 20], σ_n² ∈ [1e-6; 0,5]
    (standardisierte Einheiten).
  * Optimierung: Adam (Schrittweite 0,05, 200 Schritte). Starts: das vorige Optimum (warm), eine
    Vorgabe (ℓ = 0,5, σ² = 1, σ_n² = 0,01) und zwei zufällige. Der beste Start gewinnt.
  * `ParameterRelevance = (1/ℓ_i) / max_j(1/ℓ_j)`.
* `LatinHypercube.cs`: `Sample(n, d, Random)`, Maximin aus 20 Zufalls-LHS, behalten wird der mit dem
  größten Mindestabstand.
* `NormalDistribution.cs`: `Pdf`, `Cdf` (erfc-Näherung mit relativem Fehler unter 1e-7),
  `Sample(Random)` nach Box-Muller.

**Tests:**
* Cholesky gegen eine bekannte 3×3-Matrix. Lösen mit Restfehler unter 1e-10.
* 1D: `sin(6x)` aus 8 Punkten. Mittlerer Fehler zwischen den Punkten unter 0,05. Die Varianz an
  Stützpunkten ist etwa so groß wie das Rauschen, weit weg nähert sie sich σ².
* **Gradient gegen finite Differenzen** für jeden Hyperparameter, relativer Fehler unter 1e-4.
* ARD: Bei `f(x1, x2) = sin(6 x1)` ist die Längenskala von x2 mindestens 5-mal größer als die von x1.
* Robustheit: doppelte Punkte, konstante Ausgaben, n = 1 und n = 2. Ein NaN in den Ausgaben gibt eine
  klare `ArgumentException`.
* Mit festem Seed ist alles bitgleich reproduzierbar.
* LHS: Jede Dimension hat in jedem der n Intervalle genau einen Punkt.

**Fertig, wenn:** Alle Tests grün sind. Ein Lauf mit n = 400 und d = 20 läuft als Test ohne
Zeitgrenze durch und ist nur eine Rauchprobe (F22).

### Block H — Bayes-Optimierer

#### TODO-52 — `BayesOptimizationAlgorithm`, Modus „Fitness" (Idee 1)

**Ziel:** Das neue Verfahren ist über `"OptimizationAlgorithm": "Bayes"` wählbar und lernt die Fitness
als Ganzes.

**Dateien:**
* neu `Core/Algorithms/BayesOptimizationAlgorithm.cs`, Konstruktor
  `(IFitnessCalculator fitness, BayesConfig? config = null, Random? random = null, Func<ISurrogateModel>? modelFactory = null)`.
  Die Konfiguration kommt im Lauf aus `context.Config.Bayes`, der Konstruktor-Parameter ist nur für
  Tests da.
* `Core/Models/SimulationConfig.cs`: Klasse `BayesConfig` und Property `Bayes` (siehe oben).
* `Core/Algorithms/OptimizationAlgorithmFactory.cs`: `case "bayes"` und
  `SupportedAlgorithms = "Rsm, Evolution, Bayes"`.

**Ablauf:**
1. **Erster Aufruf von `GenerateParameters`:** Parameterraum aufbauen und prüfen (TODO-50).
   n₀ = `InitialSamples` oder automatisch `max(VariantsPerIteration, 2·d_frei + 2)`. Dazu ein
   LHS-Plan mit n₀ − 1 Punkten.
2. **Iteration 1, Variante 1 = Startwerte.** Danach werden die Vorschläge aus der Warteschlange
   ausgegeben. Ist sie leer, kommt ein LHS-Ersatzpunkt mit einmaliger Warnung.
3. **`EvaluateAndSelectBest`:** Fitness über den Rechner des Projekts berechnen, wie beim Rsm, und
   die Vorhersagespalten eintragen (TODO-55). Sonst ändert sich nichts (F19).
4. **`PrepareNextIteration`:**
   * Lernmenge bauen (TODO-50).
   * **Weniger als n₀ nutzbare Rechnungen:** Zuerst kommen die restlichen LHS-Punkte in die
     Warteschlange. Freie Plätze füllen Modellvorschläge, sobald es mindestens `d_frei + 2` nutzbare
     Rechnungen gibt, sonst weitere LHS-Punkte. Die Meldung lautet wie beim Rsm: „Benötige noch X
     Basis-Simulationen".
   * **Sonst:**
     * Modell lernen.
     * Kandidaten-Pool erzeugen: `CandidatePool` Punkte. 50 % liegen gleichverteilt im Raum, 50 %
       sind gaußsche Streuung (σ = 0,1 je Dimension) um die 5 besten Rechnungen. Die 10 Besten nach
       EI werden danach mit 30 Schritten lokaler Zufallssuche (σ halbiert bei Misserfolg)
       nachgeschärft.
     * Auswahl nach Expected Improvement:
       `EI = (μ − f* − ξ) Φ(z) + σ φ(z)`, `z = (μ − f* − ξ) / σ`, ξ = 0,01 (standardisiert),
       f* = beste nutzbare Fitness.
     * **Ausgabe-Transformation:** `ln(Fitness)`, wenn alle nutzbaren Werte > 0 sind, sonst keine.
     * **Abstoßung:** EI wird mit `Π_j (1 − exp(−‖x − e_j‖² / (2 r²)))` multipliziert, mit
       `r = 0,05 · √d_frei` und e_j = fehlgeschlagene oder disqualifizierte Punkte. Dort, wo das
       Netz oder SU2 abgestürzt sind, wird nicht weitergebohrt.
     * **Batch per Kriging Believer:** den besten Kandidaten wählen, seine Vorhersage als
       Scheinbeobachtung eintragen (`AddPseudoObservation`), den nächsten wählen, bis
       `VariantsPerIteration` erreicht ist. Ab 4 Varianten ist der letzte Platz der **Erkunder**:
       größte Varianz in einem frischen, gleichverteilten Pool über den ganzen Raum, mit Abstoßung.
     * Kein Vorschlag näher als 1e-3 (normiert) an einer Rechnung oder einem anderen Vorschlag.
   * **`SurrogateFitException`:** warnen und die Iteration mit LHS-Punkten füllen (F22).
   * Kurze Zusammenfassung ins Log (Details in TODO-55).

**Tests:**
* Die Fabrik erkennt `"Bayes"`, `"bayes"` und `" BAYES "`. `SupportedAlgorithms` stimmt. Die
  Vorgabe ist weiterhin `Rsm` (der bestehende Test bleibt).
* Iteration 1, Variante 1 = Startwerte. Alle Vorschläge liegen in den Grenzen. Der DoE-Teil ist
  LHS-geschichtet.
* Eine fehlende Grenze bricht vor der ersten Rechnung ab. Ein fester Parameter bleibt fest.
* Fehlgeschlagene oder disqualifizierte Datensätze fehlen in der Lernmenge, und kein Vorschlag liegt
  näher als r/2 an einem solchen Punkt (konstruiertes Szenario).
* `EvaluateAndSelectBest` ist zustandsfrei: Zusätzliche Aufrufe ändern die folgenden Vorschläge
  nicht (fester Seed).
* Ein Batch hat genau `VariantsPerIteration` verschiedene Punkte, ohne Dubletten zur History.
* **Vergleich über den echten `WorkflowController`** mit gemockter Geometrie und gemocktem Solver
  (Muster: `WorkflowControllerTests`). Die Solver-Attrappe schreibt den Kennwert einer
  Testfunktion. Testfunktionen sind Branin (2D) und Hartmann-6 (6D). Budget: 6 Iterationen × 6
  Varianten, 3 feste Seeds. Erwartet wird, dass `Bayes` jeweils mindestens so gut ist wie `Rsm` und
  nah am bekannten Optimum liegt. Die Toleranz wird beim Schreiben einmal ermittelt und mit Abstand
  festgelegt. Der Türsteher läuft dabei mit.
* `Rsm` und `Evolution`: alle bestehenden Tests unverändert grün.

**Fertig, wenn:** `"Bayes"` lokal mit den Mocks einen kompletten Lauf fährt und alle Tests grün sind.

#### TODO-53 — Modus „Metrics": ein Ersatzmodell je Kennwert (Idee 2)

**Ziel:** Für Fitness-Formeln mit Strafen, Grenzen und Brüchen werden die glatten Kennwerte einzeln
gelernt, und die Fitness rechnet die Formel des Projekts.

**Umsetzung (in `BayesOptimizationAlgorithm`, eventuell Hilfsklasse `Core/Surrogates/MetricSurrogateSet.cs`):**

1. **Modus festlegen.** `SurrogateTarget`:
   * `"Fitness"`: TODO-52.
   * `"Metrics"`: verlangt eine nicht leere `RequiredMetrics`. Ist sie leer, bricht der Lauf **vor
     der ersten Rechnung** mit klarer Meldung ab.
   * `"Auto"`: `Metrics`, wenn `RequiredMetrics` nicht leer ist, sonst `Fitness`.
   * Der wirksame Modus steht in einer Property `ActiveMode`, für Log und Tests.
2. **Gegenprobe der Fitness.** Sie läuft beim ersten Lernen und danach für jede neue Rechnung. Für
   jeden nutzbaren Datensatz wird ein Wegwerf-Datensatz gebaut, nur mit den Parametern und den
   Kennwerten aus `RequiredMetrics`. Weicht dessen Fitness von der echten ab (mehr als
   1e-5 · max(1, |f|)), liest die Fitness offenbar weitere Kennwerte. Dann gibt es eine Warnung mit
   Namen, und **der Rest des Laufs fällt auf `Fitness` zurück**. Robustheit geht vor Strenge.
3. **Ein GP je Kennwert** aus `RequiredMetrics`. Transformation je Kennwert: `ln`, wenn alle
   nutzbaren Werte > 0 sind, sonst keine. Datensätze, denen genau dieser Kennwert fehlt oder bei
   denen er nicht endlich ist, fallen nur aus dessen Modell heraus.
4. **Expected Improvement per Monte-Carlo.**
   * S = 64 Zufallssätze `z[s, m]`, einmal je `PrepareNextIteration` gezogen und für alle
     Kandidaten gleich verwendet. So bleibt der Vergleich ruhig.
   * Je Satz: Kennwert = Rücktransformation von μ_m + σ_m · z. Daraus einen Wegwerf-Datensatz
     bauen, `fitness.CalculateFitness(...)` aufrufen und f_s lesen.
   * `EI = Mittel(max(0, T(f_s) − T(f*)))`, wobei T = ln, wenn alle Fitnesswerte > 0 sind.
     f_s ≤ 0 zählt als keine Verbesserung.
   * **Zwei Stufen** wegen der Rechenzeit: Der ganze Pool wird mit 8 Sätzen vorsortiert, die besten
     200 Kandidaten danach mit 64 Sätzen bewertet.
   * Der Wegwerf-Datensatz wird wiederverwendet (vor jedem Aufruf leeren) und kommt **nie** in die
     History.
5. **Kriging Believer** trägt die Scheinbeobachtung in **jedes** Kennwert-Modell ein.
6. **Vorhersage fürs Log:** Fitness bei z = 0 (Median), Band aus dem 2,5- und 97,5-%-Quantil der
   64 Sätze.

**Tests:**
* `Auto` wählt `Metrics` bei vorhandener `RequiredMetrics` und `Fitness` ohne.
* `Metrics` mit leerer Liste bricht vor der ersten Rechnung ab.
* Eine Gegenprobe-Fitness, die einen nicht deklarierten Kennwert liest, führt zu Warnung und
  `ActiveMode == Fitness`.
* `ln` wird bei positivem Kennwert gewählt, keine Transformation bei Vorzeichenwechsel.
* **Manta-ähnlicher Vergleich**, 5 Parameter, glatte Kennwerte A, B, C:
  `Fitness = A·B / C³`, ×0,1 außerhalb eines A-Bereichs. Gleiches Budget und feste Seeds. Erwartet:
  `Metrics` ist besser als `Fitness`, und `Fitness` ist besser als `Rsm`.
* Die Anzahl der History-Einträge ändert sich durch `PrepareNextIteration` nicht.
* Ein Rechner, der zusätzliche Schlüssel schreibt (wie `GlideRatio`), funktioniert.

**Fertig, wenn:** Alle Tests grün sind. Ein Lauf mit gemocktem Manta-Rechner zeigt `ActiveMode =
Metrics` im Log.

#### TODO-54 — Viele Parameter, lange Läufe: Vertrauensbereich und Deckel für die Lernmenge

**Ziel:** Auch mit 20 oder mehr Parametern und vielen hundert Rechnungen bleibt das Verfahren
treffsicher und schnell (R4).

**Umsetzung:**
* **Vertrauensbereich** (nach TuRBO). Bei `TrustRegion = "Auto"` ist er aktiv ab `d_frei ≥ 10`,
  sonst `"On"` oder `"Off"`.
  * Zentrum = bester nutzbarer Punkt.
  * Seitenlänge L: Start 0,8, Minimum 0,5⁷, Maximum 1,6. Je Dimension
    `L · w_i` mit `w_i = ℓ_i / geomittel(ℓ)`. Im Modus „Metrics" ist ℓ das geometrische Mittel der
    normierten Längenskalen aller Kennwert-Modelle. Begrenzt auf [0, 1].
  * Nach jeder Iteration: Ein **Erfolg** liegt vor, wenn der Beste der Runde f* um mehr als
    1e-3 · |f*| verbessert.
    * 3 Erfolge in Folge: L verdoppeln.
    * `⌈max(4/V, d_frei/V)⌉` Misserfolge in Folge: L halbieren.
  * Fällt L unter das Minimum: **Neustart**. L = 0,8, Zentrum = höchster Wert von μ + 2σ in einem
    gleichverteilten Pool über den ganzen Raum. Die Daten bleiben erhalten. Meldung: „Vertrauens-
    bereich ausgereizt, erkunde neues Gebiet".
  * Der Kandidaten-Pool liegt im Vertrauensbereich. **Der Erkunder (F17) sucht weiter im ganzen
    Raum** und ist damit das globale Sicherheitsnetz.
* **Deckel für die Lernmenge.** Gibt es mehr als `MaxTrainingPoints` nutzbare Rechnungen, werden die
  besten 10 % nach Fitness behalten und mit den Nächstgelegenen zum Zentrum aufgefüllt (Abstand mit
  Längenskalen gewichtet). Das gilt je Modell.
* Der Zustand des Vertrauensbereichs (L, Zentrum, Zähler) steht im Log.

**Tests:** L schrumpft nach der festgelegten Zahl von Misserfolgen und wächst nach 3 Erfolgen.
Neustart unter dem Minimum. Die Auto-Schwelle stimmt (9 freie Parameter: aus, 10: an). Der Deckel
wird eingehalten (das Modell sieht höchstens `MaxTrainingPoints` Punkte). Eine 20D-Testfunktion
(verschobene Quadratsumme mit leichter Kopplung) wird innerhalb des Budgets klar besser als der
beste DoE-Punkt. Keine Zeitgrenzen.

**Fertig, wenn:** Alle Tests grün sind.

#### TODO-55 — Transparenz: Vorhersage gegen Ergebnis, Modellgüte, Parameter-Wichtigkeit

**Ziel:** Du siehst ohne KI-Wissen, ob das Modell etwas taugt und welche Parameter wirklich zählen.

**Umsetzung:**
* **Vorhersage je Variante in der CSV.** Für jeden Vorschlag wird beim Ausgeben in
  `GenerateParameters` unter (Iteration, Variante) gespeichert:
  * Median der vorhergesagten Fitness, 95-%-Band unten und oben
  * die Art des Vorschlags (Modell oder Erkunder)

  In `EvaluateAndSelectBest` kommen diese Werte bei passender Iteration, Variante **und** identischen
  Parametern in `PassiveParameters`: `Bayes_PredictedFitness`, `Bayes_PredictedLow`,
  `Bayes_PredictedHigh`, `Bayes_Explorer` (1/0). Sie landen damit automatisch als Spalten in
  `Simulation_Results.csv`. DoE-Zeilen zeigen dort 0, so verhält sich der CSV-Export heute bei jedem
  fehlenden Wert, und das wird im Handbuch erklärt. Diese Schlüssel fließen nirgends ein.
* **Treffsicherheit je Iteration**, im Log und in der Datei. Sie wird zu Beginn von
  `PrepareNextIteration` aus den Varianten der eben beendeten Runde berechnet: mittlere relative
  Abweichung zwischen Vorhersage und echter Fitness und der Anteil der Varianten im 95-%-Band.
  Beispiel:
  `[BAYES] Treffsicherheit der letzten Runde: Ø Abweichung 6 %, 9 von 10 im Erwartungsband.`
  Unter 50 % im Band kommt der Hinweis „Modell noch unsicher oder Ergebnisse stark verrauscht".
* **Parameter-Wichtigkeit** aus `ParameterRelevance`, als Textbalken sortiert, je Kennwert-Modell
  eine Zeile. Bei mehr als 8 Parametern werden die 8 wichtigsten gezeigt und dann „…". Beispiel:
  `Drag: Length ██████████  Width ██████  MainRadius ███  TailTaper ▏`
* **`Analyseergebnisse/Bayes_Verlauf.csv`** (Semikolon wie die übrigen CSVs). Je Iteration eine
  Zeile: `Iteration;Modus;Lernpunkte;Vertrauensbereich_L;Abweichung_Mittel;Anteil_im_Band;BesteFitness;Relevanz_<Parameter>…`.
  Die Datei wird nach jeder Iteration vollständig neu geschrieben.

**Tests:** Vorhersagespalten sind bei Modellvorschlägen vorhanden, bei DoE-Zeilen nicht. Die
Treffsicherheit wird aus konstruierten Zahlen richtig berechnet. Ein Parameter ohne Einfluss steht
in der Wichtigkeit ganz hinten. `Bayes_Verlauf.csv` wird mit Kopfzeile geschrieben. Bei `Rsm`
entsteht keine solche Datei.

**Fertig, wenn:** Ein Mock-Lauf eine CSV mit Vorhersagespalten und eine `Bayes_Verlauf.csv` erzeugt.

### Block I — Doku und Praxistest

#### TODO-56 — Bedienhandbuch, READMEs, Kommentarköpfe der JSON-Dateien

* `src/projects/BEDIENHANDBUCH.md`
  * Optimierer-Tabelle um `"Bayes"` ergänzen, in Alltagssprache. Dazu eine Mini-Entscheidungshilfe:
    *teure Rechnungen und bis etwa 20 Parameter: `Bayes`; Vergleich und Gegenprobe: `Rsm` oder
    `Evolution`*.
  * Alle `BayesConfig`-Schlüssel mit Vorgabe und Ampel, wie im Handbuch üblich.
  * Hinweis: `MaxDeviations` wirkt bei `Bayes` nicht, `ParameterBounds` sind Pflicht.
  * Die Spalten `Bayes_*` und die Datei `Bayes_Verlauf.csv` erklären, samt Lesebeispiel.
  * Glossar: Bayes-Optimierung, Ersatzmodell (Gauß-Prozess), Erkunder, Vertrauensbereich,
    Parameter-Wichtigkeit.
* `tests/.../HandbookCoverageTests.cs:41`: `typeof(BayesConfig)` in `SettingTypes` aufnehmen.
* `src/projects/README.md`: Regel für Fitness-Autoren. Der Modus „Metrics" wirkt nur, wenn die
  Fitness ausschließlich mit `RequiredMetrics` und Parametern rechnet. Sonst fällt er automatisch
  und ohne Schaden zurück.
* `src/Automatisierung_v2/README.md`: Abschnitt „Bayes-Optimierer und Ersatzmodelle". Inhalt:
  `Core/Surrogates/`, `ISurrogateModel`, Trainingsdaten-Regel, Modi, Vertrauensbereich, Grenzen des
  Verfahrens und die Andockstelle für fremde Modelle (siehe Ausblick).
* `README.md` (Hauptverzeichnis): Kernfunktionen um den Bayes-Optimierer ergänzen.
* Kommentarköpfe der `simulation.json` in `MantaAuv`, `Naca2D`, `TestprojektNACA`, `_Vorlage` und
  `_Vorlage2D`: `"Rsm", "Evolution" oder "Bayes"`, dazu der `Bayes`-Block **auskommentiert** als
  Beispiel. Die Werte bleiben, wie sie sind (F9), auch die zwei erlaubten Abweichungen der Vorlage.

**Fertig, wenn:** `dotnet build` 0 Warnungen meldet und `dotnet test` grün ist, einschließlich
`HandbookCoverageTests` und `TemplateProjectTests`.

#### TODO-57 — Vergleichslauf auf dem Server *(du)*

**Voraussetzung:** Die Server-Checkliste aus `anmerkungen_umsetzung.md` (Abschnitt „Offene Fragen",
Punkte 1–4) ist abgearbeitet. Sonst optimiert der Bayes-Optimierer sehr effizient eine womöglich noch
ungenaue Physik.

1. **Naca2D** (3 Parameter, 2D, schnell): drei Läufe mit gleichem Budget, Vorschlag 5 Iterationen ×
   6 Varianten. Einmal `"Rsm"`, einmal `"Evolution"`, einmal `"Bayes"`, jeweils in
   `src/projects/Naca2D/simulation.json` umstellen, dann `deploy`, `run` und `fetch`.
2. Je Lauf eintragen: beste Fitness nach 10, 20 und 30 Varianten. Bei `Bayes` zusätzlich die
   Modell-Rechenzeit je Iteration aus dem Log und die Treffsicherheit aus `Bayes_Verlauf.csv`.
3. Optional: **MantaAuv** mit `"Bayes"`. Das ist der eigentliche Einsatzfall, 5 Parameter, 3D,
   Modus „Metrics". **Achtung:** Bleibt Manta dauerhaft auf `Bayes`, muss der Test
   `Repository_Simulation_Json_Selects_Rsm` (`OptimizationAlgorithmFactoryTests.cs:67`) bewusst
   angepasst werden.

| Lauf | beste Fitness nach 10 | nach 20 | nach 30 | Modellzeit je Iteration | Treffsicherheit |
| --- | --- | --- | --- | --- | --- |
| Naca2D Rsm | | | | — | — |
| Naca2D Evolution | | | | — | — |
| Naca2D Bayes | | | | | |

**Fertig, wenn:** die Tabelle ausgefüllt ist und du entschieden hast, ob `Bayes` Standard wird.

---

## Reihenfolge und Abhängigkeiten

```text
TODO-50 ──► TODO-51 ──► TODO-52 ──► TODO-53 ──► TODO-54 ──► TODO-56 ──► TODO-57 (Server)
                             └──────► TODO-55 ───────┘
```

* TODO-55 kann direkt nach TODO-52 beginnen. Die Spalten für den Modus „Metrics" kommen mit TODO-53
  dazu.
* TODO-57 hängt zusätzlich an der Server-Checkliste des vorigen Plans.
* Für `/delegate`: TODO-50 und TODO-56 sind weitgehend mechanisch (`haiku-worker`, bei der
  Doku `sonnet-worker`). TODO-51 bis TODO-54 brauchen Urteilsvermögen bei der Numerik
  (`sonnet-worker`, Review durch den Supervisor, vor allem bei Gradient und EI).

---

## Risiken und Fallstricke

| Risiko | Gegenmittel |
| --- | --- |
| Das Modell ist selbstsicher und falsch und bleibt hängen. | EI erkundet von selbst. Dazu kommen der Erkunder-Platz (F17), der Neustart des Vertrauensbereichs (TODO-54) und die Treffsicherheit im Log (TODO-55). |
| CFD-Ergebnisse rauschen (Netz, Konvergenz). | Der Rauschterm im GP wird mitgelernt. Der Türsteher bleibt. Disqualifizierte Datensätze fließen nicht ein. |
| Sprünge in der Fitness (Strafen) | Modus „Metrics" (TODO-53) und `ln`-Transformation |
| Die Fitness liest nicht deklarierte Kennwerte. | Gegenprobe mit automatischem Rückfall auf „Fitness" |
| Ganzzahlige Parameter (z. B. Flossenzahl) | Sie werden stetig behandelt, der Generator rundet. Das Modell sieht Stufen, das ist vertretbar. Im Handbuch erwähnen. |
| Viele Abstürze in einer Region (Vernetzung) | Abstoßung (TODO-52). Ein eigenes Modell, das Ausfälle vorhersagt, steht im Ideenspeicher. |
| Numerisches Scheitern (Cholesky) | Jitter-Leiter, danach LHS-Ersatzpunkte. Der Lauf bricht nie wegen des Modells ab (F22). |
| Rechenzeit des Modells wächst mit n³. | `MaxTrainingPoints`, `CandidatePool` und das zweistufige Monte-Carlo. Erwartet sind Sekunden bis unter eine Minute je Iteration, verglichen mit CFD-Minuten bis -Stunden. |
| Rechnungen verschiedener Netzstufen werden vermischt. | Filter auf `MeshRefinementFactor` (F13) |
| 20 Parameter bei wenig Daten | DoE mit 2·d + 2 Punkten, Vertrauensbereich |
| Wackelige Tests | Feste Seeds, keine Zeitgrenzen (F22) |
| Verhalten ändert sich für bestehende Läufe. | Standard `Rsm`, Rsm und EA unverändert. Neu ist nur die rein informative Spalte `Disqualified`. |

---

## Ausblick (NICHT Teil der TODOs)

### Idee 3 — Gedankenspiel: mehrstufige Genauigkeit

**Ja, du hast es richtig verstanden.** Die volle Ausbaustufe ist genau das: Das Verfahren entscheidet
selbst, „das hier reicht in 2D, das brauche ich in 3D, und der Gewinner bekommt URANS". In der
Fachliteratur heißt das *Multi-Fidelity-Bayes-Optimierung mit Kostenabwägung*. Man kann sich drei
Stufen vorstellen:

| Stufe | Wer entscheidet die Genauigkeit? | Wie | Stand |
| --- | --- | --- | --- |
| **A** | du, über feste Regeln | grob für alle, fein für den Champion (TODO-43), 2D-Vorkalibrierung (TODO-48) | **gibt es schon** |
| **B** | ein fester Plan, aber das Modell lernt aus beidem | z. B. je Iteration alle Varianten auf grobem Netz und die besten 2 auf dem Arbeitsnetz. Ein Modell lernt „fein = ρ · grob + Korrektur" (*Co-Kriging*): Die vielen billigen Rechnungen zeichnen die Landkarte, die wenigen teuren korrigieren sie. | mittlerer Aufwand, baut direkt auf TODO-50/51 auf |
| **C** | das Verfahren selbst | je Kandidat: welche Genauigkeit liefert die meiste Information je CPU-Minute? | Forschungsniveau, echter Aufwand |

**Was Stufe C bräuchte:**
1. Gemessene Rechenzeiten je Genauigkeit. Die Stoppuhr im Controller gibt es schon.
2. Ein Modell, das den Zusammenhang zwischen den Genauigkeiten lernt.
3. Eine **Pflichtquote teurer Rechnungen**, damit 2D das Verfahren nicht systematisch in die Irre
   führt, etwa bei 3D-Effekten wie Randwirbeln (siehe E7 im vorigen Plan).
4. Im Kern eine Erweiterung der Schnittstelle, etwa
   `IFidelityAwareOptimizer : IOptimizationAlgorithm` mit
   `GenerateRequest(...) → (Parameter, Genauigkeit)`. Der `WorkflowController` reicht die Genauigkeit
   dann an `RunPipeline(parameters, iteration, variant, refinementFactor, tag)` weiter. Diese
   Methode kann schon heute jede Netzstufe einzeln rechnen, und für 2D gibt es
   `IHasRepresentativeSection`.

**Warum nicht jetzt:** Es braucht (1) physikalisch geprüfte Genauigkeitsstufen (Servertest), (2)
echte Laufzeiten und (3) einen funktionierenden einstufigen Bayes-Optimierer als Basis. Dieser Plan
legt die Andockstellen schon an: die Netzstufe in jedem Datensatz, die Trainingsdaten-Regel je
Netzstufe (F13) und `ISurrogateModel`.

### Idee 5 — Notiz: LLM-Entwurfsrunden

Das LLM arbeitet als **äußere Schleife**, langsam, wenige Runden. Es ändert die **Struktur** (neue
Flosse, Teil weg, neuer Parameter). Die **Zahlen** optimiert weiter der Bayes-Optimierer, die
**Physik** rechnen SU2 und später CalculiX.

Eine Runde liefe so ab:
1. Das LLM liest eine Entwurfsvorgabe (z. B. `.plans/entwurfsvorgabe_<Projekt>.md`: Bauraum,
   Mindestvolumen, Sensorlage, Fertigbarkeit), die Ergebnisse der letzten Runde
   (`Simulation_Results.csv`, `Bayes_Verlauf.csv` mit der Parameter-Wichtigkeit) und den aktuellen
   Generator.
2. Es legt einen **neuen Projektordner** an, z. B. `MantaAuv_E02`, als Kopie über
   `new-project.ps1`. Darin ändert es Generator und `project.json`.
3. `dotnet build`, `dotnet test` und ein Geometrie-Rauchtest (dicht, Volumen im Bereich).
4. Ein kurzer `Bayes`-Lauf auf dem Server. Eine neue Parametrisierung startet ein frisches Modell,
   das passt gut, weil der Bayes-Optimierer schnell lernt.
5. Zusammenfassung, dann die nächste Runde.

Die einfachste Form wäre ein Claude-Code-Skill, etwa `/entwurfsrunde`, bei dem **du jede Runde
freigibst**. Dafür braucht es fast keine neue Software. Leitplanken: Die Fitness-Formel und die
Entwurfsvorgabe darf das LLM nicht ändern, sonst optimiert es die Bewertung statt des Bauteils.
Vollautonome Nachtläufe mit API-Anbindung oder ein Programm, das sich zur Laufzeit selbst umschreibt,
sind ausdrücklich **nicht** gewollt.

### PINN eines Partners — wo es andocken würde

Ein PINN oder ein anderes neuronales Ersatzmodell von außen hätte drei saubere Andockstellen, ohne
dass der Partner C# können muss:

1. **Als Solver (am natürlichsten).** Für das Framework ist so ein Modell einfach ein weiterer,
   schneller und ungenauerer Solver: ein `ISimulationSolver` in `Solvers/Ml/`. Der Austausch läuft
   über Dateien, so wie SU2 heute angesprochen wird:
   * C# schreibt `input.json` mit Parametern und dem STL-Pfad.
   * Dann startet es den Befehl des Partners, z. B. `python predict.py input.json output.json`.
   * Es liest `output.json` mit Kennwerten und optional deren Unsicherheit.

   Python und PyTorch bleiben ganz auf der Seite des Partners. Das Modell taugt dann für schnelle
   Vorstudien, wenn ein Projekt die Stufe tauscht, oder als **billigste Genauigkeitsstufe** in
   Idee 3.
2. **Als Ersatzmodell.** Das Modell implementiert `ISurrogateModel` (TODO-51). Sinnvoll ist das nur,
   wenn es vortrainiert ist und über einen Lauf hinaus verallgemeinert. Am elegantesten wäre es als
   *Vorwissen* für den GP: Der GP lernt dann nur noch die Differenz „echt minus Partnermodell". Das
   ist physik-informiert im praktischen Sinn und eine kleine spätere Erweiterung des GP (Mittelwert-
   funktion).
3. **Trainingsdaten für den Partner.** Liegen schon je Variante vor: Parameter und Kennwerte in
   `Simulation_Results.csv`, STL, `Surface_*.vtu` und `Volume_*.vtu` in `Analyseergebnisse/` und die
   Verläufe. Ein späterer Befehl wie `export-dataset` könnte das zu einem Paket schnüren.

### Ideenspeicher (klein)

* **Warmstart aus früheren Läufen** desselben Projekts, nur bei identischer Physik und Netzstufe.
  Bewusst nicht jetzt (F12).
* **Geometrie-Vorprüfung:** Billige Kennwerte wie `Volume` sind nach Sekunden bekannt. Kandidaten,
  die harte Grenzen verletzen, könnten die CFD überspringen. Das spart Rechenzeit, ändert aber den
  Ablauf. Die ×0,1-Strafe ist bewusst so gewählt, dass auch „Reste der Form bewertet werden".
* **Ausfall-Modell:** lernen, wo Vernetzung oder SU2 abstürzen, statt der Abstoßungs-Heuristik.
* **CMA-ES** als modernes evolutionäres Verfahren für mehr als 30 Parameter.
* **Mehrere Ziele (Pareto-Front):** zusammen mit FEM zurückgestellt.

---

## Offene Fragen

Zurzeit keine, die die Umsetzung blockiert. Nach TODO-57 ist zu entscheiden:
* Wird `Bayes` Standard im Code (`OptimizationAlgorithmFactory.DefaultAlgorithm`) und/oder in
  MantaAuv?

---

## Abgrenzung

* **Nicht Teil dieses Plans:** FEM (→ `fem_ausblick.md`), Idee 3 (mehrstufige Genauigkeit),
  Idee 5 (LLM-Entwurfsrunden), PINN, jede LLM- oder API-Anbindung, Python-ML-Bibliotheken,
  Warmstart aus alten Läufen, Pareto-Optimierung.
* **Bleibt unverändert:** `RsmOptimizationAlgorithm`, `EvolutionaryAlgorithm`, `WorkflowController`,
  `ChampionFineVerifier` und die Fitness-Rechner aller Projekte. Am `ChampionValidator` kommt nur die
  zusätzliche Markierung `Disqualified` hinzu.
