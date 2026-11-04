# Umsetzung der Anmerkungen: Skripte, SU2-Verständnis, Kennwert-Verlauf, Physik, 2D

> **Stand: 04.11.2026** — Branch `MajorPhysikFixes`. Quelle ist `.plans/Anmerkungen.txt`, und zwar
> alles **außer** dem Block „Erweiterungen" (KI/ML, FEM). Die Nummerierung setzt
> `projektbuendel.md` fort (letzter Schritt dort: TODO-30), hier also **TODO-31 ff.**
> **Status: nur Plan, noch nichts umgesetzt.** Die Rückfragen sind beantwortet (siehe
> „Entscheidungen"). Offen ist nur noch ein Zahlenwert (die Anströmgeschwindigkeit von MantaAuv,
> siehe „Offene Fragen").

---

## Fortschritt (für die Übergabe)

| Block | Schritt | Inhalt | Status |
| --- | --- | --- | --- |
| A | TODO-31 | `fetch`/`status` nehmen das Projekt des letzten Laufs, ein Archiv statt vieler scp-Aufrufe | [ ] |
| A | TODO-32 | Sensible Daten: `~`-Pfade statt Benutzername, IP raus, Hygiene-Test | [ ] |
| A | TODO-33 | Kleinkram: wackeliger Test (Umgebungsvariable + Parallelität), doppelt kodiertes UTF-8 in `JsonConfigLoader` | [ ] |
| B | TODO-34 | Randbedingungen und Gleichungen als Kommentar in cfg, geo und JSON | [ ] |
| C | TODO-35 | `history.csv` je Variante sichern, Konvergenz und Schwingung bewerten | [ ] |
| C | TODO-36 | `scripts/plot_history.py` + fetch-Anbindung | [ ] |
| C | TODO-37 | *Später:* zeitgenauer Modus (URANS) für Einzelprüfungen | [ ] |
| D | TODO-38 | Anströmung in m/s statt Mach × Schallgeschwindigkeit, Reynoldszahl ins Log | [ ] |
| D | TODO-39 | `MeshRequest` + ein Netzfaktor für alle Zellgrößen, Nachlauf-Verfeinerung, Zellzahl | [ ] |
| D | TODO-40 | Prismen-Grenzschicht mit vorgegebenem y+ (3D), Server-Spike zuerst | [ ] |
| D | TODO-41 | y+ nach jeder Rechnung auswerten und melden | [ ] |
| D | TODO-42 | Netzkalibrierung: eigener Befehl **und** Auto-Modus am Laufanfang | [ ] |
| D | TODO-43 | Gewinner in feinerer Auflösung nachrechnen (Modus je Lauf wählbar) | [ ] |
| E | TODO-44 | 2D-Kernel `Kernels/Planar2D` (C#, Clipper2) mit PicoGK-ähnlicher API | [ ] |
| E | TODO-45 | `Gmsh2DMesher` mit strukturierter Quad-Grenzschicht | [ ] |
| E | TODO-46 | SU2 in 2D: Anstellwinkel, Bezugslänge statt -fläche, `Cfd2DProjectDefinition` | [ ] |
| E | TODO-47 | Projekt `Naca2D` + `_Vorlage2D` + Validierung gegen NASA-TMR-Referenzdaten | [ ] |

**Verifikationsstand vor Beginn (04.11.2026):** siehe Abschnitt „Ausgangslage", letzte Zeile.

---

## Ziel

Die Anmerkungen fallen in fünf Gruppen:

1. **Skript:** `fetch` funktioniert nicht zuverlässig; keine Schlüssel oder sensiblen Daten im Code.
2. **SU2 verstehen:** Wo steht, wo Fluid ein- und ausströmt, und welche Randbedingungen gelten?
   Die Antwort kommt als kurzer Kommentar in die Konfigurationsdateien.
3. **Kennwert-Verlauf:** Drag und Co. über den Verlauf *einer* Simulation ansehen und plotten, um
   eine Rechnung von Hand prüfen zu können.
4. **Physikalische Belastbarkeit:** strukturierte Wandschichten mit einheitlichem y+, feinere
   Netze um den Körper und im Nachlauf, eine Gitterstudie zur Wahl der Netzfeinheit und eine
   Nachrechnung der Gewinner in feinerer Auflösung.
5. **2D-Simulation:** detailliert und schnell — mit einer Empfehlung, wie das am elegantesten und
   zukunftsfähigsten geht.

---

## Ausgangslage (geprüft am 04.11.2026)

| # | Befund | Beleg | Bedeutung |
| --- | --- | --- | --- |
| 1 | **`fetch` holt das falsche Projekt.** Die drei Ordner `Serverergebnisse/BIC_12_20261023_*` enthalten nur `simulation.log`, keine CSV. Im Kopf des Logs steht `Projekt: TestprojektNACA`, `fetch` lief aber ohne `-Project` und hat die Vorgabe `MantaAuv` genommen. | `sim.ps1:72` (Vorgabe), `sim.ps1:364-372` (sucht `Ergebnisse/MantaAuv/`), `sim.ps1:380` (das Log ist global und kommt deshalb trotzdem) | Es sieht halb erfolgreich aus: zwei gelbe Warnungen, das Log kommt trotzdem. Deshalb wurde es dreimal wiederholt. |
| 2 | **Keine Geheimnisse im Repo.** Es gibt keine Passwörter, Tokens oder Schlüssel, weder im Arbeitsstand noch in der Historie. Gesucht wurde in allen Branches über 71 Commits, nach Inhalten (`passw`, `token=`, `api_key`, `PRIVATE KEY`, `ghp_`, `github_pat_`, `sshpass`, `secret=`) und nach Dateinamen (`id_rsa`, `.pem`, `.key`, `.env`, `*.local.json`, `*.log`, `*.csv`). | — | Hier gibt es nichts zu bereinigen. |
| 3 | **Personen- und infrastrukturbezogene Angaben** (keine Geheimnisse): der Server-Benutzername steht in absoluten Pfaden, dazu kommt eine interne IP. | `src/projects/MantaAuv/simulation.json:40-42`, `src/projects/TestprojektNACA/simulation.json:9-11`, `scripts/sim.ps1:58` (`192.168.122.121`), Kommentare in `SimulationConfig.cs:12`, `ServerHochschuleEinrichten.txt:35`, `.plans/*.md` | Nur dann relevant, wenn `github.com/Lionheart005/OptimierungsAlgoCFD` öffentlich ist. Mit `~`-Pfaden bleibt die Server-Eigenheit abgedeckt, ohne dass der Name im Repo steht. |
| 4 | `.claude/settings.local.json` ist nur **zufällig** ignoriert, nämlich über das Muster `*.local.json`. | `git check-ignore -v` → `.gitignore:10` | Sollte ausdrücklich in der `.gitignore` stehen. |
| 5 | **Doppelt kodiertes UTF-8 steht auch in Ausgabetexten**, nicht nur in Kommentaren. Im Server-Log steht deshalb „nicht vorhanden, Ã¼bersprungen". | `JsonConfigLoader.cs:96`, `:113` | `projektbuendel.md` behauptet „keine Codezeile betroffen". Das stimmt nicht. |
| 6 | **Der Verlauf einer Simulation geht verloren.** SU2 schreibt `history.csv` mit jeder Iteration, aber vor jeder Variante wird die Datei gelöscht, und danach wird nur die letzte Zeile gelesen. | `Su2Solver.cs:56`, `:221` | Für Feature „Kennwert-Verlauf" sind die Daten schon da. Sie müssen nur aufgehoben werden. |
| 7 | **Ob eine Rechnung konvergiert ist, wird nicht geprüft.** Erfolg heißt heute nur „`history.csv` hat ≥ 5 Zeilen". Eine Rechnung, die bei `ITER= 500` abbricht, ohne das Cauchy-Kriterium zu erfüllen, zählt als Erfolg. | `Su2Solver.cs:88-95`, `Su2ConfigGenerator.cs:93-98` | Ein nicht konvergierter CD-Wert sieht genauso aus wie ein konvergierter. |
| 8 | **Das Netz ist rein tetraedrisch, eine Wandschicht gibt es nicht.** Die Wandnähe wird nur über ein Threshold-Größenfeld gesteuert (SizeMin 1,2 mm). Das Oberflächennetz ist die unveränderte STL-Triangulierung aus dem Voxelmodell: Gmsh liest das STL per `Merge` ein und vernetzt es nicht neu. | `GmshCfdMesher.cs:98-128`, `GmshMesherOptions.cs:45-51` | Die Oberflächenfeinheit hängt an der Voxelgröße, nicht an den Netzeinstellungen. |
| 9 | **y+ ist weder gleichmäßig noch passend.** Ausgewertet wurde `Serverergebnisse/Testlauf/Surface_Gen1_Var1.vtu` (NACA, Sehne 50 mm): **y+ min 2,3, p05 30,6, Median 45,9, p95 67,4, max 95,7**. Die Oberflächenkanten sind im Median 0,50 mm lang, das ist die Voxelgröße. | einmalige Auswertung der VTU (Rohdaten im appended-Format) | SA ohne Wandfunktion braucht y+ ≈ 1. Die Wandschubspannung und damit der Reibungswiderstand sind so **nicht belastbar**, und die Streuung beträgt Faktor 40. Das bestätigt die Anmerkung. |
| 10 | **Der Nachlauf wird nicht verfeinert.** Der Wake-Box-Block ist auskommentiert. | `GmshCfdMesher.cs:130-158` | Wirbel und Nachlauf werden mit Fernfeldzellen (bis 120 mm) aufgelöst. |
| 11 | **Das Gebiet ist knapp.** Der Kanal misst 600 × 300 × 300 mm um einen Körper von etwa 50 mm, der mittig sitzt: stromauf und stromab je 6 L, seitlich 3 L. | `gmsh.json` beider Projekte | Für den Nachlauf sind ≥ 10 L stromab üblich. Das gehört in die Kalibrierung (Domänenprüfung). |
| 12 | **Die Anströmung von MantaAuv ist physikalisch fragwürdig.** v = MachNumber 0,1 × `SpeedOfSound` 343,2 m/s (Schallgeschwindigkeit in **Luft**) = 34,3 m/s, und das in **Wasser** (ρ = 1025) bei einem 50 mm langen Körper. Das ergibt Re ≈ 1,8·10⁶ und etwa 67 Knoten. | `Su2ConfigGenerator.cs:20`, `SimulationConfig.cs:46`, `MantaAuv/su2.json:17-20` | Laut Rückmeldung nicht gewollt (siehe Entscheidungen). |
| 13 | Die Server-Logs zeigen für NACA CD 0,31, CL 1,58, Gleitzahl 5,1. **Bezugsfläche ist die Stirnfläche** (260 mm²), nicht die Grundrissfläche (≈ 2150 mm²). Der Flügel ist ein 3D-Segment mit freien Enden (Seitenverhältnis ≈ 0,9). | `simulation.log`, `TestprojektNACA/su2.json` | Kein Rechenfehler (siehe `architektur_kritikpunkte_naca.md` Punkt 5), aber ein starkes Argument für echtes 2D, wenn ein Profil optimiert werden soll. |
| 14 | Die Werkzeuge haben sich geändert: Das **Bash-Tool läuft jetzt** (GNU bash 5.3, cygwin), damit ist `bash -n scripts/sim-runner.sh` möglich. git liegt unter `GitHubDesktop\app-3.6.10`. | — | Shell-Skripte sind jetzt syntaktisch prüfbar, nicht mehr nur strukturell. |
| 15 | **Build und Tests vor Beginn:** Der Build ist grün, es gibt **260 Tests**, aber **ein Test ist wackelig.** `ProjectLayoutTests.MantaAuv_Names_All_Three_Program_Paths_Explicitly` ist in 2 von 4 Läufen rot und einzeln immer grün. Die Ursache: `ConfigLayerTests.The_Environment_Variable_Is_Used_When_No_Path_Is_Given` setzt die **prozessweite** Variable `SIM_PROJECTS_DIR` auf einen Testordner mit einer falschen `MantaAuv`. xUnit führt Testklassen parallel aus, und `MantaProjectFiles.Directory` löst den Ordner über genau diese Variable auf. | `ConfigLayerTests.cs:473-486`, `MantaProjectFiles.cs:26-38` | Das wird vor allem anderen behoben (TODO-33), sonst ist „alle Tests grün" als Abnahme nichts wert. |

---

## Antworten auf die Verständnisfragen (SU2)

Diese Antworten kommen in TODO-34 als Kommentare an die Stellen, wo die Werte entstehen. Hier
stehen sie als Referenz. Was mit *(prüfen)* markiert ist, wird beim Umsetzen gegen den Quelltext
bzw. die Doku von **SU2 7.5.1** abgeglichen, bevor es als Kommentar ins Programm geht.

**Welche Gleichungen werden gelöst?** `SOLVER= INC_RANS` steht für die inkompressiblen,
Reynolds-gemittelten Navier-Stokes-Gleichungen (Masse + Impuls). Die Dichte ist konstant
(`INC_DENSITY_MODEL= CONSTANT`), die Viskosität ebenfalls (`VISCOSITY_MODEL= CONSTANT_VISCOSITY`),
und eine Energiegleichung gibt es nicht (`INC_ENERGY_EQUATION= NO`). Die Turbulenz modelliert
Spalart-Allmaras (`KIND_TURB_MODEL= SA`) mit **einer** zusätzlichen Transportgleichung für ν̃. Die
Rechnung ist **stationär**: SU2 iteriert in einer Pseudozeit (implizites Euler-Verfahren, CFL
adaptiv), bis sich der Zustand nicht mehr ändert.

**Wo wird gesagt, wo Fluid ein- und ausströmt?** Ausdrücklich nirgends. Die Kette sieht so aus:

1. Der Windkanal ist **eine einzige** geschlossene STL-Hülle (`TunnelGeometry`, sechs Seiten).
2. Gmsh ordnet alle Flächen dieser Hülle **einer** physikalischen Gruppe zu:
   `Physical Surface("Farfield") = {tunnel_surfs[]}` (`GmshCfdMesher.cs:112`).
3. Der Name landet als `MARKER_TAG= Farfield` in der `.su2`-Netzdatei.
4. SU2 bekommt `MARKER_FAR= ( Farfield )` (`Su2ConfigGenerator.cs:75`). Das ist eine
   **Fernfeld-Randbedingung**: An der Außenseite jeder Randfläche wird der Freistromzustand
   angesetzt, also Geschwindigkeit aus `FREESTREAM_VELOCITY`, Druck und Turbulenzgrößen.
5. **Ob an einer Fläche Fluid ein- oder austritt, ergibt sich aus der Richtung der
   Freistromgeschwindigkeit relativ zur Flächennormale** *(prüfen: `CIncEulerSolver::BC_Far_Field`)*.
   Mit `FREESTREAM_VELOCITY= ( v, 0, 0 )` strömt es an der Fläche bei x-min ein und bei x-max aus.
   Die vier Seitenflächen werden nahezu tangential angeströmt. **Die Strömungsrichtung steckt also
   ausschließlich im Geschwindigkeitsvektor.**

Die Alternative wären getrennte Ränder: `MARKER_INLET` (Geschwindigkeitseinlass), `MARKER_OUTLET`
(Druckauslass) und `MARKER_SYM` oder `MARKER_EULER` (Gleitwand) an den Seiten. Dafür müsste der Kanal
als sechs einzeln benannte Flächen geschrieben werden. Für eine Außenumströmung ist das Fernfeld der
übliche Weg; die getrennte Variante lohnt sich erst, wenn ein echter Windkanal mit Wänden
nachgebildet werden soll. **Sie ist nicht Teil dieses Plans**, wird aber im Kommentar erwähnt.

**Welche Randbedingungen gelten am Körper?** `MARKER_HEATFLUX= ( Wall, 0.0 )` ist die Haftbedingung
für eine adiabate Wand: Die Geschwindigkeit ist an der Wand null. Weil die Energiegleichung aus ist,
wirkt davon nur die Haftbedingung. SU2 legt viskose Wände genau so fest. Für SA gilt an der Wand
ν̃ = 0.

**Was bedeuten die übrigen Marker?** `MARKER_MONITORING= ( Wall )` legt fest, über welche Fläche CD,
CL, CSF und CMx/y/z integriert werden. `MARKER_PLOTTING= ( Wall )` legt fest, welche Fläche in
`surface.vtu` landet.

**Was ist mit Anfangsbedingungen?** `INC_VELOCITY_INIT` und `INC_DENSITY_INIT` geben das Startfeld
im ganzen Gebiet vor: überall Freistrom. Bei einer stationären Rechnung beeinflusst das nur den Weg
zur Lösung, nicht die Lösung selbst.

**Und die Turbulenzgrößen am Fernfeld?** Sie sind nicht gesetzt, es gelten also die SU2-Vorgaben
*(prüfen: `FREESTREAM_TURBULENCEINTENSITY`, `FREESTREAM_TURB2LAMVISCRATIO` und ihre Werte in
7.5.1)*. Sie kommen nur als Kommentar mit ihren Standardwerten dazu, nicht als Schlüssel, damit
sich das Verhalten nicht ändert.

**Wie hängen Gmsh und SU2 zusammen?** Ausschließlich über den **Namen**: `Physical Surface("Wall")`
bzw. `("Farfield")` im `.geo`-Skript wird zum `MARKER_TAG` im `.su2`-Netz und dort zum Marker in der
`.cfg`. Ein Tippfehler auf einer der beiden Seiten führt zum Abbruch von SU2.

**Nach welchen Regeln wird gerechnet und wann ist Schluss?** Konvektion mit FDS und MUSCL
(2. Ordnung), dazu der Venkatakrishnan-Limiter. Die Turbulenzgleichung wird mit `SCALAR_UPWIND`
gerechnet. Schluss ist nach `ITER` Iterationen **oder** wenn `CONV_FIELD` (DRAG) über
`CONV_CAUCHY_ELEMS` Iterationen um weniger als `CONV_CAUCHY_EPS` schwankt (geprüft ab
`CONV_STARTITER= 100`) **oder** wenn das Residuum unter 10⁻⁸ fällt (`CONV_RESIDUAL_MINVAL= -8`).

---

## Entscheidungen

### Mit dir abgestimmt (04.11.2026)

| # | Frage | Entscheidung |
| --- | --- | --- |
| **E1** | Was heißt „instationäre Auswertung"? | **Erst der Iterationsverlauf** der stationären Rechnung je Variante: sichern, plotten, Konvergenz und Schwingung bewerten (TODO-35/36). **Später** eine echte zeitgenaue Rechnung (URANS) als optionaler Modus nur für Einzelprüfungen, z. B. den Champion (TODO-37). |
| **E2** | Wie wird die Gitterstudie eingebunden? | **Beides.** (a) Ein eigener Befehl `calibrate`, der allein läuft. (b) Ein kompletter Lauf mit Kalibrierung am Anfang, dessen Ergebnis **automatisch** übernommen wird. **Bedingung:** Es muss im Nachhinein klar und übersichtlich sein, welche Werte genutzt wurden. Im Auto-Modus lassen sich die kalibrierten Größen **nicht** mehr von Hand eintragen, einzutragen ist nur noch die akzeptable Abweichung. |
| **E3** | Welche Gewinner werden fein nachgerechnet? | **Je Lauf einstellbar**, mit genau den Optionen aus der Rückfrage: nur Endchampion, Top-N am Ende (Vorgabe N = 3), Gewinner jeder Iteration, dazu „aus". |
| **E4** | Sind 34,3 m/s Anströmung bei MantaAuv gewollt? | **Nein.** Die Geschwindigkeit wird direkt in m/s angegeben. **Der Wert fehlt noch** (siehe Offene Fragen). |

### Folgerungen und eigene Festlegungen

| # | Festlegung | Begründung |
| --- | --- | --- |
| **F1** | **Eine Quelle der Wahrheit pro Einstellung: die JSON-Datei des Projekts.** Kalibrier-Modus und Verifikations-Modus stehen in `simulation.json`. Einen zusätzlichen Schalter im Startaufruf gibt es nicht. | Das ist der Grundsatz aus `projektbuendel.md`: „was hochgeladen ist, läuft". Zwei Stellräder für dieselbe Sache sind genau das Durcheinander, das E2 vermeiden will. |
| **F2** | **Kalibriert wird ein einziger Faktor**, `MeshRefinementFactor`, der **alle** Zellgrößen gemeinsam skaliert (Oberfläche, Nahfeld, Nachlauf, Fernfeld). Die Größen in `gmsh.json` beschreiben die Stufe 1,0, also nur die *Form* der Größenverteilung. | Eine Gitterstudie ist nur mit systematischer Verfeinerung aussagekräftig. Würde jede Größe einzeln verändert, ließe sich nicht sagen, welche Änderung welchen Effekt hatte. |
| **F3** | Der Faktor gehört in **`simulation.json`** (Framework), nicht in `gmsh.json`. Er erreicht den Mesher über einen neuen `MeshRequest`. | Die Kalibrierung ist ein Framework-Ablauf und muss für jeden Mesher gelten, also für 3D und 2D. Faktor, Kalibrierung und Verifikation stehen damit in einer Datei; die Regel „im Auto-Modus kein Faktor" sieht man dort auf einen Blick. |
| **F4** | **Die Höhe der ersten Wandzelle wird nicht mitskaliert.** Sie folgt aus dem y+-Ziel. Die Zahl der Schichten wird je Stufe neu bestimmt. | So ist es in der Praxis üblich: y+ ist eine physikalische Vorgabe und keine Netzfeinheit. Würde die erste Zelle mitskaliert, liefe die Studie über verschiedene Wandmodelle. Das wird im Kalibrier-Bericht ausdrücklich genannt. |
| **F5** | **y+-Ziel ist 1** (Wand aufgelöst, SA ohne Wandfunktion). Wandfunktionen (y+ 30–100) sind **nicht** Teil dieses Plans. | Das ist robust bei Ablösung, und Ablösung bestimmt den Druckwiderstand. Ob SU2 7.5.1 Wandfunktionen im INC-Löser unterstützt, ist ungeprüft. Eine spätere Option bleibt offen. |
| **F6** | Die **Mindestschichtzahl ist 4** (deine Vorgabe). Die tatsächliche Zahl wird aus der Grenzschichtdicke berechnet und liegt bei y+ = 1 typisch bei **20–25**. | Mit 4 Schichten und y+ ≈ 1 endet die strukturierte Zone weit innerhalb der Grenzschicht: Bei Wachstumsrate 1,2 ist sie nur etwa 0,05 mm dick, die Grenzschicht aber 1,5–2 mm. Das wäre kaum besser als heute. |
| **F7** | **Die Verifikation der Gewinner ändert die Optimierung nicht.** Sie meldet und markiert nur. | Würden feine Fitnesswerte in die Stützstellen des RSM gemischt, verglichen die Stützstellen Äpfel mit Birnen. Weicht das feine Netz zu stark ab, ist das ein Kalibrierproblem und wird laut gemeldet. |
| **F8** | **2D bekommt einen eigenen schlanken C#-Kernel** auf Basis von **Clipper2**. Die Begründung steht im folgenden Abschnitt. | — |

---

## 2D-Simulation: Bewertung und Empfehlung

**Geht das mit SU2?** Ja, nativ. Ein 2D-Netz (`NDIME= 2`) wird mit denselben cfg-Schlüsseln
gerechnet. Eine 2D-RANS-Rechnung mit aufgelöster Grenzschicht hat typisch 50–150 Tsd. Zellen und
braucht auf 12 Kernen Minuten statt Stunden *(Schätzung, im Spike messen)*.

**Geht das mit Gmsh?** Ja, und 2D ist dort sogar die stärkere Disziplin. Das `BoundaryLayer`-Feld
erzeugt in 2D strukturierte Quad-Schichten (genau das „gleiche y+, gleichmäßige Quader" aus der
Anmerkung) mit Fächern an spitzen Hinterkanten. Das ist deutlich robuster als die Extrusion in 3D.

**Geht das mit PicoGK?** Nein. PicoGK ist ein reiner 3D-Voxelkernel (OpenVDB). Beide Umwege
taugen nichts: Eine dünne Scheibe ist weiterhin eine 3D-CFD-Rechnung, und ein Schnitt durch das
Voxelmodell liefert eine treppige Kontur in Voxelgröße.

| Option | Vorteile | Nachteile |
| --- | --- | --- |
| **A — eigener 2D-Kernel in C# auf Clipper2** | Rein managed .NET: keine native Bibliothek, kein Xvfb, testbar unter Windows. Boolesche Operationen und Offsets sind robust. Die Kontur ist exakt, statt einer Voxeltreppe. Dieselbe Pipeline wie in 3D. | Kurven liegen intern als fein abgetastete Polygone vor. Gmsh legt beim Vernetzen wieder Splines hindurch. |
| B — Gmsh-OpenCASCADE direkt (Geometrie als `.geo`-Text) | Echte Splines und Kreise, Booleans in Gmsh. | Die Geometrie lebt in der Gmsh-Skriptsprache statt in C# und ist ohne Gmsh nicht unit-testbar. Eine zweite Sprache für Mensch und LLM. |
| C — NetTopologySuite statt Clipper2 | Sehr ausgereift, double-Genauigkeit, viele Operationen. | GIS-orientiert und größer. Für Booleans und Offsets gleichwertig zu A, also nur ein Ersatz, falls Clipper2 hakt. |
| D — Python (shapely, pygmsh) | Viele Werkzeuge. | Verlässt die .NET-Welt und braucht eine zweite Laufzeit auf dem Server. |
| E — Schnitt durch das PicoGK-Modell | Kein neuer Kernel. | Treppige Kontur; das ist keine echte 2D-Geometrie. |

**Empfehlung: A.**

* **Am elegantesten:** Ein 2D-Projekt ist genau wie ein 3D-Projekt ein Ordner mit
  `IGeometryGenerator` und `IFitnessCalculator`. Optimierer, Bouncer, Kalibrierung, Verifikation und
  Export werden ohne Änderung mitbenutzt. Nur Kernel und Mesher sind andere, ausgewählt über eine
  `Cfd2DProjectDefinition`.
* **Gleiche Begriffe wie PicoGK:** `Shape2D` mit `BoolAdd`, `BoolSubtract`, `BoolIntersect`,
  `Offset`, `DoubleOffset` und `TripleOffset` (Abrunden wie bei PicoGK, über Offset +r/−2r/+r mit
  runden Ecken), dazu `Lattice2D.AddBeam(a, b, r1, r2)` (Kapsel bzw. konischer Balken),
  `AddCircle`, `AddPolygon`, `AddSpline` und `CalculateProperties(out area, out bbox)`. Wer PicoGK
  kann, kann das auch. Die Namen lehnen sich an, sind aber kein 1:1-Nachbau.
* **Zukunftsfähig und LLM-tauglich:** Die API ist klein, typisiert und in **einer** Sprache
  dokumentiert. Ob ein (später auch KI-erzeugter) Generator stimmt, prüft `dotnet test` in
  Sekunden ohne Server. Ein SVG-Export der Kontur macht das Ergebnis für Mensch und Modell
  sichtbar. Clipper2 ist weit verbreitet und Sprachmodellen gut bekannt. Für die spätere
  KI-Erweiterung entsteht damit ein geschlossener Kreis aus Code, Kompilieren, Test und schneller
  2D-CFD. *(Die KI-Erweiterung selbst ist nicht Teil dieses Plans.)*

---

## Arbeitsschritte

Jeder Schritt endet mit grünem `dotnet build` **und** `dotnet test` (0 Warnungen). Schritte, die
SU2 oder Gmsh brauchen, haben zusätzlich eine **Server-Abnahme**, die du machst. Der Rechner hier
hat weder SU2 noch Gmsh. Für Shell-Skripte gilt nach jeder Änderung `bash -n scripts/sim-runner.sh`,
für PowerShell `[System.Management.Automation.Language.Parser]::ParseFile(...)`.

Die Spalte „Worker" ist eine Empfehlung für `/delegate`: **H** = Haiku (mechanisch, voll
spezifiziert), **S** = Sonnet (braucht Urteilsvermögen).

### Block A — Skripte und Hygiene

#### TODO-31 — `fetch` holt das richtige Projekt *(Worker: S)*

1. **`sim-runner.sh`:** `cmd_foreground` schreibt den Projektnamen nach `.last_project` (neben
   `.last_exit`, `sim-runner.sh:405`). Neuer Befehl `lastproject`, ohne `require_project`.
   `.last_project` kommt in die `.gitignore`.
2. **`sim.ps1 fetch` und `status` ohne `-Project`:** Das Skript fragt `lastproject` ab und nimmt
   dieses Projekt. Es sagt das laut, z. B. `Kein -Project angegeben – letzter Lauf auf BIC_12 war
   'TestprojektNACA', nehme dieses.` Mit `-Project` bleibt alles wie bisher. Gehört das Log zu einem
   anderen Projekt als dem gewählten, gibt es eine Warnung. Für `deploy`, `run` und `doctor` bleibt
   die heutige Vorgabe samt Hinweis (`sim.ps1:433-438`).
3. **Ein Archiv statt vieler scp-Aufrufe:** Neuer Befehl `sim-runner.sh pack <Projekt> <Umfang>`
   packt auf dem Server ein `tar.gz`. `fetch` holt es mit **einem** scp und entpackt es lokal mit
   `tar.exe`. Über das VPN kostet jede SSH-Verbindung Zeit, und jede einzelne kann hängen bleiben
   (Memory `bic-server-ssh-haenger`). Es gibt drei Umfänge:
   * **Standard:** `Simulation_Results.csv`, `effective-config.json`, das zum Projekt gehörende
     Log, `Analyseergebnisse/Verlaeufe/` (TODO-35), `Kalibrierung/` (TODO-42) und
     `Verifikation.csv` (TODO-43). Alles klein.
   * **`-Champion`:** zusätzlich die VTUs von Endchampion und Top-N.
   * **`-All`:** der ganze Projektordner wie bisher.
4. **Fehlt die CSV,** kommt eine klare rote Meldung mit der Liste der Projekte, für die auf dem
   Server Ergebnisse liegen (Logik aus `cmd_projects`), und Exit-Code 1. Bisher war es ein gelber
   Nebensatz.
5. **Der Zielordner nennt das Projekt:** `Serverergebnisse/<Server>_<Projekt>_<Zeitstempel>/`.
6. *Optional, nur mit deiner Zustimmung (siehe Offene Fragen):* Allen ssh- und scp-Aufrufen in
   `sim.ps1` werden zentral `-o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=4`
   mitgegeben. Am 19.09. war das für `~/.ssh/config` ausdrücklich nicht gewollt. Im Skript wirkt
   es nur auf diese Aufrufe.

**Abnahme:** `ScriptContractTests` werden erweitert: `fetch` fragt `lastproject` ab,
`.last_project` steht in der `.gitignore`, der Zielordner enthält den Projektnamen, `pack` kennt
die drei Umfänge. Dazu laufen Parser-Check und `bash -n`. **Server:** Nach einem NACA-Lauf liefert
`.\scripts\sim.ps1 fetch` **ohne** `-Project` die CSV.

#### TODO-32 — Sensible Daten *(Worker: H für 2–5, S für 1)*

1. **`~` in Programmpfaden** (der Teil von Kritikpunkt 7 aus `architektur_kritikpunkte_naca.md`,
   der hier gebraucht wird): Ein Helfer `ExecutablePath.Expand` macht aus einem führenden `~/`
   `$HOME/`. Angewendet wird er dort, wo `MpiRunPath`, `Su2Path` und `GmshPath` benutzt werden
   (`Su2Solver.cs:63-64`, `GmshCfdMesher.cs:170`), **und** in der Ausgabe von `--print-config`,
   damit `doctor` den aufgelösten Pfad prüft. Das muss in C# passieren, weil `Process` mit
   `UseShellExecute=false` keine Shell startet und `~` sonst niemand auflöst. Beide Projekt-JSONs
   bekommen danach `"~/software/bin/SU2_CFD"` usw. Der Benutzername verschwindet aus dem Repo, die
   Pfade bleiben absolut, und die Server-Eigenheit ist weiter abgedeckt.
2. Die IP aus dem Kommentar `sim.ps1:58` entfernen (der Name aus `~/.ssh/config` reicht).
3. In Kommentaren und Doku den Benutzernamen durch `~` bzw. `<benutzer>` ersetzen:
   `SimulationConfig.cs:12`, `ServerHochschuleEinrichten.txt:35`, `.plans/*.md`.
4. In die `.gitignore` ausdrücklich `.claude/settings.local.json` eintragen.
5. **Neuer Test `RepositoryHygieneTests`:** Er durchsucht alle versionierten Textdateien unter
   `src/`, `scripts/` und `config/` nach Schlüsselmustern (`PRIVATE KEY`, `ghp_`, `github_pat_`,
   `password=`), nach `/home/<name>/` und nach IPv4-Literalen. Damit kommt das nicht still zurück.
6. **Du:** Prüfe, ob `src/projects/MantaAuv/OptimierungsAlgoTestlaufBericht.pdf` etwas enthält, das
   nicht öffentlich sein soll. Den Inhalt habe ich nicht geprüft.
7. **Die Historie bleibt unverändert.** Der Benutzername steht zwar in alten Commits, aber er ist
   kein Geheimnis. Ein Umschreiben der Historie (`git filter-repo` plus Force-Push) gibt es nur auf
   ausdrücklichen Wunsch.

**Abnahme:** `dotnet run -- MantaAuv --print-config` zeigt aufgelöste Pfade ohne `~`. Der
Hygiene-Test ist grün, und die Suche nach `lpleissner` unter `src/` und `scripts/` findet nichts
mehr. **Server:** `doctor` meldet alle drei Programme mit `[ OK ]`.

#### TODO-33 — Kleinkram: wackeliger Test, UTF-8 *(Worker: S für 1, H für 2)*

Diesen Schritt **zuerst** erledigen, damit jede spätere Abnahme „alle Tests grün" belastbar ist.

1. **Wackeliger Test** (Ausgangslage Nr. 15): Jeder Test, der prozessweiten Zustand verändert
   (`Environment.SetEnvironmentVariable`, Arbeitsverzeichnis), kommt in eine xUnit-Collection mit
   `DisableParallelization = true`. Das betrifft heute `ConfigLayerTests` (die Umgebungsvariable)
   und alle Leser von `MantaProjectFiles`. Sauberer, aber größer wäre es,
   `ProjectPaths.ResolveProjectsRoot` die Umgebung als Parameter zu übergeben. Die Entscheidung
   fällt beim Umsetzen. **Abnahme:** `dotnet test` ist zehnmal hintereinander grün.
2. **UTF-8:** `JsonConfigLoader.cs` wird einmal als UTF-8 neu kodiert (aus „LÃ¤dt" wird „Lädt").
   Betroffen sind auch die Ausgabetexte in Zeile 96 und 113. Die Bemerkung „keine Codezeile
   betroffen" in `projektbuendel.md` wird richtiggestellt. **Abnahme:** Die Suche nach `Ã` bzw.
   `Â` unter `src/` findet nichts mehr.

### Block B — SU2 verstehen

#### TODO-34 — Randbedingungen und Gleichungen kommentieren *(Worker: S)*

Inhalt ist der Abschnitt „Antworten auf die Verständnisfragen", kurz gefasst. Er kommt an vier
Stellen hin:

1. **`Su2ConfigGenerator.cs`:** Die erzeugte `current_config.cfg` bekommt vor jedem Block
   `%`-Kommentarzeilen. SU2 ignoriert sie, und damit erklärt sich **jede** archivierte cfg selbst
   (siehe TODO-35).
2. **`GmshCfdMesher.cs`:** Im `.geo`-Skript steht bei den `Physical Surface`-Zeilen ein `//`-Kommentar:
   „dieser Name ist der SU2-Marker".
3. **`su2.json` und `gmsh.json`** von MantaAuv, TestprojektNACA und `_Vorlage` bekommen einen kurzen
   Block „Randbedingungen" im Kopf, der auf die Generatoren verweist.
4. **`src/Automatisierung_v2/README.md`:** neuer Abschnitt „Was SU2 rechnet" mit etwa 15 Zeilen.

Vorher werden die mit *(prüfen)* markierten Aussagen gegen SU2 7.5.1 abgeglichen.
**Abnahme:** Ein Test prüft, dass die cfg die Kommentarblöcke enthält **und** dass alle Zeilen ohne
`%` unverändert bleiben (Regression gegen den heutigen Text). **Server:** Ein Lauf mit
kommentierter cfg rechnet dieselben Zahlen.

### Block C — Kennwert-Verlauf je Simulation (E1)

#### TODO-35 — `history.csv` je Variante sichern und bewerten *(Worker: S)*

1. **Sichern:** Nach jedem SU2-Lauf wird `history.csv` nach
   `Analyseergebnisse/Verlaeufe/History_Gen{i}_Var{v}[_{Tag}].csv` kopiert und `current_config.cfg`
   nach `.../Config_Gen{i}_Var{v}[_{Tag}].cfg`. Das gilt **auch bei Abbruch**, dann landen beide
   Dateien in `FehlerLogs/`, denn gerade der Verlauf einer divergierten Rechnung zeigt, wo es kippt.
   `{Tag}` kennzeichnet Kalibrier- und Verifikationsläufe (TODO-39).
2. **Bewerten:** Eine reine Funktion `Su2HistoryAnalyzer.Evaluate(lines, options)` ist ohne SU2
   testbar. Sie liefert:
   * `Iterations`: die Zahl der Iterationen.
   * `Converged`: SU2 hat **vor** `ITER` aufgehört, also war das Cauchy- oder das
     Residuen-Kriterium erfüllt.
   * `ResidualDrop`: wie viele Dekaden das RMS-Residuum gefallen ist.
   * `Oscillation[Metrik]`: (max − min) / |Mittel| über die letzten `CONV_CAUCHY_ELEMS` Zeilen.
3. **In den Record:** `Su2_Iterations`, `Su2_Converged` (0/1) und `Su2_Osc_<Metrik>`. Sie landen
   damit automatisch in der CSV, weil `ExportToCsv` die Spalten dynamisch sammelt
   (`SimulationContext.cs:104-105`).
4. **Warnungen:**
   * Nicht konvergiert: `[WARNUNG] SU2 hat ITER=500 erreicht, ohne dass das Kriterium erfüllt war –
     CD ist der Stand bei Iteration 500, kein konvergierter Wert.`
   * Schwingung über der Schwelle: `… schwingt um ±x % – Hinweis auf instationäre Strömung (Wirbelablösung).`
5. **Neue Optionen in `su2.json`:**
   * `OscillationWarnLimit` (Vorgabe 0,02).
   * `TreatUnconvergedAsFailed` (Vorgabe `false`, damit sich das Verhalten nicht ändert, aber der
     Fall sichtbar wird).
   * `ResultAveraging` mit `"Last"` (Vorgabe, wie heute) oder `"CauchyWindowMean"` (Mittel über das
     Cauchy-Fenster, für schwingende Fälle).

**Abnahme:** Tests mit drei Beispiel-`history.csv` (konvergiert, abgebrochen bei ITER, schwingend)
prüfen Dateinamen, Kennzahlen und Warnungen. Ein Test prüft, dass `ResultAveraging = "Last"`
dieselben Zahlen liefert wie heute.

#### TODO-36 — Plotten und Abholen *(Worker: S)*

1. **`scripts/plot_history.py`** mit pandas und matplotlib, wie `Plot_RSM.py`:
   * Aufruf: `python scripts/plot_history.py <Ordner|Datei> [--var Gen2_Var3] [--metrics CD CL] [--compare]`.
   * Es entstehen drei übereinanderliegende Plots: Kennwerte über Iteration; Residuen
     logarithmisch; der Endwert als Linie ± Toleranzband, das Cauchy-Fenster schraffiert.
   * `--compare` legt mehrere Varianten übereinander.
   * Ohne `--var` druckt das Skript eine Übersichtstabelle: Varianten mit Iterationen, konvergiert
     ja/nein und Schwingung.
   * Das PNG wird neben der CSV gespeichert.
2. Die Spaltennamen werden aus der Kopfzeile gelesen, nicht fest verdrahtet. Projekte mit Lift oder
   Momenten funktionieren dadurch ohne Änderung.
3. `fetch` holt `Verlaeufe/` standardmäßig mit (TODO-31 Punkt 3).
4. `scripts/README.md` bekommt einen Abschnitt „Eine Rechnung von Hand prüfen": `fetch`, dann
   `plot_history.py`, dann in ParaView die VTU des Champions.

**Abnahme:** Das Skript läuft lokal auf den Beispiel-CSVs aus TODO-35, der Plot entsteht ohne
Fehler. Vorher einmal `pip install pandas matplotlib`.

#### TODO-37 — *Später:* zeitgenauer Modus (URANS) für Einzelprüfungen *(Worker: S)*

Nicht Teil der ersten Runde (E1). Er wird hier skizziert, damit die anderen Schritte ihn nicht
verbauen.

* **Neue Optionen in `su2.json`:** ein Block `TimeDomain` mit `Enabled`, `TimeStep` (s) oder
  `StepsPerPeriod` zusammen mit `StrouhalEstimate` (f = St · U / D, Vorgabe St ≈ 0,2),
  `TimeIterations`, `InnerIterations` und `AveragingStartFraction`.
* **SU2-Schlüssel** *(gegen das 7.5.1-config_template prüfen)*: `TIME_DOMAIN= YES`,
  `TIME_MARCHING= DUAL_TIME_STEPPING-2ND_ORDER`, `TIME_STEP`, `TIME_ITER`, `INNER_ITER`,
  `WINDOW_START_ITER`, `WINDOW_FUNCTION`, zeitgemittelte Beiwerte (`TAVG_…`). Gestartet wird aus
  der stationären Lösung (Restart).
* **Einsatz:** als weiterer Verifikationsmodus (TODO-43) und für das Nachrechnen einer einzelnen
  Variante (TODO-43 Punkt 5, `recheck -Unsteady`). `plot_history.py` zeigt dann den Verlauf über die
  **physikalische** Zeit.

### Block D — Netz und Physik

#### TODO-38 — Anströmung in m/s, Reynoldszahl ins Log *(Worker: S)*

1. **`Su2SolverOptions.FreestreamVelocity`** in m/s, neben `Density` und `DynamicViscosity`: Das
   ist der Fluidzustand und gehört zusammen. Die Vorgabe `0` bedeutet „nicht gesetzt", dann gilt
   das alte Verhalten Mach × `SpeedOfSound` **mit Hinweis im Log**. Ist der Wert > 0, gewinnt er,
   und ein ausdrücklich gesetztes `MachNumber` wird als „wirkungslos" gemeldet.
2. **Ein Record `FlowConditions`** (Geschwindigkeit, Dichte, Viskosität) wird in der
   `CfdProjectDefinition` gebaut und an Mesher **und** Solver gegeben. Die Grenzschichtberechnung
   im Mesher (TODO-40) braucht dieselben Zahlen wie SU2, und sie sollen nur an einer Stelle stehen.
3. **Log je Variante:** `Anströmung 2.0 m/s, Re_L = 1.0e5 (L = 50.0 mm aus der Bounding Box)`.
4. **Die Projekte:**
   * MantaAuv bekommt den Wert, den du nennst (Offene Fragen).
   * TestprojektNACA bekommt ausdrücklich 68,64 m/s, also den heutigen Wert, nur eben explizit.

**Hinweis zur Physik:** Ein 50 mm langer Körper in Wasser bei 1–3 m/s liegt bei Re ≈ 0,5–1,5·10⁵.
Das ist **laminar bis transitional**. SA rechnet voll turbulent und überschätzt dort die Reibung.
Ein Transitionsmodell gehört nicht in diesen Plan, steht aber unter Risiken.

**Abnahme:** Tests für explizite Geschwindigkeit, den Rückfall auf Mach, die Warnung bei
wirkungslosem `MachNumber` und den cfg-Inhalt. Dazu `SolverOptionsTests` für den neuen Schlüssel.

#### TODO-39 — `MeshRequest`, ein Netzfaktor, Nachlauf, Zellzahl *(Worker: S)*

Dieser Schritt ist Voraussetzung für TODO-40 bis 43 und für die 2D-Mesher.

1. **Schnittstelle:** `IMeshGenerator.GenerateMesh(MeshRequest request)` mit
   `MeshRequest(GeometryPath, Iteration, Variant, Config, ScaleFactor, WorkingDirectory,
   RefinementFactor = 1.0, Tag = "")`. Ist ein Tag gesetzt, bekommen die Dateien einen Zusatz
   (`Model_Gen1_Var1_Kal3.su2`), damit Kalibrier- und Verifikationsläufe die Optimierungsnetze nicht
   überschreiben. Mitziehen müssen `WorkflowController.RunPipeline` (`WorkflowController.cs:250-256`),
   `GmshCfdMesher`, die Mocks in `WorkflowControllerTests` und `GmshCfdMesherTunnelTests`.
2. **`SimulationConfig.MeshRefinementFactor`** (Vorgabe 1,0) ist der manuelle Netzfaktor. Der
   wirksame Faktor eines Laufs liegt im `SimulationContext` (Laufzeitzustand, wie
   `CurrentBaseParameters`), damit Auto-Kalibrierung und Verifikation ihn setzen können, ohne die
   Konfiguration anzufassen.
3. **Alle Zellgrößen werden mit dem Faktor multipliziert**: Oberfläche, Nahfeld (heute das
   Threshold-Feld), Nachlauf und Fernfeld. Ausgenommen ist die erste Wandzelle (F4).
4. **Nachlauf-Verfeinerung:** Der auskommentierte Block (`GmshCfdMesher.cs:130-158`) wird als
   Option wiederbelebt. Neue Schlüssel in `gmsh.json`: `WakeRefinement` (an/aus), `WakeLengthFactor`
   (Vorgabe 2,0 × Modelllänge), `WakeWidthFactor`, `WakeCellSize`. Alles wird aus der Bounding Box
   des Modells abgeleitet, so wie es dort schon angelegt ist.
5. **Oberflächenkrümmung:** `Mesh.MeshSizeFromCurvature` als Option. Das wirkt erst, wenn die
   Oberfläche neu vernetzt wird (TODO-40).
6. **Zellzahl:** `NELEM=` wird aus dem Kopf der `.su2`-Datei gelesen und als `MeshCells` in den
   Record geschrieben. Die Kalibrierung braucht die Zahl, und sie ist das ehrlichste Maß für die
   Kosten.
7. **Gebiet:** Für den Nachlauf `TunnelCenterX` so setzen, dass stromab ≥ 10 L Platz ist. Das ist
   eine Empfehlung im Kommentar von `gmsh.json` und kein Zwang, denn die Domänenprüfung macht
   TODO-42.

**Abnahme:** Mit Faktor 1,0 und ausgeschalteten neuen Optionen ist das `.geo` **byteweise** wie
heute (Regression). Ein Test prüft, dass alle Größen mit dem Faktor multipliziert werden. Getestet
werden außerdem die Wake-Box aus einer Bounding Box, das Lesen von `MeshCells` aus einem Beispielkopf
und Dateinamen mit Tag.

#### TODO-40 — Prismen-Grenzschicht mit vorgegebenem y+ (3D) *(Worker: S, Spike durch dich)*

1. **`BoundaryLayerCalculator`** ist eine reine Funktion. Aus ρ, U, μ, L und dem y+-Ziel berechnet
   sie:
   * Re_L = ρUL/μ.
   * Cf = 0,026 · Re_L^(−1/7) (Flachplatte, turbulent).
   * τ_w = ½ρU²Cf und u_τ = √(τ_w/ρ).
   * Erste Zellhöhe y₁ = y⁺ · μ / (ρ · u_τ).
   * Die Grenzschichtdicke δ ≈ 0,37 · L · Re_L^(−0,2) und daraus die Schichtzahl N, sodass
     y₁ · (r^N − 1)/(r − 1) ≥ δ gilt, mindestens `BoundaryLayerMinLayers` (4).

   Richtwerte: NACA (Luft, 68,6 m/s, c = 50 mm) ergibt y₁ ≈ 0,004 mm und N ≈ 24. MantaAuv bei
   2 m/s ergibt y₁ ≈ 0,01 mm und N ≈ 20.
2. **Spike zuerst, auf dem Server (du), noch vor dem eigentlichen Code:** Der Mesher bekommt einen
   Schalter `BoundaryLayerMode` mit `"Threshold"` (Vorgabe, wie heute) und `"Prism"`. Im
   Prism-Modus schreibt er:
   * `ClassifySurfaces{…}; CreateGeometry;` für das Modell-STL. Danach ist die Oberfläche neu
     vernetzbar, ihre Feinheit ist von der Voxelgröße entkoppelt, und
     `MeshSizeFromCurvature` greift.
   * Eine Grenzschicht-Extrusion entlang der Normalen
     (`Extrude{ Surface{…}; Layers{ {1,…}, {h₁, h₁+h₂, …} }; Recombine; }`,
     *Syntax gegen Gmsh 4.11.1 prüfen*).
   * Tetraeder zwischen der Oberkante der Grenzschicht und dem Kanal.
   * Die Marker bleiben unverändert: `"Wall"` ist die ursprüngliche Oberfläche.

   Getestet wird an je einem STL von Manta und NACA. Zu prüfen ist: Gmsh läuft durch, es gibt keine
   negativen Volumina (`Mesh.QualityType`), SU2 startet, und es gibt eine y+-Statistik. **Das
   Ergebnis kommt in diesen Plan, bevor es weitergeht.**
3. **Optionen in `gmsh.json`:** `BoundaryLayerMode`, `TargetYPlus` (1,0), `BoundaryLayerMinLayers`
   (4), `BoundaryLayerGrowthRate` (1,2), `BoundaryLayerLayers` (0 = automatisch nach δ),
   `SurfaceCellSize` (mm, Stufe 1,0), `CurvatureElements`, `ClassifyAngleDeg` (40°). Die
   bestehenden `BoundaryLayerSize*` bleiben und steuern im Prism-Modus die Übergangszone außerhalb
   der Prismen.
4. **Log je Variante:** `Grenzschicht: y₁ = 0.0044 mm (y⁺ 1 bei Re 2.3e5), 24 Schichten, Dicke 1.6 mm`.
5. **Rückfall:** Scheitert Gmsh im Prism-Modus, gilt die Variante als fehlgeschlagen
   (`SimulationFailed`), und das `.geo` liegt in `FehlerLogs/`. **Kein** stiller Rückfall auf
   Threshold, weil sonst Varianten mit verschiedenen Wandmodellen verglichen würden.

**Abnahme:** Der Rechner stimmt mit handgerechneten Werten auf 1 % überein, die Mindestschichtzahl
greift, unsinnige Eingaben werfen `ArgumentOutOfRangeException`. Der `.geo`-Inhalt im Prism-Modus
wird getestet, der Threshold-Modus bleibt byteweise unverändert. **Server:** Die y+-Werte aus
TODO-41 liegen im Band (p95 ≤ 2 bei Ziel 1).

#### TODO-41 — y+ nach jeder Rechnung prüfen *(Worker: S)*

1. In `OUTPUT_FILES` kommt `SURFACE_CSV` dazu. SU2 schreibt dann ein ASCII-`surface.csv` mit den
   Oberflächenwerten *(prüfen: heißt die Spalte `Y_Plus`?)*. Als Rückfall dient ein kleiner
   C#-Leser für `surface.vtu` (appended raw, UInt64-Kopf). Ein Python-Prototyp dafür lief am
   04.11. erfolgreich.
2. Neue Metriken `YPlus_Median`, `YPlus_P95` und `YPlus_Max` im Record und damit in der CSV.
3. Eine Warnung, wenn `YPlus_P95` über `YPlusWarnFactor` × `TargetYPlus` liegt (Vorgabe 2).

**Abnahme:** Ein Test mit einer kleinen Beispiel-`surface.csv` prüft Statistik und Warnung.

#### TODO-42 — Netzkalibrierung: eigener Befehl **und** Auto-Modus *(Worker: S)*

**Konfiguration** in `simulation.json`:

```jsonc
// Entweder MANUELL: der Netzfaktor steht hier und gilt.
"MeshRefinementFactor": 1.0,
"MeshCalibration": { "Mode": "Manual" },

// ODER AUTOMATISCH: der Faktor wird am Laufanfang kalibriert und übernommen.
// Dann darf "MeshRefinementFactor" NICHT gesetzt sein – einzutragen ist nur
// die akzeptable Abweichung (relativ, 0.02 = 2 %).
"MeshCalibration": { "Mode": "Auto", "Tolerance": 0.02 }
```

**Die Nachvollziehbarkeits-Regeln aus E2:**

1. **Im Auto-Modus ist `MeshRefinementFactor` verboten.** Steht der Schlüssel in einer Projekt- oder
   local-Datei, bricht der Start **vor** dem ersten Rechenschritt ab. Woher ein Schlüssel kommt,
   weiß der `ConfigLoadReport`. Die Meldung: `Im Auto-Modus wird der Netzfaktor kalibriert. Bitte
   'MeshRefinementFactor' aus src/projects/X/simulation.json entfernen – einzutragen ist nur
   MeshCalibration.Tolerance.`
2. **Im Auto-Modus ist nur `Tolerance` einstellbar.** Verfeinerungsverhältnis und Stufenzahl sind
   Code-Konstanten (siehe unten) und in der JSON nicht vorgesehen. Ein solcher Schlüssel ist dort
   ein unbekannter Schlüssel und bricht ab (Entscheidung 8 aus `projektbuendel.md`).
3. **Jeder Start sagt im Kopf, woher der Faktor kommt.** Im Auto-Modus zum Beispiel:
   `Netzfaktor: 0.71 (KALIBRIERT am Laufanfang, Toleranz 2 %, Stufe 3 von 5, Bericht:
   Ergebnisse/X/Kalibrierung/20261104_101500/)`. Im manuellen Modus:
   `Netzfaktor: 1.00 (MANUELL aus simulation.json)`.
4. **`effective-config.json`** nennt Faktor, Quelle und Kalibrier-Bericht. Die
   `Simulation_Results.csv` bekommt je Zeile die Spalte `MeshRefinementFactor`.
5. **Im manuellen Modus mit vorhandener Kalibrierung** gibt es einen Hinweis, wenn die Werte
   auseinanderliegen: `Letzte Kalibrierung (04.11.2026) empfahl 0.71, eingetragen ist 1.00.`
6. **Jeder Kalibrier-Bericht trägt einen Fingerabdruck** über Startgeometrie, `gmsh.json`,
   `su2.json` und den Code-Stand. Passt er nicht mehr zur aktuellen Konfiguration, meldet der
   Start: `Kalibrierung veraltet`. Das ist nur ein Hinweis.

**Zwei Wege:**

* **Eigener Befehl:** `.\scripts\sim.ps1 calibrate -Project X` startet das Programm mit `--calibrate`
  (in tmux wie bei `start`). Es kalibriert nur, schreibt den Bericht und gibt die **genaue Zeile**
  aus, die in `simulation.json` gehört (`"MeshRefinementFactor": 0.71`). Den Modus der Datei
  ändert der Befehl nicht.
* **Kompletter Lauf** mit `"Mode": "Auto"`: `run` startet wie gewohnt. Erst wird kalibriert, dann
  mit dem kalibrierten Faktor optimiert, alles in einem Prozess.

**Ablauf.** Deine Idee bleibt der Kern, ergänzt um Leitplanken:

1. **Geometrie:** Gerechnet wird die Startgeometrie (`BaseParameters`), einmal erzeugt, mit
   unveränderter Voxelauflösung. Die Voxelauflösung ist eine eigene Achse, siehe Risiken.
2. **Stufen:** Die Faktoren lauten 2,0 · 1,41 · 1,0 · 0,71 · 0,5 (Verhältnis √2, fünf Stufen; als
   Code-Konstanten). In 3D steigt die Zellzahl je Stufe etwa um 2–3×.
3. **Reihenfolge:**
   * Zuerst die **grobe** Stufe 2,0. Sie ist billig und liefert die Zellzahl, aus der die
     Zellzahl der feinsten Stufe hochgerechnet und gemeldet wird. Liegt sie über einer
     Sicherheitsgrenze, bricht die Kalibrierung **vorher** ab, statt Stunden zu verlieren.
   * Dann die **sehr feine** Referenzstufe 0,5.
   * Dann von grob nach fein die Zwischenstufen, bis eine passt.
4. **Abbruchkriterium:** Gewählt wird die **gröbste** Stufe k, bei der für **alle** überwachten
   Kennwerte |Q_k − Q_ref| / |Q_ref| ≤ `Tolerance` gilt. Überwacht werden alle `ResultMetrics`
   (Drag, Lift, …), weil das Netz genau sie beeinflusst. Die Fitness steht im Bericht, ist aber
   kein Kriterium: Bei Manta geht Drag in der dritten Potenz ein, 2 % Drag wären 6 % Fitness.
5. **Gültigkeit:** Eine Stufe zählt nur, wenn sie **iterativ konvergiert** ist (TODO-35) und y+
   im Band liegt (TODO-41). Sonst wäre ein Unterschied ein Iterations- oder Wandmodellfehler und
   kein Netzfehler.
6. **Unsicherheit:** Aus den drei feinsten gerechneten Stufen werden die beobachtete Ordnung p und
   der Grid Convergence Index (GCI nach Celik et al. 2008, ASME J. Fluids Eng.) berechnet. Der
   Bericht nennt dann etwa: „gewählte Stufe: CD = 0,0312 ± 1,4 %". Läuft die Folge nicht monoton
   auf die Referenz zu (die Differenzen wechseln das Vorzeichen), steht das als Warnung im Bericht.
7. **Wenn nichts passt:** Erfüllt keine Stufe die Toleranz, bricht der Auto-Modus den Lauf ab. Die
   Ergebnisse sollen belastbar sein, und weiterzurechnen hieße, mit einer unbekannten Netzabhängigkeit
   zu optimieren. Der eigene Befehl meldet dasselbe und schlägt vor, die Referenzstufe zu nehmen
   oder die Toleranz zu lockern.
8. **Domänenprüfung (optional):** Ein zusätzlicher Lauf mit 1,5-fach vergrößertem Kanal auf der
   gewählten Stufe. Er zeigt, ob der Kanal das Ergebnis mitbestimmt. Abgefragt wird er über einen
   eigenen Code-Schalter oder den Befehl, nicht über die Toleranz.

**Ablage:** `Ergebnisse/<Projekt>/Kalibrierung/<Zeitstempel>/` mit:
* `calibration.csv`: Stufe, Faktor, Zellen, Laufzeit, Kennwerte, Abstand zur Referenz, Änderung zur
  Vorstufe, konvergiert, y+.
* `calibration.json`: Ergebnis, Eingaben und Fingerabdruck.
* `Verlaeufe/` je Stufe.
* Im Log steht dieselbe Tabelle.

**Umsetzung:**
* `Core/Pipeline/MeshCalibration.cs` orchestriert über einen Rückruf, wie der `ChampionValidator`:
  `Func<MeshLevel, ModelRecord>`.
* `Core/Pipeline/CalibrationEvaluator.cs` ist rein: Abbruchregel, GCI, Monotonie.
* `Program.cs` bekommt `--calibrate` und den Auto-Zweig. Das ist ein Framework-Eingriff, kein
  Projekteingriff.
* `sim-runner.sh` bekommt `calibrate`, `sim.ps1` bekommt `calibrate`.

**Abnahme:**
* `CalibrationEvaluator` mit synthetischen Folgen: konvergiert monoton, oszilliert, erreicht die
  Toleranz nie, eine Stufe ist nicht konvergiert.
* GCI gegen das Rechenbeispiel aus Celik et al. 2008; die Zahlen werden beim Umsetzen aus der
  Quelle übernommen.
* Konfigurationsregeln: Auto mit gesetztem `MeshRefinementFactor` bricht mit Meldung ab; Auto mit
  einem zusätzlichen Schlüssel bricht ab; Manual mit abweichender Kalibrierung gibt einen Hinweis;
  `effective-config.json` nennt die Quelle.
* **Server:** `calibrate` auf TestprojektNACA (oder schneller auf `Naca2D`) liefert einen Bericht.

#### TODO-43 — Gewinner fein nachrechnen (E3) *(Worker: S)*

**Konfiguration** in `simulation.json`. Die Modi sind genau die abgestimmten Optionen:

```jsonc
"ChampionVerification": {
  "Mode": "Final",          // "Off" | "Final" | "FinalTopN" | "EachIteration"
  "TopN": 3,                // nur bei FinalTopN
  "RefinementSteps": 1,     // eine Stufe feiner als der wirksame Faktor (÷ √2 je Stufe)
  "Tolerance": 0.05         // Abweichung, ab der laut gewarnt wird
}
```

1. **Vorgabe `"Final"`**, also eine zusätzliche feine Rechnung am Ende. Die Anmerkung sagt „immer
   nochmal überprüfen". Mit `"Off"` gilt das Verhalten von heute.
2. **Das feine Netz** hat den Faktor `wirksamer Faktor / √2^RefinementSteps`, also eine Stufe der
   Kalibrierfolge feiner. Die Tags sind `Fein1`, `Fein2`, ….
3. **Ausgabe:**
   * `Ergebnisse/<Projekt>/Verifikation.csv` mit Gen, Var, Rang normal, Rang fein, Kennwerten
     normal/fein und Abweichung.
   * Ein Block im Log.
   * Bei `FinalTopN` die Aussage, ob die Rangfolge im feinen Netz hält.
   * Die Spalten `FineVerified` und `FineDeviation` im Record.
4. **Die Auswahl ändert sich nicht (F7).** Das gilt auch bei `EachIteration`: Der Iterationssieger
   wird nach dem Bouncer fein nachgerechnet, und das Ergebnis wird nur gemeldet. Weicht es über
   `Tolerance` ab, gibt es eine laute Warnung (`Ergebnis netzabhängig – Kalibrierung prüfen`).
   Bouncer und Verifikation prüfen Verschiedenes: der Bouncer die Robustheit gegen kleine
   Parameteränderungen, die Verifikation die Netzunabhängigkeit.
5. **Gemeinsamer Mechanismus:** `RunPipeline` bekommt einen `ResimulationRequest` (Parameter,
   Iteration, Variante, Faktor, Tag). Darauf baut ein **eigener Befehl zum Nachrechnen einer
   einzelnen Variante**: `.\scripts\sim.ps1 recheck -Project X -Variant Gen3_Var2 [-Finer 1]`. Er
   liest die Parameter aus der `Simulation_Results.csv`. Das deckt das „Simulation händisch
   überprüfen" ab, und TODO-37 hängt dort später `-Unsteady` an. *(Optional; er kostet wenig, wenn
   der Mechanismus steht.)*

