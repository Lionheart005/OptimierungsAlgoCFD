using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MyPicoGkProject
{
    /// <summary>
    /// Zentrale Laufzeitdatenbank (ehemals SimulationData).
    /// Kombiniert Framework-Config, Projekt-Config und Zustand.
    /// Directory-Erstellung ist aus dem Konstruktor herausgezogen.
    /// </summary>
    public class SimulationContext
    {
        public SimulationConfig Config { get; set; }
        public ProjectConfig Project { get; set; }
        public string WorkingDirectory { get; set; }
        public List<ModelRecord> History { get; set; } = new();

        public SimulationContext(SimulationConfig config, ProjectConfig project, string? workingDirectory = null)
        {
            Config = config;
            Project = project;
            WorkingDirectory = workingDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "Ergebnisse");
        }

        /// <summary>
        /// Erstellt die benötigten Verzeichnisse. Separat vom Konstruktor für bessere Testbarkeit.
        /// </summary>
        public void EnsureDirectories()
        {
            Directory.CreateDirectory(WorkingDirectory);
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "FehlerLogs"));
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, "Analyseergebnisse"));
        }

        // Dynamischer CSV Export (Verarbeitet automatisch alle aktiven und passiven Parameter)
        public void ExportToCsv()
        {
            if (History.Count == 0) return;

            string csvPath = Path.Combine(WorkingDirectory, "Simulation_Results.csv");
            StringBuilder csvBuilder = new StringBuilder();
            
            // Spalten über ALLE Records sammeln, nicht nur über den ersten: sonst fehlt eine
            // Metrik im gesamten Export, sobald sie ausgerechnet im ersten Record fehlt
            // (z.B. weil dessen Simulation abgebrochen ist).
            var activeKeys = History.SelectMany(r => r.ActiveParameters.Keys).Distinct().ToList();
            var passiveKeys = History.SelectMany(r => r.PassiveParameters.Keys).Distinct().ToList();

            // Header
            string header = "Iteration;Variant;" +
                            string.Join(";", activeKeys) + ";" +
                            string.Join(";", passiveKeys) + ";" +
                            "Fitness;StlPath;MeshPath;SimulationFailed";
            csvBuilder.AppendLine(header);

            // Daten
            foreach (var record in History)
            {
                var activeVals = activeKeys.Select(k => record.ActiveParameters.ContainsKey(k) ? record.ActiveParameters[k].ToString("F3") : "0");
                var passiveVals = passiveKeys.Select(k => record.PassiveParameters.ContainsKey(k) ? record.PassiveParameters[k].ToString("F5") : "0");
                
                string line = $"{record.Iteration};{record.Variant};" +
                              $"{string.Join(";", activeVals)};" +
                              $"{string.Join(";", passiveVals)};" +
                              $"{record.Fitness:F5};{record.StlPath};{record.MeshPath};" +
                              $"{(record.SimulationFailed ? 1 : 0)}";
                csvBuilder.AppendLine(line);     
            }

            File.WriteAllText(csvPath, csvBuilder.ToString(), Encoding.UTF8);
        }
    }
}
