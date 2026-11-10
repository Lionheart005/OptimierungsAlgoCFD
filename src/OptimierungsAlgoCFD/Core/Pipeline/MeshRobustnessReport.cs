using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Zusammenfassung am Laufende: Wie robust war die Vernetzung? Zählt die Varianten nach Stufe der Wandschicht-Rückfall-Leiter
    /// (A geglättete Richtungen, B örtlich gedünnt, C Ersatznetz) und nennt die Formen, die nur mit Ersatznetz oder gar nicht
    /// vernetzbar waren, damit besondere Formen nicht unbemerkt verloren gehen (Plan wandschicht_zellzahl.md, Abschnitt 4).
    /// Reine Funktion, damit testbar.
    /// </summary>
    public static class MeshRobustnessReport
    {
        /// <summary>Zeilen für das Log; leer, wenn keine Variante Wandschicht-Kennwerte oder Vernetzungsfehler hat.</summary>
        public static IReadOnlyList<string> Lines(IEnumerable<ModelRecord> history)
        {
            var records = history.ToList();
            int stageA = 0, stageB = 0, stageC = 0, meshFailed = 0;
            var fallback = new List<ModelRecord>();
            var failed = new List<ModelRecord>();

            foreach (ModelRecord r in records)
            {
                if (r.PassiveParameters.TryGetValue(ModelRecord.MeshFailedKey, out float mf) && mf > 0.5f)
                {
                    meshFailed++;
                    failed.Add(r);
                    continue;
                }

                if (!r.PassiveParameters.TryGetValue("BoundaryLayerStage", out float stage)) continue;
                int st = (int)System.Math.Round(stage);
                if (st == 1) stageA++;
                else if (st == 2) stageB++;
                else if (st >= 3) { stageC++; fallback.Add(r); }
            }

            var lines = new List<string>();
            if (stageA + stageB + stageC + meshFailed == 0) return lines;

            lines.Add($"Netz-Robustheit ({records.Count} Varianten): {stageA} mit Standard-Wandschicht (Stufe A), {stageB} örtlich gedünnt (Stufe B), "
                      + $"{stageC} mit Ersatznetz (Stufe C), {meshFailed} nicht vernetzbar.");

            if (fallback.Count > 0)
            {
                lines.Add("Formen mit Ersatznetz (Wandschicht überall verdünnt, nur eingeschränkt vergleichbar; von Hand ansehen und gezielt nachrechnen):");
                foreach (ModelRecord r in fallback)
                    lines.Add($"  Iteration {r.Iteration} | Variante {r.Variant} | Fitness {r.Fitness.ToString("0.##", CultureInfo.InvariantCulture)} | {r.StlPath}");
            }

            if (failed.Count > 0)
            {
                lines.Add("Nicht vernetzbare Formen (zählen nicht als Strömungsabsturz, siehe Fehlerprotokolle unter FehlerLogs/):");
                foreach (ModelRecord r in failed)
                    lines.Add($"  Iteration {r.Iteration} | Variante {r.Variant} | {r.StlPath}");
            }

            return lines;
        }
    }
}
