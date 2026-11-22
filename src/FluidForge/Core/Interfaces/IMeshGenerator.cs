namespace FluidForge.Core
{
    /// <summary>
    /// Parameters for generating a simulation mesh from a geometry.
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
        /// Base suffix for generated mesh files (e.g., "Gen1_Var2" or "Gen1_Var2_Cal3").
        /// </summary>
        public string BaseSuffix => string.IsNullOrWhiteSpace(Tag)
            ? $"Gen{Iteration}_Var{Variant}"
            : $"Gen{Iteration}_Var{Variant}_{Tag.Trim()}";
    }

    /// <summary>
    /// Generates a simulation mesh (CFD, FEM) from an STL or contour file.
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
