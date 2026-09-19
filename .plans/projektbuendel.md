# Projektbündel: ein Ordner pro Projekt, Umschalten nur über das Startskript

> **Stand: 19.09.2026** — Nachfolgeplan zu `architektur_refactoring.md` (dort offen: TODO-21,
> der End-to-End-Lauf auf dem Server). Die Nummerierung wird hier mit **TODO-22** fortgesetzt.

## Ziel

Drei Anforderungen, wörtlich aus der Aufgabenstellung:

1. **Jede JSON-Datei wird projektabhängig.** Auch `simulation.json`, `su2.json` und `gmsh.json` —
   nicht nur die Projektdatei.
2. **Ein neues Projekt darf die Einstellungen alter Projekte nicht überschreiben.** Zwei Projekte
   müssen nebeneinander existieren können, jederzeit umschaltbar.
3. **Umgeschaltet wird nur über den Startaufruf unter `scripts/`.** Kein Eingriff in `Program.cs`
   oder sonstigen Kern-Code — weder zum Umschalten noch zum Anlegen eines neuen Projekts,
   weder für neue Geometrie-Metriken noch für neue Solver-Ergebnisgrößen.

Zusatzziel: Für einen fremden Nutzer muss auf den ersten Blick klar sein, **welcher Name der
offizielle Projektname ist** und **wo er ihn einstellt**.

---

## Ausgangslage (geprüft am 19.09.2026)

| Befund | Stelle | Bedeutung für den Umbau |
|---|---|---|
| `config/simulation.json` ist eine **1:1-Kopie der Code-Standards** | `SimulationConfig.cs` vs. `config/simulation.json`; `OptimizationAlgorithmFactoryTests` prüft das aktiv | Die globale Basis-Schicht trägt **keine Information**. Sie kann ersatzlos entfallen — es geht nichts verloren. Dasselbe gilt für `su2.json` / `gmsh.json` gegen `Su2SolverOptions` / `GmshMesherOptions`. |
| Der Loader kann bereits beliebig viele Schichten | `JsonConfigLoader.Load<T>(defaults, params string[])` | Eine Projektschicht ist ein zusätzlicher Pfad im Aufruf, kein neuer Mechanismus. |
| SU2 schreibt Lift schon heraus | `HISTORY_OUTPUT= (ITER, RMS_RES, AERO_COEFF)` in `Su2ConfigGenerator.cs:85` | `AERO_COEFF` enthält CD, CL, CSF, CMx/y/z. Der Umbau betrifft fast nur die **Leseseite**. |
| Geometrie-Metriken sind bereits frei | `GeometryResult.AddMetric`, `WorkflowController.ApplyGeometryMetrics`, `SimulationContext.ExportToCsv` | Volumen/Oberfläche/Spannweite brauchen **keine** Core-Änderung. Details unter „Frage 3". |
| Die Programmpfade stehen als Code-Standard drin | `SimulationConfig.cs:10-12`: `/home/lpleissner/...` | Ein fremder Nutzer erbt die Pfade eines konkreten Servers. Gehört korrigiert (TODO-24). |
| Der Solver kennt einen festen Metriknamen | `Su2Solver.cs:104` schreibt `PassiveParameters["Drag"] = float.MaxValue` im Fehlerfall | Letzter hartcodierter Projektbegriff im Solver — fällt mit TODO-26. |
| `Ergebnisse/` ist ein gemeinsamer Topf | `SimulationContext.cs:44`, `sim-runner.sh:33` | Ein zweites Projekt überschreibt die `Simulation_Results.csv` des ersten. Gleiche Problemklasse wie Ziel 2. |
| `sim.ps1` kennt `-Project` bereits | `sim.ps1:57` → `SIM_PROJECT` → `sim-runner.sh:22` → `dotnet ... "$PROJECT_NAME"` | Die Umschaltkette **existiert schon**. Sie scheitert nur am `switch` in `Program.cs:28-43`. |

---

## Design-Entscheidungen (verbindlich)

