using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace MyPicoGkProject
{
    public class FluidDynamicsAnalyzer
    {
       public float AnalyzeMesh(string meshPath, Dictionary<string, float> parameters, SimulationData data, int generation, int variant, float realAreaMm2)
        {
            float refArea = realAreaMm2 * 0.000001f;

            Console.WriteLine($"    -> Strömungsanalyse läuft für: {Path.GetFileName(meshPath)}...");
            
            string configPath = Path.Combine(data.BaseDirectory, "current_config.cfg");
            string historyPath = Path.Combine(data.BaseDirectory, "history.csv");


            if (File.Exists(historyPath)) File.Delete(historyPath);


            GenerateSu2Config(configPath, meshPath, refArea, data);

            try
            {
                Process su2Process = new Process();
                su2Process.StartInfo.FileName = data.Settings.Paths.MpiRun;
                su2Process.StartInfo.Arguments = $"-n {data.Settings.MpiCores} {data.Settings.Paths.Su2} current_config.cfg"; 
                su2Process.StartInfo.WorkingDirectory = data.BaseDirectory; 
                su2Process.StartInfo.RedirectStandardOutput = true;
                su2Process.StartInfo.RedirectStandardError = true;
                
                System.Text.StringBuilder su2Output = new System.Text.StringBuilder();
                object outputLock = new object(); 

                su2Process.OutputDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) lock(outputLock) { su2Output.AppendLine(e.Data); }
                };
                su2Process.ErrorDataReceived += (s, e) => 
                { 
                    if (!string.IsNullOrEmpty(e.Data)) lock(outputLock) { su2Output.AppendLine(e.Data); }
                };

                su2Process.Start();
                su2Process.BeginOutputReadLine();
                su2Process.BeginErrorReadLine();
                su2Process.WaitForExit();

                // --- DER NEUE, KUGELSICHERE CHECK ---
                // Wir ignorieren ExitCodes und Konsolen-Spam. 
                // Wenn SU2 eine history.csv mit mehr als 5 Zeilen (Header + Iterationen) 
                // geschrieben hat, lassen wir die Methode ReadDragFromHistory entscheiden!
                bool isSu2Successful = false;
                
                if (File.Exists(historyPath))
                {
                    string[] lines = File.ReadAllLines(historyPath);
                    if (lines.Length >= 5) 
                    {
                        isSu2Successful = true;
                    }
                }
                // --- ENDE DES CHECKS ---

                if (!isSu2Successful)
                {
                    string errorLogDirectory = Path.Combine(data.BaseDirectory, "FehlerLogs");
                    Directory.CreateDirectory(errorLogDirectory);

                    string modelName = Path.GetFileNameWithoutExtension(meshPath);
                    string errorLogPath = Path.Combine(errorLogDirectory, $"SU2_Error_{modelName}.txt");
                    File.WriteAllText(errorLogPath, su2Output.Length == 0 ? "Kein Konsolen-Output von SU2 abgefangen." : su2Output.ToString());
                    Console.WriteLine($"\n[WARNUNG] CFD-Simulation für {modelName} abgebrochen.");
                    Console.WriteLine($"-> Details exportiert nach: Ergebnisse/FehlerLogs/{Path.GetFileName(errorLogPath)}\n");
                    
                    return float.MaxValue;
                }

                string analysisResultsDir = Path.Combine(data.BaseDirectory, "Analyseergebnisse");
                Directory.CreateDirectory(analysisResultsDir);
                
                string defaultSurfaceVtu = Path.Combine(data.BaseDirectory, "surface.vtu");
                string savedSurfaceVtu = Path.Combine(data.BaseDirectory, "Analyseergebnisse", $"Surface_Gen{generation}_Var{variant}.vtu");
                if (File.Exists(defaultSurfaceVtu)) File.Copy(defaultSurfaceVtu, savedSurfaceVtu, true);

                string defaultVolumeVtu = Path.Combine(data.BaseDirectory, "vol_solution.vtu");
                string savedVolumeVtu = Path.Combine(data.BaseDirectory, "Analyseergebnisse", $"Volume_Gen{generation}_Var{variant}.vtu");
                if (File.Exists(defaultVolumeVtu)) File.Copy(defaultVolumeVtu, savedVolumeVtu, true);
            }
            catch (Exception e)
            {
                throw new Exception($"Kritischer Systemfehler beim Ausführen von SU2: {e.Message}");
            }

            return ReadDragFromHistory(historyPath);
        }

        private void GenerateSu2Config(string configPath, string meshPath, float refArea, SimulationData data)
        {
            string refAreaString = refArea.ToString(CultureInfo.InvariantCulture);
            
            // Umrechnung: Mach zu Geschwindigkeit in m/s (Schallgeschwindigkeit ca. 343.2 m/s bei 20°C)
            float velocity = data.Settings.MachNumber * 343.2f;
            string velocityString = velocity.ToString(CultureInfo.InvariantCulture);

            string[] cfgContent = {
                "%",
                "% --- SOLVER & PHYSIK ---",
                "SOLVER= INC_RANS",                      // Inkompressibler Navier-Stokes
                "KIND_TURB_MODEL= SA",                   // Spalart-Allmaras Turbulenzmodell
                "MATH_PROBLEM= DIRECT",
                "INC_DENSITY_MODEL= CONSTANT",           // Wichtig für SU2 7.x
                "INC_ENERGY_EQUATION= NO",               // Keine Temperatur/Wärme berechnen (spart Zeit)
                "%",
                "% --- FLUID EIGENSCHAFTEN (Luft auf Meereshöhe) ---",
                $"FREESTREAM_VELOCITY= ( {velocityString}, 0.0, 0.0 )", // Geschwindigkeit in m/s
                "FREESTREAM_DENSITY= 1025.0",
                "VISCOSITY_MODEL= CONSTANT_VISCOSITY",   // NEU: Zwingend für SU2 7.5.1
                "MU_CONSTANT= 1.001e-3",             // NEU: Ersetzt das alte FREESTREAM_VISCOSITY
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
                "MARKER_HEATFLUX= ( Wall, 0.0 )",        // Adiabatische Wandbedingung (No-Slip)
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
                "CFL_ADAPT= YES",                          // Automatisches Anpassen aktivieren
                "CFL_ADAPT_PARAM= ( 0.5, 1.2, 1.0, 50.0 )",// (Faktor Abwärts, Faktor Aufwärts, Min CFL, Max CFL)
                "%",                       
                "%",
                "% --- TURBULENZ NUMERIK ---",
                "CONV_NUM_METHOD_TURB= SCALAR_UPWIND",
                "% --- ABBRUCHKRITERIEN ---",
                $"ITER= {data.Settings.Su2MaxIterations}",          
                "CONV_FIELD= DRAG",
                $"CONV_CAUCHY_ELEMS= {data.Settings.CauchyElements}",
                $"CONV_CAUCHY_EPS= {data.Settings.CauchyTolerance}",
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

        private float ReadDragFromHistory(string historyPath)
        {
            try
            {
                string[] lines = File.ReadAllLines(historyPath);
                if (lines.Length < 2) return float.MaxValue;

                string[] headers = lines[0].Replace("\"", "").Split(',');
                int dragIndex = Array.FindIndex(headers, h => h.Trim().StartsWith("CD", StringComparison.OrdinalIgnoreCase));                
                
                if (dragIndex == -1) 
                    throw new Exception("Spalte 'CD' nicht in der CSV gefunden.");

                string[] finalValues = lines[lines.Length - 1].Split(',');
                
                if (float.TryParse(finalValues[dragIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float dragScore))
                {
                    Console.WriteLine($"    -> Analyse erfolgreich. Ermittelter Widerstand (CD): {dragScore:F5}");
                    return dragScore;
                }
                else
                {
                    Console.WriteLine($"    -> Fehler: SU2 hat keinen gültigen Zahlenwert berechnet: '{finalValues[dragIndex]}'.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    -> Fehler beim Lesen der Ergebnisse: {ex.Message}");
            }

            return float.MaxValue;
        }
    }
}