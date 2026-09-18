using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MyPicoGkProject
{
    /// <summary>
    /// Orchestriert den Optimierungs-Workflow.
    /// Arbeitet NUR noch mit Interfaces — keine konkreten Klassen mehr.
    /// </summary>
    public class WorkflowController
    {
        private readonly SimulationContext _context;
        private readonly IOptimizationAlgorithm _optimizer;
        private readonly IGeometryGenerator _geometry;
        private readonly IMeshGenerator _mesher;
        private readonly ISimulationSolver[] _solvers;
        private readonly IFitnessCalculator _fitness;
        private readonly IModelValidator _validator;

        public WorkflowController(
            IGeometryGenerator geometry,
            IMeshGenerator mesher,
            ISimulationSolver[] solvers,
            IFitnessCalculator fitness,
            IModelValidator validator,
            IOptimizationAlgorithm optimizer,
            SimulationContext context)
        {
            _geometry = geometry;
            _mesher = mesher;
            _solvers = solvers;
            _fitness = fitness;
            _validator = validator;
            _optimizer = optimizer;
            _context = context;
        }

        public void RunOptimization()
        {
            Console.WriteLine($"[SYSTEM] Nutze Optimierungsalgorithmus: {_optimizer.Name}");
            Console.WriteLine($"[SYSTEM] Projekt: {_context.Project.ProjectName}");
            Console.WriteLine($"[SYSTEM] Solver: {string.Join(", ", _solvers.Select(s => s.Name))}");

            int totalVariants = _context.Config.MaxIterations * _context.Config.VariantsPerIteration;
            int completedVariants = 0;
            double totalSecondsPassed = 0;
            
            ModelRecord? previousWinner = null;

            // =========================================================
            // ÄUẞERE SCHLEIFE: ITERATIONEN (z.B. Generationen)
            // =========================================================
            for (int iter = 1; iter <= _context.Config.MaxIterations; iter++)
            {
                Console.WriteLine("\n==================================================");
                Console.WriteLine($" ITERATION {iter} VON {_context.Config.MaxIterations}");
                Console.WriteLine("==================================================");
                
                List<ModelRecord> currentIterationRecords = new List<ModelRecord>();

                // =========================================================
                // INNERE SCHLEIFE: VARIANTEN (Modelle)
                // =========================================================
                for (int var = 1; var <= _context.Config.VariantsPerIteration; var++)
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    
                    // 1. Parameter vom Algorithmus generieren lassen
                    var activeParams = _optimizer.GenerateParameters(_context, iter, var, _context.Config.VariantsPerIteration);

                    PrintVariantHeader(iter, var, activeParams);

                    // 2. Skalierung (Gummiband) — mit dimensionalen Parametern aus dem Projekt
                    RubberBandScaler scaler = new RubberBandScaler(
                        activeParams, 
                        _context.Project.DimensionalParameters,
                        _context.Config.TargetPicoGkSize);

                    // 3. Datensatz vorbereiten
                    ModelRecord record = new ModelRecord
                    {
                        Iteration = iter,
                        Variant = var,
                        ActiveParameters = activeParams
                    };

                    // 4. Geometrie erzeugen (IGeometryGenerator) → GeometryResult
                    var geoResult = _geometry.GenerateAndExport(
                        iter, var, 
                        scaler.ShrunkParameters, 
                        _context.WorkingDirectory,
                        _context.Config.VoxelSmoothingIterations,
                        _context.Config.VoxelSmoothingPremeltingSteps);

                    record.StlPath = geoResult.StlPath;

                    // Metriken aus dem GeometryResult in den Record übernehmen und zurückskalieren
                    ApplyGeometryMetrics(record, geoResult, scaler);
                    record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;
                    
                    Console.WriteLine($"       -> Voxel-Geometrie erstellt. (Metriken: {string.Join(", ", record.PassiveParameters.Where(p => p.Key != "ScaleFactor").Select(p => $"{p.Key}={p.Value:F1}"))})");
                    
                    // 5. Mesh generieren (IMeshGenerator)
                    try
                    {
                        record.MeshPath = _mesher.GenerateMesh(
                            geoResult.StlPath, iter, var,
                            _context.Config, scaler.ShrinkFactor,
                            _context.WorkingDirectory);

                        // 6. FÜR JEDEN Solver in _solvers: solver.Solve(...)
                        foreach (var solver in _solvers)
                        {
                            solver.Solve(record.MeshPath, record, _context.Config, _context.WorkingDirectory);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"       -> [FEHLER] Simulation abgebrochen: {ex.Message}");
                        // Setze einen generischen Fehler-Marker — der FitnessCalculator muss damit umgehen
                        if (!record.PassiveParameters.ContainsKey("Drag"))
                            record.PassiveParameters["Drag"] = float.MaxValue;
                    }

                    // 7. Datensatz speichern
                    _context.History.Add(record);
                    currentIterationRecords.Add(record);
                    _context.ExportToCsv(); 

                    UpdateTimer(timer, ref completedVariants, ref totalSecondsPassed, totalVariants);
                } 

                // =========================================================
                // AUSWERTUNG & VALIDIERUNG
                // =========================================================
                // 1. Der Algorithmus bewertet die Modelle und wählt den Gewinner
                _optimizer.EvaluateAndSelectBest(currentIterationRecords, _context);
                
                // 2. Den Validator (ehemals Türsteher/Bouncer) anwenden
                ModelRecord trueWinner = _validator.ValidateChampion(
                    currentIterationRecords, 
                    _context.Config,
                    (testParams) => ResimulateForValidation(testParams, iter));

                previousWinner = trueWinner;
                
                _context.ExportToCsv(); 
                
                Console.WriteLine($"\n[ERGEBNIS] Iteration {iter} abgeschlossen.");
                Console.WriteLine($"           Champion ist Variante {trueWinner.Variant} (Score: {trueWinner.Fitness:F3})");

                if (iter < _context.Config.MaxIterations)
                {
                    // 3. Den Algorithmus auf die nächste Runde vorbereiten
                    _optimizer.PrepareNextIteration(_context, currentIterationRecords, trueWinner);
                }
            }

            FinishOptimization(previousWinner);
        }

        /// <summary>
        /// Callback für den ModelValidator: Führt eine vollständige Re-Simulation mit den gegebenen Parametern durch.
        /// </summary>
        private ModelRecord ResimulateForValidation(Dictionary<string, float> testParams, int iteration)
        {
            RubberBandScaler scaler = new RubberBandScaler(
                testParams, 
                _context.Project.DimensionalParameters,
                _context.Config.TargetPicoGkSize);

            var geoResult = _geometry.GenerateAndExport(
                iteration, 99, 
                scaler.ShrunkParameters, 
                _context.WorkingDirectory,
                _context.Config.VoxelSmoothingIterations,
                _context.Config.VoxelSmoothingPremeltingSteps);

            ModelRecord validationRecord = new ModelRecord
            {
                Iteration = iteration,
                Variant = 99,
                ActiveParameters = testParams
            };

            // Metriken übertragen
            ApplyGeometryMetrics(validationRecord, geoResult, scaler);

            string meshPath = _mesher.GenerateMesh(
                geoResult.StlPath, iteration, 99,
                _context.Config, scaler.ShrinkFactor,
                _context.WorkingDirectory);

            foreach (var solver in _solvers)
            {
                solver.Solve(meshPath, validationRecord, _context.Config, _context.WorkingDirectory);
            }

            return validationRecord;
        }

        /// <summary>
        /// Überträgt die Metriken des <see cref="GeometryResult"/> in den Record und rechnet sie
        /// dabei von der PicoGK-Arbeitsgröße auf die reale Größe zurück. Welche Dimension eine
        /// Metrik hat, deklariert das Projekt im <see cref="GeometryResult"/> — der Kern rät nicht.
        /// </summary>
        private static void ApplyGeometryMetrics(ModelRecord record, GeometryResult geoResult, RubberBandScaler scaler)
        {
            foreach (var metric in geoResult.Metrics)
            {
                record.PassiveParameters[metric.Key] = geoResult.ScalingFor(metric.Key) switch
                {
                    MetricScaling.Volume => scaler.RestoreVolume(metric.Value),
                    MetricScaling.Area   => scaler.RestoreArea(metric.Value),
                    MetricScaling.Linear => scaler.RestoreLength(metric.Value),
                    _                    => metric.Value
                };
            }
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

        private void FinishOptimization(ModelRecord? champion)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine(" OPTIMIERUNG ERFOLGREICH BEENDET");
            Console.WriteLine("==================================================");

            if (champion != null)
            {
                Console.WriteLine($"\n🏆 ABSOLUTER CHAMPION: Iteration {champion.Iteration} | Variante {champion.Variant}");
                Console.WriteLine($"   Score: {champion.Fitness:F2}");
                
                // Passive Parameter dynamisch anzeigen
                foreach (var kvp in champion.PassiveParameters)
                {
                    Console.WriteLine($"   {kvp.Key}: {kvp.Value:F4}");
                }

                Console.WriteLine($"\n📂 ParaView-Dateien:");
                Console.WriteLine($"   Oberfläche: Ergebnisse/Analyseergebnisse/Surface_Gen{champion.Iteration}_Var{champion.Variant}.vtu");
                Console.WriteLine($"   Volumen:    Ergebnisse/Analyseergebnisse/Volume_Gen{champion.Iteration}_Var{champion.Variant}.vtu\n");
            }
        }
    }
}