| # | Entscheidung | Begründung |
|---|---|---|
| **1** | **Der Ordnername ist der Projektname.** Nichts sonst. | Ein einziger Ort, an dem der Name steht. Kein Abgleich zwischen Datei, Feld und Argument. `src/projects/MantaAuv/` ⇒ `.\scripts\sim.ps1 run -Project MantaAuv`. |
| **2** | Die Manifestdatei heißt in **jedem** Projekt `project.json`. | Ein Dateiname, der in allen Projekten gleich ist, kann nicht mit dem Projektnamen verwechselt werden. `config/projects/MantaAuv.json` konnte das. |
| **3** | **Die globale Basis-Schicht entfällt.** Standardwerte stehen im Code, Abweichungen im Projektordner, Maschinen-Kram in `*.local.json`. | Die Basisdateien duplizieren heute nur den Code. Drei Schichten statt vier, und es gibt keine „globale Zahl", die einem Projekt hineinregiert. |
| **4** | **Programmpfade werden PATH-relativ** (`mpirun`, `SU2_CFD`, `gmsh`) statt absolut. Damit entfällt der einzige verbliebene Zweck der `*.local.json`: **Sollstand ist, dass keine existiert.** Der Mechanismus bleibt als Notausgang, jede vorhandene Datei wird beim Start gemeldet. | Absolute Pfade eines bestimmten Servers im Code oder in einer unsichtbaren Datei sind für fremde Nutzer eine Falle. `sim-runner.sh load_env` setzt den `PATH` bereits richtig. |
| **5** | **Projekte werden per Reflection gefunden**, nicht über Typnamen in JSON. | `Type.GetType("…MantaGeometryGenerator")` in einer JSON ist ein Tippfehler, der erst im Lauf auffällt. Ein `IProjectDefinition` ist compilergeprüft. |
| **6** | **Ergebnisgrößen des Solvers sind eine Namenszuordnung in JSON**, kein Code. | „Lift dazunehmen" ist eine Zeile in `su2.json`, kein Eingriff in `Solvers/`. |
| **7** | **Fitness-Formel und Geometrie-Erzeugung bleiben C#**, im Projektordner. | Unverändert Entscheidung 3 des Vorgängerplans. Zahlen gehören in JSON, Formeln in Code. |
| **8** | Fehlt ein Schlüssel, gilt der Code-Standard. **Ein unbekannter Schlüssel ist ein Fehler.** | Heute verschluckt der Merge `"MachNumer"` stillschweigend. Bei mehreren Projekten × mehreren Dateien wird das zur Falle. |

---

## Zielstruktur

```
src/
  Automatisierung_v2/              Framework — wird für ein Projekt NIE angefasst
    Core/  Solvers/  Kernels/  Program.cs
  projects/                        Alle Projekte, je ein Ordner
    _Vorlage/                      Startpunkt für neue Projekte
    MantaAuv/                      ← Ordnername = Projektname
      project.json                 Parameter, Grenzen, Optimierungsziele
      simulation.json              Framework-Werte, nur Abweichungen
      solvers/
        su2.json                   inkl. Zuordnung der Ergebnisgrößen
        gmsh.json
      MantaProject.cs              : IProjectDefinition  — verdrahtet das Projekt
      MantaGeometryGenerator.cs    : IGeometryGenerator
      MantaFitnessCalculator.cs    : IFitnessCalculator
      *.local.json                 (gitignored) dieses Projekt auf diesem Rechner

config/                            NUR noch maschinenspezifisch, komplett gitignored
  simulation.local.json            Pfade, Kernzahl — gilt für alle Projekte
  solvers/su2.local.json
  README.md                        erklärt, was hier hineingehört (im Repo)
```

`src/projects/` liegt bewusst **neben** dem Framework, nicht darin: „hier das Gerüst, dort meine
Projekte" ist für einen fremden Nutzer sofort lesbar. Preis dafür ist eine einmalige Zeile in
`Automatisierung_v2.csproj`:

```xml
<ItemGroup>
  <Compile Include="..\projects\**\*.cs" Exclude="..\projects\**\bin\**;..\projects\**\obj\**" />
</ItemGroup>
```

Der Platzhalter `src/Automatisierung_v2/Projects/Project2/` verschwindet — ein leerer Ordner
suggeriert, dort müsse etwas eingetragen werden.

### Die drei Schichten

| # | Datei | Im Repo? | Gilt für |
|---|---|---|---|
| 1 | Code-Standards (`SimulationConfig`, `Su2SolverOptions`, `GmshMesherOptions`, `CreateDefaults()`) | ja | alles |
| 2 | `src/projects/<Name>/…json` | ja | dieses Projekt, alle Rechner |
| 3 | `config/…local.json` *(Notausgang, normalerweise nicht vorhanden)* | **nein** | alle Projekte, dieser Rechner |
| 4 | `src/projects/<Name>/…local.json` *(Notausgang, normalerweise nicht vorhanden)* | **nein** | dieses Projekt, dieser Rechner |

**Regel in einem Satz: je spezifischer, desto später — und gitignoriert schlägt eingecheckt.**
Schicht 3 gewinnt über Schicht 2, damit ein Projekt dem Server nicht seinen `Su2Path`
überschreiben kann. Schicht 4 gewinnt über alles.

