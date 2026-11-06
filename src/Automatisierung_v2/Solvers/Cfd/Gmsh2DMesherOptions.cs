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
        /// Kreisförmiges Fernfeld als Vielfaches der Sehnenlänge c (Vorgabe 500 wie die NASA-TMR-Gitter;
        /// in 2D klingt die Zirkulation nur mit 1/r ab, ein zu kleines Fernfeld verfälscht den Auftrieb).
        /// Die Zellen wachsen nach außen (<see cref="NearfieldGrowthDistanceFactor"/>), das große Fernfeld
        /// kostet deshalb nur wenige tausend Zellen. Bis TODO-W6 war die Vorgabe 50.
        /// </summary>
        public float FarfieldRadiusFactor { get; set; } = 500.0f;

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

        /// <summary>
        /// Fernfeld-Zellgröße als Vielfaches von L auf Stufe 1.0 (Vorgabe 25; bei Radius 500 c sind das
        /// ~1,3 Tausend Zellen im Fernfeld). Faustregel: Radius / 20.
        /// </summary>
        public float FarfieldCellSizeFactor { get; set; } = 25.0f;

        /// <summary>
        /// Abstand vom Körper (als Vielfaches von L), über den die Zellgröße vom Nahfeld auf die
        /// Fernfeldgröße wächst. 0 = automatisch 0,4 · <see cref="FarfieldRadiusFactor"/>. Ein langer,
        /// gleichmäßiger Übergang vermeidet verzerrte Zellen und spart Zellen (TODO-W6).
        /// </summary>
        public float NearfieldGrowthDistanceFactor { get; set; } = 0.0f;

        // --- Nachlauf-Verfeinerung (Wake Box) ---

        /// <summary>Nachlauf-Verfeinerungsbox an/aus (Standard true in 2D).</summary>
        public bool WakeRefinement { get; set; } = true;

        /// <summary>Länge der Nachlauf-Box stromabwärts als Vielfaches der Sehne L (Standard 3.0).</summary>
        public float WakeLengthFactor { get; set; } = 3.0f;

        /// <summary>Halbe Überbreite der Nachlauf-Box in Y als Vielfaches der Sehne L (Standard 0.6).</summary>
        public float WakeWidthFactor { get; set; } = 0.6f;

        /// <summary>Zellgröße in der Nachlauf-Box als Bruchteil von L auf Stufe 1.0 (Standard 0.04).</summary>
        public float WakeCellSizeFactor { get; set; } = 0.04f;

        // --- Detailbox um und hinter dem Modell (TODO-W6) ---

        /// <summary>
        /// Sehr feine Box um und hinter dem Profil, damit Nachlauf und Wirbel in Modellnähe stimmen.
        /// Gilt für jede Rechnung (keine Sonderbehandlung des Anstellwinkels). Skaliert mit dem
        /// Netzfaktor, die Kalibrierung prüft sie also mit. Standard an.
        /// </summary>
        public bool DetailBox { get; set; } = true;

        /// <summary>Länge der Detailbox hinter der Hinterkante als Vielfaches von L (Standard 1.0).</summary>
        public float DetailBoxLengthFactor { get; set; } = 1.0f;

        /// <summary>Überstand der Detailbox ober- und unterhalb des Profils als Vielfaches von L (Standard 0.25).</summary>
        public float DetailBoxWidthFactor { get; set; } = 0.25f;

        /// <summary>Zellgröße in der Detailbox als Bruchteil von L auf Stufe 1.0 (Standard 0.01 = L/100).</summary>
        public float DetailBoxCellSizeFactor { get; set; } = 0.01f;

        // --- Vorder- und Hinterkante ---

        /// <summary>
        /// Faktor für die Wandzellgröße an Vorder- und Hinterkante (Standard 0.25 = ein Viertel der
        /// Konturzellgröße). Die Saugspitze an der Vorderkante und die Hinterkante bestimmen Auftrieb
        /// und Widerstand am stärksten (NASA-TMR: Auflösung an der Hinterkante ist der wichtigste
        /// Einfluss auf die Genauigkeit). 1 = keine Sonderverfeinerung.
        /// </summary>
        public float EdgeRefinementFactor { get; set; } = 0.25f;

        /// <summary>Abstand um Vorder- und Hinterkante (als Vielfaches von L), über den die Verfeinerung ausläuft (Standard 0.1).</summary>
        public float EdgeRefinementDistanceFactor { get; set; } = 0.1f;
    }
}
