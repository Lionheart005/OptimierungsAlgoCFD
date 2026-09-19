using System.Collections.Generic;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;

namespace MyPicoGkProject.Projects.MantaRauchtest
{
    /// <summary>
    /// Ein <b>zweites</b> Projekt neben MantaAuv — der Praxistest für den ganzen Umbau
    /// (TODO-22 bis TODO-30): kommt das Framework damit klar, ein anderes Projekt zu starten?
    ///
    /// <para>
    /// Es rechnet mit <b>derselben Logik</b> wie MantaAuv, aber mit eigenem Laufumfang
    /// (2 × 2 statt 10 × 10) und eigenem Ergebnisordner. Angelegt mit
    /// <c>.\scripts\new-project.ps1 MantaRauchtest</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Warum Geometrie und Fitness wiederverwendet und nicht abgeschrieben sind:</b> dieser
    /// Lauf soll genau eine Frage beantworten — trägt die Umschaltkette? Wären die Klassen
    /// kopiert, könnte eine Abweichung gegenüber einem MantaAuv-Lauf auch von einem
    /// Kopierfehler kommen. So kann sie nur aus der Konfiguration stammen. Ein echtes zweites
    /// Bauteil bringt natürlich seine eigenen Klassen mit; die Vorlage zeigt, wie.
    /// </para>
    ///
    /// <para>
    /// Damit prüft dieser Ordner trotzdem alles, worauf es ankommt: eigener Projektordner,
    /// eigene vier JSON-Dateien über alle Konfigurationsschichten, eigene Auflösung über die
    /// <see cref="ProjectRegistry"/>, eigener Ergebnisordner
    /// <c>Ergebnisse/MantaRauchtest/</c> — und <b>keine</b> Zeile im Framework.
    /// </para>
    /// </summary>
    public sealed class MantaRauchtestProject : CfdProjectDefinition
    {
        /// <summary>Muss exakt dem Ordnernamen unter <c>src/projects/</c> entsprechen.</summary>
        public override string Name => "MantaRauchtest";

        /// <summary>
        /// Dieselben Code-Standards wie MantaAuv — <c>project.json</c> nebenan überlagert sie.
        /// Bewusst über <see cref="MantaProjectConfig.Create"/> und nicht abgeschrieben: die
        /// Parameternamen müssen zum wiederverwendeten Geometrie-Generator passen, und eine
        /// Kopie könnte hier auseinanderlaufen.
        ///
        /// <para>
        /// <c>ProjectName</c> wird überschrieben, weil <see cref="MantaProjectConfig"/> dort
        /// „MantaAuv" einträgt; maßgeblich ist aber der Ordnername, den <c>Program.cs</c>
        /// ohnehin setzt. Die Zeile steht nur da, damit eine Fehlermeldung aus dem
        /// Fitness-Rechner nicht auf das falsche Projekt zeigt.
        /// </para>
        /// </summary>
        public override ProjectConfig CreateDefaults()
        {
            ProjectConfig defaults = MantaProjectConfig.Create();
            defaults.ProjectName = Name;

            return defaults;
        }

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MantaGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MantaFitnessCalculator(context.Project);
    }
}