**Abnahme:** Controller-Tests mit Mocks:
* `Off` rechnet nichts zusätzlich.
* `Final` rechnet genau einmal, mit dem richtigen Faktor und Tag.
* `FinalTopN` rechnet N-mal in Fitness-Reihenfolge.
* `EachIteration` rechnet `MaxIterations`-mal.

Außerdem werden CSV-Spalten und Ranglisten-Ausgabe geprüft, und es wird geprüft, dass die History
des Optimierers **unverändert** bleibt.

### Block E — 2D (F8)

#### TODO-44 — 2D-Kernel `Kernels/Planar2D` *(Worker: S)*

1. NuGet `Clipper2` (Angus Johnson, Boost Software License). Die Version wird beim Umsetzen auf
   nuget.org geprüft und fest eingetragen.
2. **`Shape2D`** ist eine Menge geschlossener Konturen mit Löchern, auf `PathsD`. Es bietet:
   * Mengenoperationen: `BoolAdd`, `BoolSubtract`, `BoolIntersect`.
   * Versatz: `Offset(r)`, `DoubleOffset`, `TripleOffset` (runde Ecken).
   * Formen: `AddPolygon`, `AddCircle(c, r, segments)`, `AddSpline(points, samples)`.
   * Auswertung: `CalculateProperties(out float area, out BBox2 box)` und `Perimeter`.
   * Ausgabe: `SaveContour(path)` (JSON: Konturen als Punktlisten plus markierte Knickpunkte) und
     `SaveSvg(path)`.
