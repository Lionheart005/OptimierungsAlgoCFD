using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>Ergebnis eines Gmsh-Aufrufs: Exit-Code und die Fehlerzeile(n) aus der Ausgabe.</summary>
    internal sealed record GmshRunResult(int ExitCode, string? FirstError)
    {
        /// <summary>Für Meldungen: " (Gmsh: …)" oder leer.</summary>
        public string ErrorSuffix => string.IsNullOrEmpty(FirstError) ? string.Empty : $" (Gmsh: {FirstError})";
    }

    /// <summary>
    /// Vereinheitlichte Gmsh-Prozessausführung für 2D und 3D inkl. Fehlerlog-Archivierung.
    /// </summary>
    internal static class GmshProcessRunner
    {
        /// <summary>
        /// Startet Gmsh mit den angegebenen Argumenten und erfasst bei <paramref name="captureOutput"/>
        /// die ersten aussagekräftigen Fehlerzeilen für die Diagnose.
        /// </summary>
        public static GmshRunResult Run(
            SimulationConfig config,
            string arguments,
            string workingDirectory,
            string scriptForErrorLog,
            bool captureOutput = true)
        {
            using Process mesher = new Process();
            mesher.StartInfo.FileName = ExecutableResolver.Resolve(config.GmshPath, "gmsh");
            mesher.StartInfo.Arguments = arguments;
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;

            var errorLines = new List<string>();
            if (captureOutput)
            {
                mesher.StartInfo.RedirectStandardOutput = true;
                mesher.StartInfo.RedirectStandardError = true;
                DataReceivedEventHandler collect = (_, e) =>
                {
                    if (e.Data == null) return;
                    string clean = GmshErrorParser.StripAnsi(e.Data);
                    if (clean.StartsWith("Error", StringComparison.Ordinal))
                    {
                        lock (errorLines) errorLines.Add(clean);
                    }
                };
                mesher.OutputDataReceived += collect;
                mesher.ErrorDataReceived += collect;
            }

            try
            {
                mesher.Start();
                if (captureOutput)
                {
                    mesher.BeginOutputReadLine();
                    mesher.BeginErrorReadLine();
                }

                mesher.WaitForExit();
                if (!captureOutput)
                    return new GmshRunResult(mesher.ExitCode, null);

                string[] lines;
                lock (errorLines) lines = errorLines.ToArray();
                return new GmshRunResult(mesher.ExitCode, GmshErrorParser.ExtractGmshError(lines));
            }
            catch (Exception ex)
            {
                CopyGeoToFehlerLogs(scriptForErrorLog, workingDirectory, "Startfehler");
                throw new Exception($"Gmsh konnte nicht gestartet werden: {ex.Message}. .geo-Skript nach FehlerLogs kopiert.", ex);
            }
        }

        /// <summary>
        /// Kopiert ein fehlerhaftes <c>.geo</c>- bzw. <c>.pos</c>-Skript defensiv in <c>FehlerLogs/</c>.
        /// </summary>
        public static void CopyGeoToFehlerLogs(string geoPath, string workingDirectory, string errorLabel = "")
        {
            try
            {
                string errorLogDir = Path.Combine(workingDirectory, "FehlerLogs");
                Directory.CreateDirectory(errorLogDir);
                string destGeoPath = Path.Combine(errorLogDir, Path.GetFileName(geoPath));
                if (File.Exists(geoPath))
                {
                    File.Copy(geoPath, destGeoPath, overwrite: true);
                    if (!string.IsNullOrWhiteSpace(errorLabel))
                    {
                        SimulationLog.Detail($"[FEHLER] {errorLabel}: {Path.GetFileName(geoPath)} nach FehlerLogs kopiert.");
                    }
                }
            }
            catch { }
        }
    }
}