### Wozu die `*.local.json` überhaupt da sind — und warum es sie künftig nicht mehr geben soll

Eine `*.local.json` ist eine **gitignorierte Ausnahmedatei für genau einen Rechner**. Sie wurde
eingeführt, damit der Server andere Programmpfade haben kann als der Windows-Entwicklungsrechner,
ohne dass diese Abweichung ins Repo wandert. `sim.ps1` schließt `*.local.json` deshalb vom Upload
aus (`Get-DeployFiles`, `sim.ps1:118-122`) — die Datei auf dem Server überlebt jedes Deploy und
wird von keinem Deploy überschrieben.

**Nach diesem Umbau braucht es sie nicht mehr.** `sim-runner.sh load_env` legt ohnehin
`$HOME/miniconda/bin`, `$HOME/software/bin` und das Gmsh-Verzeichnis auf den `PATH` — also genau
die Verzeichnisse, aus denen die heute hartcodierten Pfade stammen. Sobald die Code-Standards auf
`mpirun`, `SU2_CFD` und `gmsh` umgestellt sind (TODO-24), findet der Server sie von selbst.
Alles andere — Laufumfang, Physik, Netzfeinheit — ist Projektsache und gehört nach
`src/projects/<Name>/`.

> **Sollstand: es existiert nirgends eine `*.local.json`.** Der Mechanismus bleibt als Notausgang
> im Loader erhalten (er kostet nichts und ist die einzige Möglichkeit, einen Rechner mit
> exotischer Installation zu bedienen), aber jede vorhandene Datei ist ab sofort ein Sonderfall,
> den das Programm beim Start **laut meldet** — mit Dateiname und jedem einzelnen Schlüssel, den
> sie dem Projekt überschreibt (TODO-22).

Konkret betrifft das die `config/simulation.local.json` auf dem BIC_12: sie setzt nicht nur Pfade,
sondern auch den Laufumfang. Sie wird in TODO-24 gelöscht — **aber nicht ersatzlos**: ihr Inhalt
muss vorher gelesen und das, was behalten werden soll (der reduzierte Laufumfang), nach
`src/projects/MantaAuv/simulation.json` übernommen werden. Sonst rechnet der nächste Lauf wieder
die vollen `MaxIterations × VariantsPerIteration = 100` Varianten.

---

## Umschalten ohne Eingriff in den Kern

Heute steht in `Program.cs:28-43` ein `switch`, der Geometrie, Kernel und Fitness fest verdrahtet.
Solange der existiert, ist ein neues Projekt eben **nicht** ein Ordner.

### `IProjectDefinition`

```csharp
public interface IProjectDefinition
{
    /// Muss exakt dem Ordnernamen unter src/projects/ entsprechen.
    string Name { get; }

    ProjectConfig     CreateDefaults();                       // project.json überlagert das
    IGeometryKernel   CreateKernel();                         // PicoGK oder DirectGeometryKernel
    IGeometryGenerator CreateGeometry(ProjectContext context);
    IFitnessCalculator CreateFitness(ProjectContext context);
    SolverStage[]     CreateStages(ProjectContext context);   // Mesher↔Solver-Kette
}
```

`ProjectContext` reicht durch, was ein Projekt zum Verdrahten braucht: `Name`, `Directory`,
die fertig geladene `SimulationConfig` und `ProjectConfig` sowie
`LoadSolverOptions<T>(string name)`, das bereits auf den Projektordner zeigt.

Eine Basisklasse nimmt dem Normalfall die Arbeit ab:

```csharp
public abstract class CfdProjectDefinition : IProjectDefinition
{
    public virtual IGeometryKernel CreateKernel() => new PicoGkKernel();

    public virtual SolverStage[] CreateStages(ProjectContext c) => new[]
    {
        new SolverStage(
            new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
            new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2")))
    };
    // Name, CreateDefaults, CreateGeometry, CreateFitness bleiben abstrakt
}
```

`MantaProject.cs` ist damit rund 20 Zeilen. Ein Projekt mit anderer Solver-Kette überschreibt
`CreateStages`, eines ohne Voxel-Kernel `CreateKernel` — beides in seinem eigenen Ordner.

### `ProjectRegistry`

Sucht beim Start alle nicht-abstrakten `IProjectDefinition`-Typen in der Assembly und prüft hart:

