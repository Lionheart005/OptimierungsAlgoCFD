# Automatisierte CFD-Formoptimierung

Vorwort: 
Inspiriert durch verschiedene Anregungen im Internet und  Publikationen, nicht zuletzt durch Leap71, wollte ich unbedingt ausprobieren, wie eine Optimierungssoftware aussehen könnte, die unabhängig von großen CAD Programmen mit umständlichen GUI Bedienelementen, sondern nur mit Code komplexe Formen generieren und optimieren kann. Besonders war mir wichtig, die Pipeline komplett unabhängig von der 3. Party Softwarelösung zu halten, sodass man das ganze beliebig Komplex aufziehen kann. Es gibt keine Begrenzung in Parametern, Zielfunktion oder Komplexität der Modelle, noch in der Laufdauer und Algorithmuswahl. Perspektivisch wäre es das Ziel ein Neuronales Netzwerk anzubinden, das selbt den C# code für PicoGK generiert, sowie die Auswertung übernimmt von n-dimensionalen Optimierungsproblemen, die zu komplex für deterministische Bruteforce methoden werden. Natürlich ist hier viel mit KI entstanden, ich bin kein Software Entwickler, und deshalb auch sehr offen für Feedback und Kritik. 


Dieses Framework automatisiert die iterative aerodynamische bzw. hydrodynamische Formoptimierung von 3D-Bauteilen. Aus einem Satz von Entwurfsparametern wird vollautomatisch ein voxelbasiertes 3D-Modell generiert, dieses in ein Rechennetz überführt und anschließend mittels numerischer Strömungsmechanik (CFD) simuliert. Eine anpassbare Zielfunktion bewertet das Strömungsverhalten und übergibt die Fitness an den Optimierungsalgorithmus, der daraus die nächste Parametergeneration ableitet.


```text
Entwurfsparameter
       │
       ▼
 Geometrieerzeugung (PicoGK)     ──► Voxelmodell & STL-Export (optional geglättet)
       │
       ▼
 Vernetzung (Gmsh)               ──► 3D-Windkanal & Rechennetz
       │
       ▼
 Strömungssimulation (SU2)       ──► Druck- und Geschwindigkeitsfeld (optional MPI)
       │
       ▼
 Fitness-Berechnung              ──► Skalare Bewertung (z. B. Widerstand, Auftrieb, Volumen)
       │
       ▼
 Optimierungsalgorithmus         ──► Bestimmung der nächsten Variante (RSM oder Evolution)
       │
       └─────────────────────────► Neuer Zyklus (über Dutzende bis Hunderte Generationen)
```

Die Geometrieerzeugung basiert auf **Voxeln** statt klassischer CAD-Flächen (B-Rep / NURBS). Dadurch gelingen Boolesche Operationen, Verrundungen und Durchdringungen fehlerfrei und ohne abbrechende Flächenverschneidungen – eine Grundvoraussetzung für stabile, unbeaufsichtigte Optimierungsschleifen über hunderte Varianten.

Wie ein vollständiges Projekt mit Geometriebauplan, Zielfunktion und Konfiguration aufgebaut ist, lässt sich beispielhaft im Ordner [`src/projects/MantaAuv/`](src/projects/MantaAuv/) nachvollziehen.

---

## Repository-Struktur

Das Repository trennt den wiederverwendbaren Framework-Kern strikt von den individuellen Bauteilprojekten:

```text
AutomatisierungCleanVersion/
├── src/
│   ├── Automatisierung_v2/       # Modulares C#-Framework (.NET 9)
│   │   ├── Core/                 # Interfaces, Datenmodelle, Algorithmen & Workflow-Steuerung
│   │   ├── Composition/          # Vorgefertigte Kettendefinitionen (z. B. CfdProjectDefinition)
│   │   ├── Kernels/              # Geometrie-Kernel-Anbindungen (PicoGK)
│   │   └── Solvers/              # Solver- und Vernetzer-Anbindungen (Gmsh, SU2)
│   │
│   └── projects/                 # Bauteilprojekte (jeder Ordner entspricht einem Projektnamen)
│       ├── _Vorlage/             # Vollständige Kopiervorlage für neue Bauteile
│       └── MantaAuv/             # Beispielprojekt: AUV-Rumpf mit Sonar-Zielfunktion
│
├── scripts/                      # PowerShell- & Bash-Skripte für Deployment, Serverläufe & Projekt-Setup
├── tests/                        # 250+ Unit- und Integrationstests (lauffähig ohne installierte Solver)
├── config/                       # Lokaler Notausgang für maschinenspezifische Ausnahmedateien (*.local.json)
└── Ergebnisse/                   # Projektbezogene Simulationsergebnisse (CSV, VTU für ParaView, STLs)
```

---

## Kernfunktionen

- **Robuste Voxel-Modellierung (PicoGK):** Algorithmische Geometriegenerierung in C# mit impliziten Funktionen. Komplexe organische Übergänge und Durchdringungen gelingen stabil ohne CAD-Kernel-Abbrüche.
- **Integrierte Geometrieaufbereitung:** Automatische Skalierung auf optimale Voxelauflösung (*RubberBandScaler*), STL-Export und konfigurierbares Oberflächenglätten zur Vermeidung von Treppeneffekten.
- **Vollautomatische Vernetzung (Gmsh):** Parametrische Windkanal-Generierung (Quader oder Zylinder) und automatische Netzverfeinerung in der Grenzschicht.
- **Skalierbare CFD-Simulation (SU2):** Inkompressible Strömungsanalyse (RANS mit Spalart-Allmaras-Turbulenzmodell), konfigurierbar für Mehrkern- und Clusterbetrieb via OpenMPI.
- **Austauschbare Optimierungsalgorithmen:**
  - **RSM (Response Surface Methodology):** Ersatzmodellgestützte Optimierung mittels *Inverse Distance Weighting* (IDW) – ideal für schnelle Konvergenz bei 3 bis ca. 15 Parametern.
  - **Evolutionärer Algorithmus:** (1+λ)-Populationsstrategie mit Rollenverteilung (vom Feintuner bis zum Entdecker) und dynamischer Schrittweitenanpassung nach der 1/5-Erfolgsregel.
