# `_Vorlage` — 3D-Kopiervorlage für neue Projekte

> **Vollständige Schritt-für-Schritt-Anleitung zum Anlegen eines Projekts:** [`src/projects/README.md`](../README.md)  
> **3D-Modellierungs-Handbuch (PicoGK):** [`src/Automatisierung_v2/Kernels/PicoGk/README.md`](../../Automatisierung_v2/Kernels/PicoGk/README.md)  
> **Nachschlagewerk für alle JSON-Einstellungen:** [`src/projects/BEDIENHANDBUCH.md`](../BEDIENHANDBUCH.md)

Dieser Ordner ist **kein eigenständiges Projekt**, sondern die Kopiervorlage für 3D-CFD-Bauteile (PicoGK → Gmsh 3D → SU2). Er wird über das Skript

```powershell
.\scripts\new-project.ps1 MeinBauteil
```

nach `src/projects/MeinBauteil/` kopiert, wobei der Platzhalter `MeinProjekt` in allen Datei-, Klassen- und Namensraumnamen automatisch ersetzt wird. Alle weiteren Schritte (Parameter, Geometrie, Zielfunktion, Aufräumen der JSONs und Start) sind zentral in [`src/projects/README.md`](../README.md) beschrieben.

## Besonderheiten dieser Vorlage

- **Vom Hauptprogramm-Build ausgeschlossen, vom Testprojekt compiliert:**  
  `src/projects/_Vorlage/` wird in `src/Automatisierung_v2/Automatisierung_v2.csproj` vom Build ausgeschlossen, damit keine Projektdefinition mit dem Platzhalternamen `MeinProjekt` ohne passenden Ordner entsteht (sie erscheint daher auch nicht unter `--list-projects`). Compiliert und geprüft wird der Ordner stattdessen durch `tests/AutomatisierungCleanVersion.Tests/`, sodass Schnittstellenänderungen sofort bei `dotnet test` auffallen.
- **Kleiner erster Lauf:**  
  In `simulation.json` stehen `MaxIterations = 5` und `VariantsPerIteration = 5` (Standard 10 × 10). Die Netzkalibrierung steht auf `"Auto"` (in 3D Stunden); für einen schnellen Rauchtest der Kette PicoGK → STL → Gmsh → SU2 → Fitness `"Manual"` mit eingetragenem `MeshRefinementFactor` verwenden. Alle übrigen JSON-Einträge zeigen die dokumentierten Code-Standardwerte als Nachschlagewerk.
