using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Orchestriert den Optimierungs-Workflow.
    /// Arbeitet NUR noch mit Interfaces — keine konkreten Klassen mehr.
    /// </summary>
    public class WorkflowController
    {
        /// <summary>Variantennummer, unter der Validierungsläufe (Re-Simulation) laufen.</summary>
        private const int ValidationVariantNumber = 99;

        private readonly SimulationContext _context;
        private readonly IOptimizationAlgorithm _optimizer;
        private readonly IGeometryGenerator _geometry;
        private readonly SolverStage[] _stages;
        private readonly IModelValidator _validator;

        // Kein IFitnessCalculator-Feld: die Bewertung läuft über den Optimierungsalgorithmus
        // (EvaluateAndSelectBest) und im Validierungslauf über den ChampionValidator.
        // Deshalb kommen die Pflicht-Metriken als reine Namensliste herein und nicht als
        // Rechner — sonst zöge TODO-26 die mit TODO-13 entfernte Abhängigkeit wieder ein.
        private readonly IReadOnlyCollection<string> _requiredMetrics;

        /// <param name="requiredMetrics">
        /// Metriknamen, die der <see cref="IFitnessCalculator"/> des Projekts braucht
        /// (<see cref="IFitnessCalculator.RequiredMetrics"/>). Fehlt einer nach der ersten
        /// erfolgreich gerechneten Variante, bricht der Lauf ab (TODO-26). <c>null</c> oder
        /// leer = keine Prüfung.
        /// </param>
        public WorkflowController(
            IGeometryGenerator geometry,
            SolverStage[] stages,
            IModelValidator validator,
            IOptimizationAlgorithm optimizer,
            SimulationContext context,
            IReadOnlyCollection<string>? requiredMetrics = null)
        {
            _geometry = geometry;
            _stages = stages;
            _validator = validator;
            _optimizer = optimizer;
            _context = context;
            _requiredMetrics = requiredMetrics ?? Array.Empty<string>();
        }

        public void RunOptimization()
        {
            Console.WriteLine($"[SYSTEM] Nutze Optimierungsalgorithmus: {_optimizer.Name}");
            Console.WriteLine($"[SYSTEM] Projekt: {_context.Project.ProjectName}");
            Console.WriteLine($"[SYSTEM] Solver: {string.Join(", ", _stages.Select(s => s.Solver.Name))}");

            int totalVariants = _context.Config.MaxIterations * _context.Config.VariantsPerIteration;
            int completedVariants = 0;
            double totalSecondsPassed = 0;
            
            // Gewinner der zuletzt gelaufenen Iteration. Nur für die Schlussmeldung
            // interessant — der ABSOLUTE Champion wird am Ende über die ganze History
            // bestimmt (TODO-15), nicht über die letzte Iteration.
            ModelRecord? lastIterationWinner = null;

            // Die Pflicht-Metriken werden genau einmal geprüft — an der ersten Variante,
            // die tatsächlich durchgelaufen ist (siehe EnsureRequiredMetrics).
            bool requiredMetricsChecked = false;

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

                    // 2.-6. Die eigentliche Pipeline (Skalierung → Geometrie → Mesh → Solver)
                    ModelRecord record = RunPipeline(activeParams, iter, var);

                    // 6a. Liefert die Pipeline überhaupt, was die Fitness-Formel braucht?
                    if (!requiredMetricsChecked && !record.SimulationFailed)
                    {
                        requiredMetricsChecked = true;
                        EnsureRequiredMetrics(record);
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

                lastIterationWinner = trueWinner;
                
                _context.ExportToCsv(); 
                
                Console.WriteLine($"\n[ERGEBNIS] Iteration {iter} abgeschlossen.");
                Console.WriteLine($"           Champion ist Variante {trueWinner.Variant} (Score: {trueWinner.Fitness:F3})");

                if (iter < _context.Config.MaxIterations)
                {
                    // 3. Den Algorithmus auf die nächste Runde vorbereiten
                    _optimizer.PrepareNextIteration(_context, currentIterationRecords, trueWinner);
                }
            }

            // Der beste Lauf des GESAMTEN Durchgangs, nicht der Gewinner der letzten
            // Iteration: beim EA ist das meist dasselbe, beim RSM mit seiner zufälligen
            // DoE-Phase oft nicht.
            FinishOptimization(SelectBestRecord(_context.History), lastIterationWinner);
        }

        /// <summary>
        /// Der Record mit der höchsten Fitness. Disqualifizierte und fehlgeschlagene Modelle
        /// tragen eine sehr kleine Fitness und fallen dadurch von selbst heraus.
        /// Bei Gleichstand gewinnt der frühere Eintrag, damit die Auswahl reproduzierbar ist.
        /// Gibt <c>null</c> zurück, wenn die Liste leer ist.
        /// </summary>
        public static ModelRecord? SelectBestRecord(IEnumerable<ModelRecord> records)
        {
            ModelRecord? best = null;

            foreach (var record in records)
            {
                if (best == null || record.Fitness > best.Fitness) best = record;
            }

            return best;
        }

        /// <summary>
        /// Prüft, ob der Record alle Metriken trägt, die der <see cref="IFitnessCalculator"/> des
        /// Projekts braucht — und bricht sonst ab (TODO-26).
        ///
        /// <para>
        /// Bewusst erst nach der ersten Variante: vorher existiert keine einzige Metrik, weil sie
        /// aus Geometrie und Solver kommen. Und bewusst nur an einem Record, dessen Simulation
        /// <b>nicht</b> abgebrochen ist — sonst würde ein einzelner SU2-Absturz in Variante 1 als
        /// Konfigurationsfehler gemeldet. Scheitert jede Variante, bleibt die Prüfung aus; dann
        /// hat der Lauf ohnehin ein größeres Problem, über das der Solver selbst schon geredet hat.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Eine Pflicht-Metrik fehlt.</exception>
        private void EnsureRequiredMetrics(ModelRecord record)
        {
            if (_requiredMetrics.Count == 0) return;

            var missing = _requiredMetrics.Where(name => !record.PassiveParameters.ContainsKey(name)).ToList();
            if (missing.Count == 0) return;

            string present = record.PassiveParameters.Count == 0
                ? "(keine)"
                : string.Join(", ", record.PassiveParameters.Keys);

            throw new InvalidOperationException(
                $"[KONFIGURATION] Nach der ersten gerechneten Variante fehlt/fehlen die Metrik(en) "
                + $"{string.Join(", ", missing)}, ohne die sich die Fitness-Formel nicht rechnen lässt. "
                + $"Vorhanden ist: {present}. "
                + "Geometrie-Metriken meldet der IGeometryGenerator des Projekts über AddMetric(...); "
                + "Solver-Größen kommen aus 'ResultMetrics' in dessen su2.json. "
                + "Abbruch hier, statt den ganzen Lauf mit einer Ersatz-Fitness durchzurechnen.");
        }

        /// <summary>
        /// Callback für den ModelValidator: Führt eine vollständige Re-Simulation mit den gegebenen
        /// Parametern durch. Läuft durch dieselbe Pipeline wie eine reguläre Variante,
        /// nur unter der Variantennummer 99 und ohne Eintrag in der History.
        /// </summary>
        private ModelRecord ResimulateForValidation(Dictionary<string, float> testParams, int iteration)
        {
            return RunPipeline(testParams, iteration, ValidationVariantNumber);
        }

        /// <summary>
        /// Die eigentliche Pipeline für einen Parametersatz:
        /// Skalierung (Gummiband) → Geometrie → Metriken → Vernetzung → Solver-Kette.
        /// Gibt einen fertigen <see cref="ModelRecord"/> zurück; die Fitness wird hier
        /// bewusst nicht berechnet, das macht der Optimierungsalgorithmus bzw. der Validator.
        /// </summary>
        private ModelRecord RunPipeline(Dictionary<string, float> parameters, int iteration, int variant)
        {
            // Skalierung (Gummiband) — mit dimensionalen Parametern aus dem Projekt.
            // Abgeschaltet liefert Create() eine Neutral-Instanz (ShrinkFactor 1, Identität).
            RubberBandScaler scaler = RubberBandScaler.Create(
                _context.Config.UseRubberBandScaler,
                parameters,
                _context.Project.DimensionalParameters,
                _context.Config.TargetPicoGkSize);

            ModelRecord record = new ModelRecord
            {
                Iteration = iteration,
                Variant = variant,
                ActiveParameters = parameters
            };

            // Geometrie erzeugen (IGeometryGenerator) → GeometryResult
            var geoResult = _geometry.GenerateAndExport(
                iteration, variant,
                scaler.ShrunkParameters,
                _context.WorkingDirectory,
                _context.Config);

            record.StlPath = geoResult.StlPath;

            // Metriken aus dem GeometryResult in den Record übernehmen und zurückskalieren
            ApplyGeometryMetrics(record, geoResult, scaler);
            record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;

            Console.WriteLine($"       -> Voxel-Geometrie erstellt. (Metriken: {string.Join(", ", record.PassiveParameters.Where(p => p.Key != "ScaleFactor").Select(p => $"{p.Key}={p.Value:F1}"))})");

            try
            {
                // Jede Stufe vernetzt mit ihrem eigenen Mesher und rechnet dann.
                // Stufen, die sich dieselbe Mesher-Instanz teilen, vernetzen nur einmal.
                var meshCache = new Dictionary<IMeshGenerator, string>(ReferenceEqualityComparer.Instance);

                foreach (var stage in _stages)
                {
                    if (!meshCache.TryGetValue(stage.Mesher, out string? meshPath))
                    {
                        meshPath = stage.Mesher.GenerateMesh(
                            geoResult.StlPath, iteration, variant,
                            _context.Config, scaler.ShrinkFactor,
                            _context.WorkingDirectory);
                        meshCache[stage.Mesher] = meshPath;

                        // ModelRecord führt genau einen Mesh-Pfad — den der ersten Stufe.
                        if (string.IsNullOrEmpty(record.MeshPath)) record.MeshPath = meshPath;
                    }

                    stage.Solver.Solve(meshPath, record, _context.Config, _context.WorkingDirectory);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       -> [FEHLER] Simulation abgebrochen: {ex.Message}");
                // Generischer Fehler-Marker — der FitnessCalculator muss damit umgehen.
                // Der Kern kennt keine projektspezifischen Metriknamen wie "Drag".
                record.SimulationFailed = true;
            }

            return record;
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

        private void FinishOptimization(ModelRecord? champion, ModelRecord? lastIterationWinner)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine(" OPTIMIERUNG ERFOLGREICH BEENDET");
            Console.WriteLine("==================================================");

            if (champion != null)
            {
                Console.WriteLine($"\n🏆 ABSOLUTER CHAMPION: Iteration {champion.Iteration} | Variante {champion.Variant}");
                Console.WriteLine($"   Score: {champion.Fitness:F2}");

                // Sichtbar machen, wenn der beste Lauf nicht der letzte war — sonst wundert
                // sich der Nutzer über zwei verschiedene Varianten in der Ausgabe.
                if (lastIterationWinner != null && !ReferenceEquals(lastIterationWinner, champion))
                {
                    Console.WriteLine(
                        $"   (Gewinner der letzten Iteration war Iteration {lastIterationWinner.Iteration} | " +
                        $"Variante {lastIterationWinner.Variant} mit Score {lastIterationWinner.Fitness:F2})");
                }

                // Passive Parameter dynamisch anzeigen
                foreach (var kvp in champion.PassiveParameters)
                {
                    Console.WriteLine($"   {kvp.Key}: {kvp.Value:F4}");
                }

                // Die Pfade kommen aus dem Kontext und stehen nicht mehr als Text hier:
                // seit TODO-25 liegen die Ergebnisse unter Ergebnisse/<Projekt>/, und eine
                // hartcodierte Zeile hätte den Nutzer in den falschen Ordner geschickt.
                string analysis = System.IO.Path.Combine(_context.WorkingDirectory, "Analyseergebnisse");

                Console.WriteLine($"\n📂 ParaView-Dateien in {analysis}:");
                Console.WriteLine($"   Oberfläche: Surface_Gen{champion.Iteration}_Var{champion.Variant}.vtu");
                Console.WriteLine($"   Volumen:    Volume_Gen{champion.Iteration}_Var{champion.Variant}.vtu\n");
            }
        }
    }
}

