using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// CFD-Mesher basierend auf Gmsh. Erzeugt ein Volumen-Mesh für CFD-Simulationen.
    /// Der Windkanal (Tunnel) wird hier erzeugt, da er zur Simulationsdomäne gehört.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator
    {
        private readonly GmshMesherOptions _options;
        private string? _cachedTunnelPath;

        /// <param name="options">Vorgabedaten aus solvers/gmsh.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
        public GmshCfdMesher(GmshMesherOptions? options = null)
        {
            _options = options ?? new GmshMesherOptions();
        }

        /// <summary>
        /// Erzeugt den statischen Referenz-Windkanal (gehört zum CFD-Setup, nicht zur Geometrie).
        /// Die Hülle wird selbst trianguliert und als binäres STL geschrieben — ohne
        /// Geometrie-Kernel, damit die CFD-Schicht unabhängig von PicoGK bleibt (TODO-11).
        /// </summary>
        public string EnsureTunnel(string workingDirectory)
        {
            if (_cachedTunnelPath != null && File.Exists(_cachedTunnelPath))
                return _cachedTunnelPath;

            IReadOnlyList<StlTriangle> tunnel = BuildTunnel();

            Console.WriteLine($"[SYSTEM] Generiere statischen Referenz-Windkanal ({tunnel.Count} Dreiecke)...");

            _cachedTunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
            StlWriter.WriteBinary(_cachedTunnelPath, tunnel);

            return _cachedTunnelPath;
        }

        /// <summary>
        /// Baut die Tunnelhülle nach <see cref="GmshMesherOptions.TunnelShape"/>.
        /// Unbekannter Name → Warnung und Rückfall auf den Quader, wie bei der
        /// Algorithmuswahl: ein Tippfehler in der JSON soll den Lauf nicht abbrechen.
        /// </summary>
        private IReadOnlyList<StlTriangle> BuildTunnel()
        {
            Vector3 center = new Vector3(_options.TunnelCenterX, _options.TunnelCenterY, _options.TunnelCenterZ);
            string shape = (_options.TunnelShape ?? string.Empty).Replace(" ", string.Empty).Trim();

            if (shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase))
            {
                return TunnelGeometry.CreateCylinder(
                    _options.TunnelDiameter,
                    _options.TunnelLength,
                    center,
                    _options.TunnelSegments);
            }

            if (shape.Length > 0 && !shape.Equals("Box", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WARNUNG] Unbekannte Windkanal-Form '{_options.TunnelShape}' — nutze 'Box'.");
            }

            return TunnelGeometry.CreateBox(
                new Vector3(_options.TunnelSizeX, _options.TunnelSizeY, _options.TunnelSizeZ),
                center);
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
                $"Field[2].SizeMin = {Format(_options.BoundaryLayerSizeMin)};",
                $"Field[2].SizeMax = {Format(_options.BoundaryLayerSizeMax)};",
                $"Field[2].DistMin = {Format(_options.BoundaryLayerDistMin)};",
                $"Field[2].DistMax = {Format(_options.BoundaryLayerDistMax)};",
                
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

        /// <summary>Zahlen fürs .geo-Skript immer mit Punkt als Dezimaltrenner.</summary>
        private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
