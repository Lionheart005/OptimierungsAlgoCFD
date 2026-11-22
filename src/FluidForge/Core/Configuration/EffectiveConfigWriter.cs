using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Writes <c>results/&lt;Project&gt;/effective-config.json</c> — the exact configuration
    /// used by this run, including the origin of every key.
    ///
    /// <para>
    /// <b>Purpose:</b> the results CSV survives for months, while the JSON config files may be
    /// edited many times. Without this snapshot, it would later be impossible to tell which mesh
    /// refinement or run budget produced an old dataset. It is therefore placed deliberately
    /// <b>alongside</b> the results rather than with the sources.
    /// </para>
    ///
    /// <para>
    /// This is an output artifact, not a configuration file — so it may be machine-generated.
    /// The hand-authored <c>*.json</c> files under <c>src/projects/</c>, on the other hand,
    /// must never be machine-rewritten, as their inline comments carry essential documentation.
    /// </para>
    /// </summary>
    public static class EffectiveConfigWriter
    {
        /// <summary>Output filename in the working directory.</summary>
        public const string FileName = OutputConventions.EffectiveConfigFileName;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Writes the file into <paramref name="workingDirectory"/> and returns its path.
        /// Must be called <b>after</b> all solver options are loaded — otherwise they would
        /// be missing from the snapshot.
        /// </summary>
        public static string Write(string workingDirectory, ProjectContext context, SimulationContext? simContext = null)
        {
            Directory.CreateDirectory(workingDirectory);

            string path = Path.Combine(workingDirectory, FileName);
            File.WriteAllText(path, Build(context, simContext).ToJsonString(Options));

            return path;
        }

        /// <summary>Builds the JSON object. Separated from writing so it can be tested without I/O.</summary>
        public static JsonObject Build(ProjectContext context) => Build(context, null);

        /// <summary>Builds the JSON object, optionally incorporating <see cref="SimulationContext"/>.</summary>
        public static JsonObject Build(ProjectContext context, SimulationContext? simContext)
        {
            var sections = new JsonObject();

            foreach (LoadedConfiguration loaded in context.Loaded)
            {
                var origins = new JsonObject();
                foreach (KeyValuePair<string, string> entry in loaded.Report.Origins.OrderBy(e => e.Key, StringComparer.Ordinal))
                    origins[entry.Key] = entry.Value;

                sections[loaded.Name] = new JsonObject
                {
                    // Values actually used by the run.
                    ["Values"] = JsonSerializer.SerializeToNode(loaded.Value, loaded.Value.GetType(), Options),
                    // Per key, the file that provided it. Keys absent here came from code defaults.
                    ["OriginByKey"] = origins
                };
            }

            var machineOverrides = new JsonArray();
            foreach (MachineOverride entry in context.Loaded.SelectMany(loaded => loaded.Report.MachineOverrides))
            {
                machineOverrides.Add(new JsonObject
                {
                    ["File"] = entry.File,
                    ["Key"] = entry.Key,
                    ["ProjectValue"] = entry.PreviousValue,
                    ["EffectiveValue"] = entry.NewValue
                });
            }

            return new JsonObject
            {
                ["Note"] =
                    "Auto-generated. Records the exact settings used by this run -- source "
                    + "config files may have changed since. Do not edit manually; "
                    + "settings belong in src/projects/<Name>/.",
                ["Project"] = context.Name,
                ["ProjectDirectory"] = context.ProjectDirectory == null
                    ? null
                    : JsonConfigLoader.Label(context.ProjectDirectory),
                ["Timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["EffectiveScaleFactor"] = simContext != null ? simContext.EffectiveMeshRefinementFactor : context.Simulation.MeshRefinementFactor,
                ["ScaleFactorSource"] = simContext != null ? simContext.MeshRefinementSource : OutputConventions.ManualMeshSource,
                ["CalibrationReport"] = simContext?.CalibrationReportPath,
                ["Sections"] = sections,
                // Normally empty. If non-empty, a gitignored local override file took effect
                // that was not committed -- explaining any divergence later.
                ["MachineSpecificOverrides"] = machineOverrides
            };
        }
    }
}
