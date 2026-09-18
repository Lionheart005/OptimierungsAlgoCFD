using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// SU2 CFD Solver. Führt eine inkompressible RANS-Simulation durch.
    /// Schreibt den Drag-Koeffizienten in record.PassiveParameters["Drag"].
    /// </summary>
    public class Su2Solver : ISimulationSolver
    {
        /// <summary>
        /// Umrechnung der Geometrie-Metriken (mm²) in SU2-Einheiten (m²). Das ganze Framework
        /// rechnet in mm — PicoGK, die STL-Dateien und die Gmsh-Skalierung ebenso.
        /// </summary>
        private const float SquareMmToSquareM = 0.000001f;

        /// <summary>
        /// Ersatz-Referenzfläche in mm², wenn die konfigurierte Metrik fehlt. Bewusst derselbe
        /// Wert wie vor TODO-12, damit sich im Fehlerfall nur die Meldung ändert, nicht die Zahl.
        /// </summary>
        private const float FallbackReferenceAreaMm2 = 1.0f;

        private readonly Su2SolverOptions _options;

        /// <param name="options">Vorgabedaten aus config/solvers/su2.json; ohne Angabe gelten die Standardwerte.</param>
        public Su2Solver(Su2SolverOptions? options = null)
        {
            _options = options ?? new Su2SolverOptions();
        }

        public string Name => "SU2 CFD Solver";

        public void Solve(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory)
        {
            float refArea = ResolveReferenceArea(record);

            Console.WriteLine($"    -> Strömungsanalyse läuft für: {Path.GetFileName(meshPath)}...");
            
            string configPath = Path.Combine(workingDirectory, "current_config.cfg");
            string historyPath = Path.Combine(workingDirectory, "history.csv");

            if (File.Exists(historyPath)) File.Delete(historyPath);

            Su2ConfigGenerator.Generate(configPath, meshPath, refArea, config, _options);

            try
            {
                Process su2Process = new Process();
                su2Process.StartInfo.FileName = config.MpiRunPath;
                su2Process.StartInfo.Arguments = $"-n {config.MpiCores} {config.Su2Path} current_config.cfg"; 
                su2Process.StartInfo.WorkingDirectory = workingDirectory; 
                su2Process.StartInfo.RedirectStandardOutput = true;
                su2Process.StartInfo.RedirectStandardError = true;
                
                System.Text.StringBuilder su2Output = new System.Text.StringBuilder();
                object outputLock = new object(); 

                su2Process.OutputDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) lock(outputLock) { su2Output.AppendLine(e.Data); }
                };
                su2Process.ErrorDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) lock(outputLock) { su2Output.AppendLine(e.Data); }
                };

                su2Process.Start();
                su2Process.BeginOutputReadLine();
                su2Process.BeginErrorReadLine();
                su2Process.WaitForExit();

                bool isSu2Successful = false;
                
                if (File.Exists(historyPath))
                {
                    string[] lines = File.ReadAllLines(historyPath);
                    if (lines.Length >= 5) 
                    {
                        isSu2Successful = true;
                    }
                }

                if (!isSu2Successful)
                {
                    string errorLogDirectory = Path.Combine(workingDirectory, "FehlerLogs");
                    Directory.CreateDirectory(errorLogDirectory);

                    string modelName = Path.GetFileNameWithoutExtension(meshPath);
                    string errorLogPath = Path.Combine(errorLogDirectory, $"SU2_Error_{modelName}.txt");
                    File.WriteAllText(errorLogPath, su2Output.Length == 0 ? "Kein Konsolen-Output von SU2 abgefangen." : su2Output.ToString());
                    Console.WriteLine($"\n[WARNUNG] CFD-Simulation für {modelName} abgebrochen.");
                    Console.WriteLine($"-> Details exportiert nach: Ergebnisse/FehlerLogs/{Path.GetFileName(errorLogPath)}\n");
                    
                    record.PassiveParameters["Drag"] = float.MaxValue;
                    return;
                }

                // VTU-Dateien sichern
                string analysisResultsDir = Path.Combine(workingDirectory, "Analyseergebnisse");
                Directory.CreateDirectory(analysisResultsDir);
                
                string defaultSurfaceVtu = Path.Combine(workingDirectory, "surface.vtu");
                string savedSurfaceVtu = Path.Combine(analysisResultsDir, $"Surface_Gen{record.Iteration}_Var{record.Variant}.vtu");
                if (File.Exists(defaultSurfaceVtu)) File.Copy(defaultSurfaceVtu, savedSurfaceVtu, true);

                string defaultVolumeVtu = Path.Combine(workingDirectory, "vol_solution.vtu");
                string savedVolumeVtu = Path.Combine(analysisResultsDir, $"Volume_Gen{record.Iteration}_Var{record.Variant}.vtu");
                if (File.Exists(defaultVolumeVtu)) File.Copy(defaultVolumeVtu, savedVolumeVtu, true);
            }
            catch (Exception e)
            {
                throw new Exception($"Kritischer Systemfehler beim Ausführen von SU2: {e.Message}");
            }

            record.PassiveParameters["Drag"] = ReadDragFromHistory(historyPath);
        }

        /// <summary>
        /// Bestimmt SU2 REF_AREA aus den Metriken des Modells. Welche Metrik gemeint ist, steht
        /// in <see cref="Su2SolverOptions.ReferenceAreaMetric"/> — der Solver kennt keinen festen
        /// Namen mehr (TODO-12).
        ///
        /// Fehlt die Metrik, wird weitergerechnet, aber <b>nicht stillschweigend</b>: der
        /// CD-Wert wäre dann um Größenordnungen falsch, und genau das war vorher nicht zu sehen.
        /// </summary>
        public float ResolveReferenceArea(ModelRecord record)
        {
            string metric = (_options.ReferenceAreaMetric ?? string.Empty).Trim();

            if (metric.Length == 0)
            {
                WarnAboutMissingReferenceArea("Es ist keine Referenzflächen-Metrik konfiguriert", record);
                return FallbackReferenceAreaMm2 * SquareMmToSquareM;
            }

            if (record.PassiveParameters.TryGetValue(metric, out float areaMm2))
                return areaMm2 * SquareMmToSquareM;

            WarnAboutMissingReferenceArea($"Die Metrik '{metric}' fehlt in den Ergebnissen dieses Modells", record);
            return FallbackReferenceAreaMm2 * SquareMmToSquareM;
        }

        private static void WarnAboutMissingReferenceArea(string reason, ModelRecord record)
        {
            string known = record.PassiveParameters.Count == 0
                ? "(keine)"
                : string.Join(", ", record.PassiveParameters.Keys);

            Console.WriteLine($"[WARNUNG] {reason} — SU2 rechnet ersatzweise mit REF_AREA = "
                              + (FallbackReferenceAreaMm2 * SquareMmToSquareM).ToString(CultureInfo.InvariantCulture)
                              + " m². Der CD-Wert ist damit um Größenordnungen falsch.");
            Console.WriteLine($"          Vorhandene Metriken: {known}.");
            Console.WriteLine("          Der Name gehört in config/solvers/su2.json unter 'ReferenceAreaMetric'.");
        }

        private float ReadDragFromHistory(string historyPath)
        {
            try
            {
                string[] lines = File.ReadAllLines(historyPath);
                if (lines.Length < 2) return float.MaxValue;

                string[] headers = lines[0].Replace("\"", "").Split(',');
                int dragIndex = Array.FindIndex(headers, h => h.Trim().StartsWith("CD", StringComparison.OrdinalIgnoreCase));                
                
                if (dragIndex == -1) 
                    throw new Exception("Spalte 'CD' nicht in der CSV gefunden.");

                string[] finalValues = lines[lines.Length - 1].Split(',');
                
                if (float.TryParse(finalValues[dragIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float dragScore))
                {
                    Console.WriteLine($"    -> Analyse erfolgreich. Ermittelter Widerstand (CD): {dragScore:F5}");
                    return dragScore;
                }
                else
                {
                    Console.WriteLine($"    -> Fehler: SU2 hat keinen gültigen Zahlenwert berechnet: '{finalValues[dragIndex]}'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    -> Fehler beim Lesen der Ergebnisse: {ex.Message}");
            }

            return float.MaxValue;
        }
    }
}
