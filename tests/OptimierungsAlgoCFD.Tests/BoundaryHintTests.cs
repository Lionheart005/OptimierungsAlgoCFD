using System.Collections.Generic;
using System.Linq;
using Xunit;

using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Am Ende eines Laufs weist das Framework sichtbar darauf hin, wenn der Gewinner an einer Parametergrenze liegt.
    /// Die Grenzen selbst ändert es nie.
    /// </summary>
    public class BoundaryHintTests
    {
        private static readonly Dictionary<string, (float Min, float Max)> Bounds = new()
        {
            ["MaxCamber"] = (0f, 8f),
            ["MaxThickness"] = (6f, 24f),
            ["CamberPosition"] = (15f, 75f),
            ["Fixed"] = (5f, 5f)
        };

        [Fact]
        public void A_Winner_On_The_Upper_And_Lower_Bound_Is_Reported_With_Both_Parameters()
        {
            var winner = new Dictionary<string, float> { ["MaxCamber"] = 8.0f, ["MaxThickness"] = 6.0f, ["CamberPosition"] = 45f };

            var findings = BoundaryHint.Find(winner, Bounds);

            Assert.Equal(new[] { "MaxCamber", "MaxThickness" }, findings.Select(f => f.Parameter));
            Assert.True(findings.Single(f => f.Parameter == "MaxCamber").IsUpper);
            Assert.False(findings.Single(f => f.Parameter == "MaxThickness").IsUpper);
        }

        [Fact]
        public void A_Value_Within_One_Percent_Of_The_Span_Counts_As_On_The_Bound_But_Not_Two_Percent()
        {
            // Spanne 8: ein Prozent = 0,08.
            Assert.Single(BoundaryHint.Find(new Dictionary<string, float> { ["MaxCamber"] = 7.95f }, Bounds));
            Assert.Empty(BoundaryHint.Find(new Dictionary<string, float> { ["MaxCamber"] = 7.80f }, Bounds));
        }

        [Fact]
        public void A_Winner_In_The_Middle_Produces_No_Hint()
        {
            var winner = new Dictionary<string, float> { ["MaxCamber"] = 4f, ["MaxThickness"] = 12f, ["CamberPosition"] = 45f };

            var findings = BoundaryHint.Find(winner, Bounds);

            Assert.Empty(findings);
            Assert.Empty(BoundaryHint.Lines(findings));
        }

        [Fact]
        public void Fixed_Parameters_And_Parameters_Without_Bounds_Are_Ignored()
        {
            var winner = new Dictionary<string, float> { ["Fixed"] = 5f, ["Unbekannt"] = 100f };

            Assert.Empty(BoundaryHint.Find(winner, Bounds));
            Assert.Empty(BoundaryHint.Find(winner, null));
        }

        [Fact]
        public void The_Output_Names_The_Parameter_The_Bound_And_That_Nothing_Is_Changed_Automatically()
        {
            var findings = BoundaryHint.Find(new Dictionary<string, float> { ["MaxCamber"] = 8f }, Bounds);

            string text = string.Join("\n", BoundaryHint.Lines(findings));

            Assert.Contains("HINWEIS", text);
            Assert.Contains("MaxCamber = 8 (obere Grenze 8)", text);
            Assert.Contains("nie automatisch erweitert", text);
            Assert.Contains("ParameterBounds", text);
        }
    }
}
