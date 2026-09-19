# Die Projekte

Ein **Projekt** ist ein Bauteil, das optimiert werden soll: sein Geometrie-Bauplan,
seine Zielfunktion und seine Zahlen. Jedes Projekt ist genau ein Ordner hier drin.

```
src/projects/
├── _Vorlage/        Kopiervorlage -- kein Projekt, siehe _Vorlage/README.md
└── MantaAuv/        ein AUV in Mantarochen-Form
```

**Der Ordnername ist der Projektname.** Nicht ein Feld in einer JSON, nicht ein
Eintrag in einer Liste — der Ordner. Heißt der Ordner `Propeller`, dann läuft das
Projekt mit

```powershell
.\scripts\sim.ps1 run -Project Propeller
```

Zwei Projekte existieren nebeneinander und kommen sich nicht in die Quere: eigene
Zahlen, eigener Code, eigene Ergebnisse unter `Ergebnisse/<Projekt>/`. Umgeschaltet
wird **nur** über den Startaufruf — am Framework unter
[`src/Automatisierung_v2/`](../Automatisierung_v2/) ändert sich dafür nichts.

Welche Projekte es gibt:

```powershell
dotnet run --project src\Automatisierung_v2 -- --list-projects   # lokal
.\scripts\sim.ps1 projects                                       # auf dem Server
```

---

## So legst du ein Projekt an

### 1. Ordner erzeugen

```powershell
.\scripts\new-project.ps1 Propeller
```

Das kopiert `_Vorlage/` nach `Propeller/` und ersetzt das Platzhalterwort
`MeinProjekt` überall — in Dateinamen, Klassennamen, Namensraum und in den
Kommentarköpfen der JSONs. Der Name muss ein gültiger C#-Bezeichner sein, denn er
wird auch Klassen- und Namensraumname.

Danach liegt dort:

```
Propeller/
├── project.json                     Startwerte, Grenzen, Optimierungsziele
├── simulation.json                  Framework-Einstellungen dieses Projekts
├── solvers/su2.json                 Strömungslöser
├── solvers/gmsh.json                Vernetzer
├── PropellerProject.cs              verdrahtet das Projekt  (IProjectDefinition)
├── PropellerGeometryGenerator.cs    baut die Geometrie      (IGeometryGenerator)
└── PropellerFitnessCalculator.cs    bewertet sie            (IFitnessCalculator)
```

**Mehr ist nicht anzumelden.** Die `ProjectRegistry` findet die neue Klasse beim
Start per Reflection: kein `switch`, keine Liste, kein Eintrag in `Program.cs`.

### 2. Parameter festlegen

In `project.json` — und mit denselben Werten in `CreateDefaults()` in
`PropellerProject.cs`, das ist die Code-Schicht darunter:

```jsonc
"BaseParameters":  { "Length": 50.0, "Width": 30.0 },   // Startwerte in mm
"MaxDeviations":   { "Length": 5.0,  "Width": 3.0  },   // Mutationsstärke
"ParameterBounds": {
  "Length": { "Min": 20.0, "Max": 100.0 },              // harte Grenzen
  "Width":  { "Min": 10.0, "Max": 80.0  }
},
"DimensionalParameters": [ "Length", "Width" ]          // NUR Längen!
```

Die Namen sind der Vertrag mit dem Geometrie-Generator. Es gibt keine Liste
erlaubter Parameter: was hier steht, existiert.

> **`DimensionalParameters` nur für Längen.** Der RubberBandScaler rechnet die
> Geometrie auf die PicoGK-Arbeitsgröße herunter und die Ergebnisse zurück. Ein
> Verhältnis, ein Winkel oder ein Zählwert würde dabei verfälscht. MantaAuv lässt
> `TailTaper` aus genau diesem Grund bewusst weg — das war einmal ein Fehler.

### 3. Geometrie bauen

In `PropellerGeometryGenerator.cs`. Die Vorlage baut einen Quader; ersetze
`BuildVoxelModel`. Am Ende meldest du deine Messwerte:

```csharp
result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
result.AddMetric("FrontalArea", measurement.FrontalBoxArea, MetricScaling.Area);
result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);
result.AddMetric("LengthToWidth", length / width, MetricScaling.None);
```

Der Name, den du hier vergibst, ist derselbe, den die Fitness-Formel liest, den
`ReferenceAreaMetric` in der `su2.json` meinen kann und der als Spalte in der
`Simulation_Results.csv` auftaucht. Es gibt keine Metrikliste, die gepflegt werden
müsste.

> **Die Dimension muss stimmen.** Ein falsches `MetricScaling` fällt **nicht** auf:
> der Wert ist dann um den Skalierungsfaktor hoch 1, 2 oder 3 daneben und sieht
> trotzdem plausibel aus. `Volume` kubisch, `Area` quadratisch, `Linear` einfach,
> `None` gar nicht.

`PicoGkMeshMetrics.Measure(mesh)` liefert Volumen, Oberfläche und Hüllquader in einem
Durchgang — PicoGKs eigenes `CalculateProperties` kann die **Oberfläche** nicht.

