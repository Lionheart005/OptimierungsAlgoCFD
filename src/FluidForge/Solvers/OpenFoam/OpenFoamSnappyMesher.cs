using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FluidForge.Core;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Generates 3D OpenFOAM volume meshes using <c>snappyHexMesh</c> (default) or <c>cfMesh</c>
    /// (<c>cartesianMesh</c>), validates quality with <c>checkMesh</c>, and exposes cell and
    /// boundary-layer metrics via <see cref="IMeshMetricsSource"/>.
    /// </summary>
    public sealed class OpenFoamSnappyMesher : IMeshGenerator, IMeshMetricsSource, IMeshResolutionTarget
    {
        private readonly OpenFoamOptions _options;
        private readonly IOpenFoamCommandRunner _runner;
        private Dictionary<string, float> _lastMetrics = new();
        private bool _versionReported;

        /// <param name="options">OpenFOAM options loaded from <c>solvers/openfoam.json</c>; defaults apply if null.</param>
        /// <param name="runner">Command runner (injectable for unit tests).</param>
        public OpenFoamSnappyMesher(OpenFoamOptions? options = null, IOpenFoamCommandRunner? runner = null)
        {
            _options = options ?? new OpenFoamOptions();
            _runner = runner ?? new OpenFoamProcessRunner();
        }

        public OpenFoamOptions Options => _options;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, float> LastMeshMetrics => _lastMetrics;

        /// <inheritdoc />
        public float TargetSurfaceCellSizeMm => _options.BaseCellSizeMm > 0f ? _options.BaseCellSizeMm : 0f;

        /// <inheritdoc />
        public string GenerateMesh(MeshRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            _lastMetrics = new Dictionary<string, float>();

            string caseDir = Path.Combine(request.WorkingDirectory, $"OpenFoamCase_{request.BaseSuffix}");
            CleanPreviousMeshArtifacts(caseDir);

            OpenFoamCaseInfo caseInfo = OpenFoamCaseGenerator.Generate(caseDir, request, _options);

            if (!_versionReported)
            {
                string? version = OpenFoamProcessRunner.DetectOpenFoamVersion(_runner, _options, caseDir);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    _versionReported = true;
                    RunInfoWriter.SetProgramVersion(request.WorkingDirectory, "OpenFOAM", version);
                }
            }

            string backend = (_options.MesherBackend ?? "SnappyHexMesh").Trim();
            bool useCfMesh = backend.Equals("CfMesh", StringComparison.OrdinalIgnoreCase);

            int mpiCores = Math.Max(1, request.Config.MpiCores);
            string snappyLog = string.Empty;
            bool usedFallback = false;

            if (useCfMesh)
            {
                RunCfMesh(caseDir);
            }
            else
            {
                try
                {
                    snappyLog = RunSnappyHexMesh(caseDir, mpiCores);
                }
                catch (MeshGenerationException) when (_options.FallbackToCfMesh)
                {
                    SimulationLog.Warning("snappyHexMesh failed — falling back to cfMesh (cartesianMesh).");
                    CleanPolyMeshOnly(caseDir);
                    RunCfMesh(caseDir);
                    snappyLog = string.Empty;
                    usedFallback = true;
                }
            }

            (OpenFoamMeshQualityReport report, OpenFoamMeshGateResult gate) = RunAndValidateCheckMesh(caseDir, snappyLog);

            if (!gate.Passed && !useCfMesh && !usedFallback && _options.FallbackToCfMesh)
            {
                SimulationLog.Warning(
                    $"snappyHexMesh quality gate failed ({string.Join("; ", gate.Violations)}) — falling back to cfMesh.");
                CleanPolyMeshOnly(caseDir);
                RunCfMesh(caseDir);
                usedFallback = true;
                (report, gate) = RunAndValidateCheckMesh(caseDir, string.Empty);
            }

            if (!gate.Passed)
            {
                throw new MeshGenerationException(
                    $"OpenFOAM mesh failed quality checks: {string.Join(" ", gate.Violations)}");
            }

            PopulateMetrics(report, usedFallback, caseInfo.MeshPlan);
            File.WriteAllText(Path.Combine(caseDir, "case.foam"), string.Empty);

            SimulationLog.Detail(
                $"[OK] OpenFOAM mesh generated ({report.Cells:N0} cells, max non-ortho "
                + $"{(float.IsFinite(report.MaxNonOrthogonality) ? report.MaxNonOrthogonality.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")} deg"
                + (float.IsFinite(report.LayerCoveragePercent)
                    ? $", layer coverage {report.LayerCoveragePercent.ToString("0.0", CultureInfo.InvariantCulture)}%"
                    : string.Empty)
                + ").");

            return caseDir;
        }

        private string RunSnappyHexMesh(string caseDir, int mpiCores)
        {
            OpenFoamCommandResult blockRes = _runner.Run(caseDir, "blockMesh", _options, mpiCores: 1);
            if (blockRes.ExitCode != 0)
            {
                throw new MeshGenerationException(
                    $"blockMesh failed (ExitCode {blockRes.ExitCode}): {FirstErrorLine(blockRes.CombinedOutput)}");
            }

            string featureDict = Path.Combine(caseDir, "system", "surfaceFeatureExtractDict");
            if (File.Exists(featureDict))
            {
                OpenFoamCommandResult featRes = _runner.Run(caseDir, "surfaceFeatureExtract", _options, mpiCores: 1);
                if (featRes.ExitCode != 0)
                {
                    SimulationLog.Warning(
                        $"surfaceFeatureExtract exited with code {featRes.ExitCode}: {FirstErrorLine(featRes.CombinedOutput)}");
                }
            }

            if (mpiCores > 1)
            {
                string zeroDir = Path.Combine(caseDir, "0");
                string zeroBackup = Path.Combine(caseDir, "_0_meshing_backup");
                bool stashedZero = false;
                if (Directory.Exists(zeroDir))
                {
                    if (Directory.Exists(zeroBackup))
                        Directory.Delete(zeroBackup, recursive: true);
                    Directory.Move(zeroDir, zeroBackup);
                    stashedZero = true;
                }

                try
                {
                    OpenFoamCommandResult decompRes = _runner.Run(caseDir, "decomposePar -force", _options, mpiCores: 1);
                    if (decompRes.ExitCode != 0)
                    {
                        throw new MeshGenerationException(
                            $"decomposePar (meshing) failed (ExitCode {decompRes.ExitCode}): {FirstErrorLine(decompRes.CombinedOutput)}");
                    }

                    string parallelCmd = OpenFoamProcessRunner.BuildParallelToolCommand(
                        "snappyHexMesh", "-overwrite", _options, mpiCores);
                    OpenFoamCommandResult snappyRes = _runner.Run(caseDir, parallelCmd, _options, mpiCores);
                    if (snappyRes.ExitCode != 0)
                    {
                        throw new MeshGenerationException(
                            $"snappyHexMesh -parallel failed (ExitCode {snappyRes.ExitCode}): {FirstErrorLine(snappyRes.CombinedOutput)}");
                    }

                    OpenFoamCommandResult reconRes = _runner.Run(caseDir, "reconstructParMesh -constant", _options, mpiCores: 1);
                    if (reconRes.ExitCode != 0)
                    {
                        throw new MeshGenerationException(
                            $"reconstructParMesh failed (ExitCode {reconRes.ExitCode}): {FirstErrorLine(reconRes.CombinedOutput)}");
                    }

                    DeleteProcessorDirectories(caseDir);
                    return snappyRes.CombinedOutput;
                }
                finally
                {
                    if (stashedZero && Directory.Exists(zeroBackup) && !Directory.Exists(zeroDir))
                    {
                        Directory.Move(zeroBackup, zeroDir);
                    }
                }
            }

            OpenFoamCommandResult serialSnappy = _runner.Run(caseDir, "snappyHexMesh -overwrite", _options, mpiCores: 1);
            if (serialSnappy.ExitCode != 0)
            {
                throw new MeshGenerationException(
                    $"snappyHexMesh failed (ExitCode {serialSnappy.ExitCode}): {FirstErrorLine(serialSnappy.CombinedOutput)}");
            }

            return serialSnappy.CombinedOutput;
        }

        private void RunCfMesh(string caseDir)
        {
            OpenFoamCommandResult cfRes = _runner.Run(caseDir, "cartesianMesh", _options, mpiCores: 1);
            if (cfRes.ExitCode != 0)
            {
                throw new MeshGenerationException(
                    $"cfMesh (cartesianMesh) failed (ExitCode {cfRes.ExitCode}): {FirstErrorLine(cfRes.CombinedOutput)}");
            }
        }

        private (OpenFoamMeshQualityReport Report, OpenFoamMeshGateResult Gate) RunAndValidateCheckMesh(
            string caseDir,
            string snappyLog)
        {
            OpenFoamCommandResult checkRes = _runner.Run(caseDir, "checkMesh", _options, mpiCores: 1);
            if (checkRes.ExitCode != 0)
            {
                throw new MeshGenerationException(
                    $"checkMesh failed (ExitCode {checkRes.ExitCode}): {FirstErrorLine(checkRes.CombinedOutput)}");
            }

            string wallName = string.IsNullOrWhiteSpace(_options.WallPatchName) ? "wall" : _options.WallPatchName.Trim();
            OpenFoamMeshQualityReport report = OpenFoamMeshQualityParser.Parse(checkRes.CombinedOutput, snappyLog, wallName);
            OpenFoamMeshGateResult gate = OpenFoamMeshQualityParser.EvaluateGates(report, _options);
            return (report, gate);
        }

        private void PopulateMetrics(OpenFoamMeshQualityReport report, bool usedFallback, OpenFoamMeshPlan? meshPlan)
        {
            _lastMetrics["MeshCells"] = report.Cells;
            _lastMetrics["MeshCells_Hexa"] = report.Hexahedra;
            _lastMetrics["MeshCells_Prism"] = report.Prisms;
            _lastMetrics["MeshCells_Tet"] = report.Tetrahedra;
            _lastMetrics["MeshCells_Poly"] = report.Polyhedra;

            if (float.IsFinite(report.MaxNonOrthogonality))
                _lastMetrics["MeshNonOrthoMax"] = report.MaxNonOrthogonality;
            if (float.IsFinite(report.MaxSkewness))
                _lastMetrics["MeshSkewnessMax"] = report.MaxSkewness;

            if (_options.AddLayers)
            {
                _lastMetrics["BoundaryLayerLayers"] = meshPlan?.EffectiveLayers ?? _options.BoundaryLayerLayers;
                if (float.IsFinite(report.LayerCoveragePercent))
                    _lastMetrics["BoundaryLayerCoveragePct"] = report.LayerCoveragePercent;
                if (float.IsFinite(report.AvgLayerThicknessM))
                    _lastMetrics["BoundaryLayerThicknessMm"] = report.AvgLayerThicknessM * 1000f;
            }

            _lastMetrics[ModelRecord.MeshFallbackKey] = usedFallback ? 1f : 0f;
        }

        private static void CleanPreviousMeshArtifacts(string caseDir)
        {
            CleanPolyMeshOnly(caseDir);
            DeleteProcessorDirectories(caseDir);
        }

        private static void CleanPolyMeshOnly(string caseDir)
        {
            string polyMeshDir = Path.Combine(caseDir, "constant", "polyMesh");
            if (Directory.Exists(polyMeshDir))
            {
                try { Directory.Delete(polyMeshDir, recursive: true); } catch { }
            }
        }

        private static void DeleteProcessorDirectories(string caseDir)
        {
            if (!Directory.Exists(caseDir)) return;
            foreach (string dir in Directory.EnumerateDirectories(caseDir, "processor*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        private static string FirstErrorLine(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "(no output)";
            string[] lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Error", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < lines.Length && !string.IsNullOrWhiteSpace(lines[i + 1]))
                        return $"{line} {lines[i + 1].Trim()}";
                    return line;
                }
            }
            return output.Trim().Split('\n')[^1].Trim();
        }
    }
}
