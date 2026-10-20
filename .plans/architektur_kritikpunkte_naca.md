# Architektur-Kritikpunkte & Optimierungspotenziale (Erkenntnisse aus TestprojektNACA)

> **Stand: 20.10.2026** — Dokumentation von Schwachstellen, Randfall-Problemen und Verbesserungsvorschlägen im Framework (`Core/`, `Solvers/`, `_Vorlage/`), die bei der Implementierung des Tragflügel-Optimierungsprojekts **`TestprojektNACA`** (NACA 0012 Profil, Gleitzahl $E = C_L / C_D$) aufgefallen sind.

---

## Übersicht der Kritikpunkte

| Nr. | Bereich | Komponente | Schweregrad | Problemstellung |
|---|---|---|---|---|
| 1 | `Core/Utilities` | `RubberBandScaler` | **Hoch (Bug)** | Skaliert um $50\times$, wenn `DimensionalParameters` leer ist |
| 2 | `Core/Pipeline` | `ChampionValidator` (`Bouncer`) | **Mittel** | Additiver, absoluter Jitter statt relativer Streuung; keine Begrenzung auf Bounds |
| 3 | `Core/Pipeline` | `WorkflowController` | **Niedrig** | Konsolenausgabe schneidet aktive Parameter bei `:F2` ab |
| 4 | `Solvers/Cfd` | `Su2ConfigGenerator` | **Mittel** | Kein Anstellwinkel (`AOA`) / fester Zustromvektor `(v, 0, 0)` |
| 5 | `Solvers/Cfd` | `Su2SolverOptions` | **Niedrig** | Standard-Bezugsfläche `FrontalArea` vs. Flügelgrundrissfläche `PlanformArea` |
| 6 | `Kernels/PicoGK` | Geometrieaufbau | **Mittel** | Dünne Hinterkanten bei Voxelauflösung $0.5\text{ mm}$ (Gefahr von Voxelinseln) |
| 7 | `Configuration` / `Core` | Programmpfade & Solver-Auflösung | **Mittel** | Starre Wahl zwischen PATH-relativ und Absolutpfaden; fehlende Fallback-Kette bei Server-Umgebungen |

---

## 1. Bug in `RubberBandScaler`: Fehlskalierung bei leeren `DimensionalParameters`

