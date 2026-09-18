namespace MyPicoGkProject
{
    /// <summary>
    /// Vorgabedaten für den SU2-Solver: Fluideigenschaften, Referenzwerte und CFL-Steuerung.
    /// Werden aus <c>config/solvers/su2.json</c> geladen und per Konstruktor injiziert.
    /// Die Standardwerte hier entsprechen exakt dem, was vorher in
    /// <see cref="Su2ConfigGenerator"/> hardcodiert stand.
    /// </summary>
    public class Su2SolverOptions
    {
        // --- Fluideigenschaften ---
        /// <summary>Dichte des Mediums in kg/m³ (1025 = Meerwasser).</summary>
        public float Density { get; set; } = 1025.0f;
        /// <summary>Dynamische Viskosität in Pa·s.</summary>
        public float DynamicViscosity { get; set; } = 1.001e-3f;
        /// <summary>Turbulenzmodell, z.B. SA oder SST.</summary>
        public string TurbulenceModel { get; set; } = "SA";
        /// <summary>Schallgeschwindigkeit in m/s — rechnet die Mach-Zahl in eine Anströmgeschwindigkeit um.</summary>
        public float SpeedOfSound { get; set; } = 343.2f;

        // --- Referenzwerte ---
        /// <summary>SU2 REF_LENGTH.</summary>
        public float ReferenceLength { get; set; } = 0.01f;

        // --- CFL-Steuerung ---
        public float CflNumber { get; set; } = 5.0f;
        public bool CflAdapt { get; set; } = true;
        /// <summary>CFL_ADAPT_PARAM: Faktor beim Verkleinern.</summary>
        public float CflAdaptFactorDown { get; set; } = 0.5f;
        /// <summary>CFL_ADAPT_PARAM: Faktor beim Vergrößern.</summary>
        public float CflAdaptFactorUp { get; set; } = 1.2f;
        /// <summary>CFL_ADAPT_PARAM: untere Schranke.</summary>
        public float CflAdaptMin { get; set; } = 1.0f;
        /// <summary>CFL_ADAPT_PARAM: obere Schranke.</summary>
        public float CflAdaptMax { get; set; } = 50.0f;
    }
}
