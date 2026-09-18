================================================================================
PicoGK AUTOMATED CFD OPTIMIZATION FRAMEWORK (CLEAN ARCHITECTURE EDITION)

BESCHREIBUNG & PHILOSOPHIE

Dieses Framework ist eine vollständig automatisierte, algorithmen-gesteuerte
Pipeline zur aerodynamischen Formoptimierung (Upfront-CAE). Es verbindet
parametrische Voxel-Geometrie (PicoGK) mit robuster Netzgenerierung (Gmsh) und
professioneller numerischer Strömungsmechanik (SU2).

Das System zeichnet sich durch seine absolute "Absturzsicherheit" in Bezug auf
die Geometrie aus: Da Voxel anstelle von klassischen B-Rep/NURBS-Flächen
verwendet werden, können sich Bauteile beliebig durchdringen oder verschmelzen,
ohne dass die Automatisierungskette reißt.

Nach einem umfassenden Refactoring basiert das Framework auf strenger
Modularität ("Clean Architecture") und zentraler Datenhaltung. Algorithmen,
Fitness-Gleichungen und Konfigurationen sind strikt voneinander getrennt.

VORAUSSETZUNGEN

Folgende Software muss im System installiert und die Pfade in der
SimulationData.cs hinterlegt sein:

C# / .NET SDK (Empfohlen: .NET 9.0+).

PicoGK Framework (Im Projekt referenziert). Version 26.2.0

Gmsh (Aufruf via Pfad). Genutzte Version 4.12.2

SU2 CFD-Suite (MPI-fähig). Version 7.5.1

================================================================================
TEIL 1: DIE ARCHITEKTUR & MODULE

Jede Klasse hat einen extrem klaren, isolierten Aufgabenbereich (Separation
of Concerns):

Program.cs (Der Startschuss)
-> Hier wird definiert, welcher Algorithmus (via IOptimizationAlgorithm)
verwendet wird, bevor die PicoGK-Schleife startet.

SimulationData.cs (Die Datenbank & Das Kontrollzentrum)
-> Beinhaltet ALLE Einstellungen (Pfade, Multi-Core, Voxel-Auflösung,
Strömungsgeschwindigkeit, Mutationsraten).
-> Speichert die Historie mit strikter Trennung von aktiven (mutierten)
und passiven (gemessenen) Parametern.

WorkflowController.cs (Der Dirigent)
-> Die reine Hauptschleife. Koordiniert Mesher, Solver und Algorithmus.

FitnessCalculator.cs (Der Schiedsrichter)
-> ZENTRALER ORT FÜR ZIEL-GLEICHUNGEN. Hier wird definiert, ob Abtrieb,
Widerstand oder Volumen maximiert/minimiert werden sollen.

PicoGkGenerator.cs (Die Voxel-Fabrik)
-> Baut Geometrien aus Parametern und glättet sie (StlSmoother).

GmshConverter.cs (Der Mesher)
-> Nutzt Distanzfelder (Background Fields), um am Modell extrem fein und
nach außen hin effizient grob zu vernetzen. Unterstützt Multi-Core!

FluidDynamicsAnalyzer.cs (Der CFD-Solver)
-> Nutzt Euler (Reibungsfrei) + JST-Dämpfung + ohne MUSCL, um maximale
Robustheit gegen numerisches "Voxel-Rauschen" zu garantieren.

================================================================================
TEIL 2: DIE ALGORITHMEN (EA vs. RSM)

Das System unterstützt durch das Interface IOptimizationAlgorithm beliebig
viele Lösungsstrategien. Aktuell integriert:

EvolutionaryAlgorithm.cs (Der blinde Bergsteiger)
-> Mutiert das beste bekannte Modell (Survival of the Fittest).
-> Nutzt 4 Rollen (Feintuner, Vorsichtiger, Normaler, Entdecker) und die
1/5-Erfolgsregel nach Rechenberg zur dynamischen Anpassung der Streuung.

RsmOptimizationAlgorithm.cs (Der smarte Kartograf / Surrogate Model)
-> PHASE 1 (DoE): Generiert zufällige Designs, um den Raum zu verstehen.
-> PHASE 2: Baut ein mathematisches Ersatzmodell (Inverse Distance Weighting)
auf Basis aller bisherigen CFD-Ergebnisse. Simuliert 50.000 Modelle
in Millisekunden virtuell und schickt nur die vielversprechendsten in
den echten Windkanal. Massive Zeitersparnis!

================================================================================
TEIL 3: DAS HERZSTÜCK ANPASSEN (DIE ZIELE)

