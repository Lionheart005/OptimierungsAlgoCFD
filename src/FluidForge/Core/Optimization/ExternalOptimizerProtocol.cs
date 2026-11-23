using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluidForge.Core
{
    /// <summary>
    /// Shared JSON serialization and protocol constants for the versioned Ask/Tell
    /// external optimizer bridge (BoTorch, pymoo, or custom Python workers).
    /// </summary>
    public static class ExternalOptimizerProtocol
    {
        public const string CurrentProtocolVersion = "1.0";

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static string SerializeRequest(ExternalOptimizerRequest request)
            => JsonSerializer.Serialize(request, JsonOptions);

        public static ExternalOptimizerRequest? DeserializeRequest(string json)
            => JsonSerializer.Deserialize<ExternalOptimizerRequest>(json, JsonOptions);

        public static string SerializeResponse(ExternalOptimizerResponse response)
            => JsonSerializer.Serialize(response, JsonOptions);

        public static ExternalOptimizerResponse? DeserializeResponse(string json)
            => JsonSerializer.Deserialize<ExternalOptimizerResponse>(json, JsonOptions);
    }

    /// <summary>Bounds and baseline value of a single free optimization parameter.</summary>
    public sealed class ExternalParameterSpec
    {
        public string Name { get; set; } = string.Empty;
        public double Min { get; set; }
        public double Max { get; set; }
        public double BaseValue { get; set; }
    }

    /// <summary>Definition of a single optimization objective and optional hypervolume reference point.</summary>
    public sealed class ExternalObjectiveSpec
    {
        public string Name { get; set; } = string.Empty;
        public string Direction { get; set; } = "Minimize";
        public double? ReferencePoint { get; set; }
    }

    /// <summary>Definition of a single pre-solve or post-solve constraint.</summary>
    public sealed class ExternalConstraintSpec
    {
        public string Name { get; set; } = string.Empty;
        public bool IsPreSolve { get; set; }
    }

    /// <summary>
    /// Historical evaluation passed from FluidForge to the external optimizer worker.
    /// Constraint convention: <c>&lt;= 0</c> feasible, <c>&gt; 0</c> violated (<c>c.Violation = -c.Margin</c>).
    /// </summary>
    public sealed class ExternalObservation
    {
        public int Iteration { get; set; }
        public int Variant { get; set; }
        public string Status { get; set; } = "Feasible";
        public double Fidelity { get; set; } = 1.0;
        public double? Fitness { get; set; }
        public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> Objectives { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> ConstraintViolations { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>Versioned Ask request sent to an external optimizer worker.</summary>
    public sealed class ExternalOptimizerRequest
    {
        public string ProtocolVersion { get; set; } = ExternalOptimizerProtocol.CurrentProtocolVersion;
        public string Algorithm { get; set; } = "BoTorch";
        public string Acquisition { get; set; } = "Auto";
        public int Iteration { get; set; }
        public int BatchSize { get; set; } = 1;
        public int Seed { get; set; }
        public bool MultiFidelity { get; set; }
        public double[] AvailableFidelities { get; set; } = new[] { 1.0 };
        public List<ExternalParameterSpec> Parameters { get; set; } = new();
        public List<ExternalObjectiveSpec> Objectives { get; set; } = new();
        public List<ExternalConstraintSpec> Constraints { get; set; } = new();
        public List<ExternalObservation> History { get; set; } = new();
    }

    /// <summary>Single candidate parameter vector proposed by an external optimizer worker.</summary>
    public sealed class ExternalCandidate
    {
        public Dictionary<string, double> Parameters { get; set; } = new(StringComparer.Ordinal);
        public double Fidelity { get; set; } = 1.0;
        public Dictionary<string, double> PredictedObjectives { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, double> PredictedStd { get; set; } = new(StringComparer.Ordinal);
        public double? PredictedFeasibilityProb { get; set; }
    }

    /// <summary>Response returned by an external optimizer worker.</summary>
    public sealed class ExternalOptimizerResponse
    {
        public string ProtocolVersion { get; set; } = ExternalOptimizerProtocol.CurrentProtocolVersion;
        public string Worker { get; set; } = string.Empty;
        public string BackendVersion { get; set; } = string.Empty;
        public List<ExternalCandidate> Candidates { get; set; } = new();
        public Dictionary<string, double> ArdImportances { get; set; } = new(StringComparer.Ordinal);
        public List<bool> ParetoMask { get; set; } = new();
        public double? Hypervolume { get; set; }
        public Dictionary<string, string> Diagnostics { get; set; } = new(StringComparer.Ordinal);
        public string? Error { get; set; }
    }
}
