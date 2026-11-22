using System;
using System.IO;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Archiviert SU2-Laufartefakte (Oberflächen-/Volumen-VTU, CSV, History, Config)
    /// sowie Fehlerprotokolle bei abgebrochenen Simulationen.
    /// </summary>
    internal static class Su2ResultArchiver
    {
        /// <summary>
        /// Bildet den Dateinamen-Stamm für die Archivierung von Verlaufs- und Konfigurationsdateien (TODO-35).
        /// Format: <c>Gen{i}_Var{v}</c> bzw. <c>Gen{i}_Var{v}_{tag}</c>.
        /// </summary>
        public static string ArchiveBaseName(ModelRecord record, string? tag = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            return string.IsNullOrWhiteSpace(tag)
                ? $"Gen{record.Iteration}_Var{record.Variant}"
                : $"Gen{record.Iteration}_Var{record.Variant}_{tag.Trim()}";
        }

        /// <summary>Tag aus dem Netznamen (<c>Model_Gen1_Var1_Kal5.su2</c> → <c>Kal5</c>), sonst leer.</summary>
        public static string ExtractTag(string meshPath, ModelRecord record)
        {
            string plain = ArchiveBaseName(record);
            string baseName = ArchiveBaseNameFromMesh(meshPath, record);
            return baseName.StartsWith(plain + "_", StringComparison.Ordinal)
                ? baseName.Substring(plain.Length + 1)
                : string.Empty;
        }

        /// <summary>
        /// Gehört der Tag zu einer Kontrollrechnung (Kalibrierstufen <c>Kal…</c>, Feinprüfung <c>Fein…</c>,
        /// <c>Recheck</c>)? Die Türsteher-Kontrolle (ohne Tag) zählt bewusst nicht dazu: sie wird mit normalen
        /// Varianten verglichen und muss dieselbe Genauigkeit haben (TODO-W4).
        /// </summary>
        public static bool IsControlRunTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string t = tag.Trim();
            return t.StartsWith("Kal", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Fein", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Recheck", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Archivname aus dem Netznamen: <c>Model_Gen1_Var1_Kal5.su2</c> → <c>Gen1_Var1_Kal5</c>.
        /// Der Tag (Kalibrierstufe, Feinprüfung, Recheck) steckt nur im Netznamen. Passt der Name
        /// nicht zum Schema, gilt <see cref="ArchiveBaseName(ModelRecord, string?)"/> ohne Tag.
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
                Console.WriteLine($"[WARNUNG] Datei konnte nicht archiviert werden ({Path.GetFileName(source)} -> {destination}): {ex.Message}");
            }
        }

        internal static void ArchiveRunArtifacts(
            string workingDirectory,
            string meshPath,
            ModelRecord record,
            string historyPath,
            string configPath)
        {
            string analysisResultsDir = Path.Combine(workingDirectory, "Analyseergebnisse");
            Directory.CreateDirectory(analysisResultsDir);

            // Mit Tag (Kal1..5, Fine, Recheck): eigener Name, sonst überschreibt z. B. die
            // Feinprüfung die Dateien der normalen Variante (Servertest 06.11.2026).
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

            // Verlauf und Konfiguration sichern (TODO-35)
            string verlaeufeDir = Path.Combine(analysisResultsDir, "Verlaeufe");
            Directory.CreateDirectory(verlaeufeDir);
            TryCopyArchiveFile(historyPath, Path.Combine(verlaeufeDir, $"History_{archiveBase}.csv"));
            TryCopyArchiveFile(configPath, Path.Combine(verlaeufeDir, $"Config_{archiveBase}.cfg"));
        }

        internal static void ArchiveFailureArtifacts(
            string workingDirectory,
            string meshPath,
            ModelRecord record,
            string historyPath,
            string configPath,
            string? su2Output)
        {
            string errorLogDirectory = Path.Combine(workingDirectory, "FehlerLogs");
            Directory.CreateDirectory(errorLogDirectory);

            string modelName = Path.GetFileNameWithoutExtension(meshPath);
            string errorLogPath = Path.Combine(errorLogDirectory, $"SU2_Error_{modelName}.txt");
            File.WriteAllText(
                errorLogPath,
                string.IsNullOrWhiteSpace(su2Output) ? "Kein Konsolen-Output von SU2 abgefangen." : su2Output);
            Console.WriteLine();
            SimulationLog.Warning($"CFD-Simulation für {modelName} abgebrochen.");
            SimulationLog.Detail($"Details exportiert nach: Ergebnisse/FehlerLogs/{Path.GetFileName(errorLogPath)}");
            Console.WriteLine();

            record.SimulationFailed = true;
            string errorBase = ArchiveBaseNameFromMesh(meshPath, record);
            TryCopyArchiveFile(historyPath, Path.Combine(errorLogDirectory, $"History_{errorBase}.csv"));
            TryCopyArchiveFile(configPath, Path.Combine(errorLogDirectory, $"Config_{errorBase}.cfg"));
        }
    }
}
