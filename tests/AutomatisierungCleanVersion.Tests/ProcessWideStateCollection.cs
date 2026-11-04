using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Sammelt alle Testklassen, die <b>prozessweiten</b> Zustand verändern — Umgebungsvariablen,
    /// <c>Console.SetOut</c>, das Arbeitsverzeichnis. xUnit führt Testklassen sonst parallel aus,
    /// und ein solcher Test verbiegt dann einem völlig fremden Test die Umgebung, während der
    /// gerade liest.
    ///
    /// <para>
    /// <b>Regel für neue Tests:</b> Wer eines davon anfasst, setzt
    /// <c>[Collection(ProcessWideStateCollection.Name)]</c> an seine Klasse. Heute sind das
    /// <c>ProjectPathsTests</c> (Umgebungsvariable), <c>ConfigLayerTests</c> und
    /// <c>Su2ReferenceAreaTests</c> (beide lesen die Konsole mit).
    /// </para>
    ///
    /// <para>
    /// <b>Der Anlass (TODO-33):</b> <c>ProjectPathsTests</c> setzt <c>SIM_PROJECTS_DIR</c> auf
    /// einen Testordner mit einer leeren <c>MantaAuv</c>. <c>MantaProjectFiles.Directory</c> löst
    /// den Projektordner über genau diese Variable auf — lief
    /// <c>ProjectLayoutTests.MantaAuv_Names_All_Three_Program_Paths_Explicitly</c> zur selben Zeit,
    /// las er die falsche <c>simulation.json</c> und wurde rot. In etwa jedem zweiten Lauf.
    /// Beim Nachprüfen fiel dasselbe Muster mit <c>Console.SetOut</c> auf: zwei Klassen biegen
    /// die Konsole gleichzeitig um, und eine liest dann die Ausgabe der anderen (selten, etwa
    /// einer von zehn Läufen).
    /// </para>
    ///
    /// <para>
    /// <c>DisableParallelization</c> lässt diese Collection erst laufen, wenn alle parallelen
    /// fertig sind, und dann der Reihe nach. Die <b>lesenden</b> Tests müssen deshalb nicht
    /// hierher — es reicht, dass die schreibenden mit niemandem überlappen.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class ProcessWideStateCollection
    {
        public const string Name = "Prozessweiter Zustand";
    }
}
