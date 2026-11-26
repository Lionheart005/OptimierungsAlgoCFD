using System;
using System.Collections.Generic;
using System.Numerics;
using System.Globalization;
using System.IO;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Generates SU2 configuration files for CFD simulations.
    /// </summary>
    public static class Su2ConfigGenerator
    {
        /// <summary>SU2 output group for time-averaged aerodynamic coefficients (<c>tavg[CD]</c>, etc.).</summary>
        public const string TimeAveragedHistoryGroup = "TAVG_AERO_COEFF";

        public static void Generate(
            string configPath,
            string meshPath,
            float refArea,
            SimulationConfig config,
            Su2SolverOptions options,
            FlowConditions? flow = null,
            float? refLength = null,
            string? steadyRestartFile = null,
            int? convStartIterOverride = null)
        {
            string refAreaString = refArea.ToString(CultureInfo.InvariantCulture);

            float velocity = flow?.Velocity ?? ResolveVelocity(config, options, out _);
            string velocityString = velocity.ToString(CultureInfo.InvariantCulture);

            float density = flow?.Density ?? options.Density;
            string densityString = density.ToString(CultureInfo.InvariantCulture);

            float viscosity = flow?.DynamicViscosity ?? options.DynamicViscosity;
            string viscosityString = viscosity.ToString(CultureInfo.InvariantCulture);

            float effectiveRefLength = refLength ?? options.ReferenceLength;
            string refLengthString = effectiveRefLength.ToString(CultureInfo.InvariantCulture);

            bool isRotatedOr2D = options.AngleOfAttackDeg != 0.0f || options.Dimensions == 2;
            string freestreamVelocityLine;
            string initVelocityLine;
            string? aoaLine = null;

            if (!isRotatedOr2D)
            {
                freestreamVelocityLine = $"FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )";
                initVelocityLine = $"INC_VELOCITY_INIT= ( {velocityString}, 0.0, 0.0 )";
            }
            else
            {
                var (vx, vy, vz) = ResolveVelocityVector(velocity, options.AngleOfAttackDeg, options.Dimensions);
                string vecStr = $"{vx.ToString(CultureInfo.InvariantCulture)}, {vy.ToString(CultureInfo.InvariantCulture)}, {vz.ToString(CultureInfo.InvariantCulture)}";
                freestreamVelocityLine = $"FREESTREAM_VELOCITY= ( {vecStr} )";
                initVelocityLine = $"INC_VELOCITY_INIT= ( {vecStr} )";
                aoaLine = $"AOA= {options.AngleOfAttackDeg.ToString(CultureInfo.InvariantCulture)}";
            }

            string cflAdaptParams = string.Join(", ", new[]
            {
                options.CflAdaptFactorDown,
                options.CflAdaptFactorUp,
                options.CflAdaptMin,
                options.CflAdaptMax
            }.Select(value => value.ToString(CultureInfo.InvariantCulture)));

            // Empty means "unconfigured" and falls back to the code default without warning;
            // an empty line would otherwise cause SU2 to abort on config parsing.
            string convergenceField = string.IsNullOrWhiteSpace(options.ConvergenceField)
                ? Su2SolverOptions.DefaultConvergenceField
                : options.ConvergenceField.Trim();

            string[] historyFields = options.HistoryOutput != null && options.HistoryOutput.Length > 0
                ? options.HistoryOutput
                : Su2SolverOptions.DefaultHistoryOutput;

            string historyOutput = string.Join(", ", historyFields.Select(field => field.Trim()));
            string turbModel = ResolveTurbulenceModel(options.TurbulenceModel);
            string slopeLimiter = ResolveSlopeLimiterFlow(options.SlopeLimiterFlow);
            float venkatLimiterCoeff = ValidateVenkatLimiterCoeff(options.VenkatLimiterCoeff);
            float? freestreamNuFactor = ValidateFreestreamNuFactor(options.FreestreamNuFactor);
            SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(options.SymmetryPlane);

            var cfgContent = new List<string> {
                "%",
                "% =============================================================================",
                "% SOLVER & PHYSICS",
                "% Incompressible RANS equations (mass + momentum, constant density & viscosity).",
                "% No energy equation (INC_ENERGY_EQUATION= NO).",
                $"% Turbulence model per KIND_TURB_MODEL ({turbModel}).",
                "% Steady-state solution via pseudo-time stepping (implicit Euler).",
                "% =============================================================================",
                "SOLVER= INC_RANS",
                $"KIND_TURB_MODEL= {turbModel}",
                "MATH_PROBLEM= DIRECT",
                "INC_DENSITY_MODEL= CONSTANT",
                "INC_ENERGY_EQUATION= NO",
                "%",
                "% =============================================================================",
                "% FLUID PROPERTIES",
                "% Constant density and dynamic viscosity.",
                "% Freestream turbulence unset -> SU2 7.5.1 defaults apply:",
                "% FREESTREAM_TURBULENCEINTENSITY= 0.05 and FREESTREAM_TURB2LAMVISCRATIO= 10.0",
                "% =============================================================================",
                freestreamVelocityLine,
                $"FREESTREAM_DENSITY= {densityString}",
                "VISCOSITY_MODEL= CONSTANT_VISCOSITY",
                $"MU_CONSTANT= {viscosityString}",
            };

            if (freestreamNuFactor.HasValue && turbModel == "SA")
            {
                cfgContent.Add($"FREESTREAM_NU_FACTOR= {freestreamNuFactor.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            cfgContent.AddRange(new[] {
                "%",
                "% =============================================================================",
                "% INITIALIZATION (initial field for steady run)",
                "% INC_VELOCITY_INIT / INC_DENSITY_INIT set freestream values everywhere.",
                "% Affects only the convergence path, not the converged solution.",
                "% =============================================================================",
                $"INC_DENSITY_INIT= {densityString}",
                initVelocityLine,
                "%",
                "% =============================================================================",
                "% REFERENCE VALUES",
                "% Reference quantities for aerodynamic coefficients (CD, CL, moments).",
                "% =============================================================================",
                $"REF_AREA= {refAreaString}", 
                $"REF_LENGTH= {refLengthString}",
                "REF_ORIGIN_MOMENT_X = 0.00",
                "REF_ORIGIN_MOMENT_Y = 0.00",
                "REF_ORIGIN_MOMENT_Z = 0.00",
                "%",
                "% =============================================================================",
                "% BOUNDARY CONDITIONS",
                "% Marker names must match the Physical Surface names from Gmsh exactly.",
                "% A typo causes SU2 to abort immediately.",
                "% - MARKER_FAR: farfield on the wind-tunnel shell. Flow direction follows from",
                "%   FREESTREAM_VELOCITY relative to surface normal: inlet at x-min, outlet at x-max.",
                "% - MARKER_HEATFLUX=(Wall, 0.0): no-slip wall condition (v=0) on adiabatic wall.",
                "%   With energy equation disabled, only no-slip applies (nu_tilde=0 for SA).",
                "% - MARKER_MONITORING: surface integrated for CD, CL, and moments.",
                "% - MARKER_PLOTTING: surface written to surface.vtu.",
                "% =============================================================================",
                "MARKER_HEATFLUX= ( Wall, 0.0 )",
                "MARKER_FAR= ( Farfield )",
                "MARKER_MONITORING= ( Wall )",
                "MARKER_PLOTTING= ( Wall )",
            });

            if (symAxis != SymmetryAxis.None)
            {
                cfgContent.Add("MARKER_SYM= ( Symmetry )");
            }

            cfgContent.AddRange(new[] {
                "%",
                "% =============================================================================",
                "% NUMERICAL ROBUSTNESS FOR RANS",
                "% FDS (Flux Difference Splitting) with MUSCL (2nd order) and Venkatakrishnan limiter.",
                "% Time discretization: implicit Euler with adaptive CFL control.",
                "% =============================================================================",
                "CONV_NUM_METHOD_FLOW= FDS",
                "MUSCL_FLOW= YES",
                $"SLOPE_LIMITER_FLOW= {slopeLimiter}",
            });

            if (slopeLimiter.StartsWith("VENKATAKRISHNAN", StringComparison.Ordinal))
            {
                cfgContent.Add($"VENKAT_LIMITER_COEFF= {venkatLimiterCoeff.ToString("G", CultureInfo.InvariantCulture)}");
            }

            cfgContent.AddRange(new[] {
                "TIME_DISCRE_FLOW= EULER_IMPLICIT",
                $"CFL_NUMBER= {options.CflNumber.ToString(CultureInfo.InvariantCulture)}",
                $"CFL_ADAPT= {(options.CflAdapt ? "YES" : "NO")}",
                $"CFL_ADAPT_PARAM= ( {cflAdaptParams} )",
                "%",
                "%",
                "% =============================================================================",
                "% TURBULENCE NUMERICS",
                $"% Turbulence equations ({turbModel}) discretized with SCALAR_UPWIND (1st order).",
                "% =============================================================================",
                "CONV_NUM_METHOD_TURB= SCALAR_UPWIND",
                "%",
                "% =============================================================================",
                "% STOPPING CRITERIA",
                "% Usually stops via Cauchy (CONV_FIELD over CONV_CAUCHY_ELEMS < CONV_CAUCHY_EPS)",
                "% or at ITER. CONV_RESIDUAL_MINVAL= -8 rarely triggers in 3D RANS (plateaus at -4..-5).",
                "% =============================================================================",
                $"ITER= {config.Su2MaxIterations}",          
                $"CONV_FIELD= {convergenceField}",
                $"CONV_CAUCHY_ELEMS= {config.CauchyElements}",
                $"CONV_CAUCHY_EPS= {config.CauchyTolerance}",
                $"CONV_STARTITER= {convStartIterOverride ?? 100}",
                "CONV_RESIDUAL_MINVAL= -8",
                $"HISTORY_OUTPUT= ({historyOutput})",
                "%",
                "% =============================================================================",
                "% MESH & OUTPUT FILES",
                "% =============================================================================",
                $"MESH_FILENAME= {Path.GetFileName(meshPath)}", 
                "MESH_FORMAT= SU2",
                "TABULAR_FORMAT= CSV",
                "OUTPUT_FILES= (RESTART, PARAVIEW, SURFACE_PARAVIEW, SURFACE_CSV)",
                "VOLUME_OUTPUT= (COORDINATES, SOLUTION, PRIMITIVE, VELOCITY, PRESSURE, MACH, DENSITY, SKIN_FRICTION, Y_PLUS)",
            });

            if (aoaLine != null)
            {
                int velIdx = cfgContent.IndexOf(freestreamVelocityLine);
                if (velIdx >= 0)
                    cfgContent.Insert(velIdx + 1, aoaLine);
                else
                    cfgContent.Add(aoaLine);
            }

            bool isUnsteady = options.TimeDomain != null && options.TimeDomain.Enabled;
            if (isUnsteady && options.TimeDomain != null)
            {
                float dt;
                if (options.TimeDomain.TimeStep > 0f)
                {
                    dt = options.TimeDomain.TimeStep;
                }
                else
                {
                    float l = effectiveRefLength > 0f ? effectiveRefLength : 0.05f;
                    float st = options.TimeDomain.StrouhalEstimate > 0f ? options.TimeDomain.StrouhalEstimate : 0.2f;
                    float u = velocity > 0f ? velocity : 1.0f;
                    float period = l / (st * u);
                    int steps = Math.Max(1, options.TimeDomain.StepsPerPeriod);
                    dt = period / steps;
                }

                // TIME_ITER and WINDOW_START_ITER are ABSOLUTE time indices: when restarting from index n,
                // executed steps only count starting at n.
                int restartIter = Math.Max(0, options.TimeDomain.RestartIteration);
                int timeSteps = options.TimeDomain.TimeIterations;
                int windowStart = restartIter + (int)MathF.Round(timeSteps * Math.Clamp(options.TimeDomain.AveragingStartFraction, 0f, 0.95f));

                cfgContent.Add("%");
                cfgContent.Add("% =============================================================================");
                cfgContent.Add("% TIME-ACCURATE MODE (URANS / DUAL-TIME-STEPPING)");
                cfgContent.Add("% =============================================================================");
                // SU2 7.5.1 aborts if ITER is present alongside TIME_ITER/INNER_ITER.
                int iterIdx = cfgContent.FindIndex(l => l.StartsWith("ITER=", StringComparison.Ordinal));
                if (iterIdx >= 0)
                    cfgContent[iterIdx] = $"% ITER= {config.Su2MaxIterations} omitted in time-accurate mode (see TIME_ITER/INNER_ITER)";

                // Include time-averaged coefficients (tavg[CD], etc. from WINDOW_START_ITER onward).
                int histIdx = cfgContent.FindIndex(l => l.StartsWith("HISTORY_OUTPUT=", StringComparison.Ordinal));
                if (histIdx >= 0 && !historyFields.Any(f => f.Trim().Equals(TimeAveragedHistoryGroup, StringComparison.OrdinalIgnoreCase)))
                    cfgContent[histIdx] = $"HISTORY_OUTPUT= ({historyOutput}, {TimeAveragedHistoryGroup})";
                cfgContent.Add("TIME_DOMAIN= YES");
                cfgContent.Add("TIME_MARCHING= DUAL_TIME_STEPPING-2ND_ORDER");
                cfgContent.Add($"TIME_STEP= {dt.ToString("G6", CultureInfo.InvariantCulture)}");
                cfgContent.Add($"TIME_ITER= {restartIter + timeSteps}");
                cfgContent.Add($"INNER_ITER= {options.TimeDomain.InnerIterations}");
                cfgContent.Add($"WINDOW_START_ITER= {windowStart}");
                cfgContent.Add("WINDOW_FUNCTION= HANN_SQUARE");
                // Without this line, SU2 writes a restart file at every time step (~6 GB for 1000 steps in 2D).
                // Final output is still written at the end.
                cfgContent.Add("OUTPUT_WRT_FREQ= 100000");

                if (restartIter > 0)
                {
                    // Restart from steady solution: restart_00000.dat and restart_00001.dat must exist in workingDirectory.
                    cfgContent.Add("RESTART_SOL= YES");
                    cfgContent.Add($"RESTART_ITER= {restartIter}");
                    cfgContent.Add("SOLUTION_FILENAME= restart.dat");
                }
            }
            else if (!string.IsNullOrWhiteSpace(steadyRestartFile))
            {
                cfgContent.Add("%");
                cfgContent.Add("% =============================================================================");
                cfgContent.Add("% STEADY RESTART");
                cfgContent.Add("% =============================================================================");
                cfgContent.Add("RESTART_SOL= YES");
                cfgContent.Add($"SOLUTION_FILENAME= {steadyRestartFile.Trim()}");
            }

            File.WriteAllLines(configPath, cfgContent);
        }

        /// <summary>
        /// Validates and normalizes <see cref="Su2SolverOptions.TurbulenceModel"/> (<c>"SA"</c> or <c>"SST"</c>).
        /// </summary>
        public static string ResolveTurbulenceModel(string? turbulenceModel)
        {
            if (!string.IsNullOrWhiteSpace(turbulenceModel))
            {
                string trimmed = turbulenceModel.Trim().ToUpperInvariant();
                if (trimmed == "SA" || trimmed == "SST")
                    return trimmed;
            }

            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} Unknown TurbulenceModel '{turbulenceModel}'. Allowed values are 'SA' or 'SST'.");
        }

        private static readonly HashSet<string> AllowedSlopeLimiters = new(StringComparer.Ordinal)
        {
            "NONE",
            "VENKATAKRISHNAN",
            "VENKATAKRISHNAN_WANG",
            "BARTH_JESPERSEN",
            "VAN_ALBADA_EDGE"
        };

        /// <summary>
        /// Validates and normalizes <see cref="Su2SolverOptions.SlopeLimiterFlow"/>.
        /// Empty or whitespace falls back to <c>"VENKATAKRISHNAN"</c>.
        /// </summary>
        public static string ResolveSlopeLimiterFlow(string? slopeLimiterFlow)
        {
            if (string.IsNullOrWhiteSpace(slopeLimiterFlow))
                return "VENKATAKRISHNAN";

            string normalized = slopeLimiterFlow.Trim().ToUpperInvariant();
            if (AllowedSlopeLimiters.Contains(normalized))
                return normalized;

            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} Unknown SlopeLimiterFlow '{slopeLimiterFlow}'. "
                + "Allowed values are 'NONE', 'VENKATAKRISHNAN', 'VENKATAKRISHNAN_WANG', 'BARTH_JESPERSEN', or 'VAN_ALBADA_EDGE'.");
        }

        private static float ValidateVenkatLimiterCoeff(float venkatLimiterCoeff)
        {
            if (!float.IsFinite(venkatLimiterCoeff) || venkatLimiterCoeff <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} VenkatLimiterCoeff must be a finite positive number (got {venkatLimiterCoeff.ToString(CultureInfo.InvariantCulture)}).");
            }

            return venkatLimiterCoeff;
        }

        private static float? ValidateFreestreamNuFactor(float? freestreamNuFactor)
        {
            if (!freestreamNuFactor.HasValue)
                return null;

            float value = freestreamNuFactor.Value;
            if (!float.IsFinite(value) || value <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} FreestreamNuFactor must be a finite positive number (got {value.ToString(CultureInfo.InvariantCulture)}).");
            }

            return value;
        }

        /// <summary>
        /// Freestream velocity in m/s: <see cref="Su2SolverOptions.FreestreamVelocity"/> when set,
        /// otherwise <c>MachNumber * SpeedOfSound</c> for backward compatibility.
        /// </summary>
        /// <param name="source">Human-readable log label indicating where the velocity originated.</param>
        public static float ResolveVelocity(SimulationConfig config, Su2SolverOptions options, out string source)
        {
            if (options.FreestreamVelocity > 0f)
            {
                source = "FreestreamVelocity from su2.json (MachNumber ignored)";
                return options.FreestreamVelocity;
            }

            source = $"MachNumber {config.MachNumber.ToString(CultureInfo.InvariantCulture)} * SpeedOfSound "
                     + $"{options.SpeedOfSound.ToString(CultureInfo.InvariantCulture)} m/s — "
                     + "prefer setting 'FreestreamVelocity' directly in su2.json";
            return config.MachNumber * options.SpeedOfSound;
        }

        /// <summary>
        /// Decomposes a scalar velocity at a given angle of attack into vector components.
        /// In 2D lift is along +y: (v*cos(a), v*sin(a), 0).
        /// In 3D lift is along +z: (v*cos(a), 0, v*sin(a)).
        /// Tiny magnitudes (|v| &lt; 1e-7) are clamped to 0.0.
        /// </summary>
        public static (float Vx, float Vy, float Vz) ResolveVelocityVector(float speed, float angleOfAttackDeg, int dimensions)
        {
            if (angleOfAttackDeg == 0.0f)
            {
                return (speed, 0.0f, 0.0f);
            }

            float rad = angleOfAttackDeg * (MathF.PI / 180.0f);
            float vx = speed * MathF.Cos(rad);
            float vLift = speed * MathF.Sin(rad);

            if (MathF.Abs(vx) < 1e-7f) vx = 0.0f;
            if (MathF.Abs(vLift) < 1e-7f) vLift = 0.0f;

            if (dimensions == 2)
            {
                return (vx, vLift, 0.0f);
            }

            return (vx, 0.0f, vLift);
        }

        /// <summary>
        /// Projects an aerodynamic force from body axes (Fx, Fy) at angle of attack alpha
        /// into wind axes (drag D, lift L).
        /// D = Fx*cos(a) + Fy*sin(a), L = -Fx*sin(a) + Fy*cos(a).
        /// </summary>
        public static Vector2 ProjectForceToWindAxes(Vector2 bodyForce, float angleOfAttackDeg)
        {
            float rad = angleOfAttackDeg * (MathF.PI / 180.0f);
            float cosA = MathF.Cos(rad);
            float sinA = MathF.Sin(rad);

            float drag = (bodyForce.X * cosA) + (bodyForce.Y * sinA);
            float lift = (-bodyForce.X * sinA) + (bodyForce.Y * cosA);

            if (MathF.Abs(drag) < 1e-7f) drag = 0.0f;
            if (MathF.Abs(lift) < 1e-7f) lift = 0.0f;

            return new Vector2(drag, lift);
        }
    }
}