### Fundstelle
[`src/Automatisierung_v2/Core/Utilities/RubberBandScaler.cs#L57-L70`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Core/Utilities/RubberBandScaler.cs#L57-L70)

### Analyse
Im Konstruktor von `RubberBandScaler` wird die maximale Abmessung wie folgt ermittelt:
```csharp
float maxDimension = 0f;
foreach (var kvp in realParameters)
{
    if (dimensionalParameters.Contains(kvp.Key) && kvp.Value > maxDimension)
    {
        maxDimension = kvp.Value;
    }
}

if (maxDimension <= 0f) maxDimension = 1f;

ShrinkFactor = targetSize / maxDimension;
```

Wenn ein Projekt ausschließlich dimensionslose Formparameter optimiert (z. B. relative Profilwölbung $m$, Wölbungslage $p$, relative Dicke $t$ bei fester Sehnenlänge), ist `DimensionalParameters` leer (`[]`).
* Dadurch bleibt `maxDimension` auf `0f` und wird von `if (maxDimension <= 0f)` auf **`1.0f`** gesetzt.
* Bei `TargetPicoGkSize = 50.0` ergibt sich:
  $$\text{ShrinkFactor} = \frac{50.0}{1.0} = 50.0$$
* **Folgefehler in der Pipeline:**
  1. [`GmshCfdMesher`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Solvers/Cfd/GmshCfdMesher.cs#L164) skaliert das Netz mit `Mesh.ScalingFactor = (1.0 / 50.0) * 0.001` $\Rightarrow$ Die Geometrie wird im CFD-Netz um das **50-Fache zu klein** simuliert!
  2. [`WorkflowController.ApplyGeometryMetrics`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Core/Pipeline/WorkflowController.cs#L281-L293) rechnet Flächen über `scaler.RestoreArea` mit $(1 / 50.0)^2 = 1 / 2500$ zurück $\Rightarrow$ Die Referenzfläche `FrontalArea` weicht um den Faktor **2500** ab!

### Workaround im Projekt
In `src/projects/TestprojektNACA/simulation.json` wurde `"UseRubberBandScaler": false` gesetzt, wodurch der neutrale Scaler (`ShrinkFactor = 1.0`) greift, da die Sehne von $50\text{ mm}$ ohnehin der PicoGK-Zielgröße entspricht.

### Lösungsvorschlag für `Core`
Wenn keine dimensionalen Parameter vorliegen, darf der Scaler nicht auf `1.0f` als Nennmaß zurückfallen, sondern muss neutral bleiben:
```csharp
if (maxDimension <= 0f)
{
    ShrinkFactor = 1.0f;
}
else
{
    ShrinkFactor = targetSize / maxDimension;
}
```

---

## 2. `ChampionValidator`: Additiver absoluter Jitter ohne Parametergrenzen-Prüfung

### Fundstelle
[`src/Automatisierung_v2/Core/Pipeline/ChampionValidator.cs#L96-L104`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Core/Pipeline/ChampionValidator.cs#L96-L104)

### Analyse
Die Jitter-Methode verstellt einen zufälligen Parameter rein additiv:
```csharp
string keyToJitter = testParams.Keys.ElementAt(_random.Next(testParams.Count));
testParams[keyToJitter] += _random.NextDouble() > 0.5 ? jitter : -jitter;
```
Mit `BouncerJitter = 0.01` aus `simulation.json`:
1. **Größenordnungsfehler bei normierten Werten:**
   Wird ein Parameter als relatives Verhältnis formuliert (z. B. maximale Wölbung $m = 0.02$ für NACA 2412), bedeutet ein absoluter Jitter von $\pm 0.01$ eine **50%ige Änderung** der Wölbung! Das Modell weicht in der Fitness massiv ab und wird vom Bouncer fälschlicherweise als „instabil“ disqualifiziert.
2. **Verletzung der harten Parametergrenzen (`ParameterBounds`):**
   Startet ein Parameter an der Untergrenze (z. B. `MaxCamber = 0.0` für symmetrisches NACA 0012), kann `testParams[keyToJitter]` auf `-0.01` fallen. Es findet kein Clamping gegen `ProjectConfig.ParameterBounds` statt $\Rightarrow$ Es wird eine unzulässige Variante berechnet.

### Workaround im Projekt
Die NACA-Parameter wurden in `project.json` bewusst in **Prozent** skaliert (`0.0` %, `40.0` %, `12.0` %), sodass $\pm 0.01$ einer winzigen Variation von $0.01\text{ Prozentpunkten}$ entspricht.

### Lösungsvorschlag für `Core`
1. Den Jitter relativ zur Spanne des Parameters berechnen:
   $$\Delta p = \text{jitter} \times (p_{\max} - p_{\min})$$
2. Den verjitterten Wert strikt auf `[Min, Max]` clampen:
   ```csharp
   if (config.Project.ParameterBounds.TryGetValue(keyToJitter, out var bounds))
   {
       float range = bounds.Max - bounds.Min;
       float delta = (float)(_random.NextDouble() > 0.5 ? jitter : -jitter) * range;
       testParams[keyToJitter] = Math.Clamp(testParams[keyToJitter] + delta, bounds.Min, bounds.Max);
   }
   ```

---

## 3. Konsolenausgabe schneidet aktive Parameter bei `:F2` ab

### Fundstelle
[`src/Automatisierung_v2/Core/Pipeline/WorkflowController.cs#L299`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Core/Pipeline/WorkflowController.cs#L299)

### Analyse
```csharp
foreach (var kvp in parameters) Console.Write($"[{kvp.Key}: {kvp.Value:F2}]  ");
```
Parameter werden in der Konsole starr mit zwei Nachkommastellen ausgegeben. Bei Parametern mit kleinen Wertebereichen (z. B. Winkel in Rad, $0.005$ bis $0.050$) sieht der Nutzer im Live-Log nur `[Param: 0.01]  [Param: 0.01]`, obwohl feine Mutationen stattfinden.

### Lösungsvorschlag für `Core`
Formatierung dynamisch an den Wert anpassen (z. B. `:F4` bei Werten $< 1.0$ oder generisches `G4`).

---

## 4. `Su2ConfigGenerator`: Fester Zustromvektor, kein Anstellwinkel (`AOA`)

### Fundstelle
[`src/Automatisierung_v2/Solvers/Cfd/Su2ConfigGenerator.cs#L57`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Solvers/Cfd/Su2ConfigGenerator.cs#L57)

### Analyse
`Su2ConfigGenerator` generiert die Randbedingung starr als:
```text
FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )
```
* Die Strömung trifft immer exakt parallel zur $X$-Achse ein.
* Ein symmetrisches Tragflügelprofil (NACA 0012) erzeugt bei $0^\circ$ Anströmung **Auftrieb $C_L = 0$**.
* Für eine Tragflügeloptimierung nach Gleitzahl $E = C_L / C_D$ muss der Flügel entweder mechanisch im CAD angestellt werden oder SU2 muss mit Anstellwinkel rechnen.

### Workaround im Projekt
In `TestprojektNACAGeometryGenerator.cs` wurde die Geometrie analytisch um den Viertelpunkt $0.25c$ um $\alpha = 5.0^\circ$ rotiert, bevor sie ins Voxelgitter überführt wird.

### Lösungsvorschlag für `Core/Solvers`
In `Su2SolverOptions` und `Su2ConfigGenerator` Optionen für `AngleOfAttackDeg` und `SideSlipAngleDeg` vorsehen:
```csharp
float alphaRad = options.AngleOfAttackDeg * (MathF.PI / 180.0f);
float vx = velocity * MathF.Cos(alphaRad);
float vz = velocity * MathF.Sin(alphaRad);
// FREESTREAM_VELOCITY= ( vx, 0.0, vz )
```
Oder direkt SU2s `AOA= ...` Parameter in der Konfiguration ansteuern.

---

## 5. Bezugsfläche für aerodynamische Beiwerte (`ReferenceAreaMetric`)

### Fundstelle
[`src/Automatisierung_v2/Solvers/Cfd/Su2SolverOptions.cs#L36`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Solvers/Cfd/Su2SolverOptions.cs#L36)

### Analyse
Standardmäßig nutzt das Framework `ReferenceAreaMetric = "FrontalArea"` (Stirnfläche quer zur Anströmung in der $Y$-$Z$-Ebene).
* Bei AUVs und Fahrzeugen ist die Stirnfläche physikalisch sinnvoll.
* Bei Tragflügeln beziehen sich Literaturwerte von $C_L$ und $C_D$ immer auf die **Flügelgrundrissfläche** ($S = b \cdot c$).
* **Mathematischer Effekt auf die Fitness:** Da SU2 sowohl $C_L$ als auch $C_D$ über dieselbe Referenzfläche normiert ($C_L = \frac{L}{q \cdot S}$, $C_D = \frac{D}{q \cdot S}$), kürzt sich $S$ in der Gleitzahl $E = C_L / C_D$ vollständig heraus. Die Optimierung funktioniert daher mathematisch einwandfrei. Die Einzelbeiwerte in `Simulation_Results.csv` weichen jedoch von Literatur-Tragflügelwerten ab.

### Empfehlung
Für künftige Aerodynamik-Projekte sollte im Generator optional eine Metrik `PlanformArea` gemeldet und in `su2.json` als `ReferenceAreaMetric` gewählt werden können.

---

## 6. Voxel-Auflösung & dünne Hinterkanten bei PicoGK

### Fundstelle
[`src/projects/TestprojektNACA/TestprojektNACAGeometryGenerator.cs`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/projects/TestprojektNACA/TestprojektNACAGeometryGenerator.cs)

### Analyse
Die analytische NACA-Formel läuft an der Hinterkante ($\xi = 1$) auf eine Dicke nahe 0 zu.
* Bei einer Voxelauflösung von $0.5\text{ mm}$ (`BaseVoxelResolution`) führt ein Radius unter $\sim 0.6\text{ mm}$ dazu, dass Voxel an der Hinterkante abreißen und isolierte Gitterinseln bilden.
* Nicht-zusammenhängende Dreiecke führen in Gmsh zu `Surface Loop`-Fehlern beim Bilden des 3D-Volumens.

### Lösung im Projekt
Im Generator wurde ein Mindestradius von $\max(y_t, 1.2 \times \text{VoxelSize})$ erzwungen sowie fünf sehnenseitige Verbindungsstege (`AddBeam`) entlang der Spannweite gelegt, gefolgt von einem organischen Verschmelzen (`voxModel.Offset(0.4f); voxModel.Offset(-0.4f);`). Dies sollte als Best Practice in `src/projects/README.md` dokumentiert werden.

---

## 7. Programmpfade, Server-Eigenheiten & Notwendigkeit einer Fallback-Auflösung

### Fundstelle
[`src/projects/MantaAuv/simulation.json#L40-L42`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/projects/MantaAuv/simulation.json#L40-L42) sowie [`src/Automatisierung_v2/Core/Models/SimulationConfig.cs`](file:///c:/Users/leona/OneDrive/Dokumente/PicoGK/AutomatisierungCleanVersion/src/Automatisierung_v2/Core/Models/SimulationConfig.cs)

### Realer Server-Kontext & Ursache für harte Pfade
Auf typischen Hochschul- oder HPC-Clustern (wie `BIC_12`) ohne Sudo-/Root-Rechte existieren mehrere systemspezifische Hürden:
1. **Nicht-interaktive SSH-Aufrufe (`ssh host befehl`):**
   Ubuntus standardmäßige `~/.bashrc` steigt bei nicht-interaktiven Sitzungen in den ersten Zeilen sofort aus (`[ -z "$PS1" ] && return`). Dadurch greifen weder `conda init`, noch manuell dort exportierte `PATH`-Erweiterungen. Ein reiner Aufruf über den Programmnamen (`"SU2_CFD"`, `"gmsh"`, `"mpirun"`) schlägt fehl, da das System die Binaries nicht findet.
2. **Manuelle Builds & Conda-Abhängigkeiten:**
   SU2 (insbesondere mit OpenMPI) oder Gmsh müssen häufig vom Nutzer in `~/miniconda` oder `~/software/` kompiliert bzw. abgelegt werden. Werden sie global über unpassende System-Bibliotheken gestartet, drohen Fehler wie `symbol lookup error` bei `mpirun`.
3. **Konsequenz bisher:**
   Um den Lauf stabil zu halten, wurden in `simulation.json` harte Absolutpfade eingetragen:
   ```json
   "MpiRunPath": "/home/lpleissner/miniconda/bin/mpirun",
   "Su2Path": "/home/lpleissner/software/bin/SU2_CFD",
   "GmshPath": "/home/lpleissner/software/gmsh-4.11.1-Linux64/bin/gmsh"
   ```

### Das Problem der aktuellen Architektur
Die aktuelle Implementierung erzwingt ein starres **Entweder-oder**:
* **Nur relativer Name (`"SU2_CFD"`):** Funktioniert lokal oder auf perfekt vorkonfigurierten Maschinen, bricht aber auf Server-Umgebungen sofort ab, wenn `PATH` in Non-Interactive SSH nicht greift.
* **Harter Absolutpfad (`"/home/lpleissner/..."`):** Funktioniert zuverlässig auf genau einer Maschine für genau einen Benutzer-Account, bricht aber sofort, sobald ein anderer Nutzer das Repo klont, das Projekt auf einem anderen Server gerechnet wird oder lokal unter Windows ohne Pfadanpassung getestet werden soll.

### Empfohlene Lösungsarchitektur: Zweistufige Resolver-Kette mit Fallbacks

Das Framework (`Core`) sollte eine intelligente Pfad-Auflösung mit automatischer Fallback-Kette implementieren (z. B. `ExecutableResolver.Resolve` vor Start der Prozesse in `Su2Solver` und `GmshCfdMesher`):

#### 1. Prüfreihenfolge (Resolution Pipeline)
Für jeden externen Prozess (`Su2Path`, `GmshPath`, `MpiRunPath`):
1. **Stufe 1 — Konfigurierter Pfad / PATH-Check:**
   * Ist der konfigurierte Pfad absolut und existiert die Datei (`File.Exists`), nimm diesen.
   * Ist der Name relativ/ein bloßer Befehl (`"SU2_CFD"`): Prüfe, ob er über das aktuelle System-`PATH` auflösbar und ausführbar ist.
2. **Stufe 2 — Automatische Server-/Conda-Fallback-Kandidaten:**
   Findet Stufe 1 nichts, durchsucht der Resolver eine Liste vordefinierter Standardpfade im Benutzerverzeichnis (`$HOME` / `~`):
   * Für `SU2_CFD`:
     * `~/software/bin/SU2_CFD`
     * `~/software/SU2-*/bin/SU2_CFD`
     * `~/miniconda/bin/SU2_CFD`
     * `$CONDA_PREFIX/bin/SU2_CFD`
   * Für `gmsh`:
     * `~/software/gmsh-*/bin/gmsh`
     * `~/software/bin/gmsh`
     * `~/miniconda/bin/gmsh`
   * Für `mpirun`:
     * `~/miniconda/bin/mpirun`
     * `~/software/bin/mpirun`
3. **Stufe 3 — Konfigurierbare Fallback-Pfade in `simulation.json`:**
   Ermögliche in der Konfiguration die Angabe von Fallback-Listen oder Tilde-Pfade (`~`), die plattformunabhängig expandiert werden:
   ```jsonc
   "Su2Path": "SU2_CFD",
   "Su2FallbackPaths": [
     "~/software/bin/SU2_CFD",
     "/home/lpleissner/software/bin/SU2_CFD"
   ]
   ```

#### 2. Transparente Rückmeldung im Log
Schlägt die primäre Suche fehl und ein Fallback-Pfad springt ein, meldet das Framework dies beim Start klar im Konsolenlog:
```text
[RESOLVER] 'SU2_CFD' wurde nicht im PATH gefunden.
           -> Verwende automatischen Fallback: /home/lpleissner/software/bin/SU2_CFD
```
Dadurch bleibt das Projekt portabel, läuft lokal und auf fremden Maschinen ohne manuelle Pfadanpassung, fängt aber Server-Eigenheiten (Headless-Umgebungen, Conda-Installationen) vollautomatisch und robust ab.

