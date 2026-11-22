using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Calibration-relevant solver metadata so <see cref="MeshCalibration"/> can read
    /// y+ limits and monitored metrics without depending on the solver layer.
    /// </summary>
    public interface ICalibrationSolverOptions
    {
        float TargetYPlus { get; }
        float YPlusWarnFactor { get; }
        int Dimensions { get; }
        IReadOnlyCollection<string> MonitoredMetricNames { get; }
    }

    /// <summary>
    /// Calibration-relevant mesher metadata for startup consistency checks
    /// in <see cref="MeshCalibration"/>.
    /// </summary>
    public interface ICalibrationMesherOptions
    {
        float TargetYPlus { get; }
        string? BoundaryLayerMode { get; }
        bool Is3D { get; }
    }
}
