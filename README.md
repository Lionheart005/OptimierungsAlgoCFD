# Automatisierte CFD-Formoptimierung

Dieses Projekt automatisiert die Formoptimierung von 3D-Bauteilen anhand ihrer aerodynamischen bzw. hydrodynamischen Eigenschaften. Aus einem Parametersatz entsteht eine voxelbasierte Geometrie, daraus ein Rechennetz und anschliessend eine CFD-Simulation. Die Ergebnisse werden als Fitness bewertet und fuer die naechste Optimierungsrunde verwendet.

```text
Parameter
   -> PicoGK: Voxelgeometrie und STL
   -> Gmsh: Rechennetz
   -> SU2: Stroemungssimulation
   -> Fitness: Bewertung
   -> Optimierungsalgorithmus: naechste Parameter
```

Der aktuelle Beispiel-Fall ist das Projekt `MantaAuv`. Das Framework ist jedoch so strukturiert, dass weitere Geometrien, Zielfunktionen, Solver und Optimierungsverfahren ueber klar getrennte Schnittstellen ergaenzt werden koennen.

## Was im Repository steckt

- `src/Automatisierung_v2/` enthaelt das .NET-9-Programm und die eigentliche Pipeline.
- `src/Automatisierung_v2/Core/` enthaelt die projektunabhaengigen Modelle, Interfaces, Algorithmen, Konfiguration und Ablaufsteuerung.
- `src/Automatisierung_v2/Projects/MantaAuv/` enthaelt Geometriegenerator und Fitnessberechnung des Beispielprojekts.
- `src/Automatisierung_v2/Kernels/` bindet PicoGK fuer die Geometrieerzeugung an.
- `src/Automatisierung_v2/Solvers/Cfd/` bindet Gmsh und SU2 fuer Vernetzung und Stroemungssimulation an.
- `config/` enthaelt Framework-, Projekt- und Solver-Konfigurationen.
- `scripts/` steuert Deployments und Simulationslaeufe auf einem Linux-Server.
- `tests/` enthaelt automatisierte Tests fuer Kernlogik, Konfiguration und Pipeline.
- `Serverergebnisse/` ist fuer heruntergeladene Simulationsergebnisse vorgesehen und wird nicht als dauerhafte Ergebnisablage versioniert.

## Kernfunktionen

- Parametrische Voxelgeometrie mit PicoGK
- Automatische STL-Erzeugung und optionales Glatten
- Windkanal- und Netzgenerierung mit Gmsh
- CFD-Berechnung mit SU2, optional ueber MPI
- Antwortflaechenoptimierung (RSM) und evolutionaerer Algorithmus
- Projektabhaengige Fitnessfunktionen bei projektunabhaengigem Pipeline-Kern
- Champion-Validierung durch eine zusaetzliche Kontrollrechnung
- Semikolongetrennter CSV-Export mit Parametern, Metriken, Fitness und Dateipfaden
- Reproduzierbare Tests ohne installierte PicoGK-, Gmsh- oder SU2-Laufzeit

## Schnellstart

Voraussetzung fuer Entwicklung und lokale Tests ist das .NET 9 SDK. Fuer einen vollstaendigen Simulationslauf werden zusaetzlich PicoGK, Gmsh und SU2 benoetigt.

Tests aus dem Hauptordner starten:

```bash
dotnet test Automatisierung.sln
```

Das Beispielprojekt lokal starten:

```bash
dotnet run --project src/Automatisierung_v2 -- MantaAuv
```

Fuer einen kurzen Probelauf koennen lokale Konfigurationsdateien verwendet werden, zum Beispiel `config/simulation.local.json` mit reduzierter Iterations- und Variantenanzahl. Diese lokalen Dateien sind fuer maschinenspezifische Einstellungen gedacht und werden nicht deployed.

## Simulationen auf dem Server

Die vorgesehenen Einstiegspunkte fuer den Serverbetrieb sind:

```powershell
.\scripts\sim.ps1 doctor
.\scripts\sim.ps1 run
.\scripts\sim.ps1 status
.\scripts\sim.ps1 fetch
```

Der Ablauf laeuft auf dem Server in einer `tmux`-Sitzung weiter. Ergebnisse koennen als kompakte CSV und Logdatei oder als vollstaendiger Ergebnisordner abgeholt werden. Fuer diese Befehle werden eine vorbereitete Linux-Umgebung, SSH-Zugriff per Schluessel sowie Gmsh, SU2, PicoGK und .NET auf dem Server vorausgesetzt.

## Testlauf und Ergebnisbeispiel

Der Bericht [OptimierungsAlgoTestlaufBericht.pdf](OptimierungsAlgoTestlaufBericht.pdf) dokumentiert einen kleinen Testlauf des selbst entwickelten Optimierungsprogramms. Er beschreibt die modulare C#-Architektur, PicoGK-basierte Geometrieerzeugung, Gmsh-Meshing, SU2-CFD sowie die Auswertung in ParaView.

Im dort gezeigten Lauf wird ein Gewinner-Modell mit folgenden Kennwerten berichtet:

| Kennwert | Wert |
| --- | ---: |
| Score | 184311296 |
| Length | 85.93 mm |
| Width | 71.28 mm |
| MainRadius | 12.77 mm |
| WingRadius | 2.91 mm |
| TailTaper | 0.28 |
| Volume | 32994 mm3 |
| Sensorflaeche | 1108 mm2 |

Der Report stellt ausserdem Druckverteilungen und Stromlinien des Gewinner-Modells sowie besonders klobige, aerodynamisch guenstige und insgesamt schlecht bewertete Varianten gegenueber. Die Ergebnisse sind als dokumentierter Testlauf zu verstehen; die genaue physikalische Interpretation und die noch offenen Vergleichs- und Kalibrierlaeufe sind in der technischen Dokumentation beschrieben.

## Dokumentation

- [Technischer Projektleitfaden](src/Automatisierung_v2/README.md) beschreibt Bedienung, Konfiguration, Architektur, Erweiterungspunkte und Tests.
- [Server- und Deployment-Anleitung](scripts/README.md) beschreibt SSH-Voraussetzungen, `sim.ps1`, `sim-runner.sh`, Serverlaeufe und das Abrufen von Ergebnissen.
- [Testlaufbericht](OptimierungsAlgoTestlaufBericht.pdf) zeigt den dokumentierten Beispielversuch inklusive Visualisierungen und Ausblick.

Im Ordner `src/Automatisierung_v2/Projects/` liegt derzeit keine eigene README-Datei; projektspezifische Informationen zum Beispiel `MantaAuv` stehen im technischen Projektleitfaden und in `config/projects/MantaAuv.json`.

## Architektur in einem Satz

`Core` definiert die Vertraege und steuert den Ablauf, waehrend Projekte die Geometrie und Zielfunktion liefern und Solver die physikalischen Berechnungen ausfuehren.

## Bekannte Grenzen und offene Punkte

Das Framework ist fuer automatisierte Konzeptuntersuchungen und den Vergleich von Makroformen ausgelegt. Voxelaufloesung, Rechenzeit, die verwendeten physikalischen Modelle sowie die Konfiguration von Referenzflaeche und Stroemungsgeschwindigkeit beeinflussen die Aussagekraft der Ergebnisse. Der technische Leitfaden dokumentiert ausserdem offene Punkte wie den End-to-End-Vergleich mit der Vorgaengerversion, die Nachjustierung der Bouncer-Toleranz und moegliche weitere Optimierungsverfahren.

## Lizenz

Im Repository ist derzeit keine separate Lizenzdatei dokumentiert.
