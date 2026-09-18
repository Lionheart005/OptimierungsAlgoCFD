using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using PicoGK;

namespace MyPicoGkProject
{
    /// <summary>
    /// CFD-Mesher basierend auf Gmsh. Erzeugt ein Volumen-Mesh für CFD-Simulationen.
    /// Der Windkanal (Tunnel) wird hier erzeugt, da er zur Simulationsdomäne gehört.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator
    {
        private string? _cachedTunnelPath;

        /// <summary>
        /// Erzeugt den statischen Referenz-Windkanal (gehört zum CFD-Setup, nicht zur Geometrie).
        /// </summary>
        public string EnsureTunnel(string workingDirectory)
        {
            if (_cachedTunnelPath != null && File.Exists(_cachedTunnelPath))
                return _cachedTunnelPath;

            Console.WriteLine("[SYSTEM] Generiere statischen Referenz-Windkanal...");
            Vector3 minBounds = new Vector3(-300, -150, -150);
            Vector3 maxBounds = new Vector3(300, 150, 150);
            
            Vector3 vecScale = maxBounds - minBounds;
            Vector3 vecOffset = (minBounds + maxBounds) * 0.5f;

            Mesh exportTunnel = Utils.mshCreateCube(vecScale, vecOffset);
            _cachedTunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
            exportTunnel.SaveToStlFile(_cachedTunnelPath);
            
            return _cachedTunnelPath;
        }

        public string GenerateMesh(
            string modelStlPath,
            int iteration, int variant,
            SimulationConfig config,
            float scaleFactor,
            string workingDirectory)
        {
            string tunnelStlPath = EnsureTunnel(workingDirectory);

            string outputMeshPath = Path.Combine(workingDirectory, $"Model_Gen{iteration}_Var{variant}.su2");
            string geoScriptPath = Path.Combine(workingDirectory, $"Meshing_Gen{iteration}_Var{variant}.geo");
            
            string safeModelPath = modelStlPath.Replace("\\", "/");
            string safeTunnelPath = tunnelStlPath.Replace("\\", "/"); 
            string scaleString = scaleFactor.ToString(CultureInfo.InvariantCulture);

            string[] geoContent = {
                "// --- ALLGEMEINE NETZ-EINSTELLUNGEN ---",
                "Mesh.Optimize = 1;",                 
                "Mesh.OptimizeNetgen = 0;",         

                "// A) Windkanal importieren",
                $"Merge \"{safeTunnelPath}\";",
                "tunnel_surfs[] = Surface{:};",
                
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
                "Field[2].SizeMin = 1.2;",
                "Field[2].SizeMax = 120.0;",
                "Field[2].DistMin = 3.0;",
                "Field[2].DistMax = 40.0;",
                
                /*
                "// ==========================================",
                "// F) DYNAMISCHE WAKE-BOX (NACHLAUF)",
                "// ==========================================",
                "bbox() = BoundingBox Surface {model_surfs[]};",
                "minX = bbox(0); minY = bbox(1); minZ = bbox(2);",
                "maxX = bbox(3); maxY = bbox(4); maxZ = bbox(5);",
                "",
                "lenX = maxX - minX;",
                "lenY = maxY - minY;",
                "lenZ = maxZ - minZ;",
                "",
                "Field[3] = Box;",
                "Field[3].XMin = minX - (0.1 * lenX);",      
                "Field[3].XMax = maxX + (2.0 * lenX);",       
                "Field[3].YMin = minY - (0.5 * lenY);",      
                "Field[3].YMax = maxY + (0.5 * lenY);",
                "Field[3].ZMin = minZ - (0.5 * lenZ);",
                "Field[3].ZMax = maxZ + (0.5 * lenZ);",
                "",
                "Field[3].VIn = lenX / 6.0;",
                "Field[3].VOut = 120.0;",

                "// ==========================================",
                "// G) FELDER INTELLIGENT KOMBINIEREN",
                "// ==========================================",
                "Field[4] = Min;",
                "Field[4].FieldsList = {2, 3};",
                */
                "Background Field = 2;",
                "Mesh.CharacteristicLengthExtendFromBoundary = 0;", 
                $"General.NumThreads = {config.GmshCores};",

                "// H) Re-Skalierung",
                $"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;"
            };

            File.WriteAllLines(geoScriptPath, geoContent);

            Process mesher = new Process();
            mesher.StartInfo.FileName = config.GmshPath; 
            mesher.StartInfo.Arguments = $"\"{geoScriptPath}\" -3 -nt {config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"";
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
