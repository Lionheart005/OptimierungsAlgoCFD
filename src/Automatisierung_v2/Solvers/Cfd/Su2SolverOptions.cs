using System.Collections.Generic;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// Vorgabedaten für den SU2-Solver: Fluideigenschaften, Referenzwerte, CFL-Steuerung
    /// und die Zuordnung der Ergebnisgrößen (TODO-26).
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

        /// <summary>
        /// Name der Metrik in <c>ModelRecord.PassiveParameters</c>, aus der SU2 REF_AREA gebildet
        /// wird. Der Wert wird als mm² gelesen und in m² umgerechnet — das ganze Framework
        /// rechnet in mm. Ein Projekt, das seine Referenzfläche anders nennt, trägt den Namen
        /// hier ein; fehlt die Metrik zur Laufzeit, warnt <see cref="Su2Solver"/> (TODO-12).
        /// </summary>
        public string ReferenceAreaMetric { get; set; } = "FrontalArea";

        // --- Ergebnisgrößen (TODO-26) ---

        /// <summary>Code-Standard für <see cref="ConvergenceField"/>.</summary>
        public const string DefaultConvergenceField = "DRAG";

        /// <summary>Code-Standard für <see cref="HistoryOutput"/>.</summary>
        public static readonly string[] DefaultHistoryOutput = { "ITER", "RMS_RES", "AERO_COEFF" };

        /// <summary>
        /// Welche Größen der Solver aus der SU2-<c>history.csv</c> in den
        /// <see cref="ModelRecord"/> übernimmt.
        /// <para>
        /// Links steht der Name, unter dem die Fitness-Formel den Wert in
        /// <c>PassiveParameters</c> sieht; rechts der Spaltenname in der history.csv
        /// (<c>CD</c>, <c>CL</c>, <c>CSF</c>, <c>CMx</c>, <c>CMy</c>, <c>CMz</c> — alle aus
        /// <c>AERO_COEFF</c>). „Lift dazunehmen“ ist damit eine Zeile in der su2.json des
        /// Projekts und kein Eingriff in <c>Solvers/</c>.
        /// </para>
        /// <para>
        /// Der Standard <c>{ "Drag" = "CD" }</c> hält das Verhalten von vor TODO-26 exakt bei.
        /// Verschachtelte JSON-Objekte werden <b>additiv</b> gemerged: ein Projekt, das nur
        /// <c>{"Lift":"CL"}</c> schreibt, bekommt <c>Drag</c> aus dem Standard dazu. Eine Größe
        /// wieder loszuwerden geht nur über diesen Code-Standard.
        /// </para>
        /// </summary>
        public Dictionary<string, string> ResultMetrics { get; set; } = new() { ["Drag"] = "CD" };

        /// <summary>
        /// SU2 <c>CONV_FIELD</c> — die Größe, an der SU2 seine Konvergenz misst. War vor
        /// TODO-26 fest auf <c>DRAG</c> verdrahtet; ein Projekt, das auf Auftrieb optimiert,
        /// will hier <c>LIFT</c>. Leer = nicht konfiguriert, dann gilt
        /// <see cref="DefaultConvergenceField"/>.
        /// </summary>
        public string ConvergenceField { get; set; } = DefaultConvergenceField;

        /// <summary>
        /// SU2 <c>HISTORY_OUTPUT</c> — welche Spaltengruppen SU2 überhaupt in die history.csv
        /// schreibt. <c>AERO_COEFF</c> liefert sämtliche Beiwerte; wer diese Gruppe entfernt,
        /// nimmt damit auch jeder Zuordnung in <see cref="ResultMetrics"/> die Grundlage.
        /// Leere Liste = nicht konfiguriert, dann gilt <see cref="DefaultHistoryOutput"/>.
        /// </summary>
        public string[] HistoryOutput { get; set; } = (string[])DefaultHistoryOutput.Clone();

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
