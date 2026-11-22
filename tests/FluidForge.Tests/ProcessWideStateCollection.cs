using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// Collects all test classes that mutate <b>process-wide</b> state — environment variables,
    /// <c>Console.SetOut</c>, or the working directory. Otherwise xUnit runs test classes in parallel,
    /// and such a test would alter the environment under an unrelated test while it is reading it.
    ///
    /// <para>
    /// <b>Rule for new tests:</b> Any test class touching one of these must apply
    /// <c>[Collection(ProcessWideStateCollection.Name)]</c>. Currently these are
    /// <c>ProjectPathsTests</c> (environment variable), <c>ConfigLayerTests</c>, and
    /// <c>Su2ReferenceAreaTests</c> (both capture console output).
    /// </para>
    ///
    /// <para>
    /// <b>Background (TODO-33):</b> <c>ProjectPathsTests</c> sets <c>SIM_PROJECTS_DIR</c> to a
    /// test folder containing an empty <c>MantaAuv</c>. <c>MantaProjectFiles.Directory</c> resolves
    /// the project directory via that same variable — when
    /// <c>ProjectLayoutTests.MantaAuv_Names_All_Three_Program_Paths_Explicitly</c> ran concurrently,
    /// it read the wrong <c>simulation.json</c> and failed in roughly every second run.
    /// On inspection, the same pattern appeared with <c>Console.SetOut</c>: two classes redirect
    /// the console simultaneously, and one reads the other's output (rarely, roughly once in ten runs).
    /// </para>
    ///
    /// <para>
    /// <c>DisableParallelization</c> runs this collection sequentially after all parallel collections
    /// have finished. <b>Read-only</b> tests therefore do not need to be placed here — it is
    /// sufficient that mutating tests never overlap with anything else.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class ProcessWideStateCollection
    {
        public const string Name = "Process-wide state";
    }
}
