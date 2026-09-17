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
                // 1. Zentrale Datenbank und Konfiguration laden
                SimulationData data = new SimulationData();

                // 2. Optimierungsalgorithmus wählen (Hier: Evolution oder Rsm)
                // Durch das neue Interface (IOptimizationAlgorithm) können hier später 
                // ganz einfach andere Algorithmen (z.B. Gradient Descent) eingesteckt werden.
                IOptimizationAlgorithm optimizer = new RsmOptimizationAlgorithm();

                // 3. Workflow-Dirigenten mit dem gewählten Algorithmus initialisieren
                WorkflowController controller = new WorkflowController(data, optimizer);
                
                // 4. PicoGK Framework starten
                Console.WriteLine("==================================================");
                Console.WriteLine("  PicoGK AUTOMATED CFD OPTIMIZATION FRAMEWORK");
                Console.WriteLine("==================================================");

                
                Library.Go(data.Settings.BaseVoxelResolution, controller.RunOptimization);

            }
            catch (Exception e)
            {
                Console.WriteLine($"\n[KRITISCHER FEHLER] Programmabbruch:\n{e}");
            }
        }
    }
}