ALLE physikalischen Ziele werden ab sofort in EINER einzigen Datei angepasst:
-> FitnessCalculator.cs

ACHTUNG EULER-STRÖMUNG:
Da das System aktuell den SU2 Euler-Solver (reibungsfrei) nutzt, gibt es
keinen Reibungswiderstand, sondern nur Formwiderstand. CD-Werte im Bereich
von 0.005 bis 0.05 sind hier völlig normal und realistisch!

Möchtest du statt Autos (Drag minimieren) lieber Tragflächen optimieren
(Abtrieb/Downforce maximieren)?
Ändere in der FitnessCalculator.cs einfach die Endgleichung!

================================================================================
TEIL 4: ERWEITERBARKEIT (NEUE PARAMETER)

Willst du Spoiler-Winkel oder Diffusor-Höhen optimieren? Das Framework
passt CSV, Algorithmen und Skalierungen vollautomatisch an.

Gehe in den Konstruktor von SimulationData.cs:

BaseParameters.Add("SpoilerWinkel", 15.0f);

MaxDeviations.Add("SpoilerWinkel", 2.5f);

ParameterBounds.Add("SpoilerWinkel", (0f, 45f));

Nutze dann diesen Parameter in der PicoGkGenerator.cs zum Bauen der Geometrie.
Fertig!

================================================================================
GRENZEN DES SYSTEMS & HINWEISE FÜR NEUE PROJEKTE

Obwohl die Automatisierung industriellen Standards (Upfront-CAE) entspricht,
gibt es physikalische und mathematische Grenzen, die man kennen muss:

Der Fluch der Dimensionalität (RSM-Limit):
Der Ersatzmodell-Algorithmus (IDW) funktioniert exzellent bei 3 bis 15
Parametern. Je mehr Parameter (Dimensionen) du hinzufügst, desto mehr
Basis-Simulationen (DoE) braucht er, bevor er verlässliche Vorhersagen
treffen kann. Bei 50 Parametern kollabiert das System mathematisch.
Tipp: Halte die Parameterzahl für das RSM unter 10 oder nutze den
Evolutionsalgorithmus für hochdimensionale Probleme.

Gitterabhängigkeit & Grenzschicht:
Das System ist fantastisch für die "frühe Konzeptphase" (Makro-Formen
testen). Da PicoGK Voxel nutzt, ist die Oberfläche nie zu 100% mikroskopisch
glatt (wie bei NURBS/CAD-Splines). Der Euler-Solver mit JST-Dämpfung fängt
dies extrem gut ab.
ABER: Dieses System ist NICHT geeignet, um den Umschlagpunkt einer
turbulenten Grenzschicht (z.B. Navier-Stokes mit k-omega SST) im Sub-
Millimeterbereich zu berechnen.

"Reward Hacking" & Physik-Cheats:
Optimierungsalgorithmen sind "böse Genies". Wenn du das Volumen nicht hart
limitierst, wird der Algorithmus das Auto auf die Größe eines Staubkorns
schrumpfen, weil das den wenigsten Luftwiderstand hat. Nutze stets die
"BouncerTolerance" (den Validierungs-Türsteher) und harte Bounds in
der SimulationData, um numerische Bugs auszufiltern.

================================================================================
AUSSTEHENDE VERBESSERUNGEN
================================================================================
den code etwas zentralisierter was die einstellungen angeht in SimulationData
packen und nochmal unabhängigkeit vom code zu den parametern testen. 
(einmal mit neuen parametern ausprobieren, schauen wo alles der code
verändert werden muss und entsprechend evt. modularer aufbauen...)
alles nochmal bisschen aufräumen mit sinvollen kommentaren versehen, 
eine detailiertere und neue version von der readme anleitung erstellen,
inklusive dem neuen stlsmoother.
Konkret im code verbessern:
- kommentare die aus workflow stammen löschen und sinvolle neue kommentare HINZUFÜGEN
- kalibrierung am anfang? also schauen dass jitter elemente stabil bleiben und dann einstellungen beibehalten?
- wenn ein modell abstürzt soll der rsm algo das nicht berücksichtigen. 
- wenn ein paremter auf 0 mutation gesetzt ist, soll der rsm algo den aus der gleichung rausnehmen oder 
es muss eine andere möglichkeit geben parameter festzuschrauben...

andere optimierungsverfahren als alternative HINZUFÜGEN:
Gradienten abstiegsverfahren/antwortflächenbasierte optimierung (RSM) durch mathematische ersatzmodelle.
parameterkorelation 


