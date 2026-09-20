using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// SU2 CFD Solver. Führt eine inkompressible RANS-Simulation durch.
    ///
    /// Welche Größen aus der history.csv in <c>record.PassiveParameters</c> landen, steht in
    /// <see cref="Su2SolverOptions.ResultMetrics"/> — der Solver kennt seit TODO-26 keinen
    /// festen Metriknamen mehr, auch nicht im Fehlerfall.
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

        /// <param name="options">Vorgabedaten aus solvers/su2.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
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
                    
                    // Nur noch das generische Flag: vor TODO-26 stand hier
                    // PassiveParameters["Drag"] = float.MaxValue und damit der letzte
                    // projektspezifische Begriff im Solver. Der IFitnessCalculator wertet
                    // das Flag ohnehin zuerst aus.
                    record.SimulationFailed = true;
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

            // Setzt bei einer nicht lesbaren Größe selbst record.SimulationFailed.
            ReadMetricsFromHistory(historyPath, record);
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
            Console.WriteLine("          Der Name gehört in die solvers/su2.json des Projekts unter 'ReferenceAreaMetric'.");
        }

        /// <summary>
        /// Übernimmt die in <see cref="Su2SolverOptions.ResultMetrics"/> zugeordneten Größen aus
        /// der letzten Zeile der SU2-history.csv in den Record (TODO-26). Vor TODO-26 las der
        /// Solver fest die erste Spalte, die mit <c>CD</c> anfing.
        ///
        /// Öffentlich, damit sie ohne MPI und SU2 testbar ist — dasselbe Muster wie
        /// <see cref="ResolveReferenceArea"/> (TODO-12).
        /// </summary>
        /// <remarks>
        /// Kann auch nur eine der konfigurierten Größen nicht gelesen werden, setzt die Methode
        /// <see cref="ModelRecord.SimulationFailed"/>. Wer eine Größe konfiguriert, braucht sie —
        /// eine Fitness aus halben Daten wäre schlimmer als ein ausgemustertes Modell.
        /// </remarks>
        public void ReadMetricsFromHistory(string historyPath, ModelRecord record)
        {
            Dictionary<string, string> wanted = _options.ResultMetrics;

            if (wanted == null || wanted.Count == 0)
            {
                Console.WriteLine("[WARNUNG] Es ist keine Ergebnisgröße konfiguriert — SU2 hat gerechnet,");
                Console.WriteLine("          aber es wird nichts ausgelesen. Die Zuordnung gehört unter");
                Console.WriteLine("          'ResultMetrics' in die su2.json (z.B. \"Drag\": \"CD\").");
                record.SimulationFailed = true;
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(historyPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    -> Fehler beim Lesen der Ergebnisse: {ex.Message}");
                record.SimulationFailed = true;
                return;
            }

            if (lines.Length < 2)
            {
                Console.WriteLine($"    -> Fehler: {Path.GetFileName(historyPath)} enthält keine Datenzeile.");
                record.SimulationFailed = true;
                return;
            }

            string[] headers = SplitCsvLine(lines[0]);
            string[] finalValues = SplitCsvLine(lines[lines.Length - 1]);

            bool complete = true;

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                string metricName = (entry.Key ?? string.Empty).Trim();
                string columnName = (entry.Value ?? string.Empty).Trim();

                if (metricName.Length == 0 || columnName.Length == 0)
                {
                    WarnAboutMetric(metricName, columnName,
                        "Metrikname oder Spaltenname ist leer.", headers);
                    complete = false;
                    continue;
                }

                int index = FindColumn(headers, columnName, out string problem);

                if (index < 0)
                {
                    WarnAboutMetric(metricName, columnName, problem, headers);
                    complete = false;
                    continue;
                }

                // Treffer, aber nur über das Präfix: verwendbar, trotzdem meldenswert.
                if (problem.Length > 0)
                    Console.WriteLine($"[WARNUNG] Ergebnisgröße '{metricName}': {problem}");

                if (index >= finalValues.Length)
                {
                    WarnAboutMetric(metricName, columnName,
                        $"Die letzte Zeile hat nur {finalValues.Length} Felder, die Spalte steht an Position {index + 1}.",
                        headers);
                    complete = false;
                    continue;
                }

                // float.IsFinite fängt zusätzlich "nan" und "inf" ab: beides parst .NET
                // klaglos, und eine divergierte Simulation liefert genau das. Ohne die
                // Prüfung stünde NaN als Metrik im Record und vergiftete jede Formel,
                // die damit rechnet.
                if (!float.TryParse(finalValues[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value))
                {
                    WarnAboutMetric(metricName, columnName,
                        $"SU2 hat keinen gültigen Zahlenwert geschrieben: '{finalValues[index]}'.", headers);
                    complete = false;
                    continue;
                }

                record.PassiveParameters[metricName] = value;
                Console.WriteLine($"    -> Analyse erfolgreich. {metricName} ({columnName}): {value:F5}");
            }

            if (!complete) record.SimulationFailed = true;
        }

        /// <summary>
        /// Sucht die Spalte zu einem konfigurierten Namen. Zuerst <b>exakt</b> (Groß-/Kleinschreibung
        /// egal; Anführungszeichen und Leerzeichen hat <see cref="SplitCsvLine"/> schon entfernt).
        ///
        /// Erst wenn das leer ausgeht, greift ein Präfix-Treffer — aber nur, wenn er
        /// <b>eindeutig</b> ist. Vor TODO-26 suchte der Solver pauschal per
        /// <c>StartsWith("CD")</c>; bei Momenten wäre das fatal, weil <c>"CM"</c> auf CMx, CMy
        /// und CMz passt und allein die Spaltenreihenfolge entschieden hätte, welche gewinnt.
        /// Der Präfix-Weg bleibt trotzdem, weil SU2 je nach Version und MARKER_MONITORING auch
        /// Namen wie <c>CD(Wall)</c> schreibt.
        /// </summary>
        /// <param name="problem">
        /// Leer bei einem sauberen Treffer. Bei Rückgabe &lt; 0 die Begründung, bei einem
        /// Präfix-Treffer der Hinweis, welche Spalte ersatzweise genommen wurde.
        /// </param>
        /// <returns>Spaltenindex, oder -1 wenn die Spalte fehlt oder mehrdeutig ist.</returns>
        public static int FindColumn(string[] headers, string columnName, out string problem)
        {
            problem = string.Empty;

            int exact = Array.FindIndex(headers, h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (exact >= 0) return exact;

            var prefixHits = new List<int>();
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].StartsWith(columnName, StringComparison.OrdinalIgnoreCase))
                    prefixHits.Add(i);
            }

            if (prefixHits.Count == 1)
            {
                problem = $"Keine Spalte heißt genau '{columnName}' — ersatzweise '{headers[prefixHits[0]]}' verwendet.";
                return prefixHits[0];
            }

            if (prefixHits.Count > 1)
            {
                problem = $"'{columnName}' ist mehrdeutig und passt auf mehrere Spalten: "
                          + string.Join(", ", prefixHits.Select(i => headers[i]))
                          + ". Bitte den vollen Spaltennamen eintragen.";
                return -1;
            }

            problem = $"Die Spalte '{columnName}' steht nicht in der history.csv.";
            return -1;
        }

        /// <summary>
        /// Zerlegt eine Zeile der history.csv und räumt Anführungszeichen und die Auffüll-
        /// Leerzeichen weg, mit denen SU2 seine Spalten ausrichtet.
        /// </summary>
        private static string[] SplitCsvLine(string line)
        {
            string[] fields = line.Split(',');

            for (int i = 0; i < fields.Length; i++)
                fields[i] = fields[i].Replace("\"", "").Trim();

            return fields;
        }

        /// <summary>
        /// Meldet eine nicht lesbare Ergebnisgröße. Die vorhandenen Spalten mitzudrucken ist der
        /// eigentliche Nutzen — ein Tippfehler in der Zuordnung ist so sofort sichtbar, genau wie
        /// bei der Referenzflächen-Metrik aus TODO-12.
        /// </summary>
        private static void WarnAboutMetric(string metricName, string columnName, string problem, string[] headers)
        {
            string shownName = metricName.Length == 0 ? "(ohne Namen)" : metricName;
            string shownColumn = columnName.Length == 0 ? "(ohne Spalte)" : columnName;

            Console.WriteLine($"[WARNUNG] Ergebnisgröße '{shownName}' <- Spalte '{shownColumn}': {problem}");
            Console.WriteLine($"          Vorhandene Spalten: {(headers.Length == 0 ? "(keine)" : string.Join(", ", headers))}.");
            Console.WriteLine("          Die Zuordnung steht unter 'ResultMetrics' in der su2.json; welche Spalten");
            Console.WriteLine("          SU2 überhaupt schreibt, steuert 'HistoryOutput' (AERO_COEFF liefert die Beiwerte).");
            Console.WriteLine("          Dieses Modell wird als fehlgeschlagen gewertet.");
        }
    }
}
