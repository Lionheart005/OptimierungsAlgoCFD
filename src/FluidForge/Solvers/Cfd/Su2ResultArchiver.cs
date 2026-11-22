using System;
using System.IO;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Archives SU2 run artifacts (surface/volume VTU, CSV, history, config)
    /// as well as error logs for failed simulations.
    /// </summary>
    internal static class Su2ResultArchiver
    {
        /// <summary>
        /// Builds the filename stem for archiving history and configuration files.
        /// Format: <c>Gen{i}_Var{v}</c> or <c>Gen{i}_Var{v}_{tag}</c>.
        /// </summary>
        public static string ArchiveBaseName(ModelRecord record, string? tag = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            return string.IsNullOrWhiteSpace(tag)
                ? $"Gen{record.Iteration}_Var{record.Variant}"
                : $"Gen{record.Iteration}_Var{record.Variant}_{tag.Trim()}";
        }

        /// <summary>Extracts the tag from the mesh name (<c>Model_Gen1_Var1_Cal5.su2</c> -> <c>Cal5</c>), or empty.</summary>
        public static string ExtractTag(string meshPath, ModelRecord record)
        {
            string plain = ArchiveBaseName(record);
            string baseName = ArchiveBaseNameFromMesh(meshPath, record);
            return baseName.StartsWith(plain + "_", StringComparison.Ordinal)
                ? baseName.Substring(plain.Length + 1)
                : string.Empty;
        }

        /// <summary>
        /// Whether the tag belongs to a control run (calibration stages <c>Cal...</c>, fine check <c>Fine...</c>,
        /// <c>Recheck</c>). The gatekeeper check (without tag) is intentionally excluded because it is compared
        /// against normal variants and must use the same accuracy.
        /// </summary>
        public static bool IsControlRunTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string t = tag.Trim();
            return t.StartsWith("Cal", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Fine", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Recheck", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Archive base name from mesh filename: <c>Model_Gen1_Var1_Cal5.su2</c> -> <c>Gen1_Var1_Cal5</c>.
        /// Falls back to <see cref="ArchiveBaseName(ModelRecord, string?)"/> without tag if the name does not match.
        /// </summary>
        public static string ArchiveBaseNameFromMesh(string meshPath, ModelRecord record)
        {
            string plain = ArchiveBaseName(record);
            string name = Path.GetFileNameWithoutExtension(meshPath ?? string.Empty);
            const string prefix = "Model_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return plain;

            string rest = name.Substring(prefix.Length);
            if (rest == plain) return plain;
            if (rest.StartsWith(plain + "_", StringComparison.Ordinal))
                return ArchiveBaseName(record, rest.Substring(plain.Length + 1));
            return plain;
        }

        internal static void TryCopyArchiveFile(string source, string destination)
        {
            try
            {
                if (File.Exists(source))
                {
                    File.Copy(source, destination, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{OutputConventions.WarningTag} Failed to archive file ({Path.GetFileName(source)} -> {destination}): {ex.Message}");
            }
        }

        internal static void ArchiveRunArtifacts(
            string workingDirectory,
            string meshPath,
            ModelRecord record,
            string historyPath,
            string configPath)
        {
            string analysisResultsDir = Path.Combine(workingDirectory, OutputConventions.AnalysisDirectory);
            Directory.CreateDirectory(analysisResultsDir);

            // Include tag (Cal1..5, Fine, Recheck) so control runs do not overwrite the variant's own files.
            string archiveBase = ArchiveBaseNameFromMesh(meshPath, record);

            string defaultSurfaceVtu = Path.Combine(workingDirectory, "surface.vtu");
            string savedSurfaceVtu = Path.Combine(analysisResultsDir, $"Surface_{archiveBase}.vtu");
            if (File.Exists(defaultSurfaceVtu)) File.Copy(defaultSurfaceVtu, savedSurfaceVtu, true);

            string defaultSurfaceCsv = Path.Combine(workingDirectory, "surface.csv");
            string savedSurfaceCsv = Path.Combine(analysisResultsDir, $"Surface_{archiveBase}.csv");
            if (File.Exists(defaultSurfaceCsv)) File.Copy(defaultSurfaceCsv, savedSurfaceCsv, true);

            string defaultVolumeVtu = Path.Combine(workingDirectory, "vol_solution.vtu");
            string savedVolumeVtu = Path.Combine(analysisResultsDir, $"Volume_{archiveBase}.vtu");
            if (File.Exists(defaultVolumeVtu)) File.Copy(defaultVolumeVtu, savedVolumeVtu, true);

            // Save convergence history and solver config
            string historiesDir = Path.Combine(analysisResultsDir, OutputConventions.HistoriesSubDirectory);
            Directory.CreateDirectory(historiesDir);
            TryCopyArchiveFile(historyPath, Path.Combine(historiesDir, $"History_{archiveBase}.csv"));
            TryCopyArchiveFile(configPath, Path.Combine(historiesDir, $"Config_{archiveBase}.cfg"));
        }

        internal static void ArchiveFailureArtifacts(
            string workingDirectory,
            string meshPath,
            ModelRecord record,
            string historyPath,
            string configPath,
            string? su2Output)
        {
            string errorLogDirectory = Path.Combine(workingDirectory, OutputConventions.ErrorLogsDirectory);
            Directory.CreateDirectory(errorLogDirectory);

            string modelName = Path.GetFileNameWithoutExtension(meshPath);
            string errorLogPath = Path.Combine(errorLogDirectory, $"SU2_Error_{modelName}.txt");
            File.WriteAllText(
                errorLogPath,
                string.IsNullOrWhiteSpace(su2Output) ? "No console output captured from SU2." : su2Output);
            Console.WriteLine();
            SimulationLog.Warning($"CFD simulation aborted for {modelName}.");
            SimulationLog.Detail($"Details exported to: {OutputConventions.ResultsRootDirectory}/{OutputConventions.ErrorLogsDirectory}/{Path.GetFileName(errorLogPath)}");
            Console.WriteLine();

            record.SimulationFailed = true;
            string errorBase = ArchiveBaseNameFromMesh(meshPath, record);
            TryCopyArchiveFile(historyPath, Path.Combine(errorLogDirectory, $"History_{errorBase}.csv"));
            TryCopyArchiveFile(configPath, Path.Combine(errorLogDirectory, $"Config_{errorBase}.cfg"));
        }
    }
}
