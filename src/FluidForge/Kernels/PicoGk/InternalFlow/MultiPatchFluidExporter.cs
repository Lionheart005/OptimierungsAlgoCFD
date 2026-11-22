using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Kernels.PicoGk.InternalFlow
{
    /// <summary>
    /// Trims, validates, partitions, and exports watertight internal-flow fluid volumes
    /// into combined, per-patch binary STLs and a multi-solid ASCII STL.
    /// </summary>
    public static class MultiPatchFluidExporter
    {
        private const float DefaultVoxelSmoothMm = 0.5f;

        /// <summary>
        /// Cuts every <see cref="FlowPort"/> plane cleanly by subtracting a <see cref="BaseBox"/>
        /// oriented along <see cref="FlowPort.OutwardNormal"/> so port caps remain planar.
        /// </summary>
        public static Voxels TrimWithPortPlanes(
            Voxels fluidVoxels,
            IReadOnlyList<FlowPort> ports,
            float cutterThicknessMm = 20f,
            float cutterHalfWidthMm = 200f)
        {
            ArgumentNullException.ThrowIfNull(fluidVoxels);
            ArgumentNullException.ThrowIfNull(ports);
            if (cutterThicknessMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cutterThicknessMm), "cutterThicknessMm must be positive.");
            if (cutterHalfWidthMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(cutterHalfWidthMm), "cutterHalfWidthMm must be positive.");

            Voxels current = new Voxels(fluidVoxels);
            if (ports.Count == 0)
                return current;

            float fullWidth = cutterHalfWidthMm * 2f;

            foreach (FlowPort port in ports)
            {
                if (port == null)
                    throw new ArgumentNullException(nameof(ports), "FlowPort entry must not be null.");

                LocalFrame cutterFrame = new LocalFrame(port.Center, port.OutwardNormal);
                BaseBox cutterBox = new BaseBox(cutterFrame, cutterThicknessMm, fullWidth, fullWidth);
                using Voxels cutterVoxels = cutterBox.voxConstruct();

                Voxels trimmed = current.voxBoolSubtract(cutterVoxels);
                current.Dispose();
                current = trimmed;
            }

            return current;
        }

        /// <summary>
        /// Exports a PicoGK fluid voxel volume as a watertight combined STL, per-patch STLs,
        /// and a multi-solid ASCII STL after validating watertightness and seed-point containment.
        /// </summary>
        public static InternalFlowExportResult Export(
            Voxels fluidVoxels,
            IReadOnlyList<FlowPort> ports,
            Vector3 fluidSeedPoint,
            string outputDirectory,
            string baseName,
            bool trimPorts = true,
            bool applySmoothing = false)
        {
            ArgumentNullException.ThrowIfNull(fluidVoxels);
            ArgumentNullException.ThrowIfNull(ports);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

            Directory.CreateDirectory(outputDirectory);
            string sanitizedBase = baseName.Trim();

            Voxels working = trimPorts ? TrimWithPortPlanes(fluidVoxels, ports) : new Voxels(fluidVoxels);

            try
            {
                if (applySmoothing)
                {
                    Voxels smoothed = working.voxSmoothen(DefaultVoxelSmoothMm);
                    working.Dispose();
                    working = smoothed;

                    if (trimPorts)
                    {
                        Voxels reTrimmed = TrimWithPortPlanes(working, ports);
                        working.Dispose();
                        working = reTrimmed;
                    }
                }

                using Mesh mesh = new Mesh(working);
                IReadOnlyList<StlTriangle> rawTriangles = PicoGkMeshMetrics.ToTriangles(mesh);

                // PicoGK's voxel-to-mesh conversion can emit collapsed needle triangles (A == B)
                // along grid-aligned planes; stripping them preserves the exact closed topology
                // while preventing spurious non-manifold edge counts and zero-normal STL facets.
                List<StlTriangle> triangles = rawTriangles
                    .Where(t => t.A != t.B && t.B != t.C && t.C != t.A)
                    .ToList();

                if (triangles.Count == 0)
                    throw new InvalidOperationException("Fluid voxel volume produced an empty surface mesh.");

                (int openEdges, int nonManifoldEdges) = MeshMetrics.CountBoundaryAndNonManifoldEdges(triangles);
                if (openEdges != 0 || nonManifoldEdges != 0)
                {
                    throw new InvalidOperationException(
                        $"Fluid mesh is not watertight: openEdges={openEdges}, nonManifoldEdges={nonManifoldEdges}.");
                }

                if (!MultiPatchMeshPartitioner.IsPointInsideClosedMesh(triangles, fluidSeedPoint))
                {
                    throw new InvalidOperationException(
                        $"FluidSeedPoint ({fluidSeedPoint.X:G6}, {fluidSeedPoint.Y:G6}, {fluidSeedPoint.Z:G6}) is not strictly inside the fluid mesh.");
                }

                List<PatchPlaneSpec> portSpecs = ports.Select(p => p.ToPlaneSpec()).ToList();
                PartitionResult partition = MultiPatchMeshPartitioner.PartitionClosedMesh(triangles, portSpecs);

                string combinedPath = Path.Combine(outputDirectory, $"{sanitizedBase}.stl");
                string multiSolidPath = Path.Combine(outputDirectory, $"{sanitizedBase}_multisolid.stl");

                StlWriter.WriteBinary(combinedPath, triangles);

                var patchPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var patchTriCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var namedReadOnlyPatches = new Dictionary<string, IReadOnlyList<StlTriangle>>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in partition.Patches)
                {
                    string patchFile = Path.Combine(outputDirectory, $"{sanitizedBase}_{kvp.Key}.stl");
                    StlWriter.WriteBinary(patchFile, kvp.Value);
                    patchPaths[kvp.Key] = patchFile;
                    patchTriCounts[kvp.Key] = kvp.Value.Count;
                    namedReadOnlyPatches[kvp.Key] = kvp.Value;
                }

                StlWriter.WriteAsciiMultiSolid(multiSolidPath, namedReadOnlyPatches);

                MeshMeasurement measurement = MeshMetrics.Measure(triangles);

                return new InternalFlowExportResult(
                    combinedPath,
                    multiSolidPath,
                    patchPaths,
                    partition.PatchAreas,
                    patchTriCounts,
                    fluidSeedPoint,
                    measurement);
            }
            finally
            {
                working.Dispose();
            }
        }
    }
}
