using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

namespace MyPicoGkProject
{
    public class PicoGkGenerator    
    {
        private Voxels? _lastDisplayedModel = null;

        public string GenerateStaticTunnel(string baseDirectory)
        {
            Console.WriteLine("[SYSTEM] Generiere statischen Referenz-Windkanal...");
            Vector3 minBounds = new Vector3(-300, -150, -150); // Vergrößert für AUV
            Vector3 maxBounds = new Vector3(300, 150, 150);
            
            Vector3 vecScale = maxBounds - minBounds;
            Vector3 vecOffset = (minBounds + maxBounds) * 0.5f;

            Mesh exportTunnel = Utils.mshCreateCube(vecScale, vecOffset);
            string tunnelPath = Path.Combine(baseDirectory, "Static_Windtunnel.stl");
            exportTunnel.SaveToStlFile(tunnelPath);
            
            return tunnelPath;
        }

        public (string StlPath, float Volume, float FrontalArea, float SensorDistance) GenerateAndExport(int iteration, int variant, Dictionary<string, float> parameters, SimulationData data, bool showInViewer)        
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

            // --- NEUE INTEGRALE FLÜGELSTRUKTUR (MEMBRAN-FÄCHER) ---
            // Wir erzeugen ein flaches Rochen-Profil, indem wir viele extrem flache 
            // Strahlen vom Rumpf zur Flügelspitze spannen (wie Gräten einer Flosse).
            int finRays = 30; // Anzahl der Streben (bestimmt die Dichte des Fächers)
            
            // Die Flosse beginnt nah an der Nase und zieht sich weit nach hinten
            float wingRootFront = length * 0.15f; 
            float wingRootBack  = length * 0.75f; 
            
            // Dicke der Flosse (Z-Achse): Zwingend klein halten für die flache Form!
            float finRootThickness = mainRad * 0.4f; // Flach am Rumpf
            float finTipThickness  = wingRad * 0.3f; // Fast messerscharf an der Spitze

            for (int i = 0; i <= finRays; i++)
            {
                float t = i / (float)finRays;
                
                // Berechne den Ansatzpunkt am Rumpf für diesen spezifischen Strahl
                float currentRootX = wingRootFront + t * (wingRootBack - wingRootFront);
                Vector3 rootPos = new Vector3(currentRootX, 0, 0);
                
                // Spanne den "Faden" vom Rumpf zur Spitze
                mantaLat.AddBeam(rootPos, wingLeft, finRootThickness, finTipThickness, true);
                mantaLat.AddBeam(rootPos, wingRight, finRootThickness, finTipThickness, true);
            }

            // --- SENSOR-INTEGRATION ---
            // Die Sensoren werden als "Blister" auf der nun sehr flachen Membran platziert.
            // Da sensorRadius > finTipThickness, wölben sie sich realistisch aus dem Flügel heraus!
            Vector3 microStep = new Vector3(0.01f, 0, 0);
            mantaLat.AddBeam(s1, s1 + microStep, sensorRadius, sensorRadius, true); 
            mantaLat.AddBeam(s2, s2 + microStep, sensorRadius, sensorRadius, true); 
            mantaLat.AddBeam(s3, s3 + microStep, sensorRadius, sensorRadius, true); 

            // 5. In Voxel umwandeln und organisch verschmelzen
            Voxels voxModel = new Voxels(mantaLat);
            
            // Morphologisches "Closing": Dieser Schritt ist jetzt magisch.
            // Er füllt exakt die Rillen zwischen den 30 dünnen Fächer-Strahlen auf 
            // und erzeugt die perfekte organische Rundung an den Flossen-Ansätzen.
            float blendRadius = mainRad * 0.6f; 
            voxModel.Offset(blendRadius);  // Lücken schließen und Fillets bilden
            voxModel.Offset(-blendRadius); // Wieder auf Originalmaß schrumpfen lassen

            voxModel.CalculateProperties(out float modelVolume, out BBox3 boundingBox);

            float sizeY = boundingBox.vecMax.Y - boundingBox.vecMin.Y;
            float sizeZ = boundingBox.vecMax.Z - boundingBox.vecMin.Z;
            float frontalArea = sizeY * sizeZ;

            // 6. SensorDistance berechnen (Fläche des Dreiecks via Kreuzprodukt)
            float sensorDistanceScore = Vector3.Cross(s2 - s1, s3 - s1).Length() / 2.0f;

            // 7. Viewer & Export
            if (showInViewer) 
            {
                if (_lastDisplayedModel != null) Library.oViewer().Remove(_lastDisplayedModel);
                Library.oViewer().Add(voxModel);
                _lastDisplayedModel = voxModel;
            }

            Mesh exportModel = new Mesh(voxModel);
            string modelName = $"Model_Gen{iteration}_Var{variant}.stl";
            string fullModelPath = Path.Combine(data.BaseDirectory, modelName);
            exportModel.SaveToStlFile(fullModelPath);

            try
            {
                StlSmoother.SmoothStl(fullModelPath, fullModelPath, data.Settings.VoxelSmoothingIterations, data.Settings.VoxelSmoothingPremeltingSteps);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"       -> [WARNUNG] Glättung fehlgeschlagen: {ex.Message}");
            }

            return (fullModelPath, modelVolume, frontalArea, sensorDistanceScore);
        }
    }
}