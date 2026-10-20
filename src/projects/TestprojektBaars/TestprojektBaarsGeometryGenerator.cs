using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.PicoGk;

namespace MyPicoGkProject.Projects.TestprojektBaars
{
    /// <summary>
    /// Parametrischer Geometrie-Generator für 4-stellige NACA-Tragflügelprofile.
    /// Erzeugt ein 3D-Flügelsegment mit definierter Spannweite, Sehnenlänge und Anstellwinkel.
    /// </summary>
    public sealed class TestprojektBaarsGeometryGenerator : IGeometryGenerator
    {
        private const float DefaultChordLength = 50.0f;       // Sehnenlänge c in mm
        private const float DefaultWingSpan = 40.0f;          // Spannweite b in mm
        private const float DefaultAngleOfAttackDeg = 5.0f;   // Fester Anstellwinkel alpha = 5°
        private const int ChordStations = 100;

        /// <inheritdoc/>
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float maxCamberPercent = Read(parameters, "MaxCamber");
            float camberPositionPercent = Read(parameters, "CamberPosition");
            float maxThicknessPercent = Read(parameters, "MaxThickness");

            float c = DefaultChordLength;
            float span = DefaultWingSpan;
            float halfSpan = span * 0.5f;

            // Dimensionslose Kennwerte berechnen
            float m = Math.Clamp(maxCamberPercent / 100.0f, 0.0f, 0.15f);
            float p = Math.Clamp(camberPositionPercent / 100.0f, 0.05f, 0.95f);
            float t = Math.Clamp(maxThicknessPercent / 100.0f, 0.02f, 0.40f);

            float alphaRad = DefaultAngleOfAttackDeg * (MathF.PI / 180.0f);
            float pivotX = 0.25f * c;

            // Mindestradius im Voxelgitter
            float minBeamRadius = Math.Max(config.BaseVoxelResolution * 1.2f, 0.6f);

            // Stützstellen entlang der Sehne vorberechnen
            var xStations = new float[ChordStations + 1];
            var zStations = new float[ChordStations + 1];
            var rStations = new float[ChordStations + 1];

            for (int i = 0; i <= ChordStations; i++)
            {
                // Kosinus-Verteilung für feine Auflösung an Vorder- und Hinterkante
                float beta = (float)i / ChordStations * MathF.PI;
                float xi = 0.5f * (1.0f - MathF.Cos(beta)); // xi in [0, 1]

                EvaluateNaca4Digit(xi, c, m, p, t, out float yc, out float yt, out _, out _, out _);

                // Drehung um den Viertelpunkt (0.25*c) mit Anstellwinkel alpha
                float dx = (xi * c) - pivotX;
                float xRot = pivotX + dx * MathF.Cos(alphaRad) + yc * MathF.Sin(alphaRad);
                float zRot = -dx * MathF.Sin(alphaRad) + yc * MathF.Cos(alphaRad);

                xStations[i] = xRot;
                zStations[i] = zRot;
                rStations[i] = Math.Max(yt, minBeamRadius);
            }

            // PicoGK Lattice aufbauen
            Lattice wingLattice = new Lattice();

            // 1. Spannweiten-Beams quer zur Strömung (entlang Y) für jede Stützstelle
            for (int i = 0; i <= ChordStations; i++)
            {
                Vector3 ptStart = new Vector3(xStations[i], -halfSpan, zStations[i]);
                Vector3 ptEnd = new Vector3(xStations[i], halfSpan, zStations[i]);
                wingLattice.AddBeam(ptStart, ptEnd, rStations[i], rStations[i], true);
            }

            // 2. Sehnen-Verbindungsbeams in mehreren Spannweitenschnitten zur lückenlosen Kohäsion
            float[] yCuts = { -halfSpan, -0.5f * halfSpan, 0.0f, 0.5f * halfSpan, halfSpan };
            for (int i = 1; i <= ChordStations; i++)
            {
                foreach (float y in yCuts)
                {
                    Vector3 prev = new Vector3(xStations[i - 1], y, zStations[i - 1]);
                    Vector3 curr = new Vector3(xStations[i], y, zStations[i]);
                    wingLattice.AddBeam(prev, curr, rStations[i - 1], rStations[i], true);
                }
            }

            // In Voxel umwandeln und organisch verschmelzen
            Voxels voxModel = new Voxels(wingLattice);
            float blendRadius = Math.Max(config.BaseVoxelResolution * 0.8f, 0.4f);
            voxModel.Offset(blendRadius);
            voxModel.Offset(-blendRadius);

            Mesh exportModel = new Mesh(voxModel);
            MeshMeasurement measurement = PicoGkMeshMetrics.Measure(exportModel);