3. **`Lattice2D.AddBeam(a, b, r1, r2)`** erzeugt Kapsel- bzw. Konusbalken als Polygon, analog zum
   PicoGK-`Lattice`.
4. **Knickpunkte** (Winkel > `CornerAngleDeg`, z. B. eine Hinterkante) werden markiert. Der Mesher
   trennt dort die Splines, damit die Kante spitz bleibt.
5. `GeometryResult` bekommt `GeometryPath` und `GeometryKind { SurfaceStl, Contour2D }`. `StlPath`
   bleibt als Alias, damit bestehende Projekte unverändert bleiben (endgültig entschieden wird beim
   Umsetzen).
6. **`MeshMetrics2D`** liefert Fläche, Umfang, Sehne (Bounding Box x) und Dicke (Bounding Box y).

**Abnahme:** Tests für die Mengenoperationen (Fläche von Vereinigung und Differenz zweier Kreise
analytisch), Offset (Fläche eines um r versetzten Quadrats), Knickpunkterkennung, den Round-Trip
`SaveContour` → Laden und gültiges SVG. Alles läuft lokal ohne PicoGK.

#### TODO-45 — `Gmsh2DMesher` *(Worker: S, Spike durch dich)*

Der Mesher schreibt ein `.geo`-Skript mit:

