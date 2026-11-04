using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// Vorgabedaten für den 2D-Gmsh-CFD-Vernetzer (TODO-45): Fernfeldradius, Grenzschicht-Auflösung
    /// (y+, Wachstumsrate, Mindestschichten), Kontur- und Feldzellgrößen sowie Nachlaufbox.
    /// Werden aus <c>solvers/gmsh.json</c> geladen und per Konstruktor injiziert.
    /// </summary>
    public class Gmsh2DMesherOptions
    {
        // --- Fernfeld (Simulationsdomäne) ---

        /// <summary>
        /// Kreisförmiges Fernfeld als Vielfaches der Sehnenlänge c (Vorgabe 50, da in 2D die
        /// Zirkulation mit 1/r abklingt und ein zu kleines Fernfeld den Auftrieb verfälscht).
        /// </summary>
        public float FarfieldRadiusFactor { get; set; } = 50.0f;

        /// <summary>Anzahl der Segmente für die Fernfeld-Kreisbögen.</summary>
        public int FarfieldCircleSegments { get; set; } = 64;

        // --- Grenzschicht-Auflösung (BoundaryLayer Field) ---

        /// <summary>Ziel-y+ für die erste Zellhöhe an der Wand (Standard 1.0).</summary>
        public float TargetYPlus { get; set; } = 1.0f;

        /// <summary>Wachstumsfaktor von Schicht zu Schicht in der Grenzschicht (Standard 1.2).</summary>
        public float BoundaryLayerGrowthRate { get; set; } = 1.2f;

        /// <summary>Mindestanzahl an Randschichten (Standard 4).</summary>
        public int BoundaryLayerMinLayers { get; set; } = 4;

        /// <summary>Feste Schichtanzahl; 0 = automatisch aus Grenzschichtdicke delta berechnen.</summary>
        public int BoundaryLayerLayers { get; set; } = 0;

        // --- Zellgrößen relativ zur Sehnenlänge L auf Stufe 1.0 ---

        /// <summary>
        /// Kontur-Zellgröße als Bruchteil der Bezugslänge/Sehne L auf Stufe 1.0 (z. B. 0.01 = L/100).
        /// </summary>
        public float SurfaceCellSizeFactor { get; set; } = 0.01f;

        /// <summary>
        /// Kontur-Zellgröße in mm auf Stufe 1.0; > 0 überschreibt SurfaceCellSizeFactor * L.
        /// </summary>
        public float SurfaceCellSizeMm { get; set; } = 0.0f;

        /// <summary>Nahfeld-Zellgröße als Bruchteil von L auf Stufe 1.0 (z. B. 0.04 = L/25).</summary>
        public float NearfieldCellSizeFactor { get; set; } = 0.04f;

        /// <summary>Fernfeld-Zellgröße als Bruchteil von L auf Stufe 1.0 (z. B. 2.5 = 2.5 * L).</summary>
        public float FarfieldCellSizeFactor { get; set; } = 2.5f;

        // --- Nachlauf-Verfeinerung (Wake Box) ---

        /// <summary>Nachlauf-Verfeinerungsbox an/aus (Standard true in 2D).</summary>
        public bool WakeRefinement { get; set; } = true;

        /// <summary>Länge der Nachlauf-Box stromabwärts als Vielfaches der Sehne L (Standard 3.0).</summary>
        public float WakeLengthFactor { get; set; } = 3.0f;

        /// <summary>Halbe Überbreite der Nachlauf-Box in Y als Vielfaches der Sehne L (Standard 0.6).</summary>
        public float WakeWidthFactor { get; set; } = 0.6f;

        /// <summary>Zellgröße in der Nachlauf-Box als Bruchteil von L auf Stufe 1.0 (Standard 0.04).</summary>
        public float WakeCellSizeFactor { get; set; } = 0.04f;
    }
}
