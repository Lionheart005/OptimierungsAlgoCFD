using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
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

        // Lauf-Journal für die Wiederaufnahme (TODO-51). null = Verhalten exakt wie vor TODO-51.
        private readonly RunJournal? _journal;

        /// <summary>
        /// Der letzte Aufruf von <see cref="RunPipeline"/> kam aus dem Journal und hat nichts
        /// gerechnet. Die Restzeit-Schätzung lässt solche Varianten aus (TODO-51).
        /// </summary>
        public bool LastCallWasReplayed { get; private set; }

        /// <param name="requiredMetrics">
        /// Metriknamen, die der <see cref="IFitnessCalculator"/> des Projekts braucht
        /// (<see cref="IFitnessCalculator.RequiredMetrics"/>). Fehlt einer nach der ersten
        /// erfolgreich gerechneten Variante, bricht der Lauf ab (TODO-26). <c>null</c> oder
        /// leer = keine Prüfung.
        /// </param>
        /// <param name="journal">
        /// Optionales Lauf-Journal (TODO-51): jedes Ergebnis von <see cref="RunPipeline"/> wird
        /// mitgeschrieben, und im Wiedergabe-Modus antwortet es statt der Rechnung. Alle
        /// Controller eines Laufs teilen sich eine Instanz.
        /// </param>
        public WorkflowController(
            IGeometryGenerator geometry,
            SolverStage[] stages,
            IModelValidator validator,
            IOptimizationAlgorithm optimizer,
            SimulationContext context,
            IReadOnlyCollection<string>? requiredMetrics = null,
            RunJournal? journal = null)
        {
            _journal = journal;
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

            // Automatisches Ende (09.11.2026). Falsche Werte brechen hier ab, vor der ersten Rechnung.
            AutoStopConfig autoStop = _context.Config.AutoStop ?? new AutoStopConfig();
            bool autoStopEnabled = AutoStop.IsEnabled(autoStop);
            if (autoStopEnabled)
                Console.WriteLine(
                    $"[SYSTEM] Automatisches Ende: an (Mindestverbesserung {autoStop.MinImprovement * 100:0.##} % "
                    + $"über die letzten {autoStop.WindowVariants} Varianten; höchstens "
                    + $"{_context.Config.MaxIterations} × {_context.Config.VariantsPerIteration} Varianten)");

            int totalVariants = _context.Config.MaxIterations * _context.Config.VariantsPerIteration;
            int completedVariants = 0;
            double totalSecondsPassed = 0;
            int liveVariants = 0;
            
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
                SimulationLog.PrintIterationBanner(iter, _context.Config.MaxIterations);
                
                List<ModelRecord> currentIterationRecords = new List<ModelRecord>();

                // =========================================================
                // INNERE SCHLEIFE: VARIANTEN (Modelle)
                // =========================================================
                for (int var = 1; var <= _context.Config.VariantsPerIteration; var++)
                {
                    Stopwatch timer = Stopwatch.StartNew();
                    
                    // 1. Parameter vom Algorithmus generieren lassen
                    var activeParams = _optimizer.GenerateParameters(_context, iter, var, _context.Config.VariantsPerIteration);

                    SimulationLog.PrintVariantHeader(iter, _context.Config.MaxIterations, var, _context.Config.VariantsPerIteration);
                    SimulationLog.PrintGeneLine(activeParams);

                    // 2.-6. Die eigentliche Pipeline (Skalierung → Geometrie → Mesh → Solver)
                    ModelRecord record = RunPipeline(activeParams, iter, var);
                    bool replayed = LastCallWasReplayed;

                    // Sofort je Variante bewerten (wenn nicht fehlgeschlagen):
                    if (!record.SimulationFailed)
                    {
                        _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { record }, _context);
                    }

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

                    SimulationLog.PrintVariantResult(record);
                    SimulationLog.PrintVariantDiagnostics(record);
                    UpdateTimer(timer, replayed, ref completedVariants, ref liveVariants, ref totalSecondsPassed, totalVariants);
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

                // 2a. Optionale feine Nachrechnung je Iteration (Mode = "EachIteration", TODO-43)
                ChampionFineVerifier.VerifyIterationWinner(
                    trueWinner,
                    iter,
                    _context,
                    (p, f, t) => RunPipeline(p, iter, ValidationVariantNumber, f, t),
                    r => _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { r }, _context),
                    _requiredMetrics);

                lastIterationWinner = trueWinner;
                
                _context.ExportToCsv(); 
                
                SimulationLog.PrintIterationSummary(iter, _context.Config.MaxIterations, currentIterationRecords, trueWinner);

                // Automatisches Ende: nur zwischen zwei Iterationen, nie mitten in einer Generation.
                if (autoStopEnabled && iter < _context.Config.MaxIterations)
                {
                    AutoStopDecision decision = AutoStop.Evaluate(_context.History, autoStop);
                    if (decision.Stop)
                    {
                        SimulationLog.PrintBanner(
                            $"AUTOMATISCHES ENDE NACH ITERATION {iter} VON {_context.Config.MaxIterations}",
                            "Der Bestwert verbessert sich kaum noch (AutoStop).");
                        Console.WriteLine($"[AUTO-ENDE] {decision.Message}");
                        break;
                    }
                    Console.WriteLine($"[AUTO-ENDE] {decision.Message}");
                }

                if (iter < _context.Config.MaxIterations)
                {
                    // 3. Den Algorithmus auf die nächste Runde vorbereiten
                    _optimizer.PrepareNextIteration(_context, currentIterationRecords, trueWinner);
                }
            }

            // Finale Verifikation (Mode = "Final" oder "FinalTopN", TODO-43)
            ChampionFineVerifier.VerifyFinal(
                _context,
                (p, f, t) => RunPipeline(p, ValidationVariantNumber, ValidationVariantNumber, f, t),
                r => _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { r }, _context),
                _requiredMetrics);

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
        /// Unterstützt optional temporär überschriebenen Netzfaktor und Tag (TODO-42/43).
        /// Gibt einen fertigen <see cref="ModelRecord"/> zurück; die Fitness wird hier
        /// bewusst nicht berechnet, das macht der Optimierungsalgorithmus bzw. der Validator.
        /// </summary>
        public ModelRecord RunPipeline(
            Dictionary<string, float> parameters,
            int iteration,
            int variant,
            float? customRefinementFactor = null,
            string? customTag = null)
        {
            float oldFactor = _context.EffectiveMeshRefinementFactor;
            string oldTag = _context.CurrentRunTag;

            try
            {
                if (customRefinementFactor.HasValue)
                {
                    _context.EffectiveMeshRefinementFactor = customRefinementFactor.Value;
                }
                if (customTag != null)
                {
                    _context.CurrentRunTag = customTag;
                }

                LastCallWasReplayed = false;
                if (_journal == null) return RunPipelineCore(parameters, iteration, variant);

                // Wiederaufnahme (TODO-51): erst das Journal fragen, sonst rechnen und anhängen.
                // Faktor und Tag sind hier schon die wirksamen (nach TODO-42/43).
                float factor = _context.EffectiveMeshRefinementFactor;
                string tag = _context.CurrentRunTag ?? "";

                if (_journal.TryReplay(iteration, variant, factor, tag, parameters, out ModelRecord? replayed)
                    && replayed != null)
                {
                    LastCallWasReplayed = true;
                    return replayed;
                }

                ModelRecord record = RunPipelineCore(parameters, iteration, variant);

                // Sofort bei der Rückgabe: die Aufrufer verändern den Datensatz danach noch.
                _journal.Append(iteration, variant, factor, tag, parameters, record);
                return record;
            }
            finally
            {
                _context.EffectiveMeshRefinementFactor = oldFactor;
                _context.CurrentRunTag = oldTag;
            }
        }

        private ModelRecord RunPipelineCore(Dictionary<string, float> parameters, int iteration, int variant)
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
            record.PassiveParameters["MeshRefinementFactor"] = _context.EffectiveMeshRefinementFactor;

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

            SimulationLog.PrintGeometry(geoResult.StlPath, record.PassiveParameters);

            try
            {
                // Jede Stufe vernetzt mit ihrem eigenen Mesher und rechnet dann.
                // Stufen, die sich dieselbe Mesher-Instanz teilen, vernetzen nur einmal.
                var meshCache = new Dictionary<IMeshGenerator, string>(ReferenceEqualityComparer.Instance);

                foreach (var stage in _stages)
                {
                    if (!meshCache.TryGetValue(stage.Mesher, out string? meshPath))
                    {
                        var meshRequest = new MeshRequest(
                            geoResult.StlPath, iteration, variant,
                            _context.Config, scaler.ShrinkFactor,
                            _context.WorkingDirectory,
                            _context.EffectiveMeshRefinementFactor,
                            _context.CurrentRunTag);

                        meshPath = stage.Mesher.GenerateMesh(meshRequest);
                        meshCache[stage.Mesher] = meshPath;

                        // ModelRecord führt genau einen Mesh-Pfad — den der ersten Stufe.
                        if (string.IsNullOrEmpty(record.MeshPath)) record.MeshPath = meshPath;

                        int cellCount = MeshMetrics.ReadSu2CellCount(meshPath);
                        if (cellCount > 0)
                        {
                            record.PassiveParameters["MeshCells"] = cellCount;
                        }
                    }

                    stage.Solver.Solve(meshPath, record, _context.Config, _context.WorkingDirectory);
                }
            }
            catch (Exception ex)
            {
                SimulationLog.Error($"Simulation abgebrochen: {ex.Message}");
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

        public static string FormatParameterValue(float value)
        {
            float abs = MathF.Abs(value);
            if (abs > 0f && abs < 1.0f)
                return value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void UpdateTimer(Stopwatch timer, bool replayed, ref int completed, ref int live, ref double totalSeconds, int totalVariants)
        {
            timer.Stop();
            completed++;

            TimeSpan? timeLeft = null;
            if (!replayed)
            {
                live++;
                totalSeconds += timer.Elapsed.TotalSeconds;
                
                double avgSeconds = totalSeconds / live;
                int variantsLeft = totalVariants - completed;
                timeLeft = TimeSpan.FromSeconds(variantsLeft * avgSeconds);
            }
            
            SimulationLog.PrintVariantFooter(timer.Elapsed.TotalSeconds, timeLeft);
        }

        private void FinishOptimization(ModelRecord? champion, ModelRecord? lastIterationWinner)
        {
            // Eine Optimierung, in der keine einzige Variante gerechnet werden konnte, ist nicht
            // "erfolgreich" (Servertest 06.11.2026: Gmsh scheiterte an jeder Variante, die Meldung blieb) (TODO-W9).
            int valid = _context.History.Count(r => !r.SimulationFailed);
            bool anySucceeded = valid > 0;

            if (anySucceeded)
            {
                SimulationLog.PrintBanner("OPTIMIERUNG ERFOLGREICH BEENDET");
            }
            else
            {
                SimulationLog.PrintBanner("OPTIMIERUNG BEENDET – KEINE EINZIGE VARIANTE WAR GÜLTIG");
            }

            if (!anySucceeded)
            {
                SimulationLog.Warning($"Alle {_context.History.Count} Varianten sind gescheitert (Vernetzung oder SU2). Die Fehlerprotokolle liegen unter FehlerLogs/.");
                return;
            }

            if (champion != null)
            {
                Console.WriteLine();
                SimulationLog.WriteLineColored($"🏆 ABSOLUTER CHAMPION: Iteration {champion.Iteration} | Variante {champion.Variant}", ConsoleColor.Green);
                SimulationLog.WriteLineColored($"   Score: {champion.Fitness:F2}", ConsoleColor.Green);

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

                // Liegt der Gewinner am Rand des erlaubten Bereichs, bestimmt die Grenze das Ergebnis.
                // Das wird sichtbar gemeldet, die Grenzen selbst werden nie automatisch geändert.
                var atBounds = BoundaryHint.Find(champion.ActiveParameters, _context.Project.ParameterBounds);
                if (atBounds.Count > 0)
                {
                    Console.WriteLine();
                    foreach (string line in BoundaryHint.Lines(atBounds))
                        SimulationLog.WriteLineColored(line, ConsoleColor.Yellow);
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
