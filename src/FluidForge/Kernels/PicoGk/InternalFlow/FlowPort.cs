using System;
using System.Numerics;
using FluidForge.Core;

namespace FluidForge.Kernels.PicoGk.InternalFlow
{
    /// <summary>
    /// Defines a planar inlet or outlet boundary port on an internal fluid volume.
    /// </summary>
    public sealed record FlowPort
    {
        public FlowPort(
            string Name,
            Vector3 Center,
            Vector3 OutwardNormal,
            float RadiusMm,
            float PlaneToleranceMm = 0.8f,
            float CaptureMarginMm = 1.5f)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Name);
            if (RadiusMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(RadiusMm), "RadiusMm must be positive.");
            if (PlaneToleranceMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(PlaneToleranceMm), "PlaneToleranceMm must be non-negative.");
            if (CaptureMarginMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(CaptureMarginMm), "CaptureMarginMm must be non-negative.");

            float len = OutwardNormal.Length();
            if (len <= 1e-6f)
                throw new ArgumentException("OutwardNormal must be non-zero.", nameof(OutwardNormal));

            this.Name = Name.Trim();
            this.Center = Center;
            this.OutwardNormal = OutwardNormal / len;
            this.RadiusMm = RadiusMm;
            this.PlaneToleranceMm = PlaneToleranceMm;
            this.CaptureMarginMm = CaptureMarginMm;
        }

        public string Name { get; init; }
        public Vector3 Center { get; init; }
        public Vector3 OutwardNormal { get; init; }
        public float RadiusMm { get; init; }
        public float PlaneToleranceMm { get; init; }
        public float CaptureMarginMm { get; init; }

        /// <summary>
        /// Converts this port into a kernel-agnostic <see cref="PatchPlaneSpec"/> for
        /// <see cref="MultiPatchMeshPartitioner"/>.
        /// </summary>
        public PatchPlaneSpec ToPlaneSpec() =>
            new PatchPlaneSpec(
                Name,
                Center,
                OutwardNormal,
                RadiusMm + CaptureMarginMm,
                PlaneToleranceMm);
    }
}