Ein Blick in [`MantaAuv/MantaGeometryGenerator.cs`](MantaAuv/MantaGeometryGenerator.cs)
zeigt, wie eine richtige Geometrie entsteht: ein `Lattice` aus Rumpf und Flügelfächer,
über zwei `Offset`-Schritte organisch verschmolzen.

### 4. Bewerten

In `PropellerFitnessCalculator.cs`. Die Vorlage minimiert nur den Widerstand:

```csharp
record.Fitness = 1.0f / safeDrag;
```

**Das Framework maximiert immer die Fitness.** Was klein werden soll, gehört in den
Nenner; was groß werden soll, in den Zähler. MantaAuv rechnet
`(Volume × SensorDistance) / Drag^DragBalanceFactor` — Nutzraum und Sensorbasis nach
oben, Widerstand nach unten, mit einstellbarem Gewicht.

Zahlen für die Formel kommen aus `OptimizationTargets` in der `project.json`. Trage
ihre Namen in `RequiredTargets` ein: dann bricht ein fehlender Wert beim Verdrahten
ab und nicht nach Stunden Rechenzeit. Für Messwerte gilt dasselbe über
`RequiredMetrics` — die prüft der Controller nach der ersten gerechneten Variante.

> **Vorsicht vor „Reward Hacking".** Optimierungsalgorithmen sind gnadenlos. Ohne
> eine untere Volumengrenze schrumpft der Algorithmus das Bauteil auf Staubkorngröße,
> weil das den geringsten Widerstand hat. Harte Grenzen sind kein Beiwerk.

### 5. Zahlen aufräumen

`simulation.json` und `solvers/*.json` nennen aus der Vorlage **jeden** Schlüssel mit
dem Code-Standard als Wert. Das ist als Nachschlagewerk gedacht, nicht als
Konfiguration: **lösche alles, was du nicht änderst.** Ein Schlüssel mit dem
Standardwert ist Rauschen, das beim nächsten Framework-Update veraltet.

Zwei Werte stehen bewusst nicht auf dem Standard: `MaxIterations = 1` und
`VariantsPerIteration = 2`. Der erste Lauf soll ein **Rauchtest** sein — läuft die
Kette PicoGK → STL → Gmsh → SU2 → Fitness überhaupt durch? Sitzt das, werden beide
Zahlen hochgesetzt.

Was welcher Schlüssel bedeutet, steht in
[`src/Automatisierung_v2/README.md`](../Automatisierung_v2/README.md) — dort ist auch
erklärt, warum die Programmpfade PATH-relativ sind und wann ein Projekt davon
abweichen darf (MantaAuv tut es, wegen des Hochschulservers).

### 6. Starten

Erst ohne Server gegenprüfen, dass die Konfigurationskette steht — das läuft auf
Windows und braucht kein PicoGK:

```powershell
dotnet build Automatisierung.sln
dotnet run --project src\Automatisierung_v2 --no-build -- Propeller --print-config
```

Dann rechnen:

```powershell
.\scripts\sim.ps1 doctor -Project Propeller     # Werkzeuge und Pfade auf dem Server
.\scripts\sim.ps1 run    -Project Propeller     # hochladen, bauen, in tmux starten
.\scripts\sim.ps1 log                           # zusehen
.\scripts\sim.ps1 fetch  -Project Propeller     # CSV und effective-config.json holen
```

Die Ergebnisse liegen unter `Ergebnisse/Propeller/`. Dort landet neben der CSV auch
`effective-config.json` — die tatsächlich verwendete Konfiguration samt Herkunft je
Schlüssel. Damit bleibt ein alter Lauf nachvollziehbar, egal was danach an den
Dateien passiert.

---

## Wenn etwas nicht auftaucht

| Symptom | Ursache |
|---|---|
| `Unbekanntes Projekt 'X'. Vorhanden sind: …` | Der Ordnername weicht von `Name` in der Projektdefinition ab, oder der Name ist vertippt |
| `Zum Ordner src/projects/X/ gibt es keine IProjectDefinition` | Die `XProject.cs` fehlt, ist nicht `public`, hat keinen parameterlosen Konstruktor — oder ihr `Name` stimmt nicht |
| `Zur Definition XProject fehlt der Ordner …` | Umgekehrter Fall: Klasse da, Ordner nicht (oder anders geschrieben; **Linux unterscheidet Groß- und Kleinschreibung**) |
| `'…' nennt einen Schlüssel, den der Typ … nicht kennt` | Tippfehler in einer JSON. Die Meldung nennt den nächstähnlichen gültigen Namen |
| `[WARNUNG] Eine maschinenspezifische Datei ueberschreibt dieses Projekt` | Es liegt eine `*.local.json` herum. Sollstand ist, dass es keine gibt — Werte ins Projekt übernehmen und die Datei löschen |
| Der Lauf bricht nach der ersten Variante mit fehlenden Metriken ab | `RequiredMetrics` nennt einen Namen, den weder der Generator (`AddMetric`) noch `ResultMetrics` in der `su2.json` liefert |

Ein Projekt, das nicht startbar ist, zeigt `--list-projects` trotzdem an — unter
„Ordner ohne Projektdefinition".