* Namen eindeutig (Groß-/Kleinschreibung ignoriert)
* zu jedem Namen existiert `src/projects/<Name>/`
* zu jedem Ordner unter `src/projects/` (außer `_Vorlage`) existiert genau eine Definition
* unbekannter Name beim Start ⇒ Abbruch mit der **Liste der vorhandenen Projekte**

`Program.cs` schrumpft auf: Namen bestimmen → `ProjectRegistry.Resolve(name)` → Konfiguration
laden → verdrahten → `kernel.RunHosted(...)`. Danach wird die Datei nie wieder angefasst.

Projektname-Quellen, in dieser Reihenfolge: `args[0]` → Umgebungsvariable `SIM_PROJECT` → Abbruch
mit Projektliste. **Kein `"MantaAuv"` als stiller Standard mehr** — ein Vertipper im Skript würde
sonst klaglos das falsche Projekt rechnen.

### Auflösung des Projektverzeichnisses

Ein `ProjectPaths`-Helfer ersetzt die heutige „6 Ebenen hoch, suche `config`"-Heuristik:

1. expliziter Pfad aus `args[1]` (Abwärtskompatibilität zum heutigen Aufruf)
2. Umgebungsvariable `SIM_PROJECTS_DIR`
3. aufwärts suchen nach einem Verzeichnis, das `src/projects/` enthält

Bewusst **kein** `CopyToOutputDirectory` in der csproj: die JSONs lägen dann in `bin/`, und eine
Änderung an der Quelldatei würde erst nach einem Rebuild wirken. Diese Sorte Verwirrung will man
bei einem mehrstündigen Lauf nicht.

---

## Frage 2: SU2 soll mehr als nur Drag liefern

`AERO_COEFF` steht bereits in `HISTORY_OUTPUT`, die `history.csv` enthält also CD, CL, CSF und
CMx/y/z. Verworfen wird alles außer CD erst beim Lesen. Der Umbau ist deshalb klein.

### Neu in `Su2SolverOptions`

```csharp
/// Name in PassiveParameters  →  Spaltenname in der SU2-history.csv
public Dictionary<string, string> ResultMetrics { get; set; } = new() { ["Drag"] = "CD" };

public string   ConvergenceField { get; set; } = "DRAG";                       // CONV_FIELD
public string[] HistoryOutput    { get; set; } = { "ITER","RMS_RES","AERO_COEFF" };
```

In `src/projects/<Name>/solvers/su2.json` dann:

```jsonc
// Links: unter welchem Namen die Fitness-Formel den Wert sieht.
// Rechts: wie die Spalte in der SU2-history.csv heißt.
"ResultMetrics": {
  "Drag": "CD",
  "Lift": "CL",
  "Nickmoment": "CMy"
}
```

Der Standard `{ "Drag": "CD" }` hält das heutige Verhalten exakt bei — ein bestehendes Projekt
merkt vom Umbau nichts.

### Details, die dabei zu klären sind

* **Exakter Spaltenvergleich statt `StartsWith`.** `Su2Solver.cs:174` sucht heute
  `StartsWith("CD")`. Für Momente wäre das fatal: `"CM"` träfe `CMx`, `CMy` und `CMz`, und welche
  Spalte gewinnt, hinge von der Reihenfolge ab. Künftig: Anführungszeichen entfernen, trimmen,
  case-insensitiv **exakt** vergleichen; nur wenn das leer ausgeht, ersatzweise Präfix-Treffer
  **mit Warnung**.
* **Fehlende oder unlesbare Spalte ⇒ lauter Fehler.** Wer eine Größe konfiguriert, braucht sie.
  Der Solver warnt mit der Liste der tatsächlich vorhandenen Spalten und setzt
  `record.SimulationFailed = true`.
* **`Su2Solver.cs:104` fällt weg.** Statt `PassiveParameters["Drag"] = float.MaxValue` im
  Abbruchfall wird nur noch `record.SimulationFailed = true` gesetzt. Damit kennt der Solver
  keinen einzigen festen Metriknamen mehr. `MantaFitnessCalculator` prüft dieses Flag bereits
  zuerst (`MantaFitnessCalculator.cs:58`); die zusätzliche `MaxValue`-Prüfung darf als Gürtel
  neben den Hosenträgern stehenbleiben.
* **`CONV_FIELD= DRAG` ist hartcodiert** (`Su2ConfigGenerator.cs:80`). Ein Projekt, das auf Auftrieb
  optimiert, will dort `LIFT`. Wird zu `ConvergenceField`.
