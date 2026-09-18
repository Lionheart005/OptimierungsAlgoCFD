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

            // Umrechnung: Mach zu Geschwindigkeit in m/s (Schallgeschwindigkeit ca. 343.2 m/s bei 20°C)
            float velocity = config.MachNumber * options.SpeedOfSound;
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

            string[] cfgContent = {
                "%",
                "% --- SOLVER & PHYSIK ---",
                "SOLVER= INC_RANS",
                $"KIND_TURB_MODEL= {options.TurbulenceModel}",
                "MATH_PROBLEM= DIRECT",
                "INC_DENSITY_MODEL= CONSTANT",
                "INC_ENERGY_EQUATION= NO",
                "%",
                "% --- FLUID EIGENSCHAFTEN (Luft auf Meereshöhe) ---",
                $"FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )",
                $"FREESTREAM_DENSITY= {densityString}",
                "VISCOSITY_MODEL= CONSTANT_VISCOSITY",
                $"MU_CONSTANT= {viscosityString}",
                "%",
                "% --- INITIALISIERUNG (Zwingend für INC_RANS in SU2 7.5.1) ---",
                $"INC_DENSITY_INIT= {densityString}",
                $"INC_VELOCITY_INIT= ( {velocityString}, 0.0, 0.0 )",
                "%",
                "% --- REFERENZWERTE ---",
                $"REF_AREA= {refAreaString}", 
                $"REF_LENGTH= {options.ReferenceLength.ToString(CultureInfo.InvariantCulture)}",
                "REF_ORIGIN_MOMENT_X = 0.00",
                "REF_ORIGIN_MOMENT_Y = 0.00",
                "REF_ORIGIN_MOMENT_Z = 0.00",
                "%",
                "% --- RANDBEDINGUNGEN (BOUNDARY CONDITIONS) ---",
                "MARKER_HEATFLUX= ( Wall, 0.0 )",
                "MARKER_FAR= ( Farfield )",        
                "MARKER_MONITORING= ( Wall )",
                "MARKER_PLOTTING= ( Wall )",
                "%",
                "% --- NUMERISCHE ROBUSTHEIT FÜR RANS ---",
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
                "% --- TURBULENZ NUMERIK ---",
                "CONV_NUM_METHOD_TURB= SCALAR_UPWIND",
                "% --- ABBRUCHKRITERIEN ---",
                $"ITER= {config.Su2MaxIterations}",          
                "CONV_FIELD= DRAG",
                $"CONV_CAUCHY_ELEMS= {config.CauchyElements}",
                $"CONV_CAUCHY_EPS= {config.CauchyTolerance}",
                "CONV_STARTITER= 100",
                "CONV_RESIDUAL_MINVAL= -8",
                "HISTORY_OUTPUT= (ITER, RMS_RES, AERO_COEFF)",    
                "%",            
                $"MESH_FILENAME= {Path.GetFileName(meshPath)}", 
                "MESH_FORMAT= SU2",
                "TABULAR_FORMAT= CSV",
                "OUTPUT_FILES= (RESTART, PARAVIEW, SURFACE_PARAVIEW)",
                "VOLUME_OUTPUT= (COORDINATES, SOLUTION, PRIMITIVE, VELOCITY, PRESSURE, MACH, DENSITY, SKIN_FRICTION, Y_PLUS)",
            };

            File.WriteAllLines(configPath, cfgContent);
        }
    }
}
