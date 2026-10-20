using System.Collections.Generic;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.TestprojektBaars
{
    /// <summary>
    /// Die Verdrahtung des Testprojekts Baars (NACA-Tragflügelprofil).
    /// </summary>
    public sealed class TestprojektBaarsProject : CfdProjectDefinition
    {
        /// <summary>
        /// Muss exakt dem Ordnernamen unter <c>src/projects/</c> entsprechen.
        /// </summary>
        public override string Name => "TestprojektBaars";

        /// <summary>
        /// Schicht 1 der Konfiguration: Code-Standards für die 3 NACA-Profilparameter.
        /// Alle 3 Parameter sind dimensionslose Prozentwerte relativ zur Sehnenlänge.
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Startwerte der aktiven Parameter (% der Sehnenlänge c):
            // NACA 0012: 0% Wölbung, 40% Wölbungsrücklage, 12% maximale Dicke.
            BaseParameters = new Dictionary<string, float>
            {
                { "MaxCamber", 0.0f },
                { "CamberPosition", 40.0f },
                { "MaxThickness", 12.0f }
            },

            // Anfängliche Mutationsstärke je Parameter:
            MaxDeviations = new Dictionary<string, float>
            {
                { "MaxCamber", 0.8f },
                { "CamberPosition", 6.0f },
                { "MaxThickness", 2.0f }
            },

            // Harte Grenzen für physikalische Plausibilität:
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "MaxCamber", (0.0f, 8.0f) },
                { "CamberPosition", (15.0f, 75.0f) },
                { "MaxThickness", (6.0f, 24.0f) }
            },

            // Leer, da die Fitness als Gleitzahl CL / CD formuliert ist und keine externen Zielwerte benötigt:
            OptimizationTargets = new Dictionary<string, float>(),

            // Leer, da alle drei Parameter dimensionslose Prozentwerte relativ zur Sehnenlänge sind:
            DimensionalParameters = new HashSet<string>()
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new TestprojektBaarsGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new TestprojektBaarsFitnessCalculator(context.Project);
    }
}
