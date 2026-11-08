using System;
using System.Collections.Generic;
using System.Globalization;

using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Projects.Naca2D
{
    /// <summary>
    /// Ergebnis eines Validierungsabgleichs gegen die NASA-TMR-Referenzdaten (TODO-47).
    /// </summary>
    public sealed record TmrValidationResult(
        float AlphaDeg,
        float ClRef,
        float CdRef,
        float ClSim,
        float CdSim,
        float ClRelError,
        float CdRelError,
        bool Passed,
        string Summary);

    /// <summary>
    /// NASA Langley Research Center - Turbulence Modeling Resource (TMR) Validierungsdaten (TODO-47).
    /// Referenzfall: 2D NACA 0012 Tragflügelprofil bei Re_c = 6·10⁶, Mach = 0.15, Spalart-Allmaras (SA)
    /// Turbulenzmodell.
    ///
    /// <para>
    /// Quelle: NASA Langley Research Center, Turbulence Modeling Resource,
    /// "2D NACA 0012 Airfoil Validation - Spalart-Allmaras (SA) Model",
    /// <see href="https://turbmodels.larc.nasa.gov/naca0012_val_sa.html"/>
    /// Gitterkonvergierte Referenzwerte (CFL3D / FUN3D):
    /// - alpha = 0°:  CL_ref = 0.0000, CD_ref = 0.00819
    /// - alpha = 10°: CL_ref = 1.0909, CD_ref = 0.01231
    /// - alpha = 15°: CL_ref = 1.5461, CD_ref = 0.02124
    /// </para>
    /// </summary>
    public static class Naca2DTmrBenchmark
    {
        /// <summary>
        /// Offizielle NASA TMR-Referenzquelle.
        /// </summary>
        public const string SourceUrl = "https://turbmodels.larc.nasa.gov/naca0012_val_sa.html";

        /// <summary>
        /// Referenz-Reynoldszahl bezogen auf die Sehnenlänge.
        /// </summary>
        public const float ReferenceReynoldsNumber = 6.0e6f;

        /// <summary>
        /// Referenz-Machzahl.
        /// </summary>
        public const float ReferenceMachNumber = 0.15f;

        /// <summary>
        /// Tabelle der gitterkonvergierten NASA-TMR-Referenzwerte (SA-Modell).
        /// </summary>
        public static readonly IReadOnlyDictionary<int, (float ClRef, float CdRef)> ReferencePoints =
            new Dictionary<int, (float ClRef, float CdRef)>
            {
                { 0, (0.0000f, 0.00819f) },
                { 10, (1.0909f, 0.01231f) },
                { 15, (1.5461f, 0.02124f) }
            };

        /// <summary>
        /// Gleicht simulierte Beiwerte gegen die NASA-TMR-Referenz ab.
        /// Bei alpha = 0° wird CL absolut geprüft (|CL_sim - CL_ref| &lt;= 0.005), da CL_ref = 0.
        /// Bei alpha = 10° und 15° werden relative Fehler geprüft (CL &lt;= clToleranceRel, CD &lt;= cdToleranceRel).
        /// </summary>
        /// <param name="alphaDeg">Anstellwinkel in Grad (0, 10 oder 15).</param>
        /// <param name="clSimulated">Simulierter Auftriebsbeiwert CL.</param>
        /// <param name="cdSimulated">Simulierter Widerstandsbeiwert CD.</param>
        /// <param name="clToleranceRel">Erlaubter relativer Fehler für CL (Standard: 0.02 = 2 %).</param>
        /// <param name="cdToleranceRel">Erlaubter relativer Fehler für CD (Standard: 0.05 = 5 %).</param>
        public static TmrValidationResult EvaluateAgainstTmr(
            float alphaDeg,
            float clSimulated,
            float cdSimulated,
            float clToleranceRel = 0.02f,
            float cdToleranceRel = 0.05f)
        {
            int matchedAlpha = -1;
            foreach (int a in ReferencePoints.Keys)
            {
                if (MathF.Abs(alphaDeg - a) < 0.2f)
                {
                    matchedAlpha = a;
                    break;
                }
            }

            if (matchedAlpha < 0)
            {
                throw new ArgumentException(
                    $"Für alpha = {alphaDeg}° liegen keine NASA-TMR-Referenzdaten vor. "
                    + $"Verfügbare Stützpunkte sind: {string.Join("°, ", ReferencePoints.Keys)}° (Quelle: {SourceUrl}).",
                    nameof(alphaDeg));
            }

            (float clRef, float cdRef) = ReferencePoints[matchedAlpha];

            float clRelError;
            bool clPassed;

            if (matchedAlpha == 0)
            {
                // Bei alpha = 0 ist CL_ref = 0.0000; relatives Teilen durch 0 ist unzulässig.
                // Geprüft wird die absolute Abweichung mit Grenze 0.005 (0.5 % Maximalauftrieb).
                float clAbsError = MathF.Abs(clSimulated - clRef);
                clRelError = clAbsError;
                clPassed = clAbsError <= 0.005f;
            }
            else
            {
                clRelError = MathF.Abs(clSimulated - clRef) / MathF.Abs(clRef);
                clPassed = clRelError <= clToleranceRel;
            }

            float cdRelError = MathF.Abs(cdSimulated - cdRef) / cdRef;
            bool cdPassed = cdRelError <= cdToleranceRel;

            bool overallPassed = clPassed && cdPassed;

            string status = overallPassed ? "BESTANDEN" : "NICHT BESTANDEN";
            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "TMR-Validierung alpha={0:F1}° [{1}]: CL_ref={2:F4}, CL_sim={3:F4} (Fehler={4:P2}, Tol={5:P1}, {6}); " +
                "CD_ref={7:F5}, CD_sim={8:F5} (Fehler={9:P2}, Tol={10:P1}, {11})",
                alphaDeg, status,
                clRef, clSimulated, clRelError, (matchedAlpha == 0 ? 0.005f : clToleranceRel), clPassed ? "OK" : "ABWEICHUNG",
                cdRef, cdSimulated, cdRelError, cdToleranceRel, cdPassed ? "OK" : "ABWEICHUNG");

            return new TmrValidationResult(
                alphaDeg,
                clRef,
                cdRef,
                clSimulated,
                cdSimulated,
                clRelError,
                cdRelError,
                overallPassed,
                summary);
        }

        /// <summary>
        /// Erzeugt konfigurierte SU2-Solver-Optionen für den NASA-TMR-Validierungsfall:
        /// Luftströmung bei Re_c = 6·10⁶, Mach = 0.15, Spalart-Allmaras in 2D.
        /// </summary>
        /// <param name="alphaDeg">Anstellwinkel in Grad.</param>
        /// <param name="chordMeters">Sehnenlänge in Metern (Standard 1.0 m).</param>
        public static Su2SolverOptions CreateTmrFlowAndSolverOptions(float alphaDeg, float chordMeters = 1.0f)
        {
            float speedOfSound = 340.28f;
            float density = 1.225f;
            float velocity = ReferenceMachNumber * speedOfSound; // 0.15 * 340.28 = 51.042 m/s

            // Re_c = (rho * v * c) / mu = 6e6 -> mu = (rho * v * c) / 6e6
            float viscosity = (density * velocity * chordMeters) / ReferenceReynoldsNumber;

            return new Su2SolverOptions
            {
                Density = density,
                DynamicViscosity = viscosity,
                TurbulenceModel = "SA",
                SpeedOfSound = speedOfSound,
                FreestreamVelocity = velocity,
                Dimensions = 2,
                AngleOfAttackDeg = alphaDeg,
                ReferenceLength = chordMeters,
                ReferenceAreaMetric = "Chord",
                ResultMetrics = new Dictionary<string, string>
                {
                    { "Drag", "CD" },
                    { "Lift", "CL" }
                },
                ConvergenceField = "DRAG",
                HistoryOutput = new[] { "ITER", "RMS_RES", "AERO_COEFF" },
                CflNumber = 5.0f,
                CflAdapt = true,
                CflAdaptFactorDown = 0.5f,
                CflAdaptFactorUp = 1.2f,
                CflAdaptMin = 1.0f,
                CflAdaptMax = 50.0f
            };
        }
    }
}
