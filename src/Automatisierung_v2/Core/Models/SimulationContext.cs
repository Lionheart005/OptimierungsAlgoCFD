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
            
            var activeKeys = History[0].ActiveParameters.Keys.ToList();
            var passiveKeys = History[0].PassiveParameters.Keys.ToList();
            
            // Header
            string header = "Iteration;Variant;" + 
                            string.Join(";", activeKeys) + ";" + 
                            string.Join(";", passiveKeys) + ";" + 
                            "Fitness;StlPath;MeshPath";
            csvBuilder.AppendLine(header);

            // Daten
            foreach (var record in History)
            {
                var activeVals = activeKeys.Select(k => record.ActiveParameters.ContainsKey(k) ? record.ActiveParameters[k].ToString("F3") : "0");
                var passiveVals = passiveKeys.Select(k => record.PassiveParameters.ContainsKey(k) ? record.PassiveParameters[k].ToString("F5") : "0");
                
                string line = $"{record.Iteration};{record.Variant};" +
                              $"{string.Join(";", activeVals)};" +
                              $"{string.Join(";", passiveVals)};" +
                              $"{record.Fitness:F5};{record.StlPath};{record.MeshPath}";
                csvBuilder.AppendLine(line);     
            }

            File.WriteAllText(csvPath, csvBuilder.ToString(), Encoding.UTF8);
        }
    }
}
