namespace MyPicoGkProject
{
    /// <summary>
    /// Vorgabedaten für den Gmsh-CFD-Vernetzer: Abmessungen des Windkanals und
    /// Grenzschicht-Feld. Werden aus <c>config/solvers/gmsh.json</c> geladen und per
    /// Konstruktor injiziert. Die Standardwerte entsprechen exakt dem, was vorher in
    /// <see cref="GmshCfdMesher"/> hardcodiert stand (600 × 300 × 300 mm, zentriert).
    /// </summary>
    public class GmshMesherOptions
    {
        // --- Windkanal (Simulationsdomäne) in mm ---

        /// <summary>
        /// Form der Simulationsdomäne: <c>"Box"</c> (Standard, bisheriges Verhalten) oder
        /// <c>"Cylinder"</c>. Groß-/Kleinschreibung und Leerzeichen sind egal; ein unbekannter
        /// Name führt zu einer Warnung und dem Rückfall auf <c>"Box"</c>.
        /// </summary>
        public string TunnelShape { get; set; } = "Box";

        /// <summary>Kantenlängen des Quaders (nur bei <see cref="TunnelShape"/> = "Box").</summary>
        public float TunnelSizeX { get; set; } = 600.0f;
        public float TunnelSizeY { get; set; } = 300.0f;
        public float TunnelSizeZ { get; set; } = 300.0f;

        /// <summary>Mittelpunkt des Windkanals; das Modell sitzt im Ursprung.</summary>
        public float TunnelCenterX { get; set; } = 0.0f;
        public float TunnelCenterY { get; set; } = 0.0f;
        public float TunnelCenterZ { get; set; } = 0.0f;

        // --- Zylinder-Windkanal (nur bei TunnelShape = "Cylinder") ---

        /// <summary>Durchmesser quer zur Strömung; entspricht dem Quader-Querschnitt.</summary>
        public float TunnelDiameter { get; set; } = 300.0f;

        /// <summary>Länge entlang X (= Strömungsrichtung).</summary>
        public float TunnelLength { get; set; } = 600.0f;

        /// <summary>Segmente über den Umfang — bestimmt die Feinheit des Mantels.</summary>
        public int TunnelSegments { get; set; } = 64;

        // --- Grenzschicht-Feld (Gmsh Threshold Field) ---
        /// <summary>Kleinste Zellgröße direkt an der Wand.</summary>
        public float BoundaryLayerSizeMin { get; set; } = 1.2f;
        /// <summary>Größte Zellgröße weit weg vom Modell.</summary>
        public float BoundaryLayerSizeMax { get; set; } = 120.0f;
        /// <summary>Abstand, bis zu dem <see cref="BoundaryLayerSizeMin"/> gilt.</summary>
        public float BoundaryLayerDistMin { get; set; } = 3.0f;
        /// <summary>Abstand, ab dem <see cref="BoundaryLayerSizeMax"/> gilt.</summary>
        public float BoundaryLayerDistMax { get; set; } = 40.0f;
    }
}
