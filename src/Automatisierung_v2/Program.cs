using System;

namespace MyPicoGkProject
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                // 1. Projekt aus args oder Konvention auswählen
                //    Aufruf: <Projektname> [Konfigurationsverzeichnis]
                string projectName = args.Length > 0 ? args[0] : "MantaAuv";
                string? configDirectory = args.Length > 1 ? args[1] : null;
                Console.WriteLine($"[SYSTEM] Starte Projekt: {projectName}");

                // 2. Projekt-spezifische Konfiguration laden
                ProjectConfig projectConfig;
                IGeometryGenerator geometry;
                IGeometryKernel kernel;
                IFitnessCalculator fitness;

                switch (projectName)
                {
                    case "MantaAuv":
                        // Zahlen aus config/projects/MantaAuv.json, Code-Vorgaben als Fallback
                        projectConfig = JsonConfigLoader.LoadProjectConfig(
                            projectName, MantaProjectConfig.Create(), configDirectory);
                        geometry = new MantaGeometryGenerator();
                        // Die Manta-Geometrie entsteht aus PicoGK-Lattices, braucht also
                        // dessen Laufzeitumgebung. Ein Projekt ohne Voxel-Kernel wählt hier
                        // stattdessen den DirectGeometryKernel.
                        kernel = new PicoGkKernel();
                        fitness = new MantaFitnessCalculator(projectConfig);
                        break;
                    default:
                        throw new ArgumentException($"Unbekanntes Projekt: {projectName}");
                }

                // 3. Framework-Kern (identisch für alle Projekte)
                //    Zahlen und Pfade kommen aus config/simulation.json (+ simulation.local.json)
                var config = JsonConfigLoader.LoadSimulationConfig(configDirectory);
                var context = new SimulationContext(config, projectConfig);
                context.EnsureDirectories(); // Erstellt Ergebnisse-Ordner etc.

                // Simulationskette: jede Stufe bringt ihren eigenen Vernetzer mit.
                // Ein FEM-Solver bekäme hier eine eigene Mesher-Instanz, ein zweiter
                // CFD-Solver dieselbe — dann wird nur einmal vernetzt.
                // Der Bouncer ist Framework-Bestandteil und prüft die Fitness,
                // nicht eine projektspezifische Metrik.
                IModelValidator validator = new ChampionValidator(fitness);

                var cfdMesher = new GmshCfdMesher(
                    JsonConfigLoader.LoadSolverOptions<GmshMesherOptions>("gmsh", configDirectory));
                var su2Solver = new Su2Solver(
                    JsonConfigLoader.LoadSolverOptions<Su2SolverOptions>("su2", configDirectory));

                var stages = new[]
                {
                    new SolverStage(cfdMesher, su2Solver)
                };

                // Optimierungsalgorithmus: steht als "OptimizationAlgorithm" in
                // config/projects/<Projekt>.json ("Rsm" oder "Evolution").
                IOptimizationAlgorithm optimizer =
                    OptimizationAlgorithmFactory.Create(projectConfig.OptimizationAlgorithm, fitness);

                // 4. Workflow-Controller mit injizierten Abhängigkeiten erstellen
                var controller = new WorkflowController(
                    geometry,
                    stages,
                    validator,
                    optimizer, 
                    context
                );
                
                // 5. Geometrie-Kernel starten; der Optimierungslauf läuft in dessen
                //    Laufzeitumgebung. Program.cs kennt PicoGK dadurch nicht mehr.
                Console.WriteLine("==================================================");
                Console.WriteLine("  AUTOMATED CFD OPTIMIZATION FRAMEWORK");
                Console.WriteLine($"  Geometrie-Kernel: {kernel.Name}");
                Console.WriteLine("==================================================");

                kernel.RunHosted(config.BaseVoxelResolution, controller.RunOptimization);
            }
            catch (Exception e)
            {
                Console.WriteLine($"\n[KRITISCHER FEHLER] Programmabbruch:\n{e}");
            }
        }
    }
}