* `Point`/`Spline`/`Line` je Kontur, getrennt an den Knickpunkten, dann `Curve Loop`.
* Einem Fernfeld als Kreis mit `FarfieldRadius` × Sehne. Die Vorgabe ist 50, weil in 2D die
  Zirkulation nur mit 1/r abklingt und ein kleines Fernfeld den Auftrieb verfälscht.
* `Plane Surface` = Fernfeld minus Körper.
* `Field BoundaryLayer` mit:
  * `CurvesList` = Körper.
  * `Size` = y₁ aus dem `BoundaryLayerCalculator`.
  * `Ratio` = Wachstumsrate.
  * `NbLayers` = N.
  * `Quads = 1`.
  * `FanPointsList` = Knickpunkte.
* Den Größenfeldern Nahfeld und Nachlauf, mit dem Netzfaktor multipliziert.
* `Physical Curve("Wall")`, `Physical Curve("Farfield")` und `Physical Surface("Fluid")`.

Gmsh wird mit `-2 -format su2` aufgerufen, das Netz liegt in der xy-Ebene.

**Abnahme:** Tests des `.geo`-Inhalts: Marker, Grenzschicht-Feld, Fächerpunkte, Faktor.
**Server-Spike:** Das NACA-0012-Profil vernetzt sauber, SU2 startet, y+ liegt im Band, und die
Laufzeit wird hier eingetragen.

