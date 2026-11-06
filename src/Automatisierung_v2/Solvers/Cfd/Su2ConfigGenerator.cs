using System;
using System.Collections.Generic;
using System.Numerics;
using System.Globalization;
using System.IO;
using System.Linq;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// Generiert SU2 Konfigurationsdateien für CFD-Simulationen.
    /// Extrahiert aus FluidDynamicsAnalyzer.GenerateSu2Config.
    /// </summary>
    public static class Su2ConfigGenerator
    {
        /// <summary>SU2-Ausgabegruppe der zeitgemittelten Beiwerte (Spalten <c>tavg[CD]</c> usw.).</summary>
        public const string TimeAveragedHistoryGroup = "TAVG_AERO_COEFF";

        public static void Generate(
            string configPath,
            string meshPath,
            float refArea,
            SimulationConfig config,
            Su2SolverOptions options,
            FlowConditions? flow = null,
            float? refLength = null)
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

            // Beides war vor TODO-26 fest verdrahtet. Leer bedeutet „nicht konfiguriert“ und
            // fällt ohne Warnung auf den Code-Standard zurück — dasselbe Muster wie bei
            // TunnelShape (TODO-11) und der Algorithmuswahl (TODO-10). Eine leere Zeile
            // würde SU2 beim Einlesen sonst um die Ohren fliegen.
            string convergenceField = string.IsNullOrWhiteSpace(options.ConvergenceField)
                ? Su2SolverOptions.DefaultConvergenceField
                : options.ConvergenceField.Trim();

            string[] historyFields = options.HistoryOutput != null && options.HistoryOutput.Length > 0
                ? options.HistoryOutput
                : Su2SolverOptions.DefaultHistoryOutput;

            string historyOutput = string.Join(", ", historyFields.Select(field => field.Trim()));

            var cfgContent = new List<string> {
                "%",
                "% =============================================================================",
                "% SOLVER & PHYSIK",
                "% Inkompressible RANS-Gleichungen (Masse + Impuls, konstante Dichte & Viskositaet).",
                "% Keine Energiegleichung (INC_ENERGY_EQUATION= NO).",
                "% Turbulenzmodell laut KIND_TURB_MODEL (Standard SA: 1 Gleichung, Transport fuer nu_tilde).",
                "% Stationaere Rechnung ueber Pseudo-Zeitschritte (Euler implizit).",
                "% =============================================================================",
                "SOLVER= INC_RANS",
                $"KIND_TURB_MODEL= {options.TurbulenceModel}",
                "MATH_PROBLEM= DIRECT",
                "INC_DENSITY_MODEL= CONSTANT",
                "INC_ENERGY_EQUATION= NO",
                "%",
                "% =============================================================================",
                "% FLUIDEIGENSCHAFTEN",
                "% Konstante Dichte und dynamische Viskositaet.",
                "% Freistromturbulenz ist nicht gesetzt -> SU2 7.5.1 Standardwerte gelten:",
                "% FREESTREAM_TURBULENCEINTENSITY= 0.05 und FREESTREAM_TURB2LAMVISCRATIO= 10.0",
                "% =============================================================================",
                freestreamVelocityLine,
                $"FREESTREAM_DENSITY= {densityString}",
                "VISCOSITY_MODEL= CONSTANT_VISCOSITY",
                $"MU_CONSTANT= {viscosityString}",
                "%",
                "% =============================================================================",
                "% INITIALISIERUNG (Startfeld fuer stationaere Rechnung)",
                "% INC_VELOCITY_INIT / INC_DENSITY_INIT setzen ueberall Freistromwerte.",
                "% Beeinflusst nur den Weg zur Loesung, nicht die Loesung selbst.",
                "% =============================================================================",
                $"INC_DENSITY_INIT= {densityString}",
                initVelocityLine,
                "%",
                "% =============================================================================",
                "% REFERENZWERTE",
                "% Bezugsgroessen fuer die Berechnung von Beiwerten (CD, CL, Momente).",
                "% =============================================================================",
                $"REF_AREA= {refAreaString}", 
                $"REF_LENGTH= {refLengthString}",
                "REF_ORIGIN_MOMENT_X = 0.00",
                "REF_ORIGIN_MOMENT_Y = 0.00",
                "REF_ORIGIN_MOMENT_Z = 0.00",
                "%",
                "% =============================================================================",
                "% RANDBEDINGUNGEN (BOUNDARY CONDITIONS)",
                "% Markernamen muessen exakt den Physical Surface Namen aus Gmsh entsprechen.",
                "% Ein Tippfehler fuehrt zum sofortigen Abbruch von SU2.",
                "% - MARKER_FAR: Fernfeld an der Windkanalhuelle. Stroemungsrichtung folgt allein",
                "%   aus FREESTREAM_VELOCITY relativ zur Flaechennormale: Einlass bei x-min,",
                "%   Auslass bei x-max. Alternative getrennte Raender (MARKER_INLET/OUTLET/SYM)",
                "%   wuerden sechs einzeln benannte Windkanalflaechen erfordern (nicht umgesetzt).",
                "% - MARKER_HEATFLUX=(Wall, 0.0): no-slip Haftbedingung (v=0) an adiabater Wand.",
                "%   Da Energiegleichung aus ist, wirkt nur no-slip. Fuer SA gilt nu_tilde=0 an Wand.",
                "% - MARKER_MONITORING: Oberflaeche, ueber die CD, CL und Momente integriert werden.",
                "% - MARKER_PLOTTING: Oberflaeche, die in surface.vtu geschrieben wird.",
                "% =============================================================================",
                "MARKER_HEATFLUX= ( Wall, 0.0 )",
                "MARKER_FAR= ( Farfield )",        
                "MARKER_MONITORING= ( Wall )",
                "MARKER_PLOTTING= ( Wall )",
                "%",
                "% =============================================================================",
                "% NUMERISCHE ROBUSTHEIT FUER RANS",
                "% FDS (Flux Difference Splitting) mit MUSCL (2. Ordnung) und Venkatakrishnan-Limiter.",
                "% Zeitdiskretisierung: Euler implizit mit adaptiver CFL-Steuerung.",
                "% =============================================================================",
                "CONV_NUM_METHOD_FLOW= FDS",               
                "MUSCL_FLOW= YES",                         
                "SLOPE_LIMITER_FLOW= VENKATAKRISHNAN",     
                "VENKAT_LIMITER_COEFF= 0.05",              
                "TIME_DISCRE_FLOW= EULER_IMPLICIT",
                $"CFL_NUMBER= {options.CflNumber.ToString(CultureInfo.InvariantCulture)}",
                $"CFL_ADAPT= {(options.CflAdapt ? "YES" : "NO")}",
                $"CFL_ADAPT_PARAM= ( {cflAdaptParams} )",
                "%",
                "%",
                "% =============================================================================",
                "% TURBULENZ NUMERIK",
                "% Diskretisierung der Turbulenzgleichung (SA) mit SCALAR_UPWIND (1. Ordnung).",
                "% =============================================================================",
                "CONV_NUM_METHOD_TURB= SCALAR_UPWIND",
                "%",
                "% =============================================================================",
                "% ABBRUCHKRITERIEN",
                "% Abbruch nach ITER Iterationen, wenn CONV_FIELD ueber CONV_CAUCHY_ELEMS",
                "% um weniger als CONV_CAUCHY_EPS schwankt oder Residuum unter 10^-8 faellt.",
                "% =============================================================================",
                $"ITER= {config.Su2MaxIterations}",          
                $"CONV_FIELD= {convergenceField}",
                $"CONV_CAUCHY_ELEMS= {config.CauchyElements}",
                $"CONV_CAUCHY_EPS= {config.CauchyTolerance}",
                "CONV_STARTITER= 100",
                "CONV_RESIDUAL_MINVAL= -8",
                $"HISTORY_OUTPUT= ({historyOutput})",
                "%",
                "% =============================================================================",
                "% NETZ & AUSGABEDATEIEN",
                "% =============================================================================",
                $"MESH_FILENAME= {Path.GetFileName(meshPath)}", 
                "MESH_FORMAT= SU2",
                "TABULAR_FORMAT= CSV",
                "OUTPUT_FILES= (RESTART, PARAVIEW, SURFACE_PARAVIEW, SURFACE_CSV)",
                "VOLUME_OUTPUT= (COORDINATES, SOLUTION, PRIMITIVE, VELOCITY, PRESSURE, MACH, DENSITY, SKIN_FRICTION, Y_PLUS)",
            };

            if (aoaLine != null)
            {
                int velIdx = cfgContent.IndexOf(freestreamVelocityLine);
                if (velIdx >= 0)
                    cfgContent.Insert(velIdx + 1, aoaLine);
                else
                    cfgContent.Add(aoaLine);
            }

            if (options.TimeDomain != null && options.TimeDomain.Enabled)
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

                // TIME_ITER und WINDOW_START_ITER sind ABSOLUTE Zeitindizes: bei Neustart ab Index n zählen
                // die gerechneten Schritte erst ab n (Servertest 06.11.2026: TIME_ITER 60 und Neustart bei 2 = 58 Schritte).
                int restartIter = Math.Max(0, options.TimeDomain.RestartIteration);
                int timeSteps = options.TimeDomain.TimeIterations;
                int windowStart = restartIter + (int)MathF.Round(timeSteps * Math.Clamp(options.TimeDomain.AveragingStartFraction, 0f, 0.95f));

                cfgContent.Add("%");
                cfgContent.Add("% =============================================================================");
                cfgContent.Add("% ZEITGENAUER MODUS (URANS / DUAL-TIME-STEPPING, TODO-37)");
                cfgContent.Add("% =============================================================================");
                // SU2 7.5.1 bricht ab, wenn neben TIME_ITER/INNER_ITER noch ITER steht
                // ("ITER must not be used when running multizone and/or unsteady problems",
                // Servertest 06.11.2026).
                int iterIdx = cfgContent.FindIndex(l => l.StartsWith("ITER=", StringComparison.Ordinal));
                if (iterIdx >= 0)
                    cfgContent[iterIdx] = $"% ITER= {config.Su2MaxIterations} entfällt im zeitgenauen Modus (siehe TIME_ITER/INNER_ITER)";

                // Zeitgemittelte Beiwerte (tavg[CD] usw. ab WINDOW_START_ITER) mitschreiben. Ohne sie
                // läse der Solver nur den Momentanwert des letzten Zeitschritts.
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
                // Ohne diese Zeile schreibt SU2 in jedem Zeitschritt eine Restart-Datei (6 MB je Schritt in 2D,
                // bei 1000 Schritten 6 GB). Die Endausgabe schreibt es trotzdem (TODO-W7).
                cfgContent.Add("OUTPUT_WRT_FREQ= 100000");

                if (restartIter > 0)
                {
                    // Neustart aus der stationären Lösung: restart_00000.dat und restart_00001.dat müssen
                    // im Arbeitsordner liegen (legt Program.RunRecheck an).
                    cfgContent.Add("RESTART_SOL= YES");
                    cfgContent.Add($"RESTART_ITER= {restartIter}");
                    cfgContent.Add("SOLUTION_FILENAME= restart.dat");
                }
            }

            File.WriteAllLines(configPath, cfgContent);
        }

        /// <summary>
        /// Die Anströmgeschwindigkeit in m/s (TODO-38): <see cref="Su2SolverOptions.FreestreamVelocity"/>,
        /// wenn gesetzt — sonst wie vor TODO-38 <c>MachNumber × SpeedOfSound</c>, damit Projekte
        /// ohne den neuen Schlüssel unverändert rechnen.
        /// </summary>
        /// <param name="source">Klartext für das Log: woher die Zahl kommt.</param>
        public static float ResolveVelocity(SimulationConfig config, Su2SolverOptions options, out string source)
        {
            if (options.FreestreamVelocity > 0f)
            {
                source = "FreestreamVelocity aus der su2.json (MachNumber wird dafür nicht verwendet)";
                return options.FreestreamVelocity;
            }

            source = $"MachNumber {config.MachNumber.ToString(CultureInfo.InvariantCulture)} × SpeedOfSound "
                     + $"{options.SpeedOfSound.ToString(CultureInfo.InvariantCulture)} m/s — "
                     + "besser direkt 'FreestreamVelocity' in der su2.json setzen";
            return config.MachNumber * options.SpeedOfSound;
        }

        /// <summary>
        /// Warnt, wenn in einer Konfigurationsdatei ausdrücklich eine Mach-Zahl gesetzt wurde,
        /// diese aber wegen einer gesetzten FreestreamVelocity in solvers/su2.json ignoriert wird (TODO-38).
        /// </summary>
        public static bool WarnIfMachNumberOverridden(
            ConfigLoadReport? simulationReport,
            Su2SolverOptions su2Options,
            TextWriter? output = null)
        {
            if (su2Options == null || su2Options.FreestreamVelocity <= 0f)
                return false;

            if (simulationReport != null && simulationReport.Origins.TryGetValue("MachNumber", out string? originFile))
            {
                TextWriter writer = output ?? Console.Out;
                string velocityString = su2Options.FreestreamVelocity.ToString(CultureInfo.InvariantCulture);
                writer.WriteLine(
                    $"[WARNUNG] 'MachNumber' in {originFile} ist wirkungslos, weil in solvers/su2.json 'FreestreamVelocity' ({velocityString} m/s) gesetzt ist.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Zerlegt eine skalare Geschwindigkeit bei gegebenem Anstellwinkel in ihre
        /// Vektorkomponenten (TODO-46).
        /// In 2D liegt der Auftrieb auf +y: (v*cos(a), v*sin(a), 0).
        /// In 3D liegt der Auftrieb auf +z: (v*cos(a), 0, v*sin(a)).
        /// Winzige Beträge (|v| &lt; 1e-7) werden auf 0.0 gerundet.
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
        /// Projiziert eine aerodynamische Kraft aus Körperachsen (Fx, Fy) bei Anströmung
        /// unter Anstellwinkel alpha in Windachsen (Widerstand D, Auftrieb L) (TODO-46).
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
