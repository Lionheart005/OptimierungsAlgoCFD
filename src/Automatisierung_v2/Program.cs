using System;
using PicoGK;

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
                IFitnessCalculator fitness;

                switch (projectName)
                {
                    case "MantaAuv":
                        projectConfig = MantaProjectConfig.Create();
                        geometry = new MantaGeometryGenerator();
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

                var cfdMesher = new GmshCfdMesher();
                var stages = new[]
                {
                    new SolverStage(cfdMesher, new Su2Solver())
                };

                // Optimierungsalgorithmus wählen (Rsm oder Evolution)
                IOptimizationAlgorithm optimizer = new RsmOptimizationAlgorithm(fitness);
                // Alternativ: IOptimizationAlgorithm optimizer = new EvolutionaryAlgorithm(fitness);

                // 4. Workflow-Controller mit injizierten Abhängigkeiten erstellen
                var controller = new WorkflowController(
                    geometry,
                    stages,
                    validator,
                    optimizer, 
                    context
                );
                
                // 5. PicoGK Framework starten
                Console.WriteLine("==================================================");
                Console.WriteLine("  PicoGK AUTOMATED CFD OPTIMIZATION FRAMEWORK");
                Console.WriteLine("==================================================");

                Library.Go(config.BaseVoxelResolution, controller.RunOptimization);
            }
            catch (Exception e)
            {
                Console.WriteLine($"\n[KRITISCHER FEHLER] Programmabbruch:\n{e}");
            }
        }
    }
}