namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Framework-weite Konfiguration (ehemals GlobalConfig innerhalb von SimulationData).
    /// Enthält Einstellungen die für ALLE Projekte gelten.
    /// </summary>
    public class SimulationConfig
    {
        // --- PFADE ZU EXTERNER SOFTWARE ---
        //
        // PATH-relativ statt absolut (TODO-24). Vorher standen hier die Pfade eines ganz
        // bestimmten Servers (/home/lpleissner/...) — ein fremder Nutzer erbte damit eine
        // Installation, die es auf seinem Rechner nicht gibt, und merkte es erst im Lauf.
        //
        // .NET löst einen bloßen Programmnamen in Process.StartInfo.FileName über den PATH
        // auf, und scripts/sim-runner.sh legt in load_env genau die Verzeichnisse dorthin,
        // aus denen die alten Pfade stammten. Su2Path wird ohnehin nur als Argument an
        // mpirun durchgereicht und von diesem selbst aufgelöst.
        //
        // Damit entfällt der letzte Grund für eine *.local.json. Findet ein Rechner die
        // Programme trotzdem nicht, meldet das `.\scripts\sim.ps1 doctor` vor dem Lauf.
        public string MpiRunPath { get; set; } = "mpirun";
        public string Su2Path { get; set; } = "SU2_CFD";
        public string GmshPath { get; set; } = "gmsh";

        // 1. Zeit & Umfang
        public int MaxIterations { get; set; } = 10;
        public int VariantsPerIteration { get; set; } = 10;
        public float BaseVoxelResolution { get; set; } = 0.5f;
        public float TargetPicoGkSize { get; set; } = 50.0f;
        public int VoxelSmoothingIterations { get; set; } = 40;
        public int VoxelSmoothingPremeltingSteps { get; set; } = 2;

        // 1a. Abschaltbare Framework-Bausteine
        /// <summary>
        /// Skaliert die Geometrie auf <see cref="TargetPicoGkSize"/> und die Metriken zurück.
        /// Aus: der Geometrie-Kernel arbeitet direkt mit den echten Maßen, ScaleFactor bleibt 1.
        /// </summary>
        public bool UseRubberBandScaler { get; set; } = true;
        /// <summary>
        /// Glättet die exportierte STL. Aus: die STL kommt unverändert aus dem Voxel-Modell.
        /// </summary>
        public bool UseStlSmoothing { get; set; } = true;

        // 2. CFD & SU2 Einstellungen
        public float MachNumber { get; set; } = 0.1f;
        public int Su2MaxIterations { get; set; } = 500;
        public int CauchyElements { get; set; } = 150;
        public string CauchyTolerance { get; set; } = "1e-4";
        public int MpiCores { get; set; } = 12;
        public int GmshCores { get; set; } = 12;

        // 3. Wahl des Optimierungsverfahrens
        /// <summary>
        /// Welches Optimierungsverfahren gefahren wird: <c>"Rsm"</c> oder <c>"Evolution"</c>.
        /// Aufgelöst über <see cref="OptimizationAlgorithmFactory"/>; ein unbekannter Name
        /// führt zu einer Warnung und dem Standardverfahren.
        /// </summary>
        public string OptimizationAlgorithm { get; set; } = OptimizationAlgorithmFactory.DefaultAlgorithm;

        // 3a. Algorithmus-Spezifische Einstellungen (Evolution)
        public float MinimalDeviationExploration { get; set; } = 1.0f;
        public float MinimalDeviationExploitation { get; set; } = 0.05f;
        public float RoleFineTuner { get; set; } = 0.20f;
        public float RoleCautious { get; set; } = 0.40f;
        public float RoleNormal { get; set; } = 0.60f;

        // 4. Algorithmus RSM
        public int RsmVirtualSimulations { get; set; } = 50000;
        public float RsmIdwPower { get; set; } = 3.0f;
        public float RsmExploitationRatio { get; set; } = 0.7f;
        public float RsmExploitationSigma { get; set; } = 0.1f;

        // 5. Validierung des Champions (Bouncer)
        /// <summary>Erlaubte relative Abweichung der Fitness bei der Re-Simulation (0.20 = 20%).</summary>
        public float BouncerTolerance { get; set; } = 0.20f;
        /// <summary>Betrag, um den ein zufällig gewählter Parameter für die Re-Simulation verstellt wird.</summary>
        public float BouncerJitter { get; set; } = 0.01f;

        public static SimulationConfig CreateDefault() => new SimulationConfig();
    }
}
