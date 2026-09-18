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
                string projectName = args.Length > 0 ? args[0] : "MantaAuv";
                Console.WriteLine($"[SYSTEM] Starte Projekt: {projectName}");

                // 2. Projekt-spezifische Konfiguration laden
                ProjectConfig projectConfig;
                IGeometryGenerator geometry;
                IFitnessCalculator fitness;
                IModelValidator validator;

                switch (projectName)
                {
                    case "MantaAuv":
                        projectConfig = MantaProjectConfig.Create();
                        geometry = new MantaGeometryGenerator();
                        fitness = new MantaFitnessCalculator(projectConfig);
                        validator = new MantaModelValidator(projectConfig);
                        break;
                    default:
                        throw new ArgumentException($"Unbekanntes Projekt: {projectName}");
                }

                // 3. Framework-Kern (identisch für alle Projekte)
                var config = SimulationConfig.CreateDefault();
                var context = new SimulationContext(config, projectConfig);
                context.EnsureDirectories(); // Erstellt Ergebnisse-Ordner etc.

                var mesher = new GmshCfdMesher();
                var solver = new Su2Solver();
                
                // Optimierungsalgorithmus wählen (Rsm oder Evolution)
                IOptimizationAlgorithm optimizer = new RsmOptimizationAlgorithm(fitness);
                // Alternativ: IOptimizationAlgorithm optimizer = new EvolutionaryAlgorithm(fitness);

                // 4. Workflow-Controller mit injizierten Abhängigkeiten erstellen
                var controller = new WorkflowController(
                    geometry, 
                    mesher, 
                    new ISimulationSolver[] { solver }, 
                    fitness, 
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