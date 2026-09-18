using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

namespace MyPicoGkProject
{
    /// <summary>
    /// Manta-Ray AUV Geometrie-Generator.
    /// Erzeugt die bionische Manta-Geometrie mit Fin-Ray-Membranstruktur.
    /// </summary>
    public class MantaGeometryGenerator : IGeometryGenerator
    {
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            float voxelSmoothingIterations,
            int smoothingPremeltingSteps)
        {
            // 1. Gene auslesen mit Fallbacks
            float length = parameters.TryGetValue("Length", out var l) ? l : 50f;
            float width = parameters.TryGetValue("Width", out var w) ? w : 40f;
            float mainRad = parameters.TryGetValue("MainRadius", out var mr) ? mr : 8f;
            float wingRad = parameters.TryGetValue("WingRadius", out var wr) ? wr : 4f;
            float tailTaper = parameters.TryGetValue("TailTaper", out var tt) ? tt : 0.2f;

            // 2. Skelett-Knotenpunkte definieren
            Vector3 nose = new Vector3(0, 0, 0);
            Vector3 tail = new Vector3(length, 0, 0);
            
            // Flügel leicht nach hinten gepfeilt (Sweep) für bessere Hydrodynamik
            float wingRootX = length * 0.35f;
            float wingTipX = wingRootX + (length * 0.15f); 
            
            Vector3 wingLeft = new Vector3(wingTipX, width / 2.0f, 0);
            Vector3 wingRight = new Vector3(wingTipX, -width / 2.0f, 0);

            // 3. Sensoren positionieren
            float sensorRadius = 3.0f;
            Vector3 s1 = new Vector3(length * 0.15f, 0, -mainRad * 0.8f);
            Vector3 s2 = new Vector3(wingLeft.X, wingLeft.Y, -wingRad * 0.8f);
            Vector3 s3 = new Vector3(wingRight.X, wingRight.Y, -wingRad * 0.8f);

            // 4. Bionische Manta-Geometrie aufbauen
            Lattice mantaLat = new Lattice();
            
            // Hauptrumpf (Torpedo)
            mantaLat.AddBeam(nose, tail, mainRad, mainRad * tailTaper, true);

            // --- INTEGRALE FLÜGELSTRUKTUR (MEMBRAN-FÄCHER) ---
            int finRays = 30;
            float wingRootFront = length * 0.15f; 
            float wingRootBack  = length * 0.75f; 
            float finRootThickness = mainRad * 0.4f;
            float finTipThickness  = wingRad * 0.3f;

            for (int i = 0; i <= finRays; i++)
            {
                float t = i / (float)finRays;
                float currentRootX = wingRootFront + t * (wingRootBack - wingRootFront);
                Vector3 rootPos = new Vector3(currentRootX, 0, 0);
                mantaLat.AddBeam(rootPos, wingLeft, finRootThickness, finTipThickness, true);
                mantaLat.AddBeam(rootPos, wingRight, finRootThickness, finTipThickness, true);
            }

            // --- SENSOR-INTEGRATION ---
            Vector3 microStep = new Vector3(0.01f, 0, 0);
            mantaLat.AddBeam(s1, s1 + microStep, sensorRadius, sensorRadius, true); 
            mantaLat.AddBeam(s2, s2 + microStep, sensorRadius, sensorRadius, true); 
            mantaLat.AddBeam(s3, s3 + microStep, sensorRadius, sensorRadius, true); 

            // 5. In Voxel umwandeln und organisch verschmelzen
            Voxels voxModel = new Voxels(mantaLat);
            float blendRadius = mainRad * 0.6f; 
            voxModel.Offset(blendRadius);
            voxModel.Offset(-blendRadius);

            voxModel.CalculateProperties(out float modelVolume, out BBox3 boundingBox);

            float sizeY = boundingBox.vecMax.Y - boundingBox.vecMin.Y;
            float sizeZ = boundingBox.vecMax.Z - boundingBox.vecMin.Z;
            float frontalArea = sizeY * sizeZ;

            // 6. SensorDistance berechnen
            float sensorDistanceScore = Vector3.Cross(s2 - s1, s3 - s1).Length() / 2.0f;

            // 7. Export (kein Viewer mehr — wird extern gesteuert)
            Mesh exportModel = new Mesh(voxModel);
            string modelName = $"Model_Gen{iteration}_Var{variant}.stl";
            string fullModelPath = Path.Combine(outputDirectory, modelName);
            exportModel.SaveToStlFile(fullModelPath);

            try
            {
                StlSmoother.SmoothStl(fullModelPath, fullModelPath, (int)voxelSmoothingIterations, smoothingPremeltingSteps);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       -> [WARNUNG] Glättung fehlgeschlagen: {ex.Message}");
            }

            return new GeometryResult
            {
                StlPath = fullModelPath,
                Metrics = new Dictionary<string, float>
                {
                    { "Volume", modelVolume },
                    { "FrontalArea", frontalArea },
                    { "SensorDistance", sensorDistanceScore }
                }
            };
        }
    }
}