#### TODO-46 — SU2 in 2D *(Worker: S)*

1. **Anstellwinkel** (Kritikpunkt 4 aus `architektur_kritikpunkte_naca.md`):
   `Su2SolverOptions.AngleOfAttackDeg`. **Zu prüfen ist zuerst,** wie SU2 7.5.1 im INC-Löser CL und
   CD in Windachsen zerlegt: über den `AOA`-Schlüssel oder über den Geschwindigkeitsvektor. Steht
   `AOA` auf 0, während der Vektor gedreht ist, lägen CL und CD in Körperachsen, und das wäre bei
   α ≠ 0 falsch. Der Test dafür: gedrehte Anströmung und gedrehte Geometrie müssen dieselben CL/CD
   liefern. In 2D liegt der Auftrieb auf +y, in 3D auf +z, also ist der Vektor
   dimensionsabhängig.
2. **Bezugsgröße:** In 2D ist `REF_AREA` die Sehnenlänge je Einheitsspannweite, also **mm → m** und
   nicht mm² → m². `ResolveReferenceArea` (`Su2Solver.cs:145-160`) muss die Dimension kennen,
   nämlich über `GeometryKind`. `REF_LENGTH` = Sehne für die Momente.
3. **`Composition/Cfd2DProjectDefinition`:** `CreateKernel` liefert den `DirectGeometryKernel` (kein
   PicoGK, kein Xvfb), `CreateStages` liefert `Gmsh2DMesher` + `Su2Solver`. Der RubberBandScaler
   ist in 2D standardmäßig aus.

