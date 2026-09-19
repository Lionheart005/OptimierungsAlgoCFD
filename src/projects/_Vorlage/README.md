# `_Vorlage` — Startpunkt für ein neues Projekt

Dieser Ordner ist **kein Projekt**, sondern die Kopiervorlage für eines. Er zeigt vollständig,
woraus ein Projekt besteht: zwei Klassen mit Geometrie und Bewertung, eine Klasse, die beides
verdrahtet, und vier JSON-Dateien mit allen Zahlen.

## Anlegen

```powershell
.\scripts\new-project.ps1 Propeller
```

Das kopiert diesen Ordner nach `src/projects/Propeller/` und ersetzt das Platzhalterwort
`MeinProjekt` überall — in Dateinamen, Klassennamen, Namensraum und in den Kommentarköpfen der
JSONs. Danach:

1. `src/projects/Propeller/PropellerGeometryGenerator.cs` — den Quader durch deine Form ersetzen.
2. `src/projects/Propeller/PropellerFitnessCalculator.cs` — die Formel schreiben.
3. `src/projects/Propeller/project.json` — Parameter, Grenzen, Ziele. Dieselben Zahlen zusätzlich
   in `CreateDefaults()` in `PropellerProject.cs`, das ist die Code-Schicht darunter.
4. `simulation.json` und `solvers/*.json` auf das eindampfen, was vom Code-Standard **abweicht**.
5. Starten:

```powershell
.\scripts\sim.ps1 run -Project Propeller
```

Mehr ist nicht zu tun: die `ProjectRegistry` findet die neue Klasse per Reflection. Es gibt
**keinen** Eintrag in `Program.cs`, keine Liste, keinen `switch`.

## Warum die Vorlage selbst nicht startbar ist

`src/projects/_Vorlage/` ist im Build des Programms ausgeschlossen
(`src/Automatisierung_v2/Automatisierung_v2.csproj`). Sonst gäbe es eine Projektdefinition mit dem
Namen `MeinProjekt`, zu der kein Ordner gehört — und genau das lässt die `ProjectRegistry` beim
Start zu Recht abbrechen. `_Vorlage` taucht deshalb auch nicht in `--list-projects` auf.

**Compiliert wird der Ordner trotzdem** — vom Testprojekt
(`tests/AutomatisierungCleanVersion.Tests/`). Ohne das wäre die Vorlage totes Textmaterial:
änderte sich eine Schnittstelle des Frameworks, würde man es erst beim Anlegen des nächsten
Projekts merken. So bricht stattdessen `dotnet test` — mit der Dateizeile, die nicht mehr passt.

## Der erste Lauf ist ein Rauchtest

Die mitgelieferte `simulation.json` steht auf `MaxIterations = 1` und
`VariantsPerIteration = 2` — zwei Varianten, nicht hundert. Sie beantwortet die Frage, ob die
Kette PicoGK → STL → Gmsh → SU2 → Fitness überhaupt durchläuft. Sitzt das, werden beide Zahlen
hochgesetzt. Alle übrigen Schlüssel in den JSONs tragen bewusst genau den Code-Standard: die
Dateien sind als Nachschlagewerk gedacht, nicht als Konfiguration, die so bleiben soll.
