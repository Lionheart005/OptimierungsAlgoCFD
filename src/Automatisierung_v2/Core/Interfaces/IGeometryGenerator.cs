using System.Collections.Generic;

namespace MyPicoGkProject
{
    /// <summary>
    /// Erzeugt die 3D-Geometrie für ein Projekt und gibt eine STL + Metriken zurück.
    /// </summary>
    public interface IGeometryGenerator
    {
        GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            float voxelSmoothingIterations,
            int smoothingPremeltingSteps);
    }
}
