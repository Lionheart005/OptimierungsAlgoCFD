# Automatisierte CFD-Formoptimierung

Vorwort: 
Inspiriert durch verschiedene Anregungen im Internet und  Publikationen, nicht zuletzt durch Leap71, wollte ich unbedingt ausprobieren, wie eine Optimierungssoftware aussehen könnte, die unabhängig von großen CAD Programmen mit umständlichen GUI Bedienelementen, sondern nur mit Code komplexe Formen generieren und optimieren kann.
Besonders war mir wichtig, die Pipeline komplett unabhängig von der 3. Party Softwarelösung zu halten, sodass man das ganze beliebig Komplex aufziehen kann. Es gibt keine Begrenzung in Parametern, Zielfunktion oder Komplexität der Modelle, noch in der Laufdauer und Algorithmuswahl. Perspektivisch wäre es das Ziel ein Neuronales Netzwerk anzubinden, das selbt den C# code für PicoGK generiert, sowie die Auswertung übernimmt von n-dimensionalen Optimierungsproblemen, die zu komplex für deterministische Bruteforce methoden werden. Natürlich ist hier viel mit KI entstanden, ich bin kein Software Entwickler, und deshalb auch sehr offen für Feedback und Kritik. 

Wie es funktioniert:

Man beschreibt die zu optimierende Form in C# Code und definiert gewisse Variablen, die man anpassen lassen möchte. z.B. Länge und Höhe, aber auch komplexere Funktionen, eigentlich alles was man sich mathematisch vorstellen kann. Es wäre theoretisch durchaus Denkbar ein Mandelbrot zu optimieren (Warum auch immer man das tun sollte). Nun definiert man passive Parameter, die auch dazu gehören, aber indirekt aus der Form resultieren, wie Volumen und Oberfläche. 
Dann entscheidet man nach welcher Funktion der Algorithmus optimieren soll. Hier muss man sich gut überlegen was man eigentlich möchte. Ein Klassiker wäre z.B. Fitness = Volumen / Widerstand (Also möglichst großes Volumen bei geringem Widerstand.)

Nun schickt man sein Bauteil durch die Optimierungsschleife und am Ende gibt einem der Algorithmus die Werte aus, die in der Funktion die beste FItness (Score) erreichen. 

--> 'Unser Bauteil ist am besten 30 cm lang und 5 cm hoch um das beste Verhältnis aus Volumen und Widerstand zu bekommen.'
Und wir bekommen gleich das druckbare .stl Modell dazu!

Die Optimierungsverfahren und die Physiksolver sind hier austauschbar, das Prinzip bleibt das gleiche. Ich versuche das Programm möglichst Problem Unabhängig zu halten, bis jetzt sind aber nur Fluidsimulationen möglich in 2D und 3D im Strömungskanal. 

Für die technischen Details sind die folgenden Handbücher und .md Dateien da. Dort steht alles was man wissen muss an Einstellungen. (Und halleluja sind das inzwischen viele, ich habe selber an manchen Stellen den Überblick verloren.)

Für meine Arbeit habe ich jetzt SU2 und GMSH genutzt für Netz und Simulation. der 3D Kernel ist PicoGK, der 2D Kernel ist selber geschrieben (...geprompted) und versucht in den Befehlen möglichst nah an PicoGK zu bleiben. 

---

## Weiterführende Dokumentation & Handbücher

Die detaillierte Dokumentation ist thematisch aufgeteilt, sodass jedes Thema genau einen zentralen Ort hat:

- **[Bedienhandbuch (`src/projects/BEDIENHANDBUCH.md`)](src/projects/BEDIENHANDBUCH.md)**  
  Vollständiges Nachschlagewerk für die Bedienung ohne Programmierkenntnisse: Ablauf eines Laufs, häufige Aufgaben („Ich will …“), Erklärung **aller** JSON-Einstellungen (`project.json`, `simulation.json`, `su2.json`, `gmsh.json` für 3D und 2D), Ergebnisdateien, Konsolenbefehle und Fehlermeldungen. **Hier anfangen.**
- **[Neues Bauteil / Projekt anlegen (`src/projects/README.md`)](src/projects/README.md)**  
  Schritt-für-Schritt-Anleitung zum Erstellen eigener 3D- und 2D-Projekte per `.\scripts\new-project.ps1`, Programmierung von Geometrie (`IGeometryGenerator`) und Zielfunktion (`IFitnessCalculator`) sowie Umgang mit den Kopiervorlagen [`_Vorlage/`](src/projects/_Vorlage/README.md) (3D) und [`_Vorlage2D/`](src/projects/_Vorlage2D/README.md) (2D).
- **[Architektur- & Entwicklerhandbuch (`src/Automatisierung_v2/README.md`)](src/Automatisierung_v2/README.md)**  
  Technische Dokumentation des C#-Frameworks: Repository- und Schichtarchitektur (`Core`, `Composition`, `Kernels`, `Solvers`), Interfaces, Datenmodelle, Algorithmen, Konfigurations-Loader, Anbindung von PicoGK, Gmsh und SU2 sowie Test-Architektur.
- **[Server-, Deployment- & Einrichtungs-Handbuch (`scripts/README.md`)](scripts/README.md)**  
  Steuerung und Überwachung von Läufen auf dem Linux-Hochschulserver von Windows aus (`sim.ps1`, `sim-runner.sh`, `resume`, `fetch`), Konvergenz-Auswertung (`plot_history.py`) sowie die komplette Anleitung zur erstmaligen Server-Einrichtung ohne Admin-/Sudo-Rechte (`setup_server.sh` & manuelle Installation).
- **[Lokale Ausnahmekonfiguration (`config/README.md`)](config/README.md)**  
  Funktionsweise des Notausgangs über gitignorierte `*.local.json`-Dateien für maschinenspezifische Programmpfade.
- **[Entwicklungs-Roadmap & Projektpläne (`.plans/README.md`)](.plans/README.md)**  
  Übersicht über die nächsten Ausbaustufen ([`ROADMAP.md`](.plans/ROADMAP.md)), KI-/Bayes-Optimierung ([`ki_optimierung.md`](.plans/ki_optimierung.md)) und geplante FEM-Strukturkopplung ([`fem_ausblick.md`](.plans/fem_ausblick.md)).