* **Objekte werden additiv gemerged.** `Merge` verschmilzt verschachtelte Objekte, ersetzt sie
  nicht. Ein Projekt, das nur `{"Lift":"CL"}` schreibt, bekommt `Drag` aus dem Standard dazu —
  gewollt. Eine Größe wieder **los**wird man dadurch nur über den Code-Standard. Das gilt heute
  schon für `BaseParameters` und ist im Vorgängerplan so festgehalten; es muss im Kommentarkopf
  der Vorlagedatei stehen.
* **`REF_AREA` gilt auch für CL.** Die über `ReferenceAreaMetric` gewählte Fläche normiert alle
  Beiwerte. Wer Auftrieb auswertet, sollte prüfen, ob die Frontalfläche dafür die richtige
  Bezugsgröße ist — eine Flügelfläche wäre üblicher. Das ist eine Konfigurationsfrage, kein
  Codeproblem, gehört aber in den Kommentarkopf.
* **Vorzeichen und Richtung.** CL hängt an der Anströmrichtung `FREESTREAM_VELOCITY= (v,0,0)` und
  an `REF_ORIGIN_MOMENT_*`. Beim ersten Lauf mit Auftrieb einmal gegen eine bekannte Geometrie
  prüfen.

---

## Frage 3: Volumen, Oberfläche, Spannweite für die Fitness

**Kurze Antwort: das geht heute schon, ohne eine Zeile im Kern.** Der Weg ist durchgehend
namensbasiert:

```
MantaGeometryGenerator.AddMetric("SurfaceArea", wert, MetricScaling.Area)
        ↓  WorkflowController.ApplyGeometryMetrics  (generisch, rechnet zurück)
record.PassiveParameters["SurfaceArea"]
        ↓  MantaFitnessCalculator                   (liest nach Namen)
record.Fitness
        ↓  SimulationContext.ExportToCsv            (sammelt Spalten dynamisch)
```

Eine neue Metrik anzulegen heißt: **zwei Dateien im Projektordner** anfassen (Geometrie-Generator
und Fitness-Rechner), Grenzwerte dazu in `project.json` unter `OptimizationTargets`. Kein
Interface, kein `Core/`.

Drei Fallstricke:

* **Die Dimension muss stimmen.** `MetricScaling` steuert, wie der Gummiband-Skalierer von der
  PicoGK-Arbeitsgröße zurückrechnet: Oberfläche `Area`, Spannweite `Linear`, Volumen `Volume`,
  Verhältniszahlen `None`. Ein falscher Eintrag fällt nicht auf — der Wert ist dann um den
  Skalierungsfaktor hoch 1, 2 oder 3 daneben. Im `_Vorlage`-Generator gehört ein Kommentar dazu.
* **Oberfläche liefert PicoGK nicht direkt.** `CalculateProperties(out float volume, out BBox3 box)`
  gibt Volumen und Hüllquader; die Spannweite folgt aus der Box, die **Oberfläche nicht**. Sie muss
  aus dem Dreiecksnetz summiert werden. Das wäre in jedem Projekt dieselbe Schleife — deshalb
  TODO-29: ein `Core/Utilities/MeshMetrics`-Helfer neben dem vorhandenen `StlWriter`/`StlSmoother`,
  optional nutzbar. Das ist eine Framework-**Ergänzung**, keine Framework-**Änderung**: bestehende
  Projekte merken nichts davon.
* **Pflichtgrößen früh prüfen.** `MantaFitnessCalculator` prüft seine `OptimizationTargets` schon
  im Konstruktor (`MantaFitnessCalculator.cs:32-47`) — vorbildlich. Für **Metriken** geht das nicht
  beim Verdrahten, weil sie erst nach der ersten Geometrie existieren. Deshalb: `IFitnessCalculator`
  bekommt ein `RequiredMetrics`-Feld, und der `WorkflowController` prüft es **nach der ersten
  Variante** und bricht dort ab, statt stundenlang mit `Fitness = 0.0001` weiterzurechnen.

---

## Arbeitsschritte

Reihenfolge ist bindend, wo Abhängigkeiten stehen. Jeder Schritt endet mit grünem
`dotnet build` **und** `dotnet test`.

### TODO-22 — Projektpfade und Schichten im Loader
**Neu:** `Core/Configuration/ProjectPaths.cs`.
**Geändert:** `JsonConfigLoader` — vier Schichten in der oben festgelegten Reihenfolge; Abgleich
jedes Overlay-Schlüssels gegen die Properties des Zieltyps (unbekannt ⇒ `InvalidOperationException`
mit Dateiname, Schlüssel und den nächstähnlichen gültigen Namen); Log gibt je Schlüssel die
gewinnende Datei aus statt nur „N Einträge übernommen".