            if (measurement.SignedVolume < 0f)
            {
                Console.WriteLine("       -> [WARNUNG] Das Netz ist nach innen gedreht (negatives Volumen).");
            }

            string modelPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
            exportModel.SaveToStlFile(modelPath);

            if (config.UseStlSmoothing)
            {
                try
                {
                    StlSmoother.SmoothStl(
                        modelPath, modelPath,
                        config.VoxelSmoothingIterations,
                        config.VoxelSmoothingPremeltingSteps);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"       -> [WARNUNG] Glättung fehlgeschlagen: {ex.Message}");
                }
            }

            var result = new GeometryResult { StlPath = modelPath };
            result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
            result.AddMetric("SurfaceArea", measurement.SurfaceArea, MetricScaling.Area);
            result.AddMetric("FrontalArea", measurement.FrontalBoxArea, MetricScaling.Area);
            result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Berechnet für eine relative Sehnenposition xi in [0, 1] die Geometrie nach der klassischen
        /// 4-stelligen NACA-Formel (Wölbung yc, Halbdicke yt, Skelettlinien-Steigung dyc/dx,
        /// Tangentenwinkel theta sowie die Koordinaten der Profilober- und -unterseite).
        /// </summary>
        /// <param name="xi">Relative Sehnenposition xi = x / c in [0, 1].</param>
        /// <param name="c">Sehnenlänge c in mm.</param>
        /// <param name="m">Maximale Wölbung als Verhältnis m = MaxCamber / 100.</param>
        /// <param name="p">Lage der maximalen Wölbung als Verhältnis p = CamberPosition / 100.</param>
        /// <param name="t">Maximale Profildicke als Verhältnis t = MaxThickness / 100.</param>
        /// <param name="yc">Z-Koordinate der Skelettlinie (Camber) in mm.</param>
        /// <param name="yt">Halbdicke des Profils senkrecht/vertikal in mm.</param>
        /// <param name="theta">Tangentenwinkel der Skelettlinie in Radiant.</param>
        /// <param name="upperSurface">Koordinaten (x_U, z_U) der Profiloberseite in mm.</param>
        /// <param name="lowerSurface">Koordinaten (x_L, z_L) der Profilunterseite in mm.</param>
        public static void EvaluateNaca4Digit(
            float xi, float c, float m, float p, float t,
            out float yc, out float yt, out float theta,
            out Vector2 upperSurface, out Vector2 lowerSurface)
        {
            float xiClamped = Math.Clamp(xi, 0.0f, 1.0f);
            float x = xiClamped * c;

            // 1. Klassische NACA-Dickenverteilung (Halbdicke yt)
            float sqrtXi = MathF.Sqrt(xiClamped);
            yt = 5.0f * t * c * (0.2969f * sqrtXi
                               - 0.1260f * xiClamped
                               - 0.3516f * xiClamped * xiClamped
                               + 0.2843f * xiClamped * xiClamped * xiClamped
                               - 0.1015f * xiClamped * xiClamped * xiClamped * xiClamped);
            yt = Math.Max(yt, 0.0f);

            // 2. NACA-Skelettlinie (yc) und Ableitung (dyc/dx)
            float dycDx;
            if (m <= 1e-5f)
            {
                yc = 0.0f;
                dycDx = 0.0f;
            }
            else if (xiClamped < p)
            {
                yc = (m * c / (p * p)) * (2.0f * p * xiClamped - xiClamped * xiClamped);
                dycDx = (2.0f * m / (p * p)) * (p - xiClamped);
            }
            else
            {
                yc = (m * c / ((1.0f - p) * (1.0f - p))) * ((1.0f - 2.0f * p) + 2.0f * p * xiClamped - xiClamped * xiClamped);
                dycDx = (2.0f * m / ((1.0f - p) * (1.0f - p))) * (p - xiClamped);
            }

            // 3. Tangentenwinkel der Skelettlinie
            theta = MathF.Atan(dycDx);

            // 4. Exakte Profilkoordinaten senkrecht zur Skelettlinie
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);

            float xU = x - yt * sinTheta;
            float zU = yc + yt * cosTheta;
            upperSurface = new Vector2(xU, zU);

            float xL = x + yt * sinTheta;
            float zL = yc - yt * cosTheta;
            lowerSurface = new Vector2(xL, zL);
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRIE] Der Parameter '{name}' fehlt. Vorhanden ist: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(keiner)")
                + ". Er gehört in BaseParameters, MaxDeviations und ParameterBounds der "
                + "project.json dieses Projekts — und in dieselben drei Listen in "
                + "CreateDefaults().");
        }
    }
}
