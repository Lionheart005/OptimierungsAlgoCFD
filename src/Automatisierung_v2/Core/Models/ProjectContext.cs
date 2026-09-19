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
        /// Lädt die Optionen eines Solvers oder Vernetzers aus
        /// <c>&lt;Projektordner&gt;/solvers/&lt;name&gt;.json</c>, z.B. <c>"su2"</c> oder
        /// <c>"gmsh"</c> — über alle vier Schichten aus TODO-22.
        ///
        /// <para>
        /// Ohne Projektordner gelten die Code-Standards. Im regulären Lauf kommt das nicht vor
        /// (<c>Program.cs</c> bricht vorher ab); es ist der Fall für Tests, die eine
        /// Solver-Kette bauen, ohne ein Repo auf der Platte zu haben.
        /// </para>
        /// </summary>
        public T LoadSolverOptions<T>(string name) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(ProjectDirectory))
            {
                Console.WriteLine(
                    $"[CONFIG] Kein Projektordner — '{name}' nutzt die Code-Standards.");
                return new T();
            }

            var result = JsonConfigLoader.LoadForProject(
                new T(), $"solvers/{name}.json", ProjectDirectory!, ConfigDirectory);

            result.Report.PrintMachineOverrideWarning(Name);
            Reports.Add(result.Report);

            return result.Value;
        }
    }
}