**Warnblock für `*.local.json`.** Findet der Loader eine solche Datei, gibt er beim Start einen
auffälligen Block aus — nicht nur „gefunden", sondern jeden Schlüssel, der damit die
Projekteinstellung aushebelt, samt beider Werte:

```
==================================================================
[WARNUNG] Eine maschinenspezifische Datei ueberschreibt dieses Projekt:
          config/simulation.local.json
          MaxIterations        Projekt: 10   ->   local: 2
          MpiCores             Projekt: 12   ->   local: 4
          Normalerweise sollte es diese Datei nicht geben. Gehoeren die
          Werte zum Projekt, dann nach src/projects/MantaAuv/ uebernehmen
          und die Datei loeschen.
==================================================================
```

Schlüssel, die den Projektwert gar nicht ändern (identischer Wert), werden nicht aufgeführt — sonst
geht die eigentliche Meldung im Rauschen unter. Der Block steht zusätzlich im
`effective-config.json` aus TODO-25.

**Abnahme:** Test mit einem temporären Projektordner, der eine Zahl überschreibt; Test, dass ein
unbekannter Schlüssel abbricht; Test der Schichtreihenfolge (3 schlägt 2, 4 schlägt 3); Test, dass
eine local-Datei den Warnblock mit genau den abweichenden Schlüsseln erzeugt und eine
wertgleiche local-Datei keinen.
**Unabhängig von TODO-23.**

### TODO-23 — `IProjectDefinition` und `ProjectRegistry`
**Neu:** `Core/Interfaces/IProjectDefinition.cs`, `Core/Models/ProjectContext.cs`,
`Core/Configuration/ProjectRegistry.cs`, `Core/Pipeline/CfdProjectDefinition.cs`.
**Geändert:** `Program.cs` — der `switch` entfällt; Kommandos `--list-projects` und
`--print-config` (beide vor dem Kernel-Start, ohne PicoGK/Xvfb).
**Abnahme:** Test, dass `MantaAuv` aufgelöst wird; Test, dass ein unbekannter Name die Liste der
vorhandenen Projekte nennt; Test, dass doppelte Namen beim Start auffallen.

### TODO-24 — MantaAuv umziehen, `config/` eindampfen
*Setzt TODO-22 und TODO-23 voraus.*
* `src/Automatisierung_v2/Projects/MantaAuv/*.cs` → `src/projects/MantaAuv/` (mit `git mv`, damit
  die Historie erhalten bleibt), neu dazu `MantaProject.cs`.
* `config/projects/MantaAuv.json` → `src/projects/MantaAuv/project.json`, Feld `ProjectName`
  entfernen (der Ordner sagt es).
* `config/simulation.json`, `config/solvers/*.json` → `src/projects/MantaAuv/` **und**
  `src/projects/_Vorlage/`. Kommentarköpfe überarbeiten.
* **Code-Standards der Pfade neutralisieren:** `MpiRunPath` → `mpirun`, `Su2Path` → `SU2_CFD`,
  `GmshPath` bleibt `gmsh`. Alle drei liegen auf dem Server auf dem `PATH`, den `load_env` setzt
  (`sim-runner.sh:50`), und .NET löst einen bloßen Programmnamen in `Process.StartInfo.FileName`
  über den `PATH` auf. `Su2Path` wird ohnehin nur als Argument an `mpirun` durchgereicht und von
  diesem selbst aufgelöst. Damit entfällt der letzte Grund für eine `*.local.json`.
* `config/` behält nur `README.md` — erklärt, dass es hier im Normalfall **nichts** einzutragen
  gibt und wozu der Notausgang gut ist.
* `Projects/Project2/` löschen.
* `src/Automatisierung_v2/Automatisierung_v2.csproj`: `<Compile Include="..\projects\**\*.cs" />`.

**Auf dem Server von Hand — `config/simulation.local.json` löschen.**

Warum das ein Handgriff per SSH ist und kein Deploy: `sim.ps1` filtert `*.local.json` vom Upload
aus (`Get-DeployFiles`, `sim.ps1:118-122`). Ein Deploy kann so eine Datei deshalb weder anlegen
noch ändern noch löschen — sie lässt sich nur dort entfernen, wo sie physisch liegt. Genau das ist
der Grund, warum sie dem Ziel „was ich hochlade, ist was läuft" im Weg steht.

```powershell
# 1. Anschauen — nur, damit du weisst, welche Werte ab jetzt nicht mehr gelten
ssh BIC_12 "cat ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"

# 2. Loeschen
ssh BIC_12 "rm ~/Documents/AutomatisierungCleanVersion/config/simulation.local.json"

# 3. Kontrolle
ssh BIC_12 "ls ~/Documents/AutomatisierungCleanVersion/config/"
```

