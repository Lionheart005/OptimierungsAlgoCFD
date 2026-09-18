namespace MyPicoGkProject
{
    /// <summary>
    /// Erzeugt ein Simulationsnetz (CFD, FEM) aus einer STL-Datei.
    /// </summary>
    public interface IMeshGenerator
    {
        string GenerateMesh(
            string modelStlPath,
            int iteration, int variant,
            SimulationConfig config,
            float scaleFactor,
            string workingDirectory);
    }
}
