using System.Collections.Generic;

using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MyProject2D
{
    /// <summary>
    /// Project wiring for a 2D CFD project.
    /// Based on <see cref="Cfd2DProjectDefinition"/>: uses the direct 2D kernel
    /// (no PicoGK, no Xvfb) and connects Gmsh2DMesher with 2D SU2.
    ///
    /// <para>
    /// <b><c>MyProject2D</c> is a placeholder name.</b>
    /// <c>scripts\new-project.ps1 &lt;Name&gt; -Template 2D</c> copies this folder and replaces the
    /// placeholder in filenames, class names, namespaces, and JSON comment headers.
    /// </para>
    ///
    /// <para>
    /// <b>Why the template itself is not runnable:</b> <c>src/projects/_Template2D/</c> is excluded
    /// from the main application build (<c>FluidForge.csproj</c>), while still being compiled and
    /// verified by the test suite so interface changes are caught immediately.
    /// </para>
    /// </summary>
    public sealed class MyProject2DProject : Cfd2DProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> <b>exactly</b>.
        /// </summary>
        public override string Name => "MyProject2D";

        /// <summary>
        /// Layer 1 configuration: code defaults for this 2D project.
        /// <c>project.json</c> overrides these values.
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Initial active parameter values (mm).
            BaseParameters = new Dictionary<string, float>
            {
                { "Chord", 50.0f },
                { "Thickness", 6.0f }
            },

            // Initial mutation step sizes per parameter:
            MaxDeviations = new Dictionary<string, float>
            {
                { "Chord", 5.0f },
                { "Thickness", 0.6f }
            },

            // Hard bounds for physical plausibility:
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Chord", (20.0f, 100.0f) },
                { "Thickness", (2.0f, 25.0f) }
            },

            // Empty because the default fitness minimizes drag only:
            OptimizationTargets = new Dictionary<string, float>(),

            // Length parameters in mm:
            DimensionalParameters = new HashSet<string> { "Chord", "Thickness" }
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MyProject2DGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MyProject2DFitnessCalculator(context.Project);
    }
}