**Nichts davon wird „hinübergerettet".** Ab dem Löschen gilt ausschließlich
`src/projects/MantaAuv/simulation.json` — die Datei, die du auf Windows bearbeitest und mit
`sim.ps1 run` hochlädst. Willst du einen kurzen Testlauf, setzt du `MaxIterations` **dort** und
deployst. Es gibt dann kein verstecktes zweites Stellrad mehr.

Schritt 1 dient nur dazu, dass dich der erste Lauf danach nicht überrascht: stand dort bisher ein
kleinerer Laufumfang als in der Projektdatei, rechnet der nächste Lauf entsprechend länger. Das ist
dann aber der Wert, den du selbst hochgeladen hast — also richtig.

Danach einmal `.\scripts\sim.ps1 doctor`: der prüft die aufgelösten Pfade und meldet, wenn
`mpirun` oder `SU2_CFD` doch nicht auf dem `PATH` liegen. **Erst wenn doctor sauber durchläuft,**
ist das Löschen abgeschlossen. Läuft er nicht durch, ist eine `config/simulation.local.json` mit
**ausschließlich** den drei Pfad-Schlüsseln der legitime Notausgang — und der Warnblock aus
TODO-22 erinnert dich bei jedem Start daran, dass dieser Rechner eine Sonderlocke hat.

**Abnahme:** `dotnet run -- MantaAuv` liefert dieselbe effektive Konfiguration wie vor dem Umzug;
der Start meldet **keinen** local-Warnblock mehr; angepasste
`ProjectConfigJsonTests`/`SolverOptionsTests` sind grün.

### TODO-25 — Ergebnisse pro Projekt
`SimulationContext.WorkingDirectory` wird `Ergebnisse/<Projekt>/`. Mitziehen:
`sim-runner.sh:33` (`RESULT_CSV`) und `cmd_status`, `sim.ps1` `Invoke-Fetch`, die hartcodierten
Pfadtexte in `WorkflowController.FinishOptimization` (`WorkflowController.cs:288-289`).
Zusätzlich schreibt jeder Lauf `Ergebnisse/<Projekt>/effective-config.json` mit der tatsächlich
verwendeten Konfiguration samt Herkunft je Schlüssel — damit bleibt ein alter Lauf rekonstruierbar,
egal was danach an den Dateien passiert.

### TODO-26 — SU2-Ergebnisgrößen konfigurierbar
`ResultMetrics`, `ConvergenceField`, `HistoryOutput` in `Su2SolverOptions`; `ReadDragFromHistory`
wird zu `ReadMetricsFromHistory`; exakter Spaltenvergleich; Fehlerpfad auf `SimulationFailed`
umgestellt; `Su2ConfigGenerator` nutzt die neuen Optionen.
`IFitnessCalculator.RequiredMetrics` + Prüfung nach der ersten Variante im `WorkflowController`.
**Abnahme:** Test, dass der Standard weiterhin genau `Drag`←`CD` liefert; Test mit einer
Beispiel-`history.csv`, die CD, CL und CMy enthält; Test, dass `CM` nicht versehentlich `CMx` trifft;
Test, dass eine konfigurierte, aber fehlende Spalte den Record als fehlgeschlagen markiert.
**Unabhängig von TODO-22 bis 25** — kann vorgezogen werden.

### TODO-27 — Vorlage und Anlege-Befehl
`src/projects/_Vorlage/` mit durchkommentierten JSONs (alle Schlüssel mit den Code-Standards als
Wert), einem Geometrie-Generator, der einen Quader baut und Volumen/Oberfläche/Spannweite meldet,
und einer Fitness, die nur den Widerstand minimiert — lauffähig als Rauchtest.
`scripts/new-project.ps1 <Name>` kopiert die Vorlage, benennt Klassen/Namensräume um und nennt den
Startbefehl. **Nicht** über `JsonConfigLoader.Save` erzeugen: das schreibt JSON ohne Kommentare und
vernichtet genau die Erklärungen, die einen neuen Nutzer tragen.

### TODO-28 — Skripte nachziehen
* `sim.ps1`: `-Project` bleibt der Umschalter; `$DeployRoots` deckt `src/projects` über `src`
  bereits ab; neuer Befehl `projects` (listet, was der Server kennt). Der `*.local.json`-Filter in
  `Get-DeployFiles` ist ein Namensmuster und schützt die neuen Projekt-Locals ohne Änderung.
