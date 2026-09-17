using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace MyPicoGkProject
{
    public class GmshConverter
    {
        public string ConvertStlToMesh(string inputStlPath, string tunnelStlPath, int iteration, int variant, SimulationData data, float shrinkFactor)        
        {
            string outputMeshPath = Path.Combine(data.BaseDirectory, $"Model_Gen{iteration}_Var{variant}.su2");
            string geoScriptPath = Path.Combine(data.BaseDirectory, $"Meshing_Gen{iteration}_Var{variant}.geo");
            
            string safeModelPath = inputStlPath.Replace("\\", "/");
            string safeTunnelPath = tunnelStlPath.Replace("\\", "/"); 
            string scaleString = shrinkFactor.ToString(CultureInfo.InvariantCulture);


            string[] geoContent = {
                "// --- ALLGEMEINE NETZ-EINSTELLUNGEN ---",
                "Mesh.Optimize = 1;",                 
                "Mesh.OptimizeNetgen = 0;",         

                "// A) Windkanal importieren",
                $"Merge \"{safeTunnelPath}\";",
                "tunnel_surfs[] = Surface{:};", // Keine künstlichen Kanten erzwingen
                
                "// B) Bauteil importieren",
                $"Merge \"{safeModelPath}\";",
                "model_surfs[] = Surface{:};",
                "model_surfs[] -= tunnel_surfs[];",
                
                "// C) Hohlraum definieren",
                "Surface Loop(1) = {tunnel_surfs[]};",
                "Surface Loop(2) = {model_surfs[]};",
                "Volume(1) = {1, 2};",
                
                "// D) Physische SU2 Marker",
                "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                "Physical Surface(\"Wall\") = {model_surfs[]};",
                "// Physical Volume(\"Fluid\") = {1};",
                "Mesh.SaveAll = 1;",

                "// ==========================================",
                "// E) DISTANZFELD FÜR GRENZSCHICHT (WANDNÄHE)",
                "// ==========================================",
                "Field[1] = Distance;",
                "Field[1].SurfacesList = {model_surfs[]};", 
                
                "Field[2] = Threshold;",
                "Field[2].InField = 1;",
                "Field[2].SizeMin = 1.2;",     // Sehr fein direkt an JEDEM Bauteil
                "Field[2].SizeMax = 120.0;",   // Grob am äußeren Rand des Tunnels
                "Field[2].DistMin = 3.0;",     // Die ersten 1mm bleiben maximal fein (Grenzschicht)
                "Field[2].DistMax = 40.0;",    // Übergangsbereich
                
                /*
                "// ==========================================",
                "// F) DYNAMISCHE WAKE-BOX (NACHLAUF)",
                "// ==========================================",
                "// 1. Bounding Box des importierten Bauteils auslesen",
                "bbox() = BoundingBox Surface {model_surfs[]};",
                "minX = bbox(0); minY = bbox(1); minZ = bbox(2);",
                "maxX = bbox(3); maxY = bbox(4); maxZ = bbox(5);",
                "",
                "// 2. Abmessungen des spezifischen Bauteils berechnen",
                "lenX = maxX - minX;",
                "lenY = maxY - minY;",
                "lenZ = maxZ - minZ;",
                "",
                
                "// 3. Skalierbare Box definieren",
                "Field[3] = Box;",
                "// Start: 10% der Länge VOR dem Bauteil, um Staupunkte abzufangen",
                "Field[3].XMin = minX - (0.1 * lenX);",      
                "// Ende: 3-fache Bauteillänge nach hinten (Wake-Zone)",
                "Field[3].XMax = maxX + (2.0 * lenX);",       
                "// Breite & Höhe: Bauteil plus 50% Puffer in alle Richtungen",
                "Field[3].YMin = minY - (0.5 * lenY);",      
                "Field[3].YMax = maxY + (0.5 * lenY);",
                "Field[3].ZMin = minZ - (0.5 * lenZ);",
                "Field[3].ZMax = maxZ + (0.5 * lenZ);",
                "",
                "Field[3].VIn = lenX / 6.0;",         // Zellgröße innerhalb dieser dynamischen Wake-Box
                "Field[3].VOut = 120.0;",      // Außerhalb

                "// ==========================================",
                "// G) FELDER INTELLIGENT KOMBINIEREN",
                "// ==========================================",
                "// Der Min-Operator prüft für jeden Punkt: Was ist kleiner? Der Wandabstand (Feld 2)",
                "// oder die Wake-Box (Feld 3)? Der kleinere Wert gewinnt.",
                "Field[4] = Min;",
                "Field[4].FieldsList = {2, 3};",
                */
                "Background Field = 2;",       // zum ausschalten der wakebox das hier auf 2 setzten und alles mit field 3 und 4 auskommentieren, sonst zurück auf 4 
                "Mesh.CharacteristicLengthExtendFromBoundary = 0;", 
                "General.NumThreads = 12;",

                "// H) Re-Skalierung",
                $"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;"
            };

            File.WriteAllLines(geoScriptPath, geoContent);

            Process mesher = new Process();
            mesher.StartInfo.FileName = data.Settings.Paths.Gmsh; 
            
            // NEU: Multi-Core Parameter -nt (Number of Threads) wird hier übergeben
            mesher.StartInfo.Arguments = $"\"{geoScriptPath}\" -3 -nt {data.Settings.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"";
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;

            mesher.Start();
            mesher.WaitForExit();

            if (!File.Exists(outputMeshPath))
                throw new Exception($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen.");

            Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
            return outputMeshPath;
        }
    }
}