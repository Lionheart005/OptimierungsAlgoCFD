using System.Globalization;
using System.IO;

namespace MyPicoGkProject
{
    /// <summary>
    /// Generiert SU2 Konfigurationsdateien für CFD-Simulationen.
    /// Extrahiert aus FluidDynamicsAnalyzer.GenerateSu2Config.
    /// </summary>
    public static class Su2ConfigGenerator
    {
        public static void Generate(string configPath, string meshPath, float refArea, SimulationConfig config)
        {
            string refAreaString = refArea.ToString(CultureInfo.InvariantCulture);
            
            // Umrechnung: Mach zu Geschwindigkeit in m/s (Schallgeschwindigkeit ca. 343.2 m/s bei 20°C)
            float velocity = config.MachNumber * 343.2f;
            string velocityString = velocity.ToString(CultureInfo.InvariantCulture);

            string[] cfgContent = {
                "%",
                "% --- SOLVER & PHYSIK ---",
                "SOLVER= INC_RANS",
                "KIND_TURB_MODEL= SA",
                "MATH_PROBLEM= DIRECT",
                "INC_DENSITY_MODEL= CONSTANT",
                "INC_ENERGY_EQUATION= NO",
                "%",
                "% --- FLUID EIGENSCHAFTEN (Luft auf Meereshöhe) ---",
                $"FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )",
                "FREESTREAM_DENSITY= 1025.0",
                "VISCOSITY_MODEL= CONSTANT_VISCOSITY",
                "MU_CONSTANT= 1.001e-3",
                "%",
                "% --- INITIALISIERUNG (Zwingend für INC_RANS in SU2 7.5.1) ---",
                "INC_DENSITY_INIT= 1025.0",
                $"INC_VELOCITY_INIT= ( {velocityString}, 0.0, 0.0 )",
                "%",
                "% --- REFERENZWERTE ---",
                $"REF_AREA= {refAreaString}", 
                "REF_LENGTH= 0.01",
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
                "CFL_NUMBER= 5.0",                         
                "CFL_ADAPT= YES",
                "CFL_ADAPT_PARAM= ( 0.5, 1.2, 1.0, 50.0 )",
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
