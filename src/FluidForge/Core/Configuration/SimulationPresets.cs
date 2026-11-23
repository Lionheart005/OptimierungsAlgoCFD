using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Describes a curated Layer 1.5 physics and workflow preset applied after code defaults
    /// and before project-level JSON files.
    /// </summary>
    public sealed class PresetDefinition
    {
        private readonly JsonObject _simulationOverrides;
        private readonly Dictionary<string, JsonObject> _solverOverrides;

        public PresetDefinition(
            string name,
            string domain,
            string accuracy,
            string description,
            bool isPlaceholder,
            JsonObject simulationOverrides,
            IReadOnlyDictionary<string, JsonObject>? solverOverrides = null)
        {
            Name = name;
            Domain = domain;
            Accuracy = accuracy;
            Description = description;
            IsPlaceholder = isPlaceholder;
            _simulationOverrides = (JsonObject)simulationOverrides.DeepClone();
            _solverOverrides = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

            if (solverOverrides != null)
            {
                foreach (KeyValuePair<string, JsonObject> entry in solverOverrides)
                {
                    string key = NormalizeKey(entry.Key);
                    _solverOverrides[key] = (JsonObject)entry.Value.DeepClone();
                }
            }
        }

        /// <summary>Canonical preset name, e.g. <c>"External3D:StandardRans"</c>.</summary>
        public string Name { get; }

        /// <summary>Physical domain: <c>"External2D"</c>, <c>"External3D"</c>, or <c>"Internal3D"</c>.</summary>
        public string Domain { get; }

        /// <summary>Accuracy/budget tier: <c>"FastConcept"</c>, <c>"StandardRans"</c>, or <c>"VerificationFine"</c>.</summary>
        public string Accuracy { get; }

        /// <summary>Human-readable summary of intended use and key settings.</summary>
        public string Description { get; }

        /// <summary>True for presets awaiting a future solver backend (e.g. Phase 3 OpenFOAM).</summary>
        public bool IsPlaceholder { get; }

        /// <summary>Returns a deep clone of the <c>simulation.json</c> overlay for this preset.</summary>
        public JsonObject SimulationOverrides => (JsonObject)_simulationOverrides.DeepClone();

        /// <summary>
        /// Returns a case-insensitive dictionary containing deep clones of all solver overlays
        /// (keys like <c>"solvers/su2.json"</c>, <c>"solvers/gmsh.json"</c>).
        /// </summary>
        public IReadOnlyDictionary<string, JsonObject> SolverOverrides
        {
            get
            {
                var copy = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, JsonObject> entry in _solverOverrides)
                    copy[entry.Key] = (JsonObject)entry.Value.DeepClone();
                return copy;
            }
        }

        /// <summary>
        /// Returns a deep clone of the solver overlay for <paramref name="relativeName"/>
        /// (e.g. <c>"solvers/su2.json"</c>), or <c>null</c> if this preset does not override that file.
        /// </summary>
        public JsonObject? GetSolverOverrides(string relativeName)
        {
            if (string.IsNullOrWhiteSpace(relativeName)) return null;
            return _solverOverrides.TryGetValue(NormalizeKey(relativeName), out JsonObject? obj)
                ? (JsonObject)obj.DeepClone()
                : null;
        }

        private static string NormalizeKey(string key) => key.Trim().Replace('\\', '/');
    }

    /// <summary>
    /// Registry of curated 3x3 domain x accuracy presets (Layer 1.5 configuration).
    /// </summary>
    public static class SimulationPresets
    {
        private static readonly IReadOnlyList<PresetDefinition> Catalog = BuildCatalog();
        private static readonly Dictionary<string, PresetDefinition> ByName = BuildLookup(Catalog);
        private static readonly Dictionary<string, string> Shorthands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["External2D"] = "External2D:StandardRans",
            ["External3D"] = "External3D:StandardRans",
            ["Internal3D"] = "Internal3D:StandardRans"
        };

        /// <summary>All curated presets in canonical matrix order.</summary>
        public static IReadOnlyList<PresetDefinition> All => Catalog;

        /// <summary>
        /// Looks up a preset by canonical name or domain shorthand (<c>"External2D"</c>, <c>"External3D"</c>,
        /// <c>"Internal3D"</c>). Empty or <c>"Custom"</c> yields <c>false</c>.
        /// </summary>
        public static bool TryGet(string? name, out PresetDefinition? preset)
        {
            preset = null;
            if (string.IsNullOrWhiteSpace(name)) return false;

            string trimmed = name.Trim();
            if (trimmed.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return false;

            if (Shorthands.TryGetValue(trimmed, out string? canonical))
                trimmed = canonical;

            return ByName.TryGetValue(trimmed, out preset);
        }

        /// <summary>
        /// Resolves <paramref name="name"/> to a <see cref="PresetDefinition"/>, or returns <c>null</c>
        /// when empty or <c>"Custom"</c>. Throws a clear <c>[CONFIG]</c> error on unknown names.
        /// </summary>
        public static PresetDefinition? Resolve(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            string trimmed = name.Trim();
            if (trimmed.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return null;

            if (TryGet(trimmed, out PresetDefinition? preset))
                return preset;

            string validList = string.Join(", ", Catalog.Select(p => p.Name));
            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} Unknown Preset '{name}'. "
                + $"Valid presets are: {validList} "
                + "(or 'Custom' / empty for none; shorthands 'External2D', 'External3D', 'Internal3D' map to ':StandardRans').");
        }

        private static Dictionary<string, PresetDefinition> BuildLookup(IReadOnlyList<PresetDefinition> items)
        {
            var dict = new Dictionary<string, PresetDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (PresetDefinition item in items)
                dict[item.Name] = item;
            return dict;
        }

        private static IReadOnlyList<PresetDefinition> BuildCatalog() => new PresetDefinition[]
        {
            // --- External2D ---
            new(
                name: "External2D:FastConcept",
                domain: "External2D",
                accuracy: "FastConcept",
                description: "Fast 2D aerodynamic screening with coarse manual mesh (1.5x), relaxed SU2 budget, and verification off.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 5,
                    ["VariantsPerIteration"] = 6,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 12
                    },
                    ["Su2MaxIterations"] = 1500,
                    ["CauchyElements"] = 200,
                    ["CauchyTolerance"] = "1e-4",
                    ["MeshRefinementFactor"] = 1.5f,
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Manual",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 2,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 1.5f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["TargetYPlus"] = 1.5f,
                        ["FarfieldRadiusFactor"] = 200.0f
                    }
                }),

            new(
                name: "External2D:StandardRans",
                domain: "External2D",
                accuracy: "StandardRans",
                description: "Balanced 2D RANS optimization with auto mesh calibration (5%), y+ ~ 1.0, 500c farfield, and Top-3 verification.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 10,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 20
                    },
                    ["Su2MaxIterations"] = 3000,
                    ["CauchyElements"] = 300,
                    ["CauchyTolerance"] = "5e-5",
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 1
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 2,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 1.0f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["TargetYPlus"] = 1.0f,
                        ["FarfieldRadiusFactor"] = 500.0f
                    }
                }),

            new(
                name: "External2D:VerificationFine",
                domain: "External2D",
                accuracy: "VerificationFine",
                description: "High-accuracy 2D RANS with tight calibration (2%), y+ ~ 0.8, 10k SU2 iterations, AutoStop off, and 2-step Top-3 verification.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 10,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    },
                    ["Su2MaxIterations"] = 10000,
                    ["CauchyElements"] = 500,
                    ["CauchyTolerance"] = "1e-5",
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.02f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 2
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 2,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 0.8f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["TargetYPlus"] = 0.8f,
                        ["FarfieldRadiusFactor"] = 500.0f
                    }
                }),

            // --- External3D ---
            new(
                name: "External3D:FastConcept",
                domain: "External3D",
                accuracy: "FastConcept",
                description: "Fast 3D external CFD smoke-test/concept run with manual 1.5x mesh, prism y+ ~ 2.0, DetailBox off, and verification off.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 5,
                    ["VariantsPerIteration"] = 6,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 12
                    },
                    ["Su2MaxIterations"] = 2000,
                    ["CauchyElements"] = 200,
                    ["CauchyTolerance"] = "1e-4",
                    ["MeshRefinementFactor"] = 1.5f,
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Manual",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 3,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 2.0f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["BoundaryLayerMode"] = "Prism",
                        ["TargetYPlus"] = 2.0f,
                        ["DetailBox"] = false
                    },
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "External3D",
                        ["SymmetryPlane"] = "None",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 3.0f,
                        ["BoundaryLayerLayers"] = 3,
                        ["TargetYPlus"] = 30.0f,
                        ["EndTime"] = 500,
                        ["WriteInterval"] = 100,
                        ["ResidualTarget"] = 5e-4f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["Drag"] = "Drag",
                            ["Lift"] = "Lift"
                        }
                    }
                }),

            new(
                name: "External3D:StandardRans",
                domain: "External3D",
                accuracy: "StandardRans",
                description: "Production 3D external RANS optimization with auto mesh calibration (5%), prism y+ ~ 1.5, and Top-3 verification.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 8,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 20
                    },
                    ["Su2MaxIterations"] = 3000,
                    ["CauchyElements"] = 300,
                    ["CauchyTolerance"] = "5e-5",
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 1
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 3,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 1.5f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["BoundaryLayerMode"] = "Prism",
                        ["TargetYPlus"] = 1.5f,
                        ["DetailBox"] = false
                    },
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "External3D",
                        ["SymmetryPlane"] = "None",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 2.0f,
                        ["BoundaryLayerLayers"] = 5,
                        ["TargetYPlus"] = 5.0f,
                        ["EndTime"] = 1000,
                        ["WriteInterval"] = 200,
                        ["ResidualTarget"] = 1e-4f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["Drag"] = "Drag",
                            ["Lift"] = "Lift"
                        }
                    }
                }),

            new(
                name: "External3D:VerificationFine",
                domain: "External3D",
                accuracy: "VerificationFine",
                description: "High-accuracy 3D external RANS with tight calibration (2%), prism y+ ~ 1.0, DetailBox on, and 8k SU2 iterations.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 10,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    },
                    ["Su2MaxIterations"] = 8000,
                    ["CauchyElements"] = 500,
                    ["CauchyTolerance"] = "1e-5",
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.02f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 1
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/su2.json"] = new JsonObject
                    {
                        ["Dimensions"] = 3,
                        ["AutoRestartOnDrift"] = true,
                        ["TargetYPlus"] = 1.0f
                    },
                    ["solvers/gmsh.json"] = new JsonObject
                    {
                        ["BoundaryLayerMode"] = "Prism",
                        ["TargetYPlus"] = 1.0f,
                        ["DetailBox"] = true
                    },
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "External3D",
                        ["SymmetryPlane"] = "None",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 1.2f,
                        ["SurfaceRefinementLevelMin"] = 2,
                        ["SurfaceRefinementLevelMax"] = 3,
                        ["BoundaryLayerLayers"] = 8,
                        ["TargetYPlus"] = 1.5f,
                        ["EndTime"] = 2000,
                        ["WriteInterval"] = 250,
                        ["ResidualTarget"] = 5e-5f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["Drag"] = "Drag",
                            ["Lift"] = "Lift"
                        }
                    }
                }),

            // --- Internal3D (OpenFOAM internal flow) ---
            new(
                name: "Internal3D:FastConcept",
                domain: "Internal3D",
                accuracy: "FastConcept",
                description: "Fast 3D internal OpenFOAM screening with manual 1.5x mesh, 3 wall layers, y+ ~ 30, and verification off.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["UseRubberBandScaler"] = false,
                    ["UseStlSmoothing"] = false,
                    ["MaxIterations"] = 5,
                    ["VariantsPerIteration"] = 6,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 12
                    },
                    ["MeshRefinementFactor"] = 1.5f,
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Manual",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "Internal3D",
                        ["MesherBackend"] = "SnappyHexMesh",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 3.0f,
                        ["SurfaceRefinementLevelMin"] = 1,
                        ["SurfaceRefinementLevelMax"] = 1,
                        ["BoundaryLayerLayers"] = 3,
                        ["TargetYPlus"] = 30.0f,
                        ["EndTime"] = 500,
                        ["WriteInterval"] = 100,
                        ["ResidualTarget"] = 5e-4f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["PressureDrop"] = "PressureDrop"
                        }
                    }
                }),

            new(
                name: "Internal3D:StandardRans",
                domain: "Internal3D",
                accuracy: "StandardRans",
                description: "Production 3D internal OpenFOAM RANS optimization with auto mesh calibration (5%), 5 layers, y+ ~ 5, and Top-3 verification.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["UseRubberBandScaler"] = false,
                    ["UseStlSmoothing"] = false,
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 8,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "On",
                        ["MinImprovement"] = 0.01f,
                        ["WindowVariants"] = 20
                    },
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.05f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 1
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "Internal3D",
                        ["MesherBackend"] = "SnappyHexMesh",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 2.0f,
                        ["SurfaceRefinementLevelMin"] = 1,
                        ["SurfaceRefinementLevelMax"] = 2,
                        ["BoundaryLayerLayers"] = 5,
                        ["TargetYPlus"] = 5.0f,
                        ["EndTime"] = 1000,
                        ["WriteInterval"] = 200,
                        ["ResidualTarget"] = 1e-4f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["PressureDrop"] = "PressureDrop"
                        }
                    }
                }),

            new(
                name: "Internal3D:VerificationFine",
                domain: "Internal3D",
                accuracy: "VerificationFine",
                description: "High-accuracy 3D internal OpenFOAM RANS with tight calibration (2%), 8 layers, y+ ~ 1.5, and 2k simpleFoam iterations.",
                isPlaceholder: false,
                simulationOverrides: new JsonObject
                {
                    ["UseRubberBandScaler"] = false,
                    ["UseStlSmoothing"] = false,
                    ["MaxIterations"] = 10,
                    ["VariantsPerIteration"] = 10,
                    ["AutoStop"] = new JsonObject
                    {
                        ["Mode"] = "Off"
                    },
                    ["MeshCalibration"] = new JsonObject
                    {
                        ["Mode"] = "Auto",
                        ["Tolerance"] = 0.02f
                    },
                    ["ChampionVerification"] = new JsonObject
                    {
                        ["Mode"] = "FinalTopN",
                        ["TopN"] = 3,
                        ["RefinementSteps"] = 1
                    }
                },
                solverOverrides: new Dictionary<string, JsonObject>
                {
                    ["solvers/openfoam.json"] = new JsonObject
                    {
                        ["DomainMode"] = "Internal3D",
                        ["MesherBackend"] = "SnappyHexMesh",
                        ["TurbulenceModel"] = "kOmegaSST",
                        ["BaseCellSizeMm"] = 1.2f,
                        ["SurfaceRefinementLevelMin"] = 2,
                        ["SurfaceRefinementLevelMax"] = 3,
                        ["BoundaryLayerLayers"] = 8,
                        ["TargetYPlus"] = 1.5f,
                        ["EndTime"] = 2000,
                        ["WriteInterval"] = 250,
                        ["ResidualTarget"] = 5e-5f,
                        ["ResultMetrics"] = new JsonObject
                        {
                            ["PressureDrop"] = "PressureDrop"
                        }
                    }
                })
        };
    }
}
