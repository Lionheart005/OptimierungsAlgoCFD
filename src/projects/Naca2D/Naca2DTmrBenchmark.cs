using System;
using System.Collections.Generic;
using System.Globalization;

using FluidForge.Solvers.Cfd;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Result of a validation check against NASA TMR reference data.
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
    /// NASA Langley Research Center - Turbulence Modeling Resource (TMR) validation data.
    /// Reference case: 2D NACA 0012 airfoil at Re_c = 6e6, Mach = 0.15, Spalart-Allmaras (SA)
    /// turbulence model.
    ///
    /// <para>
    /// Source: NASA Langley Research Center, Turbulence Modeling Resource,
    /// "2D NACA 0012 Airfoil Validation - Spalart-Allmaras (SA) Model",
    /// <see href="https://turbmodels.larc.nasa.gov/naca0012_val_sa.html"/>
    /// Grid-converged reference values (CFL3D / FUN3D):
    /// - alpha = 0 deg:  CL_ref = 0.0000, CD_ref = 0.00819
    /// - alpha = 10 deg: CL_ref = 1.0909, CD_ref = 0.01231
    /// - alpha = 15 deg: CL_ref = 1.5461, CD_ref = 0.02124
    /// </para>
    /// </summary>
    public static class Naca2DTmrBenchmark
    {
        /// <summary>
        /// Official NASA TMR reference URL.
        /// </summary>
        public const string SourceUrl = "https://turbmodels.larc.nasa.gov/naca0012_val_sa.html";

        /// <summary>
        /// Reference Reynolds number based on chord length.
        /// </summary>
        public const float ReferenceReynoldsNumber = 6.0e6f;

        /// <summary>
        /// Reference Mach number.
        /// </summary>
        public const float ReferenceMachNumber = 0.15f;

        /// <summary>
        /// Table of grid-converged NASA TMR reference values (SA model).
        /// </summary>
        public static readonly IReadOnlyDictionary<int, (float ClRef, float CdRef)> ReferencePoints =
            new Dictionary<int, (float ClRef, float CdRef)>
            {
                { 0, (0.0000f, 0.00819f) },
                { 10, (1.0909f, 0.01231f) },
                { 15, (1.5461f, 0.02124f) }
            };

        /// <summary>
        /// Compares simulated coefficients against NASA TMR reference data.
        /// At alpha = 0 deg, CL is checked absolutely (|CL_sim - CL_ref| &lt;= 0.005) since CL_ref = 0.
        /// At alpha = 10 deg and 15 deg, relative errors are checked (CL &lt;= clToleranceRel, CD &lt;= cdToleranceRel).
        /// </summary>
        /// <param name="alphaDeg">Angle of attack in degrees (0, 10, or 15).</param>
        /// <param name="clSimulated">Simulated lift coefficient CL.</param>
        /// <param name="cdSimulated">Simulated drag coefficient CD.</param>
        /// <param name="clToleranceRel">Allowed relative error for CL (default: 0.02 = 2%).</param>
        /// <param name="cdToleranceRel">Allowed relative error for CD (default: 0.05 = 5%).</param>
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
                    $"No NASA TMR reference data available for alpha = {alphaDeg} deg. "
                    + $"Available reference angles: {string.Join(" deg, ", ReferencePoints.Keys)} deg (source: {SourceUrl}).",
                    nameof(alphaDeg));
            }

            (float clRef, float cdRef) = ReferencePoints[matchedAlpha];

            float clRelError;
            bool clPassed;

            if (matchedAlpha == 0)
            {
                // At alpha = 0, CL_ref = 0.0000; relative division by zero is invalid.
                // Check absolute deviation against 0.005 (0.5% of max lift).
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

            string status = overallPassed ? "PASSED" : "FAILED";
            string summary = string.Format(
                CultureInfo.InvariantCulture,
                "TMR Validation alpha={0:F1} deg [{1}]: CL_ref={2:F4}, CL_sim={3:F4} (Error={4:P2}, Tol={5:P1}, {6}); " +
                "CD_ref={7:F5}, CD_sim={8:F5} (Error={9:P2}, Tol={10:P1}, {11})",
                alphaDeg, status,
                clRef, clSimulated, clRelError, (matchedAlpha == 0 ? 0.005f : clToleranceRel), clPassed ? "OK" : "DEVIATION",
                cdRef, cdSimulated, cdRelError, cdToleranceRel, cdPassed ? "OK" : "DEVIATION");

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
        /// Creates configured SU2 solver options for the NASA TMR validation case:
        /// airflow at Re_c = 6e6, Mach = 0.15, Spalart-Allmaras in 2D.
        /// </summary>
        /// <param name="alphaDeg">Angle of attack in degrees.</param>
        /// <param name="chordMeters">Chord length in meters (default 1.0 m).</param>
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
