namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Parameter für die Netzerzeugung einer Simulationsgeometrie.
    /// </summary>
    public sealed record MeshRequest(
        string GeometryPath,
        int Iteration,
        int Variant,
        SimulationConfig Config,
        float ScaleFactor,
        string WorkingDirectory,
        float RefinementFactor = 1.0f,
        string Tag = "")
    {
        /// <summary>
        /// Basis-Suffix für generierte Netzdateien (z. B. "Gen1_Var2" oder "Gen1_Var2_Kal3").
        /// </summary>
        public string BaseSuffix => string.IsNullOrWhiteSpace(Tag)
            ? $"Gen{Iteration}_Var{Variant}"
            : $"Gen{Iteration}_Var{Variant}_{Tag.Trim()}";
    }

    /// <summary>
    /// Erzeugt ein Simulationsnetz (CFD, FEM) aus einer STL-Datei.
    /// </summary>
    public interface IMeshGenerator
    {
        string GenerateMesh(MeshRequest request);

        string GenerateMesh(
            string modelStlPath,
            int iteration, int variant,
            SimulationConfig config,
            float scaleFactor,
            string workingDirectory) =>
            GenerateMesh(new MeshRequest(modelStlPath, iteration, variant, config, scaleFactor, workingDirectory));
    }
}