**Abnahme:** cfg-Tests für den Geschwindigkeitsvektor bei α ≠ 0 in 2D und 3D, für die
Bezugslängen-Umrechnung und dafür, dass 3D-Projekte unverändert bleiben.

#### TODO-47 — Projekt `Naca2D`, `_Vorlage2D`, Validierung *(Worker: S)*

1. **`src/projects/Naca2D/`**: dieselben Parameter wie TestprojektNACA (Wölbung, Wölbungsrücklage,
   Dicke). Das Profil ist die exakte NACA-Kontur, wahlweise mit geschlossener oder stumpfer
   Hinterkante. Der Anstellwinkel kommt über `su2.json`, die Fitness ist CL/CD.
2. **`src/projects/_Vorlage2D/`** und `scripts/new-project.ps1 <Name> -Template 2D`.
3. **Validierung, die Abnahme der Physik insgesamt:** NACA 0012 in 2D bei Re = 6·10⁶ gegen die
   Referenzdaten der **NASA Turbulence Modeling Resource** für SA (CL und CD bei α = 0°, 10°, 15°).
   SU2 führt diesen Fall als Testfall, das Vorgehen ist also erprobt. Die Referenzwerte werden beim
   Umsetzen von der TMR-Seite übernommen und **mit Quelle** hier eingetragen. Ziel: CL innerhalb
   von 2 %, CD innerhalb von 5 % auf der kalibrierten Stufe.
