using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MyPicoGkProject
{
    public class WorkflowController
    {
        private readonly SimulationData _data;
        private readonly IOptimizationAlgorithm _optimizer;
        private readonly PicoGkGenerator _generator;
        private readonly GmshConverter _converter;
        private readonly FluidDynamicsAnalyzer _analyzer;

        public WorkflowController(SimulationData data, IOptimizationAlgorithm optimizer)
        {
            _data = data;
            _optimizer = optimizer;
            _generator = new PicoGkGenerator();
            _converter = new GmshConverter();
            _analyzer = new FluidDynamicsAnalyzer();
        }

        public void RunOptimization()
        {
            Console.WriteLine($"[SYSTEM] Nutze Optimierungsalgorithmus: {_optimizer.Name}");
            int totalVariants = _data.Settings.MaxIterations * _data.Settings.VariantsPerIteration;
            int completedVariants = 0;
            double totalSecondsPassed = 0;
            
            ModelRecord? previousWinner = null;
            string staticTunnelPath = _generator.GenerateStaticTunnel(_data.BaseDirectory);

            // =========================================================
            // ÄUẞERE SCHLEIFE: ITERATIONEN (z.B. Generationen)
            // =========================================================
            for (int iter = 1; iter <= _data.Settings.MaxIterations; iter++)
            {
                Console.WriteLine("\n==================================================");
                Console.WriteLine($" ITERATION {iter} VON {_data.Settings.MaxIterations}");
                Console.WriteLine("==================================================");
                
                List<ModelRecord> currentIterationRecords = new List<ModelRecord>();

                // =========================================================
                // INNERE SCHLEIFE: VARIANTEN (Modelle)
                // =========================================================
                for (int var = 1; var <= _data.Settings.VariantsPerIteration; var++)
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    
                    // 1. Parameter vom Algorithmus generieren lassen
                    var activeParams = _optimizer.GenerateParameters(_data, iter, var, _data.Settings.VariantsPerIteration);

                    PrintVariantHeader(iter, var, activeParams);

                    // 2. Skalierung (Gummiband)
                    RubberBandScaler scaler = new RubberBandScaler(activeParams, _data.Settings.TargetPicoGkSize);

                    // 3. Datensatz vorbereiten
                    ModelRecord record = new ModelRecord
                    {
                        Iteration = iter,
                        Variant = var,
                        ActiveParameters = activeParams
                    };

                    // 4. Geometrie generieren
                    // 4. Geometrie generieren
                    var result = _generator.GenerateAndExport(iter, var, scaler.ShrunkParameters, _data, false); 
                    record.StlPath = result.StlPath;
                    record.PassiveParameters["Volume"] = scaler.RestoreVolume(result.Volume);
                    record.PassiveParameters["FrontalArea"] = scaler.RestoreArea(result.FrontalArea);
                    record.PassiveParameters["SensorDistance"] = scaler.RestoreArea(result.SensorDistance); // NEU
                    record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;
                    
                    Console.WriteLine($"       -> Voxel-Geometrie erstellt. (Echtes Volumen: {record.PassiveParameters["Volume"]:F1} mm³)");
                    
                    // 5. Meshing & CFD Analyse
                    try
                    {
                        record.MeshPath = _converter.ConvertStlToMesh(result.StlPath, staticTunnelPath, iter, var, _data, scaler.ShrinkFactor);
                        record.PassiveParameters["Drag"] = _analyzer.AnalyzeMesh(record.MeshPath, activeParams, _data, iter, var, record.PassiveParameters["FrontalArea"]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"       -> [FEHLER] Simulation abgebrochen: {ex.Message}");
                        record.PassiveParameters["Drag"] = float.MaxValue;
                    }

                    // 6. Datensatz speichern
                    _data.History.Add(record);
                    currentIterationRecords.Add(record);
                    _data.ExportToCsv(); 

                    UpdateTimer(timer, ref completedVariants, ref totalSecondsPassed, totalVariants);
                } 

                // =========================================================
                // AUSWERTUNG & VALIDIERUNG
                // =========================================================
                // 1. Der Algorithmus bewertet die Modelle und wählt den Gewinner
                _optimizer.EvaluateAndSelectBest(currentIterationRecords, _data);
                
                // 2. Den Türsteher (Validation) anwenden
                float tolerance = _data.Settings.OptimizationTargets["BouncerTolerance"];
                ModelRecord trueWinner = ValidateChampion(currentIterationRecords, staticTunnelPath, iter, tolerance);
                previousWinner = trueWinner;
                
                _data.ExportToCsv(); 
                
                Console.WriteLine($"\n[ERGEBNIS] Iteration {iter} abgeschlossen.");
                Console.WriteLine($"           Champion ist Variante {trueWinner.Variant} (Score: {trueWinner.Fitness:F3})");
                Console.WriteLine($"           Er hat die folgenden Parameter: ");


                if (iter < _data.Settings.MaxIterations)
                {
                    // 3. Den Algorithmus auf die nächste Runde vorbereiten
                    _optimizer.PrepareNextIteration(_data, currentIterationRecords, trueWinner);
                }
            }

            FinishOptimization(previousWinner);
        }

        private void PrintVariantHeader(int iter, int variant, Dictionary<string, float> parameters)
        {
            Console.WriteLine($"\n--- Variante {variant} ---");
            Console.Write("       -> Gene: ");
            foreach (var kvp in parameters) Console.Write($"[{kvp.Key}: {kvp.Value:F2}]  ");
            Console.WriteLine();
        }

        private void UpdateTimer(Stopwatch timer, ref int completed, ref double totalSeconds, int totalVariants)
        {
            timer.Stop();
            completed++;
            totalSeconds += timer.Elapsed.TotalSeconds;
            
            double avgSeconds = totalSeconds / completed;
            int variantsLeft = totalVariants - completed;
            TimeSpan timeLeft = TimeSpan.FromSeconds(variantsLeft * avgSeconds);
            
            Console.WriteLine($"       -> Dauer: {timer.Elapsed.TotalSeconds:F1}s (Geschätzte Restzeit gesamt: {timeLeft:hh\\:mm\\:ss})");
        }

        private ModelRecord ValidateChampion(List<ModelRecord> generationRecords, string staticTunnelPath, int iter, float tolerance)
        {
            Console.WriteLine("\n[VALIDIERUNG] Teste Stabilität des Champions...");
            var sortedCandidates = generationRecords.OrderByDescending(r => r.Fitness).ToList();
            Random rnd = new Random();

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.Fitness < 0.1f) break;

                float originalDrag = candidate.PassiveParameters["Drag"];
                Console.WriteLine($"       -> Prüfe Variante {candidate.Variant} (Referenz-Drag: {originalDrag:F4})");

                var testParams = new Dictionary<string, float>(candidate.ActiveParameters);
                string keyToJitter = testParams.Keys.ElementAt(rnd.Next(testParams.Count));
                float jitter = rnd.NextDouble() > 0.5 ? 0.01f : -0.01f;
                testParams[keyToJitter] += jitter;

                RubberBandScaler scaler = new RubberBandScaler(testParams, _data.Settings.TargetPicoGkSize);
                
                try
                {
                    var result = _generator.GenerateAndExport(iter, 99, scaler.ShrunkParameters, _data, false);
                   float realArea = scaler.RestoreArea(result.FrontalArea);
                    string meshPath = _converter.ConvertStlToMesh(result.StlPath, staticTunnelPath, iter, 99, _data, scaler.ShrinkFactor);
                    float validatedDrag = _analyzer.AnalyzeMesh(meshPath, testParams, _data, iter, 99, realArea);

                    float deviation = Math.Abs(validatedDrag - originalDrag) / Math.Abs(originalDrag);

                    Console.WriteLine($"       -> Validierungs-Drag: {validatedDrag:F4} (Abweichung: {deviation * 100:F1}%)");

                    if (deviation <= tolerance)
                    {
                        Console.WriteLine("       -> [OK] Physikalisch stabil. Champion bestätigt.");
                        return candidate; 
                    }
                    else
                    {
                        Console.WriteLine("       -> [ABGELEHNT] Modell ist instabil. Disqualifiziert.");
                        candidate.Fitness = 0.0001f; 
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("       -> [FEHLER] Validierung fehlgeschlagen. Disqualifiziert.");
                    candidate.Fitness = 0.0001f;
                }
            }

            Console.WriteLine("       -> [WARNUNG] Kein Modell stabil. Wähle das Beste der Reste.");
            return sortedCandidates[0]; 
        }

        private void FinishOptimization(ModelRecord? champion)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine(" OPTIMIERUNG ERFOLGREICH BEENDET");
            Console.WriteLine("==================================================");

            if (champion != null)
            {
                Console.WriteLine($"\n🏆 ABSOLUTER CHAMPION: Iteration {champion.Iteration} | Variante {champion.Variant}");
                Console.WriteLine($"   Score: {champion.Fitness:F2} | Drag: {champion.PassiveParameters["Drag"]:F4}");
                Console.WriteLine($"\n📂 ParaView-Dateien:");
                Console.WriteLine($"   Oberfläche: Ergebnisse/Analyseergebnisse/Surface_Gen{champion.Iteration}_Var{champion.Variant}.vtu");
                Console.WriteLine($"   Volumen:    Ergebnisse/Analyseergebnisse/Volume_Gen{champion.Iteration}_Var{champion.Variant}.vtu\n");
            }
        }
    }
}