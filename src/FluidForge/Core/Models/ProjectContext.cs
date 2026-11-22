using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Everything a project needs to wire itself up: its name, directory, loaded configuration,
    /// and a helper to load solver options without knowing where config files reside.
    /// </summary>
    public sealed class ProjectContext
    {
        /// <param name="name">Project name (= folder name).</param>
        /// <param name="projectDirectory">
        /// Project folder under <c>src/projects/</c>, or <c>null</c> in standalone unit tests
        /// (see <see cref="LoadSolverOptions{T}"/>).
        /// </param>
        /// <param name="configDirectory">The <c>config/</c> directory for machine-specific overrides.</param>
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

        /// <summary>Project name as passed at startup.</summary>
        public string Name { get; }

        /// <summary>Project directory, or <c>null</c> if none exists.</summary>
        public string? ProjectDirectory { get; }

        /// <summary>Config directory for machine-specific layer 3 overrides.</summary>
        public string? ConfigDirectory { get; }

        /// <summary>Framework configuration, loaded across all layers.</summary>
        public SimulationConfig Simulation { get; }

        /// <summary>Project configuration, loaded across all layers.</summary>
        public ProjectConfig Project { get; }

        /// <summary>
        /// All configuration files loaded for this run, with their effective values and key origins.
        /// Written to <c>effective-config.json</c> by <see cref="EffectiveConfigWriter"/>.
        /// </summary>
        public List<LoadedConfiguration> Loaded { get; } = new();

        /// <summary>Records a loaded configuration file.</summary>
        public void Record<T>(string name, ConfigLoadResult<T> result) where T : class
            => Loaded.Add(new LoadedConfiguration(name, result.Value, result.Report));

        /// <summary>
        /// Loads solver or mesher options from <c>&lt;ProjectDirectory&gt;/solvers/&lt;name&gt;.json</c>
        /// (e.g., <c>"su2"</c> or <c>"gmsh"</c>) across all four configuration layers.
        /// Falls back to code defaults if <see cref="ProjectDirectory"/> is null.
        /// </summary>
        public T LoadSolverOptions<T>(string name) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(ProjectDirectory))
            {
                Console.WriteLine(
                    $"{OutputConventions.ConfigTag} No project directory — '{name}' uses code defaults.");
                return new T();
            }

            string relativeName = $"solvers/{name}.json";

            var result = JsonConfigLoader.LoadForProject(
                new T(), relativeName, ProjectDirectory!, ConfigDirectory);

            result.Report.PrintMachineOverrideWarning(Name);
            Record(relativeName, result);

            return result.Value;
        }
    }
}
