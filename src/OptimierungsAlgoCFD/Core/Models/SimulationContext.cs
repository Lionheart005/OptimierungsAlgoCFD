using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OptimierungsAlgoCFD.Core
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

        /// <summary>
        /// Aktueller Arbeitspunkt der Optimierung — der Satz, um den herum mutiert bzw.
        /// gesampelt wird. Startet als Kopie von <see cref="ProjectConfig.BaseParameters"/>
        /// und wird vom Algorithmus fortgeschrieben.
        ///
        /// Bewusst hier und nicht in der <see cref="ProjectConfig"/>: die Konfiguration
        /// kommt aus JSON und ist Vorgabe, kein Laufzeitzustand. Vor TODO-17 überschrieb
        /// der Algorithmus sie, wodurch ein zweiter Lauf im selben Prozess mit den
        /// Endwerten des ersten gestartet wäre.
        /// </summary>
        public Dictionary<string, float> CurrentBaseParameters { get; set; } = new();

        /// <summary>
        /// Aktuelle Mutationsstärke (Sigma) je Parameter. Startet als Kopie von
        /// <see cref="ProjectConfig.MaxDeviations"/>; der evolutionäre Algorithmus
        /// zieht sie über die Iterationen enger oder weiter.
        /// </summary>
        public Dictionary<string, float> CurrentDeviations { get; set; } = new();

        /// <summary>
        /// Wirksamer Netzverfeinerungsfaktor dieses Laufs. Startet bei <see cref="SimulationConfig.MeshRefinementFactor"/>
        /// und kann von Kalibrier- oder Verifikationsläufen überschrieben werden, ohne die Konfiguration zu ändern.
        /// </summary>
        public float EffectiveMeshRefinementFactor { get; set; } = 1.0f;

        /// <summary>
        /// Herkunft des wirksamen Netzfaktors (TODO-42).
        /// Standard: "MANUELL aus simulation.json", im Auto-Modus kalibriert.
        /// </summary>
        public string MeshRefinementSource { get; set; } = "MANUELL aus simulation.json";

        /// <summary>
        /// Relativer oder absoluter Pfad zum Kalibrierbericht, falls kalibriert (TODO-42).
        /// </summary>
        public string? CalibrationReportPath { get; set; } = null;

        /// <summary>
        /// Optionaler Bezeichner (Tag) für den aktuellen Lauf (z. B. "Kal3" oder "Verif").
        /// Fließt in die Dateinamen der Netzdateien ein, um Überschreiben zu verhindern.
        /// </summary>
        public string CurrentRunTag { get; set; } = "";

        /// <param name="workingDirectory">
        /// Ohne Angabe <c>Ergebnisse/&lt;Projektname&gt;/</c> im Arbeitsverzeichnis.
        /// </param>
        public SimulationContext(SimulationConfig config, ProjectConfig project, string? workingDirectory = null)
        {
            Config = config;
            Project = project;
            WorkingDirectory = workingDirectory ?? DefaultWorkingDirectory(project);

            ResetRuntimeState();
        }

        /// <summary>
        /// <c>Ergebnisse/&lt;Projektname&gt;/</c> (TODO-25). Vorher war <c>Ergebnisse/</c> ein
        /// gemeinsamer Topf: das zweite Projekt überschrieb die <c>Simulation_Results.csv</c>
        /// des ersten — dieselbe Problemklasse wie bei den Konfigurationsdateien.
        ///
        /// <para>
        /// Ohne Projektnamen bleibt es beim gemeinsamen Ordner. Im Lauf kommt das nicht vor
        /// (der Name ist der Ordnername und wird von der <see cref="ProjectRegistry"/>
        /// erzwungen); es ist der Fall für Tests, die eine nackte
        /// <see cref="ProjectConfig"/> bauen.
        /// </para>
        /// </summary>
        public static string DefaultWorkingDirectory(ProjectConfig project)
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), "Ergebnisse");
            string name = (project.ProjectName ?? string.Empty).Trim();

            return name.Length == 0 ? root : Path.Combine(root, name);
        }

        /// <summary>
        /// Setzt den Laufzeitzustand zurück auf die Projektvorgabe. Wird im Konstruktor
        /// aufgerufen; ein zweiter Lauf im selben Prozess startet dadurch wieder bei den
        /// konfigurierten Werten.
        /// </summary>
        public void ResetRuntimeState()
        {
            CurrentBaseParameters = new Dictionary<string, float>(Project.BaseParameters);
            CurrentDeviations = new Dictionary<string, float>(Project.MaxDeviations);
            EffectiveMeshRefinementFactor = Config.MeshRefinementFactor > 0f ? Config.MeshRefinementFactor : 1.0f;
            MeshRefinementSource = "MANUELL aus simulation.json";
            CalibrationReportPath = null;
            CurrentRunTag = "";
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
            // Ein Kennwert, der denselben Namen wie ein aktiver Parameter trägt (z. B. AngleOfAttackDeg, den der Solver
            // zusätzlich als Kennwert schreibt), bekäme sonst eine zweite Spalte gleichen Namens.
            var passiveKeys = History.SelectMany(r => r.PassiveParameters.Keys).Distinct().Where(k => !activeKeys.Contains(k)).ToList();

            // Header
            string header = "Iteration;Variant;" +
                            string.Join(";", activeKeys) + ";" +
                            string.Join(";", passiveKeys) + ";" +
                            "Fitness;StlPath;MeshPath;SimulationFailed;Disqualified";
            csvBuilder.AppendLine(header);

            // Daten. Feste Kultur: sonst steht auf einem deutschen Windows "0,500" statt "0.500", und die
            // Auswerteskripte lesen die Spalte als Text.
            CultureInfo inv = CultureInfo.InvariantCulture;
            foreach (var record in History)
            {
                var activeVals = activeKeys.Select(k => record.ActiveParameters.ContainsKey(k) ? record.ActiveParameters[k].ToString("F3", inv) : "0");
                var passiveVals = passiveKeys.Select(k => record.PassiveParameters.ContainsKey(k) ? record.PassiveParameters[k].ToString("F5", inv) : "0");

                string line = $"{record.Iteration};{record.Variant};" +
                              $"{string.Join(";", activeVals)};" +
                              $"{string.Join(";", passiveVals)};" +
                              $"{record.Fitness.ToString("F5", inv)};{record.StlPath};{record.MeshPath};" +
                              $"{(record.SimulationFailed ? 1 : 0)};{(record.Disqualified ? 1 : 0)}";
                csvBuilder.AppendLine(line);
            }

            File.WriteAllText(csvPath, csvBuilder.ToString(), Encoding.UTF8);
        }
    }
}
