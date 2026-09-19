using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Was ein Projekt zum Verdrahten braucht (TODO-23): sein Name, sein Ordner, die fertig
    /// geladene Konfiguration — und ein Weg, die Optionen seiner Solver zu laden, ohne dass es
    /// wissen muss, wo die Dateien liegen.
    /// </summary>
    public sealed class ProjectContext
    {
        /// <param name="name">Der Projektname = der Ordnername.</param>
        /// <param name="projectDirectory">
        /// Der Ordner des Projekts unter <c>src/projects/</c>, oder <c>null</c>, solange es
        /// keinen gibt (siehe <see cref="LoadSolverOptions{T}"/>).
        /// </param>
        /// <param name="configDirectory">Das <c>config/</c>-Verzeichnis für die Notausgang-Schicht.</param>
        public ProjectContext(
            string name,
            string? projectDirectory,
            string? configDirectory,
            SimulationConfig simulation,
            ProjectConfig project)
        {
            Name = name;
            ProjectDirectory = projectDirectory;
            ConfigDirectory = configDirectory;
            Simulation = simulation;
            Project = project;
        }

        /// <summary>Der Projektname, wie er beim Start angegeben wurde.</summary>
        public string Name { get; }

        /// <summary>Der Projektordner, oder <c>null</c>, wenn es (noch) keinen gibt.</summary>
        public string? ProjectDirectory { get; }

        /// <summary>Das Konfigurationsverzeichnis für die maschinenspezifische Schicht 3.</summary>
        public string? ConfigDirectory { get; }

        /// <summary>Die Framework-Konfiguration, fertig über alle Schichten geladen.</summary>
        public SimulationConfig Simulation { get; }

        /// <summary>Die Projekt-Konfiguration, fertig über alle Schichten geladen.</summary>
        public ProjectConfig Project { get; }

        /// <summary>
        /// Die Berichte aller über diesen Kontext geladenen Dateien — welcher Schlüssel kam aus
        /// welcher Datei. TODO-25 schreibt sie als <c>effective-config.json</c> neben die
        /// Ergebnisse.
        /// </summary>
        public List<ConfigLoadReport> Reports { get; } = new();

        /// <summary>
        /// Lädt die Optionen eines Solvers oder Vernetzers, z.B. <c>"su2"</c> oder <c>"gmsh"</c>.
        ///
        /// <para>
        /// Liegt ein Projektordner vor, gilt der Vierschichtenweg aus TODO-22 mit
        /// <c>&lt;Projekt&gt;/solvers/&lt;name&gt;.json</c>. Gibt es ihn nicht, bleibt es beim
        /// bisherigen Weg über <c>config/solvers/</c> — diese Weiche fällt mit TODO-24 weg,
        /// wenn die Dateien umgezogen sind. Das Projekt merkt von beidem nichts.
        /// </para>
        /// </summary>
        public T LoadSolverOptions<T>(string name) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(ProjectDirectory))
                return JsonConfigLoader.LoadSolverOptions<T>(name, ConfigDirectory);

            var result = JsonConfigLoader.LoadForProject(
                new T(), $"solvers/{name}.json", ProjectDirectory!, ConfigDirectory);

            result.Report.PrintMachineOverrideWarning(Name);
            Reports.Add(result.Report);

            return result.Value;
        }
    }
}
