namespace OptimierungsAlgoCFD.Core
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

        /// <summary>
        /// Automatisches Ende (09.11.2026): beendet den Lauf früher, wenn sich der Bestwert kaum noch
        /// verbessert. <see cref="MaxIterations"/> × <see cref="VariantsPerIteration"/> bleibt die Obergrenze.
        /// </summary>
        public AutoStopConfig AutoStop { get; set; } = new();

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
        /// <summary>
        /// Berechnet <c>FrontalArea</c> in 3D als wahre projizierte Silhouette (Schattenfläche in der
        /// YZ-Ebene quer zur Anströmrichtung X) statt als Rechteck des Hüllquaders (<c>Size.Y × Size.Z</c>).
        /// Aus (<c>false</c>): bisherige Hüllquader-Näherung (<c>FrontalBoxArea</c>).
        /// </summary>
        public bool UseProjectedFrontalArea { get; set; } = true;

        // 2. CFD & SU2 Einstellungen
        public float MachNumber { get; set; } = 0.1f;

        /// <summary>
        /// Globaler Netzverfeinerungsfaktor. Standard ist 1.0 (Basisgrößen aus gmsh.json).
        /// Werte &lt; 1.0 verfeinern das Netz (z. B. 0.5 halbiert alle Zielzellgrößen),
        /// Werte &gt; 1.0 vergröbern es für schnelle Vorabläufe.
        /// </summary>
        public float MeshRefinementFactor { get; set; } = 1.0f;

        /// <summary>
        /// Obergrenze der SU2-Iterationen je normaler Variante. 3000 reicht für RANS mit y+ ≈ 1 in 2D
        /// und 3D; eine ruhige Rechnung hört über das Cauchy-Kriterium früher auf (Vorgabe seit 08.11.2026,
        /// vorher 500/150/1e-4: viele Rechnungen endeten unkonvergiert).
        /// </summary>
        public int Su2MaxIterations { get; set; } = 3000;
        public int CauchyElements { get; set; } = 300;
        public string CauchyTolerance { get; set; } = "1e-5";

        /// <summary>
        /// Wie weit der <b>iterative</b> Fehler einer normalen Variante unter der Netz-Toleranz
        /// (<see cref="MeshCalibrationConfig.Tolerance"/>) liegen muss, damit die Rechnung als
        /// auskonvergiert gilt (TODO-W3). 0.25 = ein Viertel, also bei 2 % Netz-Toleranz höchstens
        /// 0,5 % Drift im Endfenster. Gemessen wird die Drift der Kennwerte, nicht das Residuum.
        /// </summary>
        public float IterativeToleranceFraction { get; set; } = 0.25f;

        /// <summary>
        /// Strenges Profil für Kontrollrechnungen (Netzkalibrierung, Feinprüfung, Recheck; TODO-W4).
        /// Normale Varianten und die Türsteher-Kontrolle (Variante 99) rechnen mit den Werten darüber.
        /// </summary>
        public ControlRunConfig ControlRuns { get; set; } = new();
        public int MpiCores { get; set; } = 12;
        public int GmshCores { get; set; } = 12;

        // 3. Wahl des Optimierungsverfahrens
        /// <summary>
        /// Welches Optimierungsverfahren gefahren wird: <c>"Bayes"</c> (Standard seit 08.11.2026), <c>"Rsm"</c> oder <c>"Evolution"</c>.
        /// Aufgelöst über <see cref="OptimizationAlgorithmFactory"/>; ein unbekannter Name
        /// führt zu einer Warnung und dem Standardverfahren.
        /// </summary>
        public string OptimizationAlgorithm { get; set; } = OptimizationAlgorithmFactory.DefaultAlgorithm;

        /// <summary>
        /// Startwert aller Zufallsquellen des Laufs (TODO-50, F23). <c>0</c> = beim Start
        /// automatisch ziehen; der gezogene Wert steht im Log und in <c>run-info.json</c>.
        /// Ein fester Wert ≠ 0 macht den Lauf exakt wiederholbar. Optimierer bekommen
        /// <c>new Random(seed)</c>, der Türsteher <c>new Random(seed + 1)</c>.
        /// </summary>
        public int RandomSeed { get; set; } = 0;

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

        // 4a. Algorithmus Bayes (TODO-55). Alles optional, die Vorgaben passen meist.
        /// <summary>Einstellungen des Bayes-Optimierers; wirkt nur bei <c>"OptimizationAlgorithm": "Bayes"</c>.</summary>
        public BayesConfig Bayes { get; set; } = new();

        // 5. Validierung des Champions (Bouncer)
        /// <summary>Erlaubte relative Abweichung der Fitness bei der Re-Simulation (0.20 = 20%).</summary>
        public float BouncerTolerance { get; set; } = 0.20f;
        /// <summary>Betrag, um den ein zufällig gewählter Parameter für die Re-Simulation verstellt wird.</summary>
        public float BouncerJitter { get; set; } = 0.01f;

        // 6. Netzkalibrierung & Gewinner-Verifikation (TODO-42 & TODO-43)
        /// <summary>Automatisierte Netzkalibrierung vor dem Lauf oder per Befehl (TODO-42).</summary>
        public MeshCalibrationConfig MeshCalibration { get; set; } = new();

        /// <summary>Feine Nachrechnung der Gewinner auf verfeinertem Netz (TODO-43).</summary>
        public ChampionVerificationConfig ChampionVerification { get; set; } = new();

        public static SimulationConfig CreateDefault() => new SimulationConfig();

        /// <summary>
        /// Kopie dieser Konfiguration mit dem strengen Profil für Kontrollrechnungen
        /// (<see cref="ControlRuns"/>): mehr Iterationen, längeres Fenster, schärfere Drift-Grenze.
        /// Nie weniger Iterationen als die normale Rechnung.
        /// </summary>
        public SimulationConfig WithControlRunProfile()
        {
            var clone = (SimulationConfig)MemberwiseClone();
            clone.Su2MaxIterations = System.Math.Max(Su2MaxIterations, ControlRuns.Su2MaxIterations);
            clone.CauchyElements = System.Math.Max(CauchyElements, ControlRuns.CauchyElements);
            clone.CauchyTolerance = ControlRuns.CauchyTolerance;
            clone.IterativeToleranceFraction = ControlRuns.IterativeToleranceFraction;
            return clone;
        }
    }

    /// <summary>
    /// Automatisches Ende der Optimierung (siehe <see cref="AutoStop"/>). Nach jeder Iteration gilt:
    /// Hat sich der beste Fitnesswert innerhalb der letzten <see cref="WindowVariants"/> Varianten um
    /// weniger als <see cref="MinImprovement"/> (relativ) verbessert, endet der Lauf.
    /// </summary>
    public class AutoStopConfig
    {
        /// <summary>"On" oder "Off" (Standard: Off = Laufdauer nur über MaxIterations × VariantsPerIteration).</summary>
        public string Mode { get; set; } = "Off";

        /// <summary>Mindestverbesserung des Bestwerts, relativ zum Bestwert vor dem Fenster (0.01 = 1 %).</summary>
        public float MinImprovement { get; set; } = 0.01f;

        /// <summary>Über so viele zuletzt gerechnete Varianten wird die Verbesserung gemessen.</summary>
        public int WindowVariants { get; set; } = 20;
    }

    /// <summary>
    /// Einstellungen des Bayes-Optimierers (TODO-55 bis TODO-57). Alles optional.
    /// </summary>
    public class BayesConfig
    {
        /// <summary>"Auto" (Standard), "Fitness" oder "Metrics". Auto = Metrics, wenn die Fitness des Projekts <c>RequiredMetrics</c> nennt.</summary>
        public string SurrogateTarget { get; set; } = "Auto";

        /// <summary>Anzahl der Startrechnungen (Versuchsplan). 0 = automatisch: max(VariantsPerIteration, 2 × freie Parameter + 2).</summary>
        public int InitialSamples { get; set; } = 0;

    }

    /// <summary>
    /// Interne Stellschrauben des Bayes-Optimierers. Bewusst <b>nicht</b> in <c>simulation.json</c>
    /// (Entscheidung 08.11.2026: so wenige Einstellungen wie möglich); die Vorgaben passen, und Tests
    /// können sie über den Konstruktor ändern.
    /// </summary>
    public sealed class BayesTuning
    {
        /// <summary>So viele Kandidaten prüft das Modell je Vorschlag auf seiner Landkarte.</summary>
        public int CandidatePool { get; init; } = 5000;

        /// <summary>"Auto" (ab 10 freien Parametern), "On" oder "Off": Suche in einem Vertrauensbereich um den besten Punkt.</summary>
        public string TrustRegion { get; init; } = "Auto";

        /// <summary>Deckel für die Lernmenge. Hält die Rechenzeit des Modells klein (wächst mit n³).</summary>
        public int MaxTrainingPoints { get; init; } = 400;
    }

    /// <summary>
    /// Strenges Profil für die wenigen, dafür genauen Kontrollrechnungen (TODO-W4).
    /// </summary>
    public class ControlRunConfig
    {
        /// <summary>Höchstzahl der SU2-Iterationen je Kontrollrechnung.</summary>
        public int Su2MaxIterations { get; set; } = 20000;

        /// <summary>Länge des Konvergenzfensters (Zeilen der history.csv).</summary>
        public int CauchyElements { get; set; } = 500;

        /// <summary>
        /// SU2-eigene Cauchy-Schwelle. Bewusst sehr streng: ob eine Kontrollrechnung als konvergiert
        /// gilt, entscheidet nicht SU2, sondern die Drift-Prüfung nach dem Lauf.
        /// </summary>
        public string CauchyTolerance { get; set; } = "1e-6";

        /// <summary>
        /// Zulässiger iterativer Fehler als Bruchteil der Netz-Toleranz (0.1 = ein Zehntel, nach
        /// Celik et al. 2008: der iterative Fehler soll mindestens eine Größenordnung unter dem
        /// Diskretisierungsfehler liegen).
        /// </summary>
        public float IterativeToleranceFraction { get; set; } = 0.1f;
    }

    /// <summary>
    /// Einstellungen für die automatisierte Netzkalibrierung (TODO-42).
    /// </summary>
    public class MeshCalibrationConfig
    {
        /// <summary>"Auto" (Standard seit 08.11.2026: Netzfaktor am Laufanfang kalibrieren) oder "Manual" (MeshRefinementFactor gilt).</summary>
        public string Mode { get; set; } = "Auto";

        /// <summary>Erlaubte relative Abweichung aller Ergebnisgrößen gegenüber der feinsten Referenzstufe (0.05 = 5 %).</summary>
        public float Tolerance { get; set; } = 0.05f;

        /// <summary>2D-Vorkalibrierung vor der 3D-Bestätigung nutzen (TODO-48).</summary>
        public bool Use2DPrecalibration { get; set; } = false;

        /// <summary>
        /// Fester Mindestbetrag der zulässigen Abweichung je Kennwert (TODO-W1). Zulässig ist
        /// <c>|Δ| ≤ Tolerance · |Referenz| + Mindestbetrag</c>: bei Werten nahe 0 (Auftrieb eines
        /// symmetrischen Profils bei 0°) zählt der Mindestbetrag statt der relativen Abweichung.
        /// Leer = Vorgaben aus <see cref="MetricTolerance.DefaultAbsolute"/> (Drag 0,0001, Lift 0,005,
        /// Momente 0,001). Einträge hier überschreiben sie je Kennwertname (additiv); unbekannte Kennwerte: 0.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, float> AbsoluteTolerances { get; set; } = new();

        /// <summary>Obergrenze der hochgerechneten Zellzahl der feinsten Stufe in 2D (TODO-W5).</summary>
        public long MaxCells2D { get; set; } = 2_000_000;

        /// <summary>
        /// Obergrenze der hochgerechneten Zellzahl der feinsten Stufe in 3D (TODO-W5). 3 Mio. wie bei MantaAuv
        /// (Servertest 06.11.2026: 1,3 Mio. Zellen stabil, ~4 h Kalibrierung); mehr kostet schnell Tage.
        /// </summary>
        public long MaxCells3D { get; set; } = 3_000_000;
    }

    /// <summary>
    /// Einstellungen für die feine Nachrechnung der Gewinner (TODO-43).
    /// </summary>
    public class ChampionVerificationConfig
    {
        /// <summary>"Off", "Final", "FinalTopN" (Standard: die besten TopN fein nachrechnen) oder "EachIteration".</summary>
        public string Mode { get; set; } = "FinalTopN";

        /// <summary>Anzahl der besten Varianten am Ende bei Mode = "FinalTopN".</summary>
        public int TopN { get; set; } = 3;

        /// <summary>Anzahl der Verfeinerungsstufen gegenüber dem wirksamen Faktor (÷ √2 je Stufe).</summary>
        public int RefinementSteps { get; set; } = 1;

        /// <summary>Relative Abweichung, ab der laut vor Netzabhängigkeit gewarnt wird (0.05 = 5 %).</summary>
        public float Tolerance { get; set; } = 0.05f;
    }
}
