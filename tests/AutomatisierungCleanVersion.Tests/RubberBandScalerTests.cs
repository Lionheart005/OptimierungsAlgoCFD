using System.Collections.Generic;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    public class RubberBandScalerTests
    {
        [Fact]
        public void Scaler_Should_Only_Scale_Dimensional_Parameters()
        {
            // Arrange
            var parameters = new Dictionary<string, float>
            {
                { "Length", 100.0f },     // Dimensional
                { "Width", 50.0f },       // Dimensional
                { "TailTaper", 1.0f }     // Nicht dimensional (dimensionslos)
            };

            var dimensionalParams = new HashSet<string> { "Length", "Width" };
            float targetSize = 20.0f; // Die Max-Dimension (100) soll auf 20 skaliert werden (Faktor 0.2)

            // Act
            var scaler = new RubberBandScaler(parameters, dimensionalParams, targetSize);

            // Assert
            Assert.Equal(0.2f, scaler.ShrinkFactor, 3); // 20 / 100
            
            // Dimensional skaliert
            Assert.Equal(20.0f, scaler.ShrunkParameters["Length"]); // 100 * 0.2
            Assert.Equal(10.0f, scaler.ShrunkParameters["Width"]);  // 50 * 0.2
            
            // Dimensionslos bleibt UNVERÄNDERT
            Assert.Equal(1.0f, scaler.ShrunkParameters["TailTaper"]); // 1.0, nicht 0.2!
        }

        [Fact]
        public void Disabled_Scaler_Passes_Everything_Through_Unchanged()
        {
            var parameters = new Dictionary<string, float>
            {
                { "Length", 100.0f },
                { "TailTaper", 1.0f }
            };

            var scaler = RubberBandScaler.Create(
                enabled: false,
                realParameters: parameters,
                dimensionalParameters: new HashSet<string> { "Length" },
                targetSize: 20.0f);

            Assert.Equal(1.0f, scaler.ShrinkFactor);
            Assert.Equal(100.0f, scaler.ShrunkParameters["Length"]);
            Assert.Equal(1.0f, scaler.ShrunkParameters["TailTaper"]);

            // Die Restore-Methoden sind im Aus-Fall die Identität
            Assert.Equal(42.0f, scaler.RestoreLength(42.0f), 4);
            Assert.Equal(42.0f, scaler.RestoreArea(42.0f), 4);
            Assert.Equal(42.0f, scaler.RestoreVolume(42.0f), 4);
        }

        [Fact]
        public void Enabled_Scaler_Via_Factory_Behaves_Like_The_Constructor()
        {
            var parameters = new Dictionary<string, float> { { "Length", 100.0f } };
            var dimensional = new HashSet<string> { "Length" };

            var viaFactory = RubberBandScaler.Create(true, parameters, dimensional, 20.0f);
            var viaConstructor = new RubberBandScaler(parameters, dimensional, 20.0f);

            Assert.Equal(viaConstructor.ShrinkFactor, viaFactory.ShrinkFactor);
            Assert.Equal(viaConstructor.ShrunkParameters["Length"], viaFactory.ShrunkParameters["Length"]);
        }
    }
}