- **Türsteher-Validierung (Bouncer):** Jeder Generationssieger wird mit minimal verjitterten Parametern kontrollgerechnet. Instabile Zufallstreffer oder numerisches „Reward Hacking“ werden so zuverlässig ausgesiebt.
- **Compiler-erzwungene Modularität:** Der Kern (`Core`) kennt weder konkrete Projekte noch Solver. Neue Bauteile werden als eigenständige Ordner unter `src/projects/` angelegt und beim Programmstart per Reflection automatisch registriert.
- **Umfassende Ergebnisdokumentation:** Semikolongetrennter CSV-Export (`Simulation_Results.csv`), Speicherung der tatsächlich wirksamen Konfiguration (`effective-config.json`) sowie 3D-Druck- und Strömungsfelddaten (`.vtu`) für ParaView.

---

## Schnellstart

### Voraussetzungen

- Für Entwicklung und Tests: **.NET 9 SDK**
- Für vollständige Simulationsläufe: **PicoGK** (inkl. nativer Laufzeitbibliothek), **Gmsh** und **SU2**

### Tests ausführen

Die gesamte Kern- und Konfigurationslogik wird über gemockte Interfaces getestet und lässt sich ohne externe Simulationssoftware ausführen:

```bash
dotnet test Automatisierung.sln
```

### Vorhandene Projekte auflisten

```powershell
dotnet run --project src/Automatisierung_v2 -- --list-projects
```

### Konfiguration prüfen (Dry-Run)

Prüft die geladene Konfiguration des Projekts, zeigt etwaige Abweichungen an und validiert Programmpfade, ohne eine Simulation zu starten:

```powershell
dotnet run --project src/Automatisierung_v2 -- MantaAuv --print-config
```

### Simulation lokal starten

```bash
dotnet run --project src/Automatisierung_v2 -- MantaAuv
```

---

## Neues Projekt anlegen

Ein neues Bauteil lässt sich mit einem einzigen PowerShell-Befehl aus der Vorlage erzeugen:

```powershell
.\scripts\new-project.ps1 MeinBauteil
```

Das Skript kopiert die Kopiervorlage nach `src/projects/MeinBauteil/` und benennt alle Klassen und Namensräume passend um. Anschließend werden dort lediglich Geometrie (`*GeometryGenerator.cs`), Zielfunktion (`*FitnessCalculator.cs`) und Parametergrenzen (`project.json`) definiert. Am bestehenden Framework-Code muss dafür keine einzige Zeile geändert werden.

---

## Betrieb auf dem Linux-Server

Für zeitintensive Optimierungsläufe stehen vorbereitete Skripte bereit, die den Code auf einen Linux-Server synchronisieren, dort in einer persistenten `tmux`-Sitzung mit `xvfb-run` ausführen und die Ergebnisse abholen:

```powershell
# 1. Umgebung und Pfade auf dem Server prüfen
.\scripts\sim.ps1 doctor -Project MantaAuv

# 2. Code synchronisieren, bauen und Lauf im Hintergrund starten
.\scripts\sim.ps1 run -Project MantaAuv

# 3. Status und Fortschritt abfragen
.\scripts\sim.ps1 status

# 4. Live-Log verfolgen (Strg+C beendet nur die Anzeige)
.\scripts\sim.ps1 log

# 5. Ergebnisse (CSV & Log) auf den lokalen Rechner laden
.\scripts\sim.ps1 fetch -Project MantaAuv
```

Die Ergebnisse werden lokal unter `Serverergebnisse/` abgelegt.

---

## Weiterführende Dokumentation

Detaillierte Anleitungen und technische Hintergründe finden sich in den jeweiligen Ordnern:

- [**Technischer Projektleitfaden** (`src/Automatisierung_v2/README.md`)](src/Automatisierung_v2/README.md)  
  Umfassende Architekturbeschreibung, Interface-Verträge, Ablaufsteuerung, Algorithmen-Details, Konfigurationsschichten und Solver-Anbindung.
- [**Projekt- & Bauteilleitfaden** (`src/projects/README.md`)](src/projects/README.md)  
  Schritt-für-Schritt-Anleitung zur Definition neuer Geometrien, Ausgestaltung von Fitnessfunktionen, Parametereinbindung und Nutzung von `_Vorlage`.
- [**Server- & Deployment-Handbuch** (`scripts/README.md`)](scripts/README.md)  
  Einrichtung des SSH-Zugriffs, Servervoraussetzungen, `sim.ps1`, `sim-runner.sh`, `tmux`-Workflows und gezieltes Herunterladen von ParaView- und STL-Ergebnisdateien.
- [**Konfigurationssystem & Notausgang** (`config/README.md`)](config/README.md)  
  Erläuterung der Konfigurationshierarchie und Handhabung von lokalen Ausnahmedateien (`*.local.json`).
