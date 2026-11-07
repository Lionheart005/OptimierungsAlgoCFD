# Umsetzung der Anmerkungen: Skripte, SU2-Verständnis, Kennwert-Verlauf, Physik, 2D

> **Stand: 04.11.2026** — Branch `MajorPhysikFixes`. Quelle ist `.plans/Anmerkungen.txt`, und zwar
> alles **außer** dem Block „Erweiterungen" (KI/ML, FEM). Die Nummerierung setzt
> `projektbuendel.md` fort (letzter Schritt dort: TODO-30), hier also **TODO-31 ff.**
> **Status (04.11.2026, zweite Runde):** Alle Rückfragen sind beantwortet, auch die zweite Runde
> (siehe „Entscheidungen" E5–E9). Die **Kleinigkeiten sind umgesetzt**: TODO-32, TODO-33,
> die SSH-Zeitgrenzen aus TODO-31 und der erste Teil von TODO-38 (MantaAuv rechnet jetzt mit
> 2 m/s). Neu im Plan: **TODO-48** (2D-Vorkalibrierung für 3D-Läufe) und **TODO-49**
> (Bedienhandbuch). Alles andere ist noch Plan.

---

## Fortschritt (für die Übergabe)

`[x]` erledigt, `[~]` teilweise erledigt (der Rest steht im jeweiligen Abschnitt), `[ ]` offen.

| Block | Schritt | Inhalt | Status |
| --- | --- | --- | --- |
| A | TODO-31 | `fetch`/`status` nehmen das Projekt des letzten Laufs, ein Archiv statt vieler scp-Aufrufe; SSH-Zeitgrenzen | [x] |
| A | TODO-32 | Keine Geheimnisse im Repo: Hygiene-Test, `.gitignore` | [x] |
| A | TODO-33 | Kleinkram: wackelige Tests (prozessweiter Zustand + Parallelität), doppelt kodiertes UTF-8, vollständige Namensliste bei unbekanntem Schlüssel | [x] |
| F | TODO-49 | **Bedienhandbuch** für Nicht-Programmierer + Test, der jeden Schlüssel darin verlangt | [x] |
| B | TODO-34 | Randbedingungen und Gleichungen als Kommentar in cfg, geo und JSON | [x] |
| C | TODO-35 | `history.csv` je Variante sichern, Konvergenz und Schwingung bewerten | [x] |
| C | TODO-36 | `scripts/plot_history.py` + fetch-Anbindung | [x] |
| C | TODO-37 | *Später:* zeitgenauer Modus (URANS) für Einzelprüfungen | [x] |
| D | TODO-38 | Anströmung in m/s statt Mach × Schallgeschwindigkeit, Reynoldszahl ins Log | [x] |
| D | TODO-39 | `MeshRequest` + ein Netzfaktor für alle Zellgrößen, Nachlauf-Verfeinerung, Zellzahl | [x] |
| D | TODO-40 | Strukturierte **Quader**-Grenzschicht (Hexaeder, Prismen nur als Rückfall) mit vorgegebenem y+ (3D), Server-Spike zuerst | [x] Servertest 06.11.2026: auf Zwei-Schritt-Verfahren umgebaut (B11 in `servertest_2026-11-06.md`). „Prism“ am Manta: y+ ≈ 1, SU2 konvergiert. „Hexa“ nur für glatte Körper |
| D | TODO-41 | y+ nach jeder Rechnung auswerten und melden | [x] |
| D | TODO-42 | Netzkalibrierung: eigener Befehl **und** Auto-Modus am Laufanfang | [x] |
| D | TODO-43 | Gewinner in feinerer Auflösung nachrechnen (Modus je Lauf wählbar) | [x] |
| E | TODO-44 | 2D-Kernel `Kernels/Planar2D` (C#, Clipper2) mit PicoGK-ähnlicher API | [x] |
| E | TODO-45 | `Gmsh2DMesher` mit strukturierter Quad-Grenzschicht | [x] |
| E | TODO-46 | SU2 in 2D: Anstellwinkel, Bezugslänge statt -fläche, `Cfd2DProjectDefinition` | [x] |
| E | TODO-47 | Projekt `Naca2D` + `_Vorlage2D` + Validierung gegen NASA-TMR-Referenzdaten | [x] |
| E | TODO-48 | 2D-Vorkalibrierung als Startpunkt für die 3D-Kalibrierung (Schalter `Use2DPrecalibration`, 3D bestätigt immer) | [x] |

**Verifikationsstand vor Beginn (04.11.2026):** 260 Tests, einer davon wackelig (siehe
„Ausgangslage", letzte Zeile).
**Verifikationsstand nach vollständigem Abschluss aller Code-TODOs (04.11.2026):** `dotnet build` ergibt
0 Fehler und 0 Warnungen. `dotnet test` ergibt **443/443 Tests grün** (von 260 auf 443 gewachsen).
`scripts/sim.ps1` und `scripts/new-project.ps1` haben 0 Parser-Fehler, `scripts/sim-runner.sh` hat
0 Syntax-Fehler (`bash -n`), `scripts/plot_history.py` kompiliert fehlerfrei (`py_compile`).
**Servertest 06.11.2026:** durchgeführt, siehe `.plans/servertest_2026-11-06.md` (11 Befunde, 10 behoben; offen: 3D-Wandschicht TODO-40).

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
   zukunftsfähigsten geht. *(Zweite Runde:)* Dazu die Frage, ob eine 2D-Kalibrierung als
   Grundlage für 3D-Läufe sauber ist (TODO-48).
6. *(Zweite Runde)* **Bedienbarkeit:** Es gibt inzwischen sehr viele Einstellungen. Ein
   übersichtliches **Bedienhandbuch**, das auch ohne Programmierkenntnisse erklärt, wo man was
   einstellt und was jede Einstellung bewirkt (TODO-49).

---

## Ausgangslage (geprüft am 04.11.2026)

| # | Befund | Beleg | Bedeutung |
| --- | --- | --- | --- |
| 1 | **`fetch` holt das falsche Projekt.** Die drei Ordner `Serverergebnisse/BIC_12_20261023_*` enthalten nur `simulation.log`, keine CSV. Im Kopf des Logs steht `Projekt: TestprojektNACA`, `fetch` lief aber ohne `-Project` und hat die Vorgabe `MantaAuv` genommen. | `sim.ps1:72` (Vorgabe), `sim.ps1:364-372` (sucht `Ergebnisse/MantaAuv/`), `sim.ps1:380` (das Log ist global und kommt deshalb trotzdem) | Es sieht halb erfolgreich aus: zwei gelbe Warnungen, das Log kommt trotzdem. Deshalb wurde es dreimal wiederholt. |
| 2 | **Keine Geheimnisse im Repo.** Es gibt keine Passwörter, Tokens oder Schlüssel, weder im Arbeitsstand noch in der Historie. Gesucht wurde in allen Branches über 71 Commits, nach Inhalten (`passw`, `token=`, `api_key`, `PRIVATE KEY`, `ghp_`, `github_pat_`, `sshpass`, `secret=`) und nach Dateinamen (`id_rsa`, `.pem`, `.key`, `.env`, `*.local.json`, `*.log`, `*.csv`). | — | Hier gibt es nichts zu bereinigen. |
| 3 | **Personen- und infrastrukturbezogene Angaben** (keine Geheimnisse): der Server-Benutzername steht in absoluten Pfaden, dazu kommt eine interne IP. | `src/projects/MantaAuv/simulation.json:40-42`, `src/projects/TestprojektNACA/simulation.json:9-11`, `scripts/sim.ps1:58` (`192.168.122.121`), Kommentare in `SimulationConfig.cs:12`, `ServerHochschuleEinrichten.txt:35`, `.plans/*.md` | **Bleibt so** (E5). |
| 4 | `.claude/settings.local.json` ist nur **zufällig** ignoriert, nämlich über das Muster `*.local.json`. | `git check-ignore -v` → `.gitignore:10` | Sollte ausdrücklich in der `.gitignore` stehen. |
| 5 | **Doppelt kodiertes UTF-8 steht auch in Ausgabetexten**, nicht nur in Kommentaren. Im Server-Log steht deshalb „nicht vorhanden, Ã¼bersprungen". | `JsonConfigLoader.cs:96`, `:113` | `projektbuendel.md` behauptet „keine Codezeile betroffen". Das stimmt nicht. |
| 6 | **Der Verlauf einer Simulation geht verloren.** SU2 schreibt `history.csv` mit jeder Iteration, aber vor jeder Variante wird die Datei gelöscht, und danach wird nur die letzte Zeile gelesen. | `Su2Solver.cs:56`, `:221` | Für Feature „Kennwert-Verlauf" sind die Daten schon da. Sie müssen nur aufgehoben werden. |
| 7 | **Ob eine Rechnung konvergiert ist, wird nicht geprüft.** Erfolg heißt heute nur „`history.csv` hat ≥ 5 Zeilen". Eine Rechnung, die bei `ITER= 500` abbricht, ohne das Cauchy-Kriterium zu erfüllen, zählt als Erfolg. | `Su2Solver.cs:88-95`, `Su2ConfigGenerator.cs:93-98` | Ein nicht konvergierter CD-Wert sieht genauso aus wie ein konvergierter. |
| 8 | **Das Netz ist rein tetraedrisch, eine Wandschicht gibt es nicht.** Die Wandnähe wird nur über ein Threshold-Größenfeld gesteuert (SizeMin 1,2 mm). Das Oberflächennetz ist die unveränderte STL-Triangulierung aus dem Voxelmodell: Gmsh liest das STL per `Merge` ein und vernetzt es nicht neu. | `GmshCfdMesher.cs:98-128`, `GmshMesherOptions.cs:45-51` | Die Oberflächenfeinheit hängt an der Voxelgröße, nicht an den Netzeinstellungen. |
| 9 | **y+ ist weder gleichmäßig noch passend.** Ausgewertet wurde `Serverergebnisse/Testlauf/Surface_Gen1_Var1.vtu` (NACA, Sehne 50 mm): **y+ min 2,3, p05 30,6, Median 45,9, p95 67,4, max 95,7**. Die Oberflächenkanten sind im Median 0,50 mm lang, das ist die Voxelgröße. | einmalige Auswertung der VTU (Rohdaten im appended-Format) | SA ohne Wandfunktion braucht y+ ≈ 1. Die Wandschubspannung und damit der Reibungswiderstand sind so **nicht belastbar**, und die Streuung beträgt Faktor 40. Das bestätigt die Anmerkung. |
| 10 | **Der Nachlauf wird nicht verfeinert.** Der Wake-Box-Block ist auskommentiert. | `GmshCfdMesher.cs:130-158` | Wirbel und Nachlauf werden mit Fernfeldzellen (bis 120 mm) aufgelöst. |
| 11 | **Das Gebiet ist knapp.** Der Kanal misst 600 × 300 × 300 mm um einen Körper von etwa 50 mm, der mittig sitzt: stromauf und stromab je 6 L, seitlich 3 L. | `gmsh.json` beider Projekte | Für den Nachlauf sind ≥ 10 L stromab üblich. Das gehört in die Kalibrierung (Domänenprüfung). |
| 12 | **Die Anströmung von MantaAuv ist physikalisch fragwürdig.** v = MachNumber 0,1 × `SpeedOfSound` 343,2 m/s (Schallgeschwindigkeit in **Luft**) = 34,3 m/s, und das in **Wasser** (ρ = 1025) bei einem 50 mm langen Körper. Das ergibt Re ≈ 1,8·10⁶ und etwa 67 Knoten. | `Su2ConfigGenerator.cs:20`, `SimulationConfig.cs:46`, `MantaAuv/su2.json:17-20` | Laut Rückmeldung nicht gewollt. **Seit dem 04.11.2026 2 m/s** (E4, TODO-38). |
| 13 | Die Server-Logs zeigen für NACA CD 0,31, CL 1,58, Gleitzahl 5,1. **Bezugsfläche ist die Stirnfläche** (260 mm²), nicht die Grundrissfläche (≈ 2150 mm²). Der Flügel ist ein 3D-Segment mit freien Enden (Seitenverhältnis ≈ 0,9). | `simulation.log`, `TestprojektNACA/su2.json` | Kein Rechenfehler (siehe `architektur_kritikpunkte_naca.md` Punkt 5), aber ein starkes Argument für echtes 2D, wenn ein Profil optimiert werden soll. |
| 14 | Die Werkzeuge haben sich geändert: Das **Bash-Tool läuft jetzt** (GNU bash 5.3, cygwin), damit ist `bash -n scripts/sim-runner.sh` möglich. git liegt unter `GitHubDesktop\app-3.6.10`. | — | Shell-Skripte sind jetzt syntaktisch prüfbar, nicht mehr nur strukturell. |
| 15 | **Build und Tests vor Beginn:** Der Build ist grün, es gibt **260 Tests**, aber **ein Test ist wackelig.** `ProjectLayoutTests.MantaAuv_Names_All_Three_Program_Paths_Explicitly` ist in 2 von 4 Läufen rot und einzeln immer grün. Die Ursache: `ConfigLayerTests.The_Environment_Variable_Is_Used_When_No_Path_Is_Given` setzt die **prozessweite** Variable `SIM_PROJECTS_DIR` auf einen Testordner mit einer falschen `MantaAuv`. xUnit führt Testklassen parallel aus, und `MantaProjectFiles.Directory` löst den Ordner über genau diese Variable auf. | `ConfigLayerTests.cs:473-486`, `MantaProjectFiles.cs:26-38` | **Behoben** (TODO-33). Dabei fiel ein zweiter, seltenerer Fall desselben Musters auf: `Console.SetOut`, etwa einer von zehn Läufen. |

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
| **E4** | Sind 34,3 m/s Anströmung bei MantaAuv gewollt? | **Nein.** Die Geschwindigkeit wird direkt in m/s angegeben. In der zweiten Runde: „nimm eine Standardgeschwindigkeit" → **2,0 m/s** (≈ 4 kn, übliche AUV-Reisegeschwindigkeit; Re ≈ 1·10⁵ bei 50 mm). **Umgesetzt** (TODO-38). |

### Zweite Runde (04.11.2026)

| # | Frage | Entscheidung |
| --- | --- | --- |
| **E5** | Benutzername und PDF im Repo? | **Beide bleiben.** TODO-32 schrumpft deshalb auf das, was wirklich schützt: ein Test gegen eingecheckte Geheimnisse und die ausdrückliche `.gitignore`-Zeile. Die `~`-Expansion für Programmpfade bleibt als *Portabilitäts*-Idee in Kritikpunkt 7 von `architektur_kritikpunkte_naca.md` und gehört nicht mehr zu diesem Plan. |
| **E6** | SSH-Zeitgrenzen? | Du hast berichtet, dass es früher mit Zeitgrenzen *immer* Abbrüche gab, wenn der Server unter Last rechnete, obwohl alles in Ordnung war. Meine Empfehlung deshalb: **ja, aber großzügig.** Eine Minute für den Verbindungsaufbau, und abgebrochen wird erst nach **3 Minuten** ohne jedes Lebenszeichen. Die Lebenszeichen beantwortet sshd selbst, auch während der Befehl drüben lange still ist; ein gesunder, langsamer Befehl wird also nicht abgebrochen. Ein Abbruch trifft nur die Verbindung von Windows aus, **nie den Lauf in tmux**. Damit bringt das keine neuen Fehlerbilder, nur ein Ende statt eines ewigen Hängers. **Umgesetzt** (TODO-31 Punkt 6). |
| **E7** | 2D kalibrieren, dann 3D rechnen: geht das, oder ist das unsauber? | **Den 2D-Faktor direkt für 3D zu übernehmen, wäre unsauber**, weil 2D keine Spannweitenrichtung, keine Randwirbel und kein Tetraedervolumen kennt. **Sauber ist es als Vorkalibrierung:** 2D liefert in Minuten den Startpunkt und prüft die Wandschicht, und 3D bestätigt mit wenigen Stufen. Die Abnahme bleibt damit in 3D. Details stehen in TODO-48. |
| **E8** | Übersicht der Einstellungen | Ein **Bedienhandbuch** unter `src/projects/BEDIENHANDBUCH.md`, also dort, wo die Einstellungen liegen, und **nicht** im Haupt-README. Die anderen READMEs und jeder JSON-Kopf verweisen darauf. Ein Test verlangt, dass jeder Schlüssel darin erklärt ist (TODO-49). |
| **E9** | Kleinigkeiten gleich umsetzen und committen | Erledigt sind TODO-32, TODO-33, die SSH-Zeitgrenzen und der m/s-Teil von TODO-38. Committet wird auf `MajorPhysikFixes`. |
| **E10** | *(Dritte Runde)* Kann ich komplette 2D-Durchläufe machen, ganz ohne 3D? | **Ja, das ist ausdrücklich das Ziel von Block E.** Ein 2D-Projekt ist ein eigenständiges Projekt wie MantaAuv: `.\scripts\sim.ps1 run -Project Naca2D` fährt die **ganze** Optimierung (Optimierer, Bouncer, Kalibrierung, Feinprüfung, CSV, fetch) mit 2D-Geometrie, 2D-Netz und 2D-SU2. Es braucht dafür kein PicoGK, kein Xvfb und keine einzige 3D-Rechnung. Ein neues 2D-Projekt legt `new-project.ps1 <Name> -Template 2D` an (TODO-47). Die 2D-Vorkalibrierung für 3D (TODO-48) ist nur eine *zusätzliche* Möglichkeit, keine Voraussetzung. |
| **E12** | *(Vierte Runde, Empfehlung des CFD-Professors)* Rechteckige, längliche Quader statt Prismen in der Wandschicht? | **Ja, wird der Standard.** Gmsh kann das: Die Oberfläche wird in Vierecke vernetzt und dann mit `Recombine` extrudiert, so entstehen Hexaeder. An schwierigen Stellen bleiben einzelne Prismen (quad-dominant). Prismen gibt es nur noch als bewusst gewählte Rückfallebene. Details in TODO-40. |
| **E11** | *(Dritte Runde)* Handbuch für den heutigen Stand **vor** allen weiteren Änderungen | **Erste Fassung erledigt** (TODO-49): `src/projects/BEDIENHANDBUCH.md`, mit Verweisen aus allen READMEs und allen zwölf Projekt-JSONs und mit `HandbookCoverageTests`. |

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

#### TODO-31 — `fetch` holt das richtige Projekt ✅ *(erledigt 04.11.2026)*

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
6. ✅ **SSH-Zeitgrenzen (erledigt 04.11.2026, E6):** `$SshOptions` oben in `sim.ps1` setzt
   `ConnectTimeout=60`, `ServerAliveInterval=30` und `ServerAliveCountMax=6`, also Abbruch erst
   nach 3 Minuten Funkstille. Die Optionen hängen an **allen** elf ssh- und scp-Aufrufen.
   `~/.ssh/config` bleibt unangetastet. Die Begründung steht im Skriptkommentar und in
   `scripts/README.md` („Stolperfallen"). Zwei neue Tests in `ScriptContractTests` sichern das
   ab: `Every_Ssh_And_Scp_Call_Carries_The_Timeouts` (kein Aufruf ohne Grenzen) und
   `The_Ssh_Timeouts_Stay_Generous` (≥ 60 s Aufbau, ≥ 3 min Funkstille).
   **Offen:** die Server-Probe, einmal `.\scripts\sim.ps1 status`, gern während ein Lauf
   rechnet. Die Punkte 1–5 sind noch offen.

**Abnahme:** `ScriptContractTests` werden erweitert: `fetch` fragt `lastproject` ab,
`.last_project` steht in der `.gitignore`, der Zielordner enthält den Projektnamen, `pack` kennt
die drei Umfänge. Dazu laufen Parser-Check und `bash -n`. **Server:** Nach einem NACA-Lauf liefert
`.\scripts\sim.ps1 fetch` **ohne** `-Project` die CSV.

#### TODO-32 — Keine Geheimnisse im Repo ✅ *(erledigt 04.11.2026)*

Nach E5 bleiben Benutzername, interne IP und PDF im Repo. Umgesetzt ist, was tatsächlich
schützt:

1. **`.gitignore`** führt `.claude/settings.local.json` jetzt ausdrücklich. Vorher fiel die Datei
   nur zufällig unter `*.local.json`.
2. **Neuer Test `RepositoryHygieneTests`:** Er durchsucht alle Textdateien unter `src/`,
   `scripts/` und `config/` (ohne `bin/`, `obj/` und `*.local.json`) nach privaten Schlüsseln,
   GitHub-Tokens (`ghp_`, `github_pat_`), AWS-Schlüsseln, `sk-`-API-Schlüsseln und nach
   *Zuweisungen* wie `password = "…"`. Gesucht wird nach der Zuweisung und nicht nach dem Wort, denn
   „Passwort eingeben" in einer Anleitung ist harmlos. Weitere Tests prüfen, dass die Muster echte
   Treffer erkennen, normale Doku *nicht* anschlagen lassen und dass der Scan überhaupt Dateien
   sieht.
3. **Die Historie bleibt unverändert**, weil sie nachweislich kein Geheimnis enthält
   (Ausgangslage Nr. 2).

#### TODO-33 — Kleinkram ✅ *(erledigt 04.11.2026)*

1. **Wackelige Tests.** Es waren zwei Fälle mit derselben Ursache: Tests verändern
   **prozessweiten** Zustand, während xUnit andere Testklassen parallel laufen lässt.
   * `ProjectPathsTests` setzt `SIM_PROJECTS_DIR`. Dadurch las
     `ProjectLayoutTests.MantaAuv_Names_All_Three_Program_Paths_Explicitly` eine falsche
     `MantaAuv`, in etwa jedem zweiten Lauf.
   * `ConfigLayerTests` und `Su2ReferenceAreaTests` biegen beide `Console.Out` um. Dadurch las
     `The_Warning_Block_Names_File_Key_And_Both_Values` die Ausgabe der anderen Klasse, in etwa
     einem von zehn Läufen.

   **Lösung:** Die neue Collection `ProcessWideStateCollection` mit
   `DisableParallelization = true` umfasst diese drei Klassen. Sie laufen erst, wenn alle
   parallelen fertig sind, und dann der Reihe nach. Die Regel für künftige Tests steht im
   Kommentar der Collection. **Nachweis:** 20 Läufe hintereinander grün.
2. **UTF-8:** `JsonConfigLoader.cs` ist neu kodiert (46 Zeilen; aus „LÃ¤dt" wurde „Lädt"). Das
   betrifft auch die beiden Ausgabetexte, die im Server-Log als „Ã¼bersprungen" standen.
   Repariert wurde zeilenweise (cp1252 zurück, dann UTF-8). Eine schon korrekte Zeile blieb
   unberührt, und die Suche nach `Ã`, `Â` und `â€` findet unter `src/`, `scripts/` und `tests/`
   nichts mehr. Die falsche Bemerkung in `projektbuendel.md` ist richtiggestellt.
3. **Unbekannter Schlüssel → vollständige Liste** (gefunden durch TODO-38): Hat ein unbekannter
   Schlüssel keinen ähnlichen Namen, kürzte `ConfigSchema` die Liste der gültigen Namen nach 15
   ab, und zwar alphabetisch. Mit `FreestreamVelocity` als 16. SU2-Schlüssel fiel so ausgerechnet
   `TurbulenceModel` heraus. Jetzt steht immer die volle Liste in der Meldung. Mit den vielen
   neuen Schlüsseln aus diesem Plan wäre das sonst ständig passiert.
   `ConfigLayerTests.An_Unknown_Key_Without_A_Near_Match_Lists_Valid_Names` prüft jetzt jeden Namen.

### Block B — SU2 verstehen

#### TODO-34 — Randbedingungen und Gleichungen kommentieren ✅ *(erledigt 04.11.2026)*

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

#### TODO-35 — `history.csv` je Variante sichern und bewerten ✅ *(erledigt 04.11.2026)*

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

#### TODO-36 — Plotten und Abholen ✅ *(erledigt 04.11.2026)*

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

#### TODO-37 — *Später:* zeitgenauer Modus (URANS) für Einzelprüfungen ✅ *(erledigt 04.11.2026)*

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

#### TODO-38 — Anströmung in m/s, Reynoldszahl ins Log ✅ *(erledigt 04.11.2026)*

1. ✅ **`Su2SolverOptions.FreestreamVelocity`** in m/s, neben `Density` und `DynamicViscosity`
   (*erledigt 04.11.2026*). Die Vorgabe `0` bedeutet „nicht gesetzt", dann gilt das alte
   Verhalten Mach × `SpeedOfSound`. Ist der Wert > 0, gewinnt er. Aufgelöst wird an einer Stelle,
   `Su2ConfigGenerator.ResolveVelocity`, für Freistrom **und** Startfeld. Das Log nennt die
   Anströmung **einmal je Lauf** samt Herkunft, z. B.
   `Anströmung: 2 m/s (FreestreamVelocity aus der su2.json …)`. Dazu kamen drei Tests in
   `SolverOptionsTests`, und `_Vorlage/solvers/su2.json` führt den Schlüssel mit Erklärung.
   *Noch offen:* Ein **ausdrücklich** gesetztes `MachNumber` soll als „wirkungslos" gemeldet
   werden. Dafür braucht es den `ConfigLoadReport`, denn heute steht der Hinweis nur im Log-Text
   und im Kommentar von `MantaAuv/simulation.json`.
2. **Ein Record `FlowConditions`** (Geschwindigkeit, Dichte, Viskosität) wird in der
   `CfdProjectDefinition` gebaut und an Mesher **und** Solver gegeben. Die Grenzschichtberechnung
   im Mesher (TODO-40) braucht dieselben Zahlen wie SU2, und sie sollen nur an einer Stelle stehen.
3. **Log je Variante:** `Anströmung 2.0 m/s, Re_L = 1.0e5 (L = 50.0 mm aus der Bounding Box)`.
4. **Die Projekte:**
   * ✅ MantaAuv: **2,0 m/s** (E4), mit Begründung im Kommentar der `su2.json`. Ein Test hält den
     Wert fest (`MantaAuv_Sets_A_Realistic_Velocity_In_Water`). **Die Ergebnisse sind damit
     nicht mehr mit Läufen vor dem 04.11.2026 vergleichbar**, weil die Reynoldszahl von etwa
     1,8·10⁶ auf etwa 1·10⁵ fällt.
   * TestprojektNACA bleibt beim Weg über Mach 0,2 × 343,2 = 68,64 m/s. In Luft ist das
     physikalisch stimmig, und ein Umstellen hätte nur die Mach-Zahl in dessen `simulation.json`
     wirkungslos gemacht.

**Hinweis zur Physik:** Ein 50 mm langer Körper in Wasser bei 1–3 m/s liegt bei Re ≈ 0,5–1,5·10⁵.
Das ist **laminar bis transitional**. SA rechnet voll turbulent und überschätzt dort die Reibung.
Ein Transitionsmodell gehört nicht in diesen Plan, steht aber unter Risiken.

**Abnahme:** ✅ Tests für explizite Geschwindigkeit, den Rückfall auf Mach und den cfg-Inhalt.
Noch offen: die Warnung bei wirkungslosem `MachNumber` und die Reynoldszahl im Log. **Server:**
Beim nächsten MantaAuv-Lauf muss im Log `Anströmung: 2 m/s` stehen, und die cfg muss
`FREESTREAM_VELOCITY= ( 2, 0.0, 0.0 )` enthalten.

#### TODO-39 — `MeshRequest`, ein Netzfaktor, Nachlauf, Zellzahl ✅ *(erledigt 04.11.2026)*

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

#### TODO-40 — Strukturierte Quader-Grenzschicht mit vorgegebenem y+ (3D) ✅ *(Code & Tests erledigt 04.11.2026, Server-Spike offen)*

> **Korrektur vom 04.11.2026 (Empfehlung deines CFD-Professors, E12):** Die Wandschicht besteht
> standardmäßig aus **Hexaedern**, also rechteckigen, flachen, länglichen Quadern, und nicht aus
> Prismen (dreieckigen Säulen). **Gmsh kann das.** Eine Extrusion übernimmt die Form des
> Oberflächennetzes: Aus Dreiecken werden Prismen, aus **Vierecken werden Hexaeder**. Der Weg ist
> also: Oberfläche zuerst in Vierecke vernetzen (Recombine), dann mit `Recombine` extrudieren.
>
> **Was die Quader bringen:** Die Zellen sind orthogonal zur Wand und liegen in Strömungsrichtung
> sauber hintereinander. Dadurch werden die Gradienten in der Grenzschicht genauer, es gibt
> weniger numerische Diffusion und weniger Zellen bei gleicher Auflösung. Die *erste Zellhöhe*,
> also das, was y+ direkt festlegt, ist bei Prismen und Quadern dieselbe; die gleichmäßigere
> Zellform macht die Wandwerte aber ruhiger.
>
> **Die ehrliche Einschränkung:** Eine Oberfläche aus dem Voxel-STL vollständig in saubere
> Vierecke zu zerlegen, ist schwieriger als in Dreiecke. Gmsh erzeugt an schwierigen Stellen
> (enge Krümmung, Spitzen) einzelne Dreiecke, die Schicht wird dort also „quad-dominant" mit ein
> paar Prismen dazwischen. Das ist üblich und für SU2 kein Problem: SU2 rechnet Hexaeder,
> Prismen, Pyramiden und Tetraeder gemischt. Wie hoch der Quader-Anteil bei Manta und NACA
> tatsächlich wird, zeigt der Spike.

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
   Schalter `BoundaryLayerMode` mit drei Werten: `"Threshold"` (Vorgabe, wie heute), `"Hexa"`
   (Quader, **Ziel**) und `"Prism"` (Prismen, Rückfallebene). In den Modi `"Hexa"` und `"Prism"`
   schreibt er:
   * `ClassifySurfaces{…}; CreateGeometry;` für das Modell-STL. Danach ist die Oberfläche neu
     vernetzbar, ihre Feinheit ist von der Voxelgröße entkoppelt, und
     `MeshSizeFromCurvature` greift.
   * **Nur bei `"Hexa"`:** Die Oberfläche wird in Vierecke vernetzt (`Mesh.RecombineAll = 1` bzw.
     `Recombine Surface{…}`, ein Quad-Verfahren über `Mesh.RecombinationAlgorithm` und
     `Mesh.Algorithm` = 8 „Frontal-Delaunay for Quads"; *die Werte gegen Gmsh 4.11.1 prüfen*).
   * Eine Grenzschicht-Extrusion entlang der Normalen mit `Recombine`
     (`Extrude{ Surface{…}; Layers{ {1,…}, {h₁, h₁+h₂, …} }; Recombine; }`,
     *Syntax gegen Gmsh 4.11.1 prüfen*). Aus Vierecken werden Hexaeder, aus übrig gebliebenen
     Dreiecken Prismen.
   * Tetraeder zwischen der Oberkante der Grenzschicht und dem Kanal. Wo Viereckflächen auf
     Tetraeder treffen, braucht es **Pyramiden** als Übergang. *Prüfen, ob Gmsh 4.11.1 sie selbst
     einsetzt*; sonst bekommt die oberste Schicht Dreiecke, und die Hexaeder enden darunter.
   * Die Marker bleiben unverändert: `"Wall"` ist die ursprüngliche Oberfläche.

   Getestet wird an je einem STL von Manta und NACA, **beide Modi** (`"Hexa"` und `"Prism"`).
   Zu prüfen ist:
   * Gmsh läuft durch, und es gibt keine negativen Volumina (`Mesh.QualityType`).
   * Wie hoch ist der **Quader-Anteil** in der Wandschicht? Gmsh meldet die Elementzahlen je Typ.
   * SU2 startet.
   * Die y+-Statistik, Hexa gegen Prism verglichen.

   **Das Ergebnis kommt in diesen Plan, bevor es weitergeht.** Liefert `"Hexa"` auf dem
   Voxel-STL kein brauchbares Netz, bleibt `"Prism"` die Rückfallebene. Das muss dann **bewusst**
   in der `gmsh.json` gewählt werden.
3. **Optionen in `gmsh.json`:** `BoundaryLayerMode`, `TargetYPlus` (1,0), `BoundaryLayerMinLayers`
   (4), `BoundaryLayerGrowthRate` (1,2), `BoundaryLayerLayers` (0 = automatisch nach δ),
   `SurfaceCellSize` (mm, Stufe 1,0), `CurvatureElements`, `ClassifyAngleDeg` (40°). Die
   bestehenden `BoundaryLayerSize*` bleiben und steuern in den Modi `"Hexa"` und `"Prism"` die
   Übergangszone außerhalb der Wandschicht. Im Bedienhandbuch wird erklärt, warum `"Hexa"` die
   Empfehlung ist.
4. **Log je Variante:** `Grenzschicht: Hexa, y₁ = 0.0044 mm (y⁺ 1 bei Re 2.3e5), 24 Schichten,
   Dicke 1.6 mm, 94 % Quader`. Der Quader-Anteil kommt auch als `BoundaryLayerHexShare` in die CSV.
5. **Rückfall:** Scheitert Gmsh im gewählten Modus, gilt die Variante als fehlgeschlagen
   (`SimulationFailed`), und das `.geo` liegt in `FehlerLogs/`. **Kein** stiller Rückfall von
   Hexa auf Prism oder auf Threshold, weil sonst Varianten mit verschiedenen Wandnetzen verglichen
   würden.

**Abnahme:**
* Der Rechner stimmt mit handgerechneten Werten auf 1 % überein, die Mindestschichtzahl greift, und
  unsinnige Eingaben werfen `ArgumentOutOfRangeException`.
* Der `.geo`-Inhalt wird für Hexa (Recombine an Oberfläche **und** Extrusion) und für Prism getestet.
  Der Threshold-Modus bleibt byteweise unverändert.
* **Server:** Die y+-Werte aus TODO-41 liegen im Band (p95 ≤ 2 bei Ziel 1). Der Quader-Anteil steht
  im Spike-Ergebnis.
* Bedienhandbuch nachgezogen.

**2D (TODO-45)** ist davon nicht betroffen: Dort ist die Wandschicht mit `Quads = 1` ohnehin aus
rechteckigen Zellen, und anders als in 3D gelingt das dort praktisch immer vollständig.

#### TODO-41 — y+ nach jeder Rechnung prüfen ✅ *(erledigt 04.11.2026)*

1. In `OUTPUT_FILES` kommt `SURFACE_CSV` dazu. SU2 schreibt dann ein ASCII-`surface.csv` mit den
   Oberflächenwerten *(prüfen: heißt die Spalte `Y_Plus`?)*. Als Rückfall dient ein kleiner
   C#-Leser für `surface.vtu` (appended raw, UInt64-Kopf). Ein Python-Prototyp dafür lief am
   04.11. erfolgreich.
2. Neue Metriken `YPlus_Median`, `YPlus_P95` und `YPlus_Max` im Record und damit in der CSV.
3. Eine Warnung, wenn `YPlus_P95` über `YPlusWarnFactor` × `TargetYPlus` liegt (Vorgabe 2).

**Abnahme:** Ein Test mit einer kleinen Beispiel-`surface.csv` prüft Statistik und Warnung.

#### TODO-42 — Netzkalibrierung: eigener Befehl **und** Auto-Modus ✅ *(erledigt 04.11.2026)*

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
   *Einzige Ergänzung* ist `Use2DPrecalibration` aus TODO-48. Er wählt nur das **Verfahren** und
   ist kein Netzwert, kann also nicht mit einem kalibrierten Wert in Konflikt geraten.
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

#### TODO-43 — Gewinner fein nachrechnen (E3) ✅ *(erledigt 04.11.2026)*

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

> **Ziel von Block E (E10):** Am Ende stehen **eigenständige, vollständige 2D-Läufe ganz ohne
> 3D**. Ein 2D-Projekt wird genau wie ein 3D-Projekt mit `sim.ps1 run -Project <Name>` gestartet
> und nutzt Optimierer, Bouncer, Kalibrierung, Feinprüfung, CSV und fetch unverändert. Es braucht
> weder PicoGK noch Xvfb noch eine 3D-Rechnung.

#### TODO-44 — 2D-Kernel `Kernels/Planar2D` ✅ *(erledigt 04.11.2026)*

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

#### TODO-45 — `Gmsh2DMesher` ✅ *(Code & Tests erledigt 04.11.2026, Server-Spike offen)*

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

#### TODO-46 — SU2 in 2D ✅ *(erledigt 04.11.2026)*

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

#### TODO-47 — Projekt `Naca2D`, `_Vorlage2D`, Validierung ✅ *(erledigt 04.11.2026)*

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

#### TODO-48 — 2D-Vorkalibrierung für 3D-Läufe (E7) ✅ *(erledigt 04.11.2026)*

**Die Frage war:** Kann man am Anfang in 2D kalibrieren und dann in 3D rechnen, oder ist das
unsauber?

**Antwort: Den 2D-Netzfaktor einfach in 3D zu übernehmen, wäre unsauber.** 2D kennt vieles von
dem nicht, was in 3D die nötige Netzfeinheit bestimmt:
* die Auflösung in Spannweitenrichtung,
* Randwirbel an Flügelenden,
* Querströmung und dreidimensionale Ablösung,
* das Tetraedervolumen, dessen Diskretisierungsfehler sich anders verhält als bei den
  Quad-Zellen in 2D.

Eine 3D-Rechnung mit einem Faktor, der nur in 2D geprüft wurde, hätte keinen
Netzunabhängigkeitsnachweis.

**Sauber ist es als Vorkalibrierung.** 2D liefert billig den *Startpunkt*, 3D bestätigt mit
wenigen Stufen, und die **Abnahme bleibt in 3D**. In der Praxis ist das ein übliches Vorgehen,
denn Grenzschicht und Konturauflösung lassen sich am Schnitt in Minuten untersuchen. Der Gewinn
liegt weniger in der Rechenzeit (geschätzt 30–50 % der 3D-Kalibrierung) als in Sicherheit: Die
3D-Studie beginnt nahe am Ziel, und die Wandschicht ist schon geprüft, bevor eine teure
3D-Rechnung läuft.

**Voraussetzungen:**
1. **Das Projekt hat einen repräsentativen 2D-Schnitt.** Die Projektdefinition bietet dafür eine
   optionale Schnittstelle an, z. B. `IHasRepresentativeSection` mit
   `IGeometryGenerator CreateSection2D(ProjectContext)`. Das passt für schlanke oder extrudierte
   Formen wie Flügel, Profile oder Leitbleche; TestprojektNACA kann den Generator von `Naca2D`
   wiederverwenden. **Für MantaAuv passt es nicht:** Der Schnitt durch die Symmetrieebene eines
   3D-Körpers ist keine 2D-Strömung. Der Modus bricht dort mit einer Erklärung ab.
2. **Beide Mesher beziehen ihre Zellgrößen auf dieselbe Bezugslänge L**, zum Beispiel
   Oberflächenzelle = L/100 auf Stufe 1,0. Erst dann heißt Faktor 0,71 in 2D und in 3D dieselbe
   Auflösung entlang der Kontur. **Das ist eine Entwurfsvorgabe für TODO-39 und TODO-45** und muss
   dort von Anfang an so angelegt werden.
3. Die Wandschicht kommt in 2D und 3D aus demselben `BoundaryLayerCalculator` (TODO-40). Was 2D
   über y+ und Schichtzahl herausfindet, gilt damit unverändert auch in 3D.

**Ablauf:**
1. **2D-Stufe:** die volle Kalibrierung aus TODO-42 (fünf Stufen, Referenz auf der feinsten) am
   Schnitt. Das liefert f₂D und eine Prüfung von y+ und Schichtzahl.
2. **3D-Bestätigung:** drei Stufen um f₂D, nämlich f₂D·√2, f₂D und f₂D/√2; die feinste ist die
   3D-Referenz. Erfüllt f₂D die Toleranz gegenüber der Referenz, gilt f₂D. Erfüllt sie schon
   f₂D·√2, gilt diese gröbere und billigere Stufe. Erfüllt sie keine, geht es wie in TODO-42
   weiter verfeinert.
3. **Unsicherheit:** GCI aus den drei 3D-Stufen.
4. **Bericht:** Beide Tabellen, 2D und 3D, mit der ausdrücklichen Überschrift
   „2D-Vorkalibrierung + 3D-Bestätigung". Der Log-Kopf zeigt dann z. B.
   `Netzfaktor: 0.71 (KALIBRIERT: 2D-Vorkalibrierung, in 3D bestätigt, Toleranz 2 %)`.

**Konfiguration:** in `simulation.json`, gleiche Regeln wie TODO-42 (kein `MeshRefinementFactor`
im Auto-Modus):

```jsonc
"MeshCalibration": { "Mode": "Auto", "Tolerance": 0.02, "Use2DPrecalibration": true }
```

Derselbe Schalter gilt für den eigenen Befehl `calibrate`. Es gibt also eine Quelle (F1) und
keinen zusätzlichen Kommandozeilenschalter.

**Abnahme:**
* Evaluator-Tests für die Drei-Stufen-Bestätigung: f₂D passt, die gröbere passt, keine passt.
* Konfigurationstest: `Use2DPrecalibration` bei einem Projekt ohne Schnitt bricht mit Meldung ab.
* **Server:** TestprojektNACA einmal mit und einmal ohne Vorkalibrierung. Gewählter Faktor,
  Zahl der 3D-Läufe und Gesamtzeit kommen in diesen Plan.

### Block F — Bedienung

#### TODO-49 — Bedienhandbuch (E8) ✅ *(erledigt 04.11.2026)*

**Warum:** Es gibt inzwischen vier JSON-Dateien je Projekt, Code-Standards, local-Notausgänge,
und bald Kalibrier- und Verifikationsmodi. Ohne eine Übersicht weiß irgendwann niemand mehr,
auch du nicht, was wo eingestellt wird und was sinnvoll ist.

**Ort:** `src/projects/BEDIENHANDBUCH.md`, also dort, wo die Einstellungen liegen, und **nicht**
im Haupt-README. Verwiesen wird darauf:
* mit je einer Zeile aus dem Haupt-README, `src/projects/README.md`, `scripts/README.md`,
  `src/Automatisierung_v2/README.md`, `config/README.md` und `_Vorlage/README.md`,
* und aus dem Kommentarkopf **jeder** Projekt-JSON: `Was jeder Schlüssel bewirkt:
  src/projects/BEDIENHANDBUCH.md`.

**Zielgruppe:** jemand, der keinen Code liest. Kein C#, keine Klassennamen im Fließtext, jeder
Fachbegriff wird beim ersten Auftreten erklärt.

**Aufbau:**
1. **Auf einen Blick:** Ein Bild des Ablaufs (Geometrie → Netz → SU2 → Fitness → Optimierer →
   Bouncer → Feinprüfung) und dazu, welche Datei welchen Schritt steuert.
2. **Wo Einstellungen leben:** `project.json`, `simulation.json`, `solvers/su2.json` und
   `solvers/gmsh.json`, dazu der Code-Standard und der `*.local.json`-Notausgang. Die drei Regeln
   in Klartext: *Fehlt ein Schlüssel, gilt der Standard. Ein Tippfehler bricht den Start ab. Im
   Auto-Modus werden Netzwerte nicht eingetippt.*
3. **„Ich will …"-Tabelle**, aufgabenorientiert, etwa 20 Zeilen, je Zeile Datei, Schlüssel und
   Beispiel. Zum Beispiel:
   * einen kurzen Testlauf, genauer rechnen, schneller rechnen,
   * eine andere Geschwindigkeit, Wasser statt Luft, Auftrieb mitnehmen,
   * das Netz kalibrieren (manuell oder automatisch), Gewinner fein prüfen,
   * den Optimierer wechseln, Parametergrenzen ändern, ein neues Projekt anlegen,
   * Ergebnisse holen, eine Rechnung von Hand prüfen, das Projekt wechseln.
4. **Alle Einstellungen, Datei für Datei**, als Tabelle mit den Spalten: Schlüssel · Bedeutung in
   Klartext · Einheit · Standard · sinnvoller Bereich · Wirkung (Genauigkeit / Rechenzeit) · wann
   anfassen. Für die letzte Spalte gibt es drei Stufen: 🟢 *oft*, 🟡 *gelegentlich*,
   🔴 *nur mit Grund*.
5. **Welche Werte galten wirklich?** Wie man nach einem Lauf nachprüft, womit gerechnet wurde:
   Log-Kopf, `effective-config.json`, Kalibrier-Bericht und die Spalten in
   `Simulation_Results.csv`. Das ist die Nachvollziehbarkeits-Forderung aus E2.
6. **Befehle** (`sim.ps1`) in Klartext, eine Zeile je Befehl, mit Verweis auf `scripts/README.md`.
7. **Häufige Meldungen und was zu tun ist:** unbekannter Schlüssel, Warnblock bei einer
   local-Datei, fehlende Referenzfläche, nicht konvergiert, y+ zu hoch, Kalibrierung veraltet,
   Ergebnis netzabhängig.
8. **Glossar:** y+, Reynoldszahl, Grenzschicht, Netzfaktor, Cauchy-Kriterium, CFL, Fernfeld,
   Bouncer, GCI, RSM/EA, Voxel.

**Damit es nicht veraltet:** Ein neuer Test `HandbookCoverageTests` liest per Reflection jeden
Schlüssel aus `SimulationConfig`, `Su2SolverOptions`, `GmshMesherOptions` und den
Projekt-Manifesten (später auch aus den 2D-Optionen) sowie jeden Befehl aus `sim.ps1`. Er
verlangt, dass jeder davon im Handbuch steht. **Ein neuer Schlüssel ohne Erklärung bricht damit
`dotnet test`.** Ab jetzt gehört zu jeder Abnahme in diesem Plan außerdem der Satz
„Bedienhandbuch nachgezogen".

**Wann:** ✅ Die **erste Fassung ist geschrieben** (04.11.2026) und beschreibt den Stand *vor* den
weiteren Änderungen: alle 29 + 16 + 14 + 6 Schlüssel, alle zehn Befehle, die „Ich will …"-Tabelle
mit 20 Aufgaben, Meldungen und Glossar. Verweise stehen in allen READMEs und in allen zwölf
Projekt-JSONs. `HandbookCoverageTests` prüft Schlüssel und Befehle. Danach wächst das Handbuch
mit jedem TODO mit, und der Test erzwingt das.

**Abnahme:** `HandbookCoverageTests` ist grün. Alle Verweise sind gesetzt. **Du** liest die
„Ich will …"-Tabelle einmal gegen und sagst, was fehlt oder unverständlich ist.

---

## Reihenfolge und Abhängigkeiten

```text
A: TODO-31 (Rest), 32 ✅, 33 ✅   unabhängig, sofort nützlich
F: TODO-49 (erste Fassung)        sofort danach, wächst mit jedem weiteren Schritt mit
B: TODO-34                        unabhängig
C: TODO-35 → 36                   35 ist Voraussetzung für die Kalibrierung (Konvergenzprüfung)

D: TODO-38 (Rest) → 39 → 40 → 41 → 42 → 43
                     │                  ↑
E:                   └→ 44 → 45 → 46 → 47 (Validierung) → 48 (2D-Vorkalibrierung, braucht 42)
                            (45 nutzt den BoundaryLayerCalculator aus 40, Punkt 1;
                             39 und 45 legen die Zellgrößen auf dieselbe Bezugslänge, für 48)

später: TODO-37 (URANS), hängt an 35 und 43
```

**Empfohlene Reihenfolge** (Stand nach der zweiten Runde):
1. **TODO-49**, die erste Fassung des Bedienhandbuchs mit dem heutigen Stand. Das war deine
   größte Sorge, und es wird mit jedem weiteren Schritt teurer, das nachzuholen.
2. Der Rest von **TODO-31** (fetch), denn das war der ursprüngliche Ärger.
3. **B**, dann **C**.
4. Der Rest von **38**, dann **39**.
5. **E vor dem 3D-Teil (40–41)**, denn 2D ist der billige Prüfstand: Grenzschicht, y+-Auswertung
   und Kalibrierlogik lassen sich dort in Minuten statt Stunden testen, und TODO-47 beweist mit
   der Validierung, dass die Kette physikalisch stimmt.
6. Erst dann die riskante 3D-Extrusion (40), danach 42 und 43 für beide Welten, und **zuletzt 48**.
   Es braucht sowohl die Kalibrierung als auch die 2D-Kette.

Die Blöcke D und E berühren kaum dieselben Dateien. Gemeinsam sind nur `MeshRequest`,
`GeometryResult` und `Su2SolverOptions`, diese kommen also zuerst. Danach können zwei Worker
parallel arbeiten.

---

## Risiken und Fallstricke

| Risiko | Gegenmaßnahme |
| --- | --- |
| Die Grenzschicht-Extrusion auf einem Voxel-STL faltet sich in konkaven Ecken oder engen Spalten (verrauschte Normalen) | Erst der Spike (TODO-40 Punkt 2). StlSmoother bleibt an. Die Gesamtdicke der Grenzschicht wird gegen den kleinsten Krümmungsradius bzw. die kleinste Spaltweite geprüft. Kein stiller Rückfall |
| `ClassifySurfaces` zerlegt das Voxel-STL in sehr viele Flächen | Der Winkel `ClassifyAngleDeg` ist einstellbar, der Spike prüft das an Manta und NACA |
| Die Oberfläche aus dem Voxel-STL lässt sich nur schlecht in Vierecke zerlegen, und die Quader verzerren sich | Der Spike misst Quader-Anteil und Qualität. Die Rückfallebene `"Prism"` wählt man bewusst, ein stiller Wechsel ist ausgeschlossen. StlSmoother und `ClassifyAngleDeg` helfen. Der Anteil steht je Variante in der CSV (`BoundaryLayerHexShare`) |
| Am Übergang von Quadern zu Tetraedern fehlen Pyramiden, und das Netz ist ungültig | Im Spike prüfen, ob Gmsh 4.11.1 sie selbst einsetzt. Sonst endet die Quaderzone eine Schicht unter dem Übergang |
| Die Wandschicht vervielfacht die Zellzahl (3D, 20–25 Schichten) | Die Zellzahl steht je Variante in der CSV. Die Kalibrierung rechnet die feinste Stufe hoch und bricht vorher ab. 2D ist der billige Weg für Profilfragen |
| Bei Re ≈ 10⁵ (Manta in Wasser) ist die Strömung laminar bis transitional, SA rechnet voll turbulent | Als Hinweis im Log neben der Reynoldszahl (TODO-38). Ein Transitionsmodell ist ein eigener späterer Schritt *(prüfen, was SU2 7.5.1 bietet)* |
| Die Kalibrierung an der Startgeometrie gilt nicht für die optimierte Form | Genau dafür gibt es TODO-43. Bei großer Abweichung kommt eine laute Warnung |
| Die Voxelauflösung ist eine zweite „Netz"-Achse, weil sie die Geometrie selbst verändert | Sie bleibt in der Kalibrierung fest. Der Bericht nennt sie. Eine eigene Voxelstudie ist möglich, aber nicht Teil dieses Plans |
| Unterschiede zwischen den Stufen sind in Wahrheit Iterationsfehler | Nur konvergierte Stufen zählen (TODO-42 Punkt 5) |
| In 2D verfälscht ein zu kleines Fernfeld den Auftrieb | Vorgabe 50 Sehnenlängen, die Domänenprüfung gilt auch in 2D |
| Der Anstellwinkel landet nur im Vektor, CL und CD liegen dann in Körperachsen | Erst prüfen, dann umsetzen. Test: gedrehte Anströmung gegen gedrehte Geometrie (TODO-46 Punkt 1) |
| Neue Spalten in `PassiveParameters` verlängern die Champion-Ausgabe | Die Ausgabe wird nach Gruppen sortiert (Geometrie, Solver, Netz, Prüfung) |
| `MeshRequest` bricht die Signatur von `IMeshGenerator` | Nur das Framework und die Tests implementieren die Schnittstelle, Projekte nicht. Alles wird in TODO-39 auf einmal mitgezogen |
| Kalibrier-Bericht und eingetragener Wert laufen auseinander | Fingerabdruck plus Hinweis beim Start (TODO-42 Regeln 5 und 6) |
| `fetch` über das VPN bleibt hängen | ✅ Großzügige SSH-Zeitgrenzen (E6). Später zusätzlich ein Archiv statt vieler Verbindungen (TODO-31 Punkt 3) |
| SSH-Zeitgrenzen brechen unter Serverlast eine gesunde Verbindung ab (das frühere Erlebnis) | Die Werte sind bewusst weit (60 s Aufbau, 3 min Funkstille), und ein Test verhindert, dass sie verschärft werden. Ein Abbruch trifft nie den Lauf in tmux. Gibt es trotzdem Abbrüche, werden die Werte gelockert und nicht entfernt |
| Kommentare in der cfg ändern ungewollt einen Wert | Regressionstest: alle Zeilen ohne `%` bleiben unverändert (TODO-34) |
| Ein 2D-Faktor wird in 3D ungeprüft übernommen | Ist nicht vorgesehen: TODO-48 verlangt immer die 3D-Bestätigung, und ohne repräsentativen Schnitt bricht der Modus ab |
| Das Bedienhandbuch veraltet, weil neue Schlüssel nicht erklärt werden | `HandbookCoverageTests` bricht `dotnet test` bei jedem unerklärten Schlüssel oder Befehl (TODO-49) |
| MantaAuv-Ergebnisse vor und nach dem 04.11.2026 werden verglichen | Sie sind nicht vergleichbar (34,3 → 2 m/s, Re ≈ 1,8·10⁶ → 1·10⁵). Das steht im Kommentar der `su2.json` und in TODO-38 |

---

## Offene Fragen

Zurzeit gibt es keine. Alle Fragen der ersten Runde sind in der zweiten beantwortet:

* Geschwindigkeit → 2 m/s (E4)
* SSH-Zeitgrenzen → großzügig, umgesetzt (E6)
* Benutzername und PDF → bleiben (E5)
* öffentliches Repo → damit nicht mehr relevant

**Was du auf dem Server prüfen solltest**, sobald du das nächste Mal dort rechnest:
1. `.\scripts\sim.ps1 deploy` — bringt den neuen Stand (alle TODOs 31–49) auf den Server.
2. `.\scripts\sim.ps1 status` und `.\scripts\sim.ps1 fetch` (ohne `-Project`) — prüft die SSH-Zeitgrenzen, `.last_project` und den Archiv-Download (`pack`).
3. `.\scripts\sim.ps1 calibrate -Project Naca2D` — führt die 2D-Netzkalibrierung durch (wenige Minuten, kein Xvfb nötig) und prüft die NASA-TMR-Werte (`Naca2DTmrBenchmark`).
4. `.\scripts\sim.ps1 run -Project MantaAuv` — prüft `Anströmung: 2 m/s`, `Re_L`-Logausgabe, strukturierte 3D-Grenzschicht (`BoundaryLayerMode`: `"Hexa"` vs. `"Prism"`), `y+`-Auswertung aus `surface.csv`/`surface.vtu` und Verlaufsarchivierung (`Analyseergebnisse/Verlaeufe/`).
5. `python scripts/plot_history.py <Pfad>` — prüft den Plot einer heruntergeladenen `History_Gen1_Var1.csv`.
6. Optional: `.\scripts\sim.ps1 recheck -Project MantaAuv -Variant Gen1_Var1 -Finer 1 -Unsteady` — prüft die Nachrechnung inkl. URANS (`TimeDomain`).

---

## Abgrenzung

* **Nicht Teil dieses Plans:** der Block „Erweiterungen" aus den Anmerkungen (KI/ML, ein
  Physics-Informed-LLM, das C#-Code bearbeitet, der Nachbau des Leap71-Workflows, Ersatz von
  Optimierer oder Simulation durch ein Modell, FEM). Der 2D-Kernel ist bewusst so angelegt, dass er
  dafür später taugt (siehe Empfehlung).
* **Aus `architektur_kritikpunkte_naca.md` aufgegriffen werden nur:** Punkt 4 (Anstellwinkel, in
  TODO-46) und Punkt 5 (Bezugsgröße in 2D, in TODO-46). Die Punkte 1–3, 6 und 7 bleiben dort
  offen. Die `~`-Expansion aus Punkt 7 war ursprünglich für TODO-32 vorgesehen und ist mit E5
  wieder herausgefallen.
* **Getrennte Ein- und Auslassränder** im Windkanal (`MARKER_INLET`/`OUTLET`/`SYM`) sind nur als
  Kommentar erwähnt (TODO-34) und werden nicht umgesetzt.
* **Wandfunktionen** (y+ 30–100) kommen erst, wenn feststeht, dass SU2 7.5.1 sie im INC-Löser kann
  (F5).
