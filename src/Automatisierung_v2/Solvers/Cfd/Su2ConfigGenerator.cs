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
        public static void Generate(string configPath, string meshPath, float refArea, SimulationConfig config, Su2SolverOptions options)
        {
            string refAreaString = refArea.ToString(CultureInfo.InvariantCulture);

            float velocity = ResolveVelocity(config, options, out _);
            string velocityString = velocity.ToString(CultureInfo.InvariantCulture);

            string densityString = options.Density.ToString(CultureInfo.InvariantCulture);
            string viscosityString = options.DynamicViscosity.ToString(CultureInfo.InvariantCulture);
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

            string[] cfgContent = {
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
                $"FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )",
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
                $"INC_VELOCITY_INIT= ( {velocityString}, 0.0, 0.0 )",
                "%",
                "% =============================================================================",
                "% REFERENZWERTE",
                "% Bezugsgroessen fuer die Berechnung von Beiwerten (CD, CL, Momente).",
                "% =============================================================================",
                $"REF_AREA= {refAreaString}", 
                $"REF_LENGTH= {options.ReferenceLength.ToString(CultureInfo.InvariantCulture)}",
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
                "OUTPUT_FILES= (RESTART, PARAVIEW, SURFACE_PARAVIEW)",
                "VOLUME_OUTPUT= (COORDINATES, SOLUTION, PRIMITIVE, VELOCITY, PRESSURE, MACH, DENSITY, SKIN_FRICTION, Y_PLUS)",
            };

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
    }
}
