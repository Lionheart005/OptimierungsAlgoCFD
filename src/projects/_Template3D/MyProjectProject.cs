using System.Collections.Generic;

using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MyProject
{
    /// <summary>
    /// Project wiring. <b>This class is all the framework needs to know about a project</b> —
    /// <see cref="ProjectRegistry"/> discovers it via reflection (no switch statement or manual list).
    ///
    /// <para>
    /// <b><c>MyProject</c> is a placeholder name.</b>
    /// <c>scripts\new-project.ps1 &lt;Name&gt;</c> copies this folder and replaces the placeholder
    /// in filenames, class names, namespaces, and JSON comment headers.
    /// </para>
    ///
    /// <para>
    /// <b>Why the template itself is not runnable:</b> <c>src/projects/_Template3D/</c> is excluded
    /// from the main application build (<c>FluidForge.csproj</c>) so no placeholder definition
    /// exists without a matching folder, while still being compiled and verified by the test suite.
    /// </para>
    /// </summary>
    public sealed class MyProjectProject : CfdProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> <b>exactly</b>.
        /// <see cref="ProjectRegistry"/> validates both directions at startup.
        /// </summary>
        public override string Name => "MyProject";

        /// <summary>
        /// Layer 1 configuration: code defaults for this project.
        /// <c>project.json</c> overrides these values.
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Initial active parameter values (mm). These names are the contract with the geometry generator.
            BaseParameters = new Dictionary<string, float>
            {
                { "Length", 50.0f },
                { "Width", 30.0f },
                { "Height", 20.0f }
            },

            // Initial mutation step sizes per parameter.
            MaxDeviations = new Dictionary<string, float>
            {
                { "Length", 5.0f },
                { "Width", 3.0f },
                { "Height", 2.0f }
            },

            // Hard bounds. The optimizer never leaves this box.
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Length", (20.0f, 100.0f) },
                { "Width", (10.0f, 80.0f) },
                { "Height", (5.0f, 50.0f) }
            },

            // Empty because this template minimizes drag only. If you add targets here,
            // also list them in MyProjectFitnessCalculator.RequiredTargets.
            OptimizationTargets = new Dictionary<string, float>(),

            // Only lengths belong here: RubberBandScaler scales them to PicoGK's working size
            // and scales reported metrics back. Dimensionless ratios/angles/counts must be omitted.
            DimensionalParameters = new HashSet<string> { "Length", "Width", "Height" }
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MyProjectGeometryGenerator();

        /// <summary>
        /// Validates required optimization targets in the constructor so missing keys fail fast.
        /// </summary>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MyProjectFitnessCalculator(context.Project);

        // ------------------------------------------------------------------
        // Everything not overridden here is inherited from CfdProjectDefinition:
        // PicoGK as geometry kernel and one Gmsh -> SU2 stage configured via
        // solvers/gmsh.json and solvers/su2.json.
        //
        // Override here if your project needs something different:
        //
        //   // Analytical geometry without voxels (no PicoGK, no Xvfb on server):
        //   public override IGeometryKernel CreateKernel() => new DirectGeometryKernel();
        //
        //   // Custom solver pipeline (e.g. multiple stages or a different mesher):
        //   public override SolverStage[] CreateStages(ProjectContext c) => new[]
        //   {
        //       new SolverStage(
        //           new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
        //           new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2")))
        //   };
        // ------------------------------------------------------------------
    }
}
