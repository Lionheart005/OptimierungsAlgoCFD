using System;
using System.Collections.Generic;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MeinProjekt
{
    /// <summary>
    /// Die Bewertung eines Projekts (TODO-27). Diese Vorlage minimiert <b>nur den Widerstand</b>:
    ///
    /// <code>Fitness = 1 / Drag</code>
    ///
    /// <para>
    /// <b>Das Framework maximiert immer die Fitness.</b> Eine Größe, die klein werden soll, gehört
    /// deshalb in den Nenner; eine, die groß werden soll, in den Zähler. MantaAuv rechnet
    /// <c>(Volume × SensorDistance) / Drag^DragBalanceFactor</c> — Nutzraum und Sensorbasis nach
    /// oben, Widerstand nach unten, mit einem einstellbaren Gewicht auf dem Widerstand.
    /// </para>
    ///
    /// <para>
    /// <b>Hier arbeitest du.</b> Die Formel ist C#-Code und bleibt es (Entscheidung 7): Zahlen
    /// gehören in JSON, Formeln in Code. Die Zahlen, mit denen die Formel rechnet, kommen aus
    /// <c>OptimizationTargets</c> in der project.json.
    /// </para>
    /// </summary>
    public sealed class MeinProjektFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fitness eines Modells, das nicht bewertbar ist — abgebrochene Simulation, fehlender
        /// Widerstand. Nicht 0, damit ein solcher Lauf in der Auswertung nicht wie ein
        /// gültiges Ergebnis mit dem Wert 0 aussieht; klein genug, dass ihn kein Algorithmus
        /// je als Vorbild nimmt.
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Ziele aus <c>OptimizationTargets</c>, ohne die sich die Formel nicht rechnen lässt.
        ///
        /// <para>
        /// <b>Leer, weil diese Formel nur den Widerstand braucht.</b> Trägst du hier einen Namen
        /// ein, prüft der Konstruktor ihn beim Verdrahten in <c>Program.cs</c> — der Lauf bricht
        /// dann in der ersten Sekunde ab und nicht nach Stunden Rechenzeit mit einer nackten
        /// <see cref="KeyNotFoundException"/> (TODO-16). Genau dafür ist diese Liste da; sie ist
        /// eine Zeile Arbeit und spart einen Abend.
        /// </para>
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metriken aus <c>PassiveParameters</c>, ohne die die Formel nicht rechnet.
        ///
        /// <para>
        /// <c>Drag</c> kommt aus der SU2-<c>history.csv</c> — die Zuordnung steht in
        /// <c>solvers/su2.json</c> unter <c>ResultMetrics</c>. Geometrie-Größen wie
        /// <c>Volume</c> oder <c>SurfaceArea</c> meldet der Generator nebenan über
        /// <c>AddMetric</c>; benutzt die Formel eine davon, gehört ihr Name zusätzlich hier hinein.
        /// </para>
        ///
        /// <para>
        /// Der <see cref="WorkflowController"/> prüft diese Liste nach der ersten gerechneten
        /// Variante und bricht dort ab (TODO-26). Früher geht es nicht: Metriken entstehen erst
        /// mit der ersten Geometrie und dem ersten Solver-Lauf.
        /// </para>
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag" };

        /// <summary>
        /// Prüft die Pflicht-Ziele sofort und übernimmt sie in <c>readonly</c>-Felder. Das Muster
        /// mit vollständiger Fehlermeldung — alle fehlenden Namen auf einmal, die vorhandenen dazu,
        /// und die Datei, in die sie gehören — steht ausgeschrieben in
        /// <c>src/projects/MantaAuv/MantaFitnessCalculator.cs</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Ein Pflicht-Ziel fehlt in der Konfiguration.</exception>
        public MeinProjektFitnessCalculator(ProjectConfig project)
        {
            var missing = RequiredTargets
                .Where(key => !project.OptimizationTargets.ContainsKey(key))
                .ToList();

            if (missing.Count > 0)
            {
                string present = project.OptimizationTargets.Count > 0
                    ? string.Join(", ", project.OptimizationTargets.Keys)
                    : "(keine)";

                throw new InvalidOperationException(
                    $"[KONFIGURATION] Projekt '{project.ProjectName}': in OptimizationTargets "
                    + $"fehlt/fehlen {string.Join(", ", missing)}. Vorhanden ist: {present}. "
                    + $"Nachtragen in src/projects/{project.ProjectName}/project.json.");
            }

            // Hier die geprüften Ziele in readonly-Felder übernehmen, z.B.:
            //   _minimumAllowedVolume = project.OptimizationTargets["MinimumAllowedVolume"];
            // Dann steht in CalculateFitness kein Dictionary-Zugriff mehr, der schiefgehen kann.
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            // Abgebrochene Vernetzung oder Simulation: der Kern meldet das generisch über dieses
            // Flag (TODO-2). Diese Prüfung muss als ERSTE stehen — die PassiveParameters eines
            // fehlgeschlagenen Modells sind unvollständig.
            if (record.SimulationFailed)
            {
                record.Fitness = FailedFitness;
                return;
            }

            float drag = record.PassiveParameters.TryGetValue("Drag", out float value)
                ? value
                : float.NaN;

            // Kein oder kein sinnvoller Widerstand: nicht bewertbar. Der Controller hat die
            // Pflicht-Metriken oben schon nach der ersten Variante geprüft, aber ein einzelner
            // Ausfall mitten im Lauf darf ihn nicht kosten.
            if (float.IsNaN(drag) || float.IsInfinity(drag))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Der Widerstand steht als Beiwert im Record und kann je nach Anströmung auch negativ
            // herauskommen; bewertet wird der Betrag. Die Untergrenze verhindert die Division
            // durch (fast) Null — ein einziger Ausreißer mit Drag ≈ 0 hätte sonst eine Fitness von
            // mehreren Millionen und würde den Rest des Laufs dominieren.
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            record.Fitness = 1.0f / safeDrag;

            // Ab hier kommen die eigenen Ziele hinein. Ein Volumenfenster sieht z.B. so aus:
            //
            //   float volume = record.PassiveParameters["Volume"];
            //   if (volume < _minimumAllowedVolume || volume > _maximumAllowedVolume)
            //       record.Fitness *= 0.1f;
            //
            // Eine Strafe statt einer 0: die Form bleibt vergleichbar, und der Algorithmus sieht,
            // in welche Richtung es besser wird. Eine harte 0 macht alle unzulässigen Varianten
            // gleich schlecht — daraus lernt er nichts.
        }
    }
}