4. **Vergleich mit TestprojektNACA (3D):** Er zeigt, wie stark die freien Flügelenden und die
   Voxelkontur das 3D-Ergebnis verschieben.

**Abnahme:** `ProjectsOnDiskTests` und `TemplateProjectTests` gelten auch für die 2D-Vorlage.
**Server:** Validierungslauf und Kalibrierung von `Naca2D`, die Ergebnisse kommen in diesen Plan.

---

## Reihenfolge und Abhängigkeiten

```
A: TODO-31, 32, 33  ─┐   unabhängig, sofort nützlich
B: TODO-34          ─┤   unabhängig
C: TODO-35 → 36     ─┤   35 ist Voraussetzung für die Kalibrierung (Konvergenzprüfung)
                     │
D: TODO-38 → 39 → 40 → 41 → 42 → 43
             │                 ↑
E:           └→ 44 → 45 → 46 → 47 (Validierung)
                     (45 nutzt den BoundaryLayerCalculator aus 40, Punkt 1)

später: TODO-37 (URANS), hängt an 35 und 43
```

**Empfohlene Reihenfolge:** Ganz zuerst TODO-33 Punkt 1 (der wackelige Test), dann der Rest von
A sowie B. Dann C, dann 38 und 39. Danach sollte **E vor dem
3D-Teil (40–41)** kommen, denn 2D ist der billige Prüfstand: Grenzschicht, y+-Auswertung und
Kalibrierlogik lassen sich dort in Minuten statt Stunden testen, und TODO-47 beweist mit der
Validierung, dass die Kette physikalisch stimmt. Erst dann die riskante 3D-Extrusion (40), danach
42 und 43 für beide Welten.

