using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Inhalt des „Fahrtenschreibers" <c>Ergebnisse/&lt;Projekt&gt;/run-info.json</c> (TODO-50, F24):
    /// mit welchem Code-Stand, welchen Programmversionen, welchem Zufalls-Seed und auf welchem
    /// Rechner ein Lauf gerechnet hat. Reine Information — nichts im Programm entscheidet danach.
    /// Die Feldnamen sind deutsch, weil die Datei für Menschen geschrieben wird.
    /// </summary>
    public sealed class RunInfo
    {
        public const string StatusRunning = "laeuft";
        public const string StatusCompleted = "abgeschlossen";
        public const string StatusAborted = "abgebrochen";

        public int Format { get; set; } = 1;
        public string Projekt { get; set; } = "";
        public string Start { get; set; } = "";
        public string? Ende { get; set; }

        /// <summary><c>laeuft</c>, <c>abgeschlossen</c> oder <c>abgebrochen</c>. Nach einem harten
        /// Abbruch (Neustart, <c>kill</c>) bleibt <c>laeuft</c> stehen; <c>status</c> erkennt den
        /// Fall über das Journal.</summary>
        public string Status { get; set; } = StatusRunning;

        public List<string> Aufruf { get; set; } = new();
        public int ZufallsSeed { get; set; }
        public RunInfoMachine Rechner { get; set; } = new();
        public RunInfoSource Quellstand { get; set; } = new();

        /// <summary>Programmname → Version, <c>"unbekannt"</c> wenn nicht ermittelbar.</summary>
        public Dictionary<string, string> Programme { get; set; } = new();

        /// <summary>Fingerabdruck der geladenen Konfigurationswerte (<see cref="ConfigFingerprint"/>).</summary>
        public string KonfigurationsHash { get; set; } = "";

        public List<RunInfoResume> Wiederaufnahmen { get; set; } = new();
    }

    public sealed class RunInfoMachine
    {
        public string Name { get; set; } = "";
        public string Betriebssystem { get; set; } = "";
        public int Prozessorkerne { get; set; }
        public string DotNet { get; set; } = "";
    }

    /// <summary>
    /// Code-Stand. Auf dem Server aus <c>deploy-info.json</c> (schreibt <c>sim.ps1</c> beim
    /// Deploy), lokal direkt aus <c>.git</c> (<see cref="GitInfo"/>).
    /// </summary>
    public sealed class RunInfoSource
    {
        public string? Commit { get; set; }
        public string? Branch { get; set; }
        public string? DeployHash { get; set; }
        public string? DeployZeit { get; set; }

        /// <summary><c>"true"</c>, <c>"false"</c> oder <c>"unbekannt"</c>.</summary>
        public string UncommittedChanges { get; set; } = "unbekannt";
    }

    public sealed class RunInfoResume
    {
        public string Zeit { get; set; } = "";
        public int UebernommeneRechnungen { get; set; }
    }
}
