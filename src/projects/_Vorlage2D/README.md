# `_Vorlage2D` — 2D-Kopiervorlage für neue Profilprojekte

> **Vollständige Schritt-für-Schritt-Anleitung zum Anlegen eines Projekts:** [`src/projects/README.md`](../README.md)  
> **2D-Modellierungs-Handbuch (`Planar2D`-Kernel: `Shape2D`, `Lattice2D`):** [`src/OptimierungsAlgoCFD/Kernels/Planar2D/README.md`](../../OptimierungsAlgoCFD/Kernels/Planar2D/README.md)  
> **Nachschlagewerk für alle JSON-Einstellungen (inkl. 2D-Vernetzungsoptionen):** [`src/projects/BEDIENHANDBUCH.md`](../BEDIENHANDBUCH.md)

Dieser Ordner ist **kein eigenständiges Projekt**, sondern die Kopiervorlage für zweidimensionale CFD-Optimierungen (`Shape2D` → `Gmsh2DMesher` → SU2 2D). Er wird über das Skript

```powershell
.\scripts\new-project.ps1 MeinProfil -Template 2D
```

nach `src/projects/MeinProfil/` kopiert, wobei der Platzhalter `MeinProjekt2D` überall automatisch ersetzt wird. Alle weiteren Schritte zum Anpassen von Kontur, Zielfunktion und Parametern sind zentral in [`src/projects/README.md`](../README.md) beschrieben.

## Besonderheiten von 2D-CFD-Projekten

- **Direkter Geometrie-Kernel (`DirectGeometryKernel`):** Arbeitet direkt auf ebenen Vektorkonturen (`Shape2D`), ohne PicoGK-Voxelgitter und ohne Xvfb/OpenGL.
- **2D-Netz (`Gmsh2DMesher`):** Erzeugt ein kreisförmiges Fernfeld mit strukturierter Quad-Grenzschicht ($y^+ \approx 1$), Kantenverfeinerung an Vorder-/Hinterkante, Detailbox und Nachlauf-Box.
- **SU2 2D:** Berechnet 2D-RANS mit `Dimensions = 2` und einstellbarem (oder aktiv optimierbarem) Anstellwinkel `AngleOfAttackDeg`.
- **Build & Test:** Wie `_Vorlage3D` ist auch `_Vorlage2D` vom Build des Hauptprogramms ausgeschlossen und wird stattdessen vom Testprojekt (`tests/OptimierungsAlgoCFD.Tests/`) compiliert und geprüft.