Die Blöcke D und E berühren kaum dieselben Dateien. Gemeinsam sind nur `MeshRequest`,
`GeometryResult` und `Su2SolverOptions`, diese kommen also zuerst. Danach können zwei Worker
parallel arbeiten.

---

## Risiken und Fallstricke

| Risiko | Gegenmaßnahme |
| --- | --- |
| Die Grenzschicht-Extrusion auf einem Voxel-STL faltet sich in konkaven Ecken oder engen Spalten (verrauschte Normalen) | Erst der Spike (TODO-40 Punkt 2). StlSmoother bleibt an. Die Gesamtdicke der Grenzschicht wird gegen den kleinsten Krümmungsradius bzw. die kleinste Spaltweite geprüft. Kein stiller Rückfall |
| `ClassifySurfaces` zerlegt das Voxel-STL in sehr viele Flächen | Der Winkel `ClassifyAngleDeg` ist einstellbar, der Spike prüft das an Manta und NACA |
| Prismen vervielfachen die Zellzahl (3D, 20–25 Schichten) | Die Zellzahl steht je Variante in der CSV. Die Kalibrierung rechnet die feinste Stufe hoch und bricht vorher ab. 2D ist der billige Weg für Profilfragen |
| Bei Re ≈ 10⁵ (Manta in Wasser) ist die Strömung laminar bis transitional, SA rechnet voll turbulent | Als Hinweis im Log neben der Reynoldszahl (TODO-38). Ein Transitionsmodell ist ein eigener späterer Schritt *(prüfen, was SU2 7.5.1 bietet)* |
| Die Kalibrierung an der Startgeometrie gilt nicht für die optimierte Form | Genau dafür gibt es TODO-43. Bei großer Abweichung kommt eine laute Warnung |
| Die Voxelauflösung ist eine zweite „Netz"-Achse, weil sie die Geometrie selbst verändert | Sie bleibt in der Kalibrierung fest. Der Bericht nennt sie. Eine eigene Voxelstudie ist möglich, aber nicht Teil dieses Plans |
| Unterschiede zwischen den Stufen sind in Wahrheit Iterationsfehler | Nur konvergierte Stufen zählen (TODO-42 Punkt 5) |
| In 2D verfälscht ein zu kleines Fernfeld den Auftrieb | Vorgabe 50 Sehnenlängen, die Domänenprüfung gilt auch in 2D |
| Der Anstellwinkel landet nur im Vektor, CL und CD liegen dann in Körperachsen | Erst prüfen, dann umsetzen. Test: gedrehte Anströmung gegen gedrehte Geometrie (TODO-46 Punkt 1) |
| Neue Spalten in `PassiveParameters` verlängern die Champion-Ausgabe | Die Ausgabe wird nach Gruppen sortiert (Geometrie, Solver, Netz, Prüfung) |
| `MeshRequest` bricht die Signatur von `IMeshGenerator` | Nur das Framework und die Tests implementieren die Schnittstelle, Projekte nicht. Alles wird in TODO-39 auf einmal mitgezogen |
| Kalibrier-Bericht und eingetragener Wert laufen auseinander | Fingerabdruck plus Hinweis beim Start (TODO-42 Regeln 5 und 6) |
| `fetch` über das VPN bleibt hängen | Ein Archiv statt vieler Verbindungen. Timeouts nur mit Zustimmung (Offene Fragen) |
| Kommentare in der cfg ändern ungewollt einen Wert | Regressionstest: alle Zeilen ohne `%` bleiben unverändert (TODO-34) |

---

## Offene Fragen

1. **Anströmgeschwindigkeit MantaAuv in m/s?** Typisch für ein AUV sind 0,5–3 m/s. Der Wert
   bestimmt Re und damit die Höhe der ersten Zelle (TODO-38/40). Ohne Wert bleibt TODO-38 umsetzbar,
   MantaAuv rechnet dann aber weiter mit 34,3 m/s und Hinweis.
2. **SSH-Timeouts in `sim.ps1`** (TODO-31 Punkt 6): einverstanden? Am 19.09. ging es um
   `~/.ssh/config`, das wäre jetzt nur das Skript.
3. **Ist das GitHub-Repo öffentlich?** Davon hängt ab, wie wichtig TODO-32 Punkt 3 ist. Ein
   Umschreiben der Historie ist ohnehin nicht vorgesehen.
4. **PDF-Bericht:** darf er im Repo bleiben (TODO-32 Punkt 6)?

---

## Abgrenzung

* **Nicht Teil dieses Plans:** der Block „Erweiterungen" aus den Anmerkungen (KI/ML, ein
  Physics-Informed-LLM, das C#-Code bearbeitet, der Nachbau des Leap71-Workflows, Ersatz von
  Optimierer oder Simulation durch ein Modell, FEM). Der 2D-Kernel ist bewusst so angelegt, dass er
  dafür später taugt (siehe Empfehlung).
* **Aus `architektur_kritikpunkte_naca.md` aufgegriffen werden nur:** Punkt 4 (Anstellwinkel, in
  TODO-46), Punkt 5 (Bezugsgröße in 2D, in TODO-46) und von Punkt 7 nur die `~`-Expansion
  (TODO-32). Die Punkte 1–3 und 6 sowie die Fallback-Kette aus Punkt 7 bleiben dort offen.
* **Getrennte Ein- und Auslassränder** im Windkanal (`MARKER_INLET`/`OUTLET`/`SYM`) sind nur als
  Kommentar erwähnt (TODO-34) und werden nicht umgesetzt.
* **Wandfunktionen** (y+ 30–100) kommen erst, wenn feststeht, dass SU2 7.5.1 sie im INC-Löser kann
  (F5).
