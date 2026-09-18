using System.Collections.Generic;
using Xunit;
using MyPicoGkProject;

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
    }
}

