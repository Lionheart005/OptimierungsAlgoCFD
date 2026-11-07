# `_Vorlage2D` — Startpunkt für ein neues 2D-CFD-Projekt

> **Welche Einstellung was bewirkt und wo man sie findet:** [Bedienhandbuch](../BEDIENHANDBUCH.md) — ohne Programmierkenntnisse lesbar.

Dieser Ordner ist **kein Projekt**, sondern die Kopiervorlage für zweidimensionale CFD-Optimierungen.
Er zeigt vollständig, woraus ein 2D-Projekt besteht: Geometrie (`Shape2D`), Bewertung (`IFitnessCalculator`),
Projektdefinition (`Cfd2DProjectDefinition`) und vier JSON-Dateien.

## Anlegen

```powershell
.\scripts\new-project.ps1 MeinProfil -Template 2D
```

Das kopiert diesen Ordner nach `src/projects/MeinProfil/` und ersetzt das Platzhalterwort
`MeinProjekt2D` überall — in Dateinamen, Klassennamen, Namensraum und in den Kommentarköpfen der
JSONs. Danach:

1. `src/projects/MeinProfil/MeinProfilGeometryGenerator.cs` — die Konturerzeugung anpassen.
2. `src/projects/MeinProfil/MeinProfilFitnessCalculator.cs` — die Zielfunktion schreiben.
3. `src/projects/MeinProfil/project.json` — Parameter, Grenzen und Ziele eintragen (und in `CreateDefaults()` spiegeln).
4. `simulation.json` und `solvers/*.json` auf das eindampfen, was vom Standard abweicht.
5. Starten:

```powershell
.\scripts\sim.ps1 run -Project MeinProfil
```

## Warum die Vorlage selbst nicht startbar ist

`src/projects/_Vorlage2D/` ist im Build des Programms ausgeschlossen
(`src/Automatisierung_v2/Automatisierung_v2.csproj`), damit keine Definition ohne zugehörigen
Ordner entsteht. Compiliert wird der Ordner trotzdem — vom Testprojekt
(`tests/AutomatisierungCleanVersion.Tests/`), um Schnittstellenänderungen sofort zu bemerken.

## Besonderheiten von 2D-CFD

* **Direkter Geometrie-Kernel:** Arbeitet rein auf ebenen Vektorkonturen (`Shape2D`), ohne PicoGK-Voxelgitter und ohne Xvfb/OpenGL.
* **2D-Netz:** `Gmsh2DMesher` erzeugt ein kreisförmiges Fernfeld mit strukturierter Randschicht (Quad-Zellen) und Nachlauf-Box.
* **SU2 2D:** Berechnet 2D-RANS mit `Dimensions = 2` und einstellbarem Anstellwinkel `AngleOfAttackDeg`.
