# FEM-Ausblick: Merkzettel für später

> **Stand: 05.11.2026. Zurückgestellt** (mit dir abgestimmt am 05.11.2026: „eins nach dem anderen").
> Das hier ist **kein Arbeitsplan** und hat keine TODO-Nummern. Der Zettel hält fest, was bei der
> Beratung aufgefallen ist, damit spätere Änderungen das Programm nicht aus Versehen FEM-untauglich
> machen. Wird FEM angegangen, entsteht daraus ein eigener Plan.

---

## Kurzfassung

* **Es ist kein Komplettumbau nötig.** Die Kette `SolverStage[]` ist schon für „erst CFD, dann FEM"
  angelegt. Alle Kennwerte landen im selben `PassiveParameters`, eine Fitness kann also schon heute
  Drag und Spannung zusammen bewerten.
* **Favorit als Werkzeug: CalculiX (`ccx`).** Leichtgewichtig, quelloffen, ohne sudo installierbar.
* **Schwierig ist nicht der Solver, sondern die Randbedingungen** auf einer Geometrie, die sich in
  jeder Variante ändert.

---

## Was schon passt

| Baustein | Beleg | Warum es für FEM reicht |
| --- | --- | --- |
| Stufenkette aus Mesher und Solver | `Core/Models/SolverStage.cs`, `WorkflowController.cs` (Schleife über `_stages`) | Eine FEM-Stufe ist eine weitere `SolverStage` mit eigenem Mesher. |
| Netz-Cache je Mesher-Instanz | `WorkflowController.cs`, `meshCache` | CFD-Netz (Fluidraum) und FEM-Netz (Bauteil) entstehen getrennt und werden je einmal erzeugt. |
| Gemeinsamer Kennwert-Topf | `ModelRecord.PassiveParameters` | Die Fitness sieht CFD- und FEM-Werte nebeneinander. |
| Kern kennt keine Solver | `Core/` hat kein `using` auf `Solvers/` (TODO-20) | Ein FEM-Solver kommt nach `Solvers/Fem/`, und der Kern bleibt unberührt. |
| Gmsh als einziger Vernetzer | `Solvers/Cfd/GmshCfdMesher.cs` | Gmsh schreibt auch das CalculiX-Eingabeformat (`.inp`), samt quadratischer Tetraeder (C3D10). |

## Lücken, die beim Einstieg zu schließen wären

| # | Lücke | Beleg | Folge |
| --- | --- | --- | --- |
| 1 | Die Stufen sind fest im Code eingetragen. | `Composition/CfdProjectDefinition.cs`, `CreateStages` | FEM ein- oder ausschalten hieße C# ändern. **Ziel:** `"Stages": ["Cfd", "Fem"]` in `simulation.json`. Fehlt der Schlüssel, bleibt alles wie heute. |
| 2 | Zwischen den Stufen fließen nur Zahlen. | `ModelRecord` hat nur `float`-Wörterbücher | Die FEM bekäme die Druckverteilung aus SU2 nicht. **Ziel:** ein allgemeines `record.Artifacts["Cfd.SurfacePressure"] = <Pfad>`. |
| 3 | `MeshCells` wird von jedem Mesher überschrieben, die Zählung liest nur das SU2-Format. | `WorkflowController.cs:313-316` | Zellzahlen beider Netze vermischen sich. **Ziel:** Der Mesher meldet seine Zellzahl selbst, mit Stufenkennung. |
| 4 | Ein einziger Netzfaktor gilt für alle Mesher. | `SimulationContext.EffectiveMeshRefinementFactor` | Die Netzkalibrierung würde CFD- und FEM-Netz immer gemeinsam verfeinern. **Ziel:** ein Faktor je Stufe. |
| 5 | `RequiredMetrics` weiß nicht, welche Stufen aktiv sind. | `IFitnessCalculator.RequiredMetrics`, `WorkflowController.EnsureRequiredMetrics` | Mit ausgeschalteter FEM bräche der Lauf ab, weil FEM-Werte fehlen. |
| 6 | Kein Frühabbruch zwischen Stufen | — | Zeigt die FEM, dass das Teil bricht, läuft trotzdem die teure CFD. Linear-statische FEM dauert Sekunden bis wenige Minuten und taugt deshalb als **billiger Vorfilter vor der CFD**, solange die Lasten nicht aus der CFD kommen. |

## Werkzeugwahl (Stand 11/2026)

* **CalculiX (Empfehlung).**
  * Auf conda-forge als Paket `calculix` (2.23, für linux-64 **und** win-64). Auf dem Server läuft
    Miniconda schon ohne sudo (`setup_server.sh`), und lokal auf Windows lässt sich auch testen.
  * Rückfall: von Hand bauen mit make und gfortran, SPOOLES und ARPACK als Quellen.
  * Für ParaView gibt es `ccx2paraview` (ebenfalls conda-forge).
  * Kann Festigkeit, Steifigkeit, Eigenfrequenzen, Beulen, Wärme, Kontakt und Plastizität.
  * Die Ergebnisse sind Textdateien (`.dat`, `.frd`) und in C# leicht auszulesen.
  * Steckt auch in FreeCAD-FEM und PrePoMax, ist also breit erprobt.
* **SU2-Strukturlöser.** Schon installiert, aber auf große Verformungen und hyperelastisches Material
  ausgelegt. Eigenfrequenz- und Beulanalyse, Plastizität und Kontakt kann er nach heutigem Wissen
  nicht. Interessant wäre er nur für eine echte Fluid-Struktur-Kopplung.
* **Elmer:** stark bei Multiphysik, für reine Mechanik aber aufwendiger.
* **Code_Aster:** zu schwergewichtig.
* **FEniCSx:** Hier schreibt man die Gleichungen selbst, das ist nicht „wenig konfigurieren".

## Physik-Fallstricke

* **Randbedingungen auf wechselnder Geometrie.** Eine STL-Datei hat keine benannten Flächen. Die beste
  Lösung ist, dass der Geometrie-Generator Bereiche mitmeldet, etwa „Halterung" als Box. Er macht das
  so, wie er heute schon Kennwerte meldet (`AddMetric`). So wandern die Bereiche mit der Form mit.
* **Last aus der CFD lohnt sich nicht immer.** Bei Flügeln und Flossen ist sie sinnvoll. Beim AUV
  dominiert der Wasserdruck: Schon in 10 m Tiefe sind das etwa 1 bar, der Staudruck bei 2 m/s nur
  etwa 0,02 bar. Dort zählen Außendruck und Beulen.
* **Vollkörper oder Schale.** Ein massiver Voxelkörper hält praktisch immer. Aussagekräftig wird FEM
  erst, wenn der Generator die Wandstärke modelliert.
* **Spannungsspitzen an Einspannkanten konvergieren nie.** Der Optimierer würde solche Netzartefakte
  ausnutzen. Robust sind Steifigkeit, Verschiebung und Eigenfrequenz. Spannungen nur in
  Auswertebereichen bewerten, die die Einspannung ausklammern.
* **Einheiten und Skalierung.** Das FEM-Netz muss in echter Größe vorliegen, also wie beim CFD-Netz
  über `ScaleFactor` zurückskaliert. Die Einheiten müssen zusammenpassen: mm, N, MPa, t/mm³.

---

## Im Hinterkopf behalten: Leitplanken für künftige Änderungen

Wer am Framework etwas ändert, kann mit wenig Aufwand dafür sorgen, dass FEM später leicht einsteckbar
bleibt:

1. **Keine CFD-Annahmen in `Core/`.** Keine Metriknamen wie „Drag" und keine SU2-Dateiformate in den
   Kern. Der Aufruf `MeshMetrics.ReadSu2CellCount` im Controller ist so eine Stelle: Wer sie ohnehin
   anfasst, lässt den Mesher die Zellzahl selbst melden.
2. **Neue Netz- oder Solver-Einstellungen je Stufe denken, nicht global.** Die Lehre aus
   `MeshRefinementFactor`: Was heute global ist, muss später für FEM aufgeteilt werden.
3. **Dateien zwischen Stufen über einen allgemeinen Mechanismus.** Kein Sonderweg „SU2 legt Datei X
   ab, Y holt sie". Wenn so etwas nötig wird, gleich `ModelRecord.Artifacts` einführen.
4. **Kennwertnamen eindeutig je Physik.** Bestehende Namen wie `Drag` bleiben. Neue Kennwerte anderer
   Physik bekommen ein Präfix, etwa `Fem_MaxVonMises` oder `Fem_Mass`.
5. **Fitness nur über `RequiredMetrics` und vorhandene Kennwerte.** Die Fitness rechnet nur mit dem,
   was sie deklariert. Das hilft auch dem KI-Plan (Modus „Metrics", TODO-53): Ein FEM-Kennwert bekommt
   dort automatisch ein eigenes Ersatzmodell, der KI-Teil ist also von selbst FEM-fähig.
6. **Neue Projekte: Wandstärke und Anbindungspunkte mitdenken.** Das heißt nicht modellieren. Es
   heißt nur: Wo ein Bauteil gehalten wird, lässt sich im Generator als Bereich benennen.
7. **Gmsh bleibt der einzige Vernetzer.** Ein zweites Vernetzungsprogramm würde Installation und Pflege
   verdoppeln.
8. **Rückwärtskompatibel erweitern.** Neuer Schlüssel fehlt, dann gilt das Verhalten von heute. So
   halten es alle bisherigen Pläne, und so bleibt „alles funktioniert auch ohne FEM" gesichert.

## Wenn es losgeht: sinnvolle Reihenfolge

1. Stufen per Konfiguration schalten (Lücke 1), Kennwerte je Stufe (Lücken 3 und 5).
2. CalculiX-Stufe mit **festen** Lasten an einem einfachen Testprojekt, etwa einem Kragbalken. Ihn mit
   der Balkentheorie gegenprüfen, so wie Naca2D mit den NASA-TMR-Daten geprüft wird.
3. Bereiche aus dem Generator (Halterung, Lastfläche).
4. Erst dann: Druck aus der CFD übertragen (Lücke 2) und optional der Frühabbruch (Lücke 6).

## Fragen, die dann zu klären sind

* Was soll die FEM bewerten: Festigkeit, Steifigkeit, Eigenfrequenz, Beulen oder Wärme? An welchem
  Projekt zuerst?
* Woher kommen die Lasten: aus der CFD, aus festen Werten (Kraft, Wasserdruck) oder aus beidem?
* Soll die Geometrie eine Wandstärke als Parameter bekommen?
* Reihenfolge: FEM als Vorfilter vor der CFD, oder CFD → FEM?
* Material: Reicht isotrop-linear-elastisch, oder braucht es 3D-Druck-Anisotropie bzw. Plastizität?
* Installation: conda als Hauptweg, Bau von Hand als dokumentierter Rückfall?
* FEM auch in 2D, etwa ein rotationssymmetrischer Querschnitt einer Druckhülle, der in Sekunden
  gerechnet ist?
