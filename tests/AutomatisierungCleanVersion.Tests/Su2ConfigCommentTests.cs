using System;
using System.IO;
using System.Linq;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Tests fuer TODO-34: Die generierte SU2-Konfiguration (.cfg) und das Gmsh-.geo-Skript
    /// enthalten erklaerende Kommentarbloecke zu physikalischen Gleichungen, Randbedingungen,
    /// Markern und Abbruchkriterien, ohne die aktiven Schluesselzeilen zu veraendern (Regression).
    /// </summary>
    public class Su2ConfigCommentTests
    {
        [Fact]
        public void Generated_Config_Contains_Explanatory_Comment_Blocks()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_comments_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] lines = File.ReadAllLines(path);
                string[] commentLines = lines.Where(l => l.TrimStart().StartsWith("%")).ToArray();

                Assert.NotEmpty(commentLines);

                // Physikalische Gleichungen (RANS, SA)
                Assert.Contains(commentLines, l => l.Contains("Inkompressible RANS-Gleichungen", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("Spalart-Allmaras", StringComparison.OrdinalIgnoreCase) || l.Contains("SA", StringComparison.OrdinalIgnoreCase));

                // Randbedingungen & Marker (Farfield, Wall, Haftwand, Fernfeld)
                Assert.Contains(commentLines, l => l.Contains("MARKER_FAR", StringComparison.OrdinalIgnoreCase) && l.Contains("Fernfeld", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("MARKER_HEATFLUX", StringComparison.OrdinalIgnoreCase) && l.Contains("no-slip", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("MARKER_MONITORING", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("MARKER_PLOTTING", StringComparison.OrdinalIgnoreCase));

                // Freistromturbulenz Vorgabewerte SU2 7.5.1
                Assert.Contains(commentLines, l => l.Contains("FREESTREAM_TURBULENCEINTENSITY= 0.05", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("FREESTREAM_TURB2LAMVISCRATIO= 10.0", StringComparison.OrdinalIgnoreCase));

                // Numerik & Abbruchkriterien
                Assert.Contains(commentLines, l => l.Contains("FDS", StringComparison.OrdinalIgnoreCase) && l.Contains("MUSCL", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("ABBRUCHKRITERIEN", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Config_Comments_Are_Ascii_Only_And_Not_Excessively_Long()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_ascii_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines.Where(l => l.TrimStart().StartsWith("%")))
                {
                    Assert.True(line.Length <= 100, $"Kommentarzeile zu lang ({line.Length} Zeichen): {line}");
                    Assert.All(line, ch => Assert.True(ch <= 127, $"Unerlaubtes Nicht-ASCII-Zeichen '{ch}' in: {line}"));
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Non_Comment_Lines_Match_Exact_Regression_Baseline()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_regression_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] nonCommentLines = File.ReadAllLines(path)
                    .Where(l => !l.TrimStart().StartsWith("%") && !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .ToArray();

                string[] expectedLines = new[]
                {
                    "SOLVER= INC_RANS",
                    "KIND_TURB_MODEL= SA",
                    "MATH_PROBLEM= DIRECT",
                    "INC_DENSITY_MODEL= CONSTANT",
                    "INC_ENERGY_EQUATION= NO",
                    "FREESTREAM_VELOCITY= ( 34.320004, 0.0, 0.0 )",
                    "FREESTREAM_DENSITY= 1025",
                    "VISCOSITY_MODEL= CONSTANT_VISCOSITY",
                    "MU_CONSTANT= 0.001001",
                    "INC_DENSITY_INIT= 1025",
                    "INC_VELOCITY_INIT= ( 34.320004, 0.0, 0.0 )",
                    "REF_AREA= 0.001",
                    "REF_LENGTH= 0.01",
                    "REF_ORIGIN_MOMENT_X = 0.00",
                    "REF_ORIGIN_MOMENT_Y = 0.00",
                    "REF_ORIGIN_MOMENT_Z = 0.00",
                    "MARKER_HEATFLUX= ( Wall, 0.0 )",
                    "MARKER_FAR= ( Farfield )",
                    "MARKER_MONITORING= ( Wall )",
                    "MARKER_PLOTTING= ( Wall )",
                    "CONV_NUM_METHOD_FLOW= FDS",
                    "MUSCL_FLOW= YES",
                    "SLOPE_LIMITER_FLOW= VENKATAKRISHNAN",
                    "VENKAT_LIMITER_COEFF= 0.05",
                    "TIME_DISCRE_FLOW= EULER_IMPLICIT",
                    "CFL_NUMBER= 5",
                    "CFL_ADAPT= YES",
                    "CFL_ADAPT_PARAM= ( 0.5, 1.2, 1, 50 )",
                    "CONV_NUM_METHOD_TURB= SCALAR_UPWIND",
                    "ITER= 500",
                    "CONV_FIELD= DRAG",
                    "CONV_CAUCHY_ELEMS= 150",
                    "CONV_CAUCHY_EPS= 1e-4",
                    "CONV_STARTITER= 100",
                    "CONV_RESIDUAL_MINVAL= -8",
                    "HISTORY_OUTPUT= (ITER, RMS_RES, AERO_COEFF)",
                    "MESH_FILENAME= mesh.su2",
                    "MESH_FORMAT= SU2",
                    "TABULAR_FORMAT= CSV",
                    "OUTPUT_FILES= (RESTART, PARAVIEW, SURFACE_PARAVIEW)",
                    "VOLUME_OUTPUT= (COORDINATES, SOLUTION, PRIMITIVE, VELOCITY, PRESSURE, MACH, DENSITY, SKIN_FRICTION, Y_PLUS)"
                };

                Assert.Equal(expectedLines, nonCommentLines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Geo_Script_Contains_Physical_Surface_Comments()
        {
            string path = Path.Combine(Path.GetTempPath(), "gmsh_geo_" + Guid.NewGuid().ToString("N") + ".geo");
            try
            {
                var mesher = new GmshCfdMesher();
                mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", SimulationConfig.CreateDefault(), 1.0f);

                string[] lines = File.ReadAllLines(path);

                int farfieldCommentIdx = Array.FindIndex(lines, l => l.Contains("Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR"));
                int farfieldLineIdx = Array.FindIndex(lines, l => l.Contains("Physical Surface(\"Farfield\")"));
                Assert.True(farfieldCommentIdx >= 0, "Kommentar für Farfield-Marker fehlt im .geo-Skript.");
                Assert.True(farfieldLineIdx == farfieldCommentIdx + 1, "Kommentar für Farfield-Marker muss direkt vor Physical Surface(\"Farfield\") stehen.");

                int wallCommentIdx = Array.FindIndex(lines, l => l.Contains("Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX"));
                int wallLineIdx = Array.FindIndex(lines, l => l.Contains("Physical Surface(\"Wall\")"));
                Assert.True(wallCommentIdx >= 0, "Kommentar für Wall-Marker fehlt im .geo-Skript.");
                Assert.True(wallLineIdx == wallCommentIdx + 1, "Kommentar für Wall-Marker muss direkt vor Physical Surface(\"Wall\") stehen.");

                // Alle Zeilen ASCII-only
                foreach (string line in lines)
                {
                    Assert.All(line, ch => Assert.True(ch <= 127, $"Unerlaubtes Nicht-ASCII-Zeichen '{ch}' im .geo-Skript: {line}"));
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
