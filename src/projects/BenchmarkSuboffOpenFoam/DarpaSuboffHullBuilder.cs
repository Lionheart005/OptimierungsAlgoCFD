using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkSuboffOpenFoam
{
    /// <summary>
    /// Analytical DARPA SUBOFF AFF-1 bare-hull builder (DTRC/SHD-1298-01, Groves, Huang &amp; Chang 1989)
    /// producing a watertight binary STL triangle mesh and exact geometric metrics without PicoGK.
    /// </summary>
    public static class DarpaSuboffHullBuilder
    {
        public const double LRefFeet = 14.291667;
        public const double RMaxFeet = 0.8333333333;
        public const double XForebodyEndFeet = 3.3333333333;
        public const double XMidbodyEndFeet = 10.6458333333;
        public const double XAfterbodyEndFeet = 13.9791666667;
        public const double SternCapRatio = 0.1175;
        public const double K0 = 10.0;
        public const double K1 = 44.6244;
        public const double FeetToMm = 304.8;

        public const float DefaultModelScaleFactor = 0.1f;
        public const float DefaultReferenceCt = 0.00314f;
        public const int DefaultAxialStations = 240;
        public const int DefaultCircumferentialSegments = 96;

        /// <summary>
        /// Evaluates the AFF-1 bare-hull radius in feet at axial coordinate <paramref name="xFeet"/>,
        /// optionally scaled by <paramref name="radiusScale"/> and <paramref name="midbodyLengthScale"/>.
        /// </summary>
        public static double EvaluateRadiusFeet(
            double xFeet,
            double radiusScale = 1.0,
            double midbodyLengthScale = 1.0)
        {
            if (xFeet < 0.0 || radiusScale <= 0.0 || midbodyLengthScale <= 0.0)
                return 0.0;

            double baseMidbodyLen = XMidbodyEndFeet - XForebodyEndFeet;
            double scaledMidbodyLen = baseMidbodyLen * midbodyLengthScale;
            double xMidEnd = XForebodyEndFeet + scaledMidbodyLen;
            double afterbodyLen = XAfterbodyEndFeet - XMidbodyEndFeet;
            double xAfterEnd = xMidEnd + afterbodyLen;
            double sternCapLen = LRefFeet - XAfterbodyEndFeet;
            double xTotalEnd = xAfterEnd + sternCapLen;

            if (xFeet > xTotalEnd)
                return 0.0;

            double rMax = RMaxFeet * radiusScale;

            if (xFeet <= XForebodyEndFeet)
            {
                double t = 0.3 * xFeet - 1.0;
                double t3 = t * t * t;
                double t4 = t3 * t;
                double poly = 1.126395101 * xFeet * t4
                            + 0.442874707 * xFeet * xFeet * t3
                            + 1.0
                            - t4 * (1.2 * xFeet + 1.0);
                return rMax * Math.Pow(Math.Max(0.0, poly), 1.0 / 2.1);
            }

            if (xFeet < xMidEnd)
            {
                return rMax;
            }

            if (xFeet <= xAfterEnd)
            {
                double xi = (xAfterEnd - xFeet) / (10.0 / 3.0);
                double xi2 = xi * xi;
                double xi3 = xi2 * xi;
                double xi4 = xi3 * xi;
                double xi5 = xi4 * xi;
                double xi6 = xi5 * xi;

                double rh2 = SternCapRatio * SternCapRatio;
                double rhK0 = SternCapRatio * K0;
                double c3 = 20.0 - 20.0 * rh2 - 4.0 * rhK0 - K1 / 3.0;
                double c4 = -45.0 + 45.0 * rh2 + 6.0 * rhK0 + K1;
                double c5 = 36.0 - 36.0 * rh2 - 4.0 * rhK0 - K1;
                double c6 = -10.0 + 10.0 * rh2 + rhK0 + K1 / 3.0;

                double poly = rh2 + rhK0 * xi2 + c3 * xi3 + c4 * xi4 + c5 * xi5 + c6 * xi6;
                return rMax * Math.Sqrt(Math.Max(0.0, poly));
            }

            double dx = xFeet - xAfterEnd;
            double term = 3.2 * dx;
            double capArg = 1.0 - term * term;
            return SternCapRatio * rMax * Math.Sqrt(Math.Max(0.0, capArg));
        }

        /// <summary>
        /// Computes the scaled hull total length in feet for the given <paramref name="midbodyLengthScale"/>.
        /// </summary>
        public static double GetTotalLengthFeet(double midbodyLengthScale = 1.0)
        {
            double baseMidbodyLen = XMidbodyEndFeet - XForebodyEndFeet;
            return LRefFeet + baseMidbodyLen * (midbodyLengthScale - 1.0);
        }

        /// <summary>
        /// Builds a watertight, outward-oriented triangle mesh of the DARPA SUBOFF bare hull along +X.
        /// </summary>
        public static List<StlTriangle> BuildHullTriangles(
            double radiusScale = 1.0,
            double midbodyLengthScale = 1.0,
            double modelScaleFactor = DefaultModelScaleFactor,
            int axialStations = DefaultAxialStations,
            int circumferentialSegments = DefaultCircumferentialSegments)
        {
            if (radiusScale <= 0.0) throw new ArgumentOutOfRangeException(nameof(radiusScale));
            if (midbodyLengthScale <= 0.0) throw new ArgumentOutOfRangeException(nameof(midbodyLengthScale));
            if (modelScaleFactor <= 0.0) throw new ArgumentOutOfRangeException(nameof(modelScaleFactor));
            if (axialStations < 16) throw new ArgumentOutOfRangeException(nameof(axialStations));
            if (circumferentialSegments < 12) throw new ArgumentOutOfRangeException(nameof(circumferentialSegments));

            double totalLengthFeet = GetTotalLengthFeet(midbodyLengthScale);
            double feetToScaledMm = FeetToMm * modelScaleFactor;

            var rings = new Vector3[axialStations - 1][];
            double[] cosTheta = new double[circumferentialSegments];
            double[] sinTheta = new double[circumferentialSegments];
            for (int j = 0; j < circumferentialSegments; j++)
            {
                double theta = (2.0 * Math.PI * j) / circumferentialSegments;
                cosTheta[j] = Math.Cos(theta);
                sinTheta[j] = Math.Sin(theta);
            }

            for (int i = 1; i < axialStations; i++)
            {
                double s = 0.5 * (1.0 - Math.Cos(Math.PI * i / axialStations));
                double xFeet = s * totalLengthFeet;
                double rFeet = EvaluateRadiusFeet(xFeet, radiusScale, midbodyLengthScale);
                float xMm = (float)(xFeet * feetToScaledMm);
                float rMm = (float)Math.Max(rFeet * feetToScaledMm, 1e-6);

                var ring = new Vector3[circumferentialSegments];
                for (int j = 0; j < circumferentialSegments; j++)
                {
                    ring[j] = new Vector3(
                        xMm,
                        (float)(rMm * cosTheta[j]),
                        (float)(rMm * sinTheta[j]));
                }
                rings[i - 1] = ring;
            }

            int triangleCount = circumferentialSegments * 2
                              + (axialStations - 2) * circumferentialSegments * 2;
            var triangles = new List<StlTriangle>(triangleCount);

            Vector3 noseTip = Vector3.Zero;
            Vector3 tailTip = new Vector3((float)(totalLengthFeet * feetToScaledMm), 0f, 0f);

            // Nose cap fan (outward normals point -X and radially outward)
            Vector3[] firstRing = rings[0];
            for (int j = 0; j < circumferentialSegments; j++)
            {
                int jNext = (j + 1) % circumferentialSegments;
                triangles.Add(new StlTriangle(noseTip, firstRing[jNext], firstRing[j]));
            }

            // Body quads split into two outward-oriented triangles each
            for (int i = 0; i < rings.Length - 1; i++)
            {
                Vector3[] curr = rings[i];
                Vector3[] next = rings[i + 1];
                for (int j = 0; j < circumferentialSegments; j++)
                {
                    int jNext = (j + 1) % circumferentialSegments;
                    triangles.Add(new StlTriangle(curr[j], curr[jNext], next[jNext]));
                    triangles.Add(new StlTriangle(curr[j], next[jNext], next[j]));
                }
            }

            // Tail cap fan (outward normals point +X and radially outward)
            Vector3[] lastRing = rings[^1];
            for (int j = 0; j < circumferentialSegments; j++)
            {
                int jNext = (j + 1) % circumferentialSegments;
                triangles.Add(new StlTriangle(lastRing[j], lastRing[jNext], tailTip));
            }

            return triangles;
        }

        /// <summary>
        /// Builds the DARPA SUBOFF hull from <paramref name="parameters"/>, validates watertightness
        /// and outward orientation, writes the binary STL, and returns a <see cref="GeometryResult"/>.
        /// </summary>
        public static GeometryResult BuildAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            float modelScaleFactor = DefaultModelScaleFactor,
            int axialStations = DefaultAxialStations,
            int circumferentialSegments = DefaultCircumferentialSegments)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

            float radiusScale = Read(parameters, "RadiusScale");
            float midbodyLengthScale = Read(parameters, "MidbodyLengthScale");

            List<StlTriangle> triangles = BuildHullTriangles(
                radiusScale,
                midbodyLengthScale,
                modelScaleFactor,
                axialStations,
                circumferentialSegments);

            (int openEdges, int nonManifoldEdges) = MeshMetrics.CountBoundaryAndNonManifoldEdges(triangles);
            if (openEdges != 0 || nonManifoldEdges != 0)
            {
                throw new InvalidOperationException(
                    $"[GEOMETRY] DARPA SUBOFF hull mesh is not watertight (openEdges={openEdges}, nonManifoldEdges={nonManifoldEdges}).");
            }

            MeshMeasurement measurement = MeshMetrics.Measure(triangles);
            if (measurement.SignedVolume <= 0f)
            {
                throw new InvalidOperationException(
                    $"[GEOMETRY] DARPA SUBOFF hull mesh has non-positive signed volume ({measurement.SignedVolume}).");
            }

            Directory.CreateDirectory(outputDirectory);
            string stlPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
            StlWriter.WriteBinary(stlPath, triangles);

            float rMaxMm = (float)(RMaxFeet * FeetToMm * modelScaleFactor * radiusScale);
            float frontalAreaMm2 = MathF.PI * rMaxMm * rMaxMm;

            var result = new GeometryResult
            {
                Kind = GeometryKind.SurfaceStl,
                StlPath = stlPath,
                MeshUnit = "mm"
            };

            result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
            result.AddMetric("SurfaceArea", measurement.SurfaceArea, MetricScaling.Area);
            result.AddMetric("WettedArea", measurement.SurfaceArea, MetricScaling.Area);
            result.AddMetric("FrontalArea", frontalAreaMm2, MetricScaling.Area);
            result.AddMetric("Length", measurement.Bounds.Size.X, MetricScaling.Linear);
            result.AddMetric("Diameter", measurement.Bounds.Size.Y, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Shared objective evaluation for DARPA SUBOFF benchmarks (minimizes drag and logs <c>Suboff_CtRelErr</c>).
        /// </summary>
        public static EvaluationOutcome EvaluateSuboffObjectives(ModelRecord record, float referenceCt)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (!record.PassiveParameters.TryGetValue("Drag", out float drag) ||
                !float.IsFinite(drag))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = ObjectiveEvaluatorBase.DefaultFailedFitness };
            }

            float ctRelErr = MathF.Abs(drag - referenceCt) / MathF.Max(referenceCt, 1e-9f);
            record.PassiveParameters["Suboff_CtRelErr"] = ctRelErr;

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("Drag", drag, OptimizationDirection.Minimize)
                }
            };
        }

        /// <summary>
        /// Reads <c>OptimizationTargets.ModelScaleFactor</c> or returns <see cref="DefaultModelScaleFactor"/>.
        /// </summary>
        public static float ReadModelScaleFactor(ProjectConfig? project)
        {
            if (project?.OptimizationTargets == null ||
                !project.OptimizationTargets.TryGetValue("ModelScaleFactor", out float scale))
            {
                return DefaultModelScaleFactor;
            }

            if (!float.IsFinite(scale) || scale <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} OptimizationTargets.ModelScaleFactor must be finite and > 0 (got {scale}).");
            }

            return scale;
        }

        /// <summary>
        /// Reads <c>OptimizationTargets.ReferenceCt</c> or returns <see cref="DefaultReferenceCt"/>.
        /// </summary>
        public static float ReadReferenceCt(ProjectConfig? project)
        {
            if (project?.OptimizationTargets == null ||
                !project.OptimizationTargets.TryGetValue("ReferenceCt", out float refCt))
            {
                return DefaultReferenceCt;
            }

            if (!float.IsFinite(refCt) || refCt <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} OptimizationTargets.ReferenceCt must be finite and > 0 (got {refCt}).");
            }

            return refCt;
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing parameter '{name}' for DARPA SUBOFF hull. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
