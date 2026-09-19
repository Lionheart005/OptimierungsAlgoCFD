using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MantaAuv
{
    /// <summary>
    /// Das Manta-Ray AUV, verdrahtet (TODO-23). Diese Klasse ersetzt den <c>case "MantaAuv":</c>,
    /// der vorher in <c>Program.cs</c> stand.
    ///
    /// <para>
    /// Kernel und Solver-Kette kommen unverändert aus <see cref="CfdProjectDefinition"/>:
    /// PicoGK, Gmsh, SU2 — genau das, was der <c>switch</c> vorher fest verbunden hat.
    /// </para>
    /// </summary>
    public sealed class MantaProject : CfdProjectDefinition
    {
        /// <summary>Muss dem Ordnernamen unter <c>src/projects/</c> entsprechen.</summary>
        public override string Name => "MantaAuv";

        /// <inheritdoc/>
        public override ProjectConfig CreateDefaults() => MantaProjectConfig.Create();

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MantaGeometryGenerator();

        /// <summary>
        /// Prüft im Konstruktor, ob die Pflicht-Ziele vorliegen (TODO-16) — der Lauf bricht
        /// dadurch hier ab und nicht erst nach Stunden Rechenzeit.
        /// </summary>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MantaFitnessCalculator(context.Project);
    }
}