* `sim-runner.sh`: `PROJECT_NAME` ohne stillen Standard; `config_value` (der `sed`-JSON-Parser,
  `sim-runner.sh:86-101`) wird durch `dotnet "$APP_DLL" --print-config` ersetzt — sonst prüft
  `doctor` Pfade aus einer Datei, die gar nicht mehr die maßgebliche ist.
* `doctor` darf ein fehlendes `config/` nicht mehr als Fehler werten (`sim-runner.sh:189`): auf
  einem frischen Rechner ist der Ordner leer und damit in git unsichtbar.

### TODO-29 — optional: `MeshMetrics`-Helfer
Oberfläche, Hüllquader, Spannweite und Volumen aus dem Dreiecksnetz, damit Projekte das nicht
jeweils neu schreiben. Rein additiv, blockiert nichts.

### TODO-30 — Dokumentation
`src/projects/README.md`: „So legst du ein Projekt an" in sechs Schritten. Ergänzungen in
`scripts/README.md` und `src/Automatisierung_v2/README.md`. Diesen Plan als erledigt markieren.

---

## Risiken und Fallstricke

| Risiko | Gegenmaßnahme |
|---|---|
| Die Server-`simulation.local.json` überschreibt als Schicht 3 jedem Projekt den Laufumfang | TODO-24: Datei ersatzlos löschen. Danach gilt nur noch, was hochgeladen wurde. Der Warnblock aus TODO-22 macht jede künftige local-Datei beim Start sichtbar |
| Nach dem Löschen läuft der nächste Lauf länger als gewohnt, weil der Laufumfang nun aus der Projektdatei kommt | Kein Fehler, sondern das Ziel. Den gewünschten Umfang vor dem nächsten `sim.ps1 run` in `src/projects/<Name>/simulation.json` setzen |
| Ein Deploy löscht eine vorhandene local-Datei **nicht** mit | Sie ist vom Upload ausgeschlossen und muss per SSH entfernt werden; genau deshalb meldet TODO-22 sie bei jedem Start |
| PATH-relative Pfade finden `mpirun`/`SU2_CFD` auf einem anderen Rechner nicht | `sim.ps1 doctor` prüft genau das, bevor ein Lauf startet; im Notfall ist eine local-Datei mit den Pfaden weiterhin zulässig |
| `config/` enthält nur noch gitignorierte Dateien und existiert auf einem frischen Rechner nicht | `config/README.md` hält den Ordner in git; Loader und `doctor` tolerieren das Fehlen |
| Unbekannter Schlüssel wird still verschluckt (heute schon) | TODO-22: Abgleich gegen die Zieltyp-Properties, Abbruch mit Vorschlag |
| `<Compile Include>` doppelt sich mit dem SDK-Glob, falls `src/projects` je unter die csproj wandert | Der Include zeigt nach `..`, also außerhalb des Glob-Wurzelverzeichnisses; bei einem späteren Umzug muss die Zeile weg |
| Verschachtelte JSON-Objekte lassen sich nur ergänzen, nicht leeren | Bewusst so (gilt heute schon); im Kommentarkopf der Vorlage festhalten |
| Ordnername und `IProjectDefinition.Name` laufen auseinander | `ProjectRegistry` prüft beide Richtungen beim Start |
| Falsches `MetricScaling` bei einer neuen Metrik fällt nicht auf | Kommentar in der Vorlage; das `effective-config.json` aus TODO-25 hält die Zuordnung fest |
| `REF_AREA` normiert auch CL | Kommentarkopf in `su2.json`; beim ersten Auftriebslauf gegen eine bekannte Geometrie prüfen |
| Ein Projekt ohne PicoGK bricht beim Kernel-Start ab | `CreateKernel()` gehört zur Projektdefinition; die Vorlage zeigt beide Varianten |
| `git mv` über OneDrive kann Dateien doppeln | Vor TODO-24 sicherstellen, dass die Synchronisierung ruht; danach `git status` prüfen |

---

## Was sich für den Nutzer ändert

**Neues Projekt anlegen:**

```powershell
.\scripts\new-project.ps1 Propeller      # kopiert _Vorlage nach src/projects/Propeller
# Geometrie und Fitness in src/projects/Propeller/ ausfüllen, Zahlen in dessen JSONs
.\scripts\sim.ps1 run -Project Propeller
```

**Zwischen Projekten wechseln:** nur der `-Project`-Wert. `MantaAuv` bleibt unberührt in seinem
Ordner liegen, mit seinen Zahlen und seinen Ergebnissen unter `Ergebnisse/MantaAuv/`.

**Angefasst wird dabei ausschließlich `src/projects/<Name>/`.**
