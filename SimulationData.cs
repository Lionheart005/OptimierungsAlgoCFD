using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MyPicoGkProject
{
    // =========================================================================
    // DATENMODELL FÜR EIN EINZELNES MODELL (VARIANT)
    // =========================================================================
    public class ModelRecord
    {
        public int Iteration { get; set; } // Früher: Generation (Allgemeiner für verschiedene Algorithmen)
        public int Variant { get; set; }
        
        // Aktive Parameter (werden vom Algorithmus gesteuert/mutiert)
        public Dictionary<string, float> ActiveParameters { get; set; } = new Dictionary<string, float>();
        
        // Passive Parameter / Metriken (Resultate aus der Simulation, z.B. Volumen, Drag)
        public Dictionary<string, float> PassiveParameters { get; set; } = new Dictionary<string, float>();

        public float Fitness { get; set; } 
        public string StlPath { get; set; } = "";
        public string MeshPath { get; set; } = "";
    }

    // =========================================================================
    // ZENTRALE DATENBANK & KONFIGURATION
    // =========================================================================
    public class SimulationData
    {
        // --- PFADE ZU EXTERNER SOFTWARE ---
        public class ProgramPaths
        {
            public string MpiRun { get; set; } = "/home/lpleissner/miniconda/bin/mpirun";
            public string Su2 { get; set; } = "/home/lpleissner/software/bin/SU2_CFD";
            public string Gmsh { get; set; } = "gmsh";
        }

        // --- DIE STELLSCHRAUBEN ---
        public class GlobalConfig
        {
            public ProgramPaths Paths { get; set; } = new ProgramPaths();

            // 1. Zeit & Umfang
            public int MaxIterations = 10;
            public int VariantsPerIteration = 10;       
            public float BaseVoxelResolution = 0.5f;     //auflösung mm, kleiner = mehr rechenzeit
            public float TargetPicoGkSize = 50.0f;      
            public int VoxelSmoothingIterations = 40; 
            public int VoxelSmoothingPremeltingSteps = 2; 

            // 2. CFD & SU2 Einstellungen
            public float MachNumber = 0.1f;             
            public int Su2MaxIterations = 500;          
            public int CauchyElements = 150;             
            public string CauchyTolerance = "1e-4";    // 1e-4 = 0.01% Restfehler, 1e-5 = 0.001% Restfehler  
            public int MpiCores = 12; //anzahl verfügbare kerne su2
            public int GmshCores = 12; //gmsh kerne

            // 3. Passive Ziel-Parameter (Leitplanken für die Optimierung)
            // Diese Werte werden nicht mutiert, sondern vom Algorithmus für Strafen/Belohnungen ausgelesen
            public Dictionary<string, float> OptimizationTargets = new Dictionary<string, float>
            {
                { "MinimumAllowedVolume", 2000f },
                { "MaximumAllowedVolume", 85000f },
                { "DragBalanceFactor", 3f },
                { "BouncerTolerance", 0.20f } // Maximal erlaubte Abweichung bei Validierung (20%)
            };

            // 4. Algorithmus-Spezifische Einstellungen (Evolution)
            public float MinimalDeviationExploration = 1.0f; 
            public float MinimalDeviationExploitation = 0.05f; 
            public float RoleFineTuner = 0.20f; 
            public float RoleCautious = 0.40f; 
            public float RoleNormal = 0.60f; 
            // rest ist rolle entdecker (RoleNormal<Entdecker<1.0)

            // Algorithmus RSM
            public int RsmVirtualSimulations = 50000; // Anzahl der in Millisekunden getesteten virtuellen Varianten
            public float RsmIdwPower = 3.0f;          // Gewichtung (Inverse Distance Weighting). Höher = lokaler, niedriger = glatter
            public float RsmExploitationRatio = 0.7f; // 0.7 = 70% der virtuellen Tests suchen nah am Optimum, 30% suchen komplett zufällig
            public float RsmExploitationSigma = 0.1f; // Streuung bei der Optimum-Suche (0.1 = 10% Schwankung vom Bestwert)
        }   

        public GlobalConfig Settings { get; private set; } = new GlobalConfig();

        // --- ZUSTAND & HISTORIE ---
        public string BaseDirectory { get; private set; } 
        public Dictionary<string, float> BaseParameters { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, float> MaxDeviations { get; set; } = new Dictionary<string, float>();
        public Dictionary<string, (float Min, float Max)> ParameterBounds { get; set; } = new Dictionary<string, (float Min, float Max)>();
        public List<ModelRecord> History { get; set; } = new List<ModelRecord>();

        public SimulationData()
        {
            BaseDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Ergebnisse");
            Directory.CreateDirectory(BaseDirectory);
            Directory.CreateDirectory(Path.Combine(BaseDirectory, "FehlerLogs"));
            Directory.CreateDirectory(Path.Combine(BaseDirectory, "Analyseergebnisse"));

            // --- INITIALE AKTIVE PARAMETER (AUV Konzept) ---
            BaseParameters.Add("Length", 50.0f);
            BaseParameters.Add("Width", 40.0f);
            BaseParameters.Add("MainRadius", 4.0f);
            BaseParameters.Add("WingRadius", 3.0f);
            BaseParameters.Add("TailTaper", 1.0f);

            // --- INITIALE MUTATIONS-STÄRKE (Sigma-Streuung) ---
            MaxDeviations.Add("Length", 5.0f);
            MaxDeviations.Add("Width", 4.0f); 
            MaxDeviations.Add("MainRadius", 1.0f);
            MaxDeviations.Add("WingRadius", 1.0f);
            MaxDeviations.Add("TailTaper", 0.1f);

            // --- PARAMETER-GRENZEN (Hard-Limits für physikalische Plausibilität) ---
            ParameterBounds.Add("Length", (30.0f, 100.0f));
            ParameterBounds.Add("Width", (20.0f, 80.0f));
            ParameterBounds.Add("MainRadius", (4.0f, 15.0f));
            ParameterBounds.Add("WingRadius", (2.0f, 10.0f));
            ParameterBounds.Add("TailTaper", (0.1f, 1.5f));
        }

        // Dynamischer CSV Export (Verarbeitet automatisch alle aktiven und passiven Parameter)
        public void ExportToCsv()
        {
            if (History.Count == 0) return;

            string csvPath = Path.Combine(BaseDirectory, "Simulation_Results.csv");
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