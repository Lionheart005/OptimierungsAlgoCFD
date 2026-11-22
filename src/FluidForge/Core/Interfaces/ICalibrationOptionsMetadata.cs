using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Kalibrierungsrelevante Metadaten eines Solvers, damit <see cref="MeshCalibration"/>
    /// y+-Limits und überwachte Größen ohne Solver-Schicht-Abhängigkeit auslesen kann.
    /// </summary>
    public interface ICalibrationSolverOptions
    {
        float TargetYPlus { get; }
        float YPlusWarnFactor { get; }
        int Dimensions { get; }
        IReadOnlyCollection<string> MonitoredMetricNames { get; }
    }

    /// <summary>
    /// Kalibrierungsrelevante Metadaten eines Vernetzers für die Start-Konsistenzprüfung
    /// in <see cref="MeshCalibration"/>.
    /// </summary>
    public interface ICalibrationMesherOptions
    {
        float TargetYPlus { get; }
        string? BoundaryLayerMode { get; }
        bool Is3D { get; }
    }
}
