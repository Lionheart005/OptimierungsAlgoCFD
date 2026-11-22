using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Cli
{
    /// <summary>
    /// Handler für die feine Nachrechnung einzelner Varianten (<c>--recheck</c>, optional <c>--unsteady</c>).
    /// </summary>
    internal static class RecheckCommand
    {
        /// <summary>
        /// Rechnet eine einzelne Variante aus Simulation_Results.csv fein nach (--recheck, TODO-43 / TODO-37).
        /// </summary>
        public static void RunRecheck(IProjectDefinition definition, ProjectContext context, string? variantTarget, int finer, bool isUnsteady = false)
        {
            Console.WriteLine($"[SYSTEM] Starte Recheck für Projekt: {context.Name} (Ziel: '{variantTarget ?? "letzte"}', Finer: {finer}, Unsteady: {isUnsteady})");

            var simulationContext = new SimulationContext(context.Simulation, context.Project);
            simulationContext.EnsureDirectories();

            string csvPath = Path.Combine(simulationContext.WorkingDirectory, "Simulation_Results.csv");
            if (!File.Exists(csvPath))
            {
                throw new InvalidOperationException(
                    $"[RECHECK] Die Datei '{csvPath}' existiert nicht. Bitte zuerst eine Simulation laufen lassen.");
            }

            string[] lines = File.ReadAllLines(csvPath);
            if (lines.Length <= 1)
            {
                throw new InvalidOperationException($"[RECHECK] '{csvPath}' enthält keine Datensätze.");
            }

            string[] headers = lines[0].Split(';');

            int targetIter = -1;
            int targetVar = -1;

            if (!string.IsNullOrWhiteSpace(variantTarget))
            {
                var match = Regex.Match(variantTarget, @"(?:Gen)?(?<iter>\d+)[_vVar]*(?<var>\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    targetIter = int.Parse(match.Groups["iter"].Value);
                    targetVar = int.Parse(match.Groups["var"].Value);
                }
            }

            string? selectedLine = null;
            if (targetIter > 0 && targetVar > 0)
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split(';');
                    if (parts.Length > 1 && int.TryParse(parts[0], out int it) && int.TryParse(parts[1], out int vr))
                    {
                        if (it == targetIter && vr == targetVar)
                        {
                            selectedLine = lines[i];
                            break;
                        }
                    }
                }
                if (selectedLine == null)
                {
                    throw new InvalidOperationException($"[RECHECK] Variante Gen{targetIter}_Var{targetVar} wurde in '{csvPath}' nicht gefunden.");
                }
            }
            else
            {
                selectedLine = lines.Last(l => !string.IsNullOrWhiteSpace(l));
            }

            var cols = selectedLine.Split(';');
            targetIter = int.Parse(cols[0]);
            targetVar = int.Parse(cols[1]);

            var activeParams = new Dictionary<string, float>();
            float originalFitness = 0f;
            var originalMetrics = new Dictionary<string, float>();

            for (int i = 2; i < headers.Length; i++)
            {
                string header = headers[i].Trim();
                if (context.Project.BaseParameters.ContainsKey(header))
                {
                    if (float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        activeParams[header] = val;
                    }
                }
                else if (header.Equals("Fitness", StringComparison.OrdinalIgnoreCase))
                {
                    float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out originalFitness);
                }
                else
                {
                    if (float.TryParse(cols[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float pVal))
                    {
                        originalMetrics[header] = pVal;
                    }
                }
            }

            IGeometryGenerator geometry = definition.CreateGeometry(context);
            IFitnessCalculator fitness = definition.CreateFitness(context);
            IGeometryKernel kernel = definition.CreateKernel();
            SolverStage[] stages = definition.CreateStages(context);
            MeshCalibration.ValidateYPlusConsistency(context, calibrationActive: false);
            // Bei --unsteady rechnet der Recheck erst stationär und startet die zeitgenaue Rechnung danach aus
            // dieser Lösung (TODO-W7); die Zeitdomäne wird deshalb erst nach der ersten Stufe eingeschaltet.

            int seed = RunSeed.Resolve(context.Simulation.RandomSeed);
            Console.WriteLine(RunSeed.Describe(seed, context.Simulation.RandomSeed));

            IModelValidator validator = new ChampionValidator(fitness, RunSeed.ForValidator(seed), context.Project.ParameterBounds);
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(
                context.Simulation.OptimizationAlgorithm, fitness, RunSeed.ForOptimizer(seed));
            var controller = new WorkflowController(
                geometry,
                stages,
                validator,
                optimizer,
                simulationContext,
                fitness.RequiredMetrics);

            // Ausgangsfaktor = der Faktor, mit dem DIESE Variante gerechnet wurde (Spalte MeshRefinementFactor der CSV).
            float baseFactor = originalMetrics.TryGetValue("MeshRefinementFactor", out float usedFactor) && usedFactor > 0f
                ? usedFactor
                : context.Simulation.MeshRefinementFactor;
            float fineFactor = baseFactor / MathF.Pow(MathF.Sqrt(2.0f), Math.Max(1, finer));

            Console.WriteLine($"[RECHECK] Gefundene Variante Gen{targetIter}_Var{targetVar}: Original-Fitness={originalFitness:F4}, Recheck-Netzfaktor={fineFactor:0.00}");

            float effectiveVoxelResolution = VoxelResolutionAdvisor.Resolve(
                context.Simulation, context.Project, stages);

            kernel.RunHosted(effectiveVoxelResolution, () =>
            {
                var record = controller.RunPipeline(
                    activeParams,
                    targetIter,
                    targetVar,
                    customRefinementFactor: fineFactor,
                    customTag: "Recheck");

                fitness.CalculateFitness(record, context.Simulation);

                ModelRecord? steadyRecord = null;
                if (isUnsteady)
                {
                    steadyRecord = record;
                    Console.WriteLine("\n[RECHECK] Stationäre Stufe fertig. Starte die zeitgenaue Rechnung (URANS) aus dieser Lösung ...");

                    // SU2 braucht für die 2. Ordnung zwei Zeitstände: die stationäre Lösung wird zweimal bereitgestellt.
                    PrepareUnsteadyRestart(simulationContext.WorkingDirectory);
                    foreach (var stage in stages)
                    {
                        if (stage.Solver is Su2Solver su2)
                        {
                            su2.Options.TimeDomain.Enabled = true;
                            su2.Options.TimeDomain.RestartIteration = 2;
                        }
                    }

                    record = controller.RunPipeline(
                        activeParams,
                        targetIter,
                        targetVar,
                        customRefinementFactor: fineFactor,
                        customTag: "RecheckURANS");
                    fitness.CalculateFitness(record, context.Simulation);

                    CleanUnsteadyRestartFiles(simulationContext.WorkingDirectory);
                }

                float relDev = Math.Abs(record.Fitness - originalFitness) / Math.Max(Math.Abs(originalFitness), 1e-7f);

                Console.WriteLine("\n==================================================");
                Console.WriteLine(" RECHECK ERGEBNIS");
                Console.WriteLine("==================================================");
                Console.WriteLine($"Variante:             Gen{targetIter}_Var{targetVar}");
                Console.WriteLine($"Original-Fitness:     {originalFitness:F4} (Faktor: {baseFactor:0.00})");
                Console.WriteLine($"Recheck-Fitness:      {record.Fitness:F4} (Faktor: {fineFactor:0.00})");
                Console.WriteLine($"Fitness-Abweichung:   {relDev * 100:0.0} %");

                foreach (var m in originalMetrics.Keys)
                {
                    if (record.PassiveParameters.TryGetValue(m, out float rVal))
                    {
                        float origVal = originalMetrics[m];
                        float mDev = Math.Abs(rVal - origVal) / Math.Max(Math.Abs(origVal), 1e-7f);
                        string steadyPart = steadyRecord != null && steadyRecord.PassiveParameters.TryGetValue(m, out float sVal)
                            ? $", stationär fein={sVal:F5}"
                            : string.Empty;
                        Console.WriteLine($"   {m}: Original={origVal:F5}{steadyPart}, Recheck={rVal:F5} (Abweichung: {mDev * 100:0.0} %)");
                    }
                }

                if (steadyRecord != null)
                {
                    Console.WriteLine("\nStationär (feines Netz) gegen zeitgemittelt (URANS, letzte Hälfte der Zeitschritte):");
                    foreach (var m in originalMetrics.Keys)
                    {
                        if (steadyRecord.PassiveParameters.TryGetValue(m, out float sv)
                            && record.PassiveParameters.TryGetValue(m, out float uv)
                            && !m.StartsWith("Su2_", StringComparison.OrdinalIgnoreCase)
                            && !m.StartsWith("YPlus_", StringComparison.OrdinalIgnoreCase))
                        {
                            float d = Math.Abs(uv - sv) / Math.Max(Math.Abs(sv), 1e-7f);
                            Console.WriteLine($"   {m}: stationär={sv:F5}, zeitgemittelt={uv:F5} (Unterschied {d * 100:0.0} %)");
                        }
                    }
                    foreach (var kv in record.PassiveParameters.Where(k => k.Key.StartsWith("Su2_Osc_", StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"   Schwingungsamplitude {kv.Key.Substring("Su2_Osc_".Length)}: ±{kv.Value * 50:0.0} % (Spannweite {kv.Value * 100:0.0} %)");
                    Console.WriteLine("   Ein großer Unterschied zwischen stationär und zeitgemittelt heißt: die Strömung ist instationär,");
                    Console.WriteLine("   der stationäre Wert ist dann nur eine Näherung.");
                }
                Console.WriteLine("==================================================\n");
            });
        }

        /// <summary>
        /// Legt aus der stationären Lösung (<c>restart.dat</c> oder <c>restart_flow.dat</c>) die zwei
        /// Zeitstände <c>restart_00000.dat</c> und <c>restart_00001.dat</c> an, aus denen SU2 die
        /// zeitgenaue Rechnung 2. Ordnung neu startet (TODO-W7).
        /// </summary>
        public static void PrepareUnsteadyRestart(string workingDirectory)
        {
            string steady = Path.Combine(workingDirectory, "restart.dat");
            if (!File.Exists(steady))
            {
                string flowRestart = Path.Combine(workingDirectory, "restart_flow.dat");
                if (File.Exists(flowRestart))
                    steady = flowRestart;
            }

            if (!File.Exists(steady))
                throw new FileNotFoundException(
                    "[RECHECK] Die stationäre Stufe hat weder restart.dat noch restart_flow.dat hinterlassen; ohne sie kann die zeitgenaue Rechnung nicht daraus starten.", steady);

            CleanUnsteadyRestartFiles(workingDirectory);
            File.Copy(steady, Path.Combine(workingDirectory, "restart_00000.dat"), overwrite: true);
            File.Copy(steady, Path.Combine(workingDirectory, "restart_00001.dat"), overwrite: true);
        }

        /// <summary>Entfernt die nummerierten Zeitstände (<c>restart_00000.dat</c> …), <c>restart.dat</c> und <c>restart_flow.dat</c> bleiben.</summary>
        public static void CleanUnsteadyRestartFiles(string workingDirectory)
        {
            foreach (string file in Directory.GetFiles(workingDirectory, "restart_*.dat"))
            {
                string fileName = Path.GetFileName(file);
                if (!Regex.IsMatch(fileName, @"^restart_\d+\.dat$", RegexOptions.IgnoreCase))
                {
                    continue;
                }
                try { File.Delete(file); } catch (IOException) { }
            }
        }
    }
}
