using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Vorgabedaten für den Gmsh-CFD-Vernetzer: Abmessungen des Windkanals und
    /// Grenzschicht-Feld. Werden aus <c>src/projects/&lt;Name&gt;/solvers/gmsh.json</c> geladen und per
    /// Konstruktor injiziert. Die Standardwerte entsprechen exakt dem, was vorher in
    /// <see cref="GmshCfdMesher"/> hardcodiert stand; seit 08.11.2026 auf genaue Rechnungen ausgelegt
    /// (Kanal 1000 × 300 × 300 mm mit 14 L Nachlauf, Prismen-Wandschicht, Nachlauf- und Detailbox).
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
        public float TunnelSizeX { get; set; } = 1000.0f;
        public float TunnelSizeY { get; set; } = 300.0f;
        public float TunnelSizeZ { get; set; } = 300.0f;

        /// <summary>
        /// Mittelpunkt des Windkanals; das Modell sitzt im Ursprung. Standard X = 200 mm: bei einem 50-mm-Modell
        /// im 1000 mm langen Kanal 300 mm = 6 L stromauf und 700 mm = 14 L stromab.
        /// Empfehlung für Nachlauf-Erfassung: Den Tunnelmittelpunkt stromabwärts verschieben (positives X),
        /// sodass hinter dem Modell mindestens 10 Körperlängen (>= 10 L) Platz bleiben (z. B. bei einem 50 mm
        /// Modell im 600 mm Tunnel: TunnelCenterX = 100 mm ergibt 200 mm = 4 L stromauf und 400 mm = 8 L stromab,
        /// oder bei TunnelSizeX = 800 mm und TunnelCenterX = 150 mm >= 10 L stromab).
        /// </summary>
        public float TunnelCenterX { get; set; } = 200.0f;
        public float TunnelCenterY { get; set; } = 0.0f;
        public float TunnelCenterZ { get; set; } = 0.0f;

        // --- Zylinder-Windkanal (nur bei TunnelShape = "Cylinder") ---

        /// <summary>Durchmesser quer zur Strömung; entspricht dem Quader-Querschnitt.</summary>
        public float TunnelDiameter { get; set; } = 300.0f;

        /// <summary>Länge entlang X (= Strömungsrichtung).</summary>
        public float TunnelLength { get; set; } = 1000.0f;

        /// <summary>Segmente über den Umfang — bestimmt die Feinheit des Mantels.</summary>
        public int TunnelSegments { get; set; } = 64;

        // --- Physikalische Domänengarantien (Mindestabstände & Versperrungsgrad) ---

        /// <summary>Mindestabstand stromaufwärts vor der Modellnase als Vielfaches der Modelllänge L (Standard 5.0).</summary>
        public float TunnelMinUpstreamFactor { get; set; } = 5.0f;

        /// <summary>Mindestabstand stromabwärts hinter dem Modellheck als Vielfaches der Modelllänge L (Standard 15.0).</summary>
        public float TunnelMinDownstreamFactor { get; set; } = 15.0f;

        /// <summary>Mindestabstand quer zur Strömung in ±Y und ±Z als Vielfaches von Breite B bzw. Höhe H (Standard 5.0).</summary>
        public float TunnelMinLateralFactor { get; set; } = 5.0f;

        /// <summary>
        /// Maximal erlaubter Versperrungsgrad A_frontal / A_tunnel in Prozent (Standard 0.3 %).
        /// Werte &lt;= 0 deaktivieren die automatische Blockage-Aufweitung.
        /// </summary>
        public float TunnelMaxBlockagePercent { get; set; } = 0.3f;

        // --- Grenzschicht-Feld (Gmsh Threshold Field) ---
        /// <summary>Kleinste Zellgröße direkt an der Wand.</summary>
        public float BoundaryLayerSizeMin { get; set; } = 1.2f;
        /// <summary>Größte Zellgröße weit weg vom Modell.</summary>
        public float BoundaryLayerSizeMax { get; set; } = 120.0f;
        /// <summary>Abstand, bis zu dem <see cref="BoundaryLayerSizeMin"/> gilt.</summary>
        public float BoundaryLayerDistMin { get; set; } = 3.0f;
        /// <summary>Abstand, ab dem <see cref="BoundaryLayerSizeMax"/> gilt.</summary>
        public float BoundaryLayerDistMax { get; set; } = 40.0f;

        // --- Nachlauf-Verfeinerung (Wake Box) ---
        /// <summary>
        /// Nachlauf-Verfeinerung an/aus (Standard an seit 08.11.2026: der Nachlauf bestimmt den Druckwiderstand mit).
        /// </summary>
        public bool WakeRefinement { get; set; } = true;

        /// <summary>Länge der Nachlauf-Box stromabwärts als Vielfaches der Modelllänge lenX.</summary>
        public float WakeLengthFactor { get; set; } = 2.0f;

        /// <summary>Halbe Überbreite der Nachlauf-Box in Y und Z als Vielfaches von lenY bzw. lenZ.</summary>
        public float WakeWidthFactor { get; set; } = 0.5f;

        /// <summary>
        /// Zellgröße in der Nachlauf-Box in mm auf Stufe 1.0; 0 = automatisch lenX / 6.0 im .geo-Skript.
        /// </summary>
        public float WakeCellSize { get; set; } = 0.0f;

        // --- Detailbox um und hinter dem Modell (TODO-W6) ---
        /// <summary>
        /// Feine Box um und hinter dem Modell (Nachlauf und Wirbel in Modellnähe). Standard an (seit 08.11.2026);
        /// in 3D kostet sie viele Zellen (Manta: einige hunderttausend), MantaAuv schaltet sie deshalb aus. Die
        /// Zellgröße skaliert mit dem Netzfaktor, die Kalibrierung prüft sie also mit.
        /// </summary>
        public bool DetailBox { get; set; } = true;

        /// <summary>Länge der Detailbox hinter dem Modell als Vielfaches der Modelllänge lenX (Standard 1.5).</summary>
        public float DetailBoxLengthFactor { get; set; } = 1.5f;

        /// <summary>Überstand in Y und Z als Vielfaches von lenY bzw. lenZ (Standard 0.25).</summary>
        public float DetailBoxWidthFactor { get; set; } = 0.25f;

        /// <summary>
        /// Mindest-Z-Puffer der Detailbox als Anteil der Modelllänge L (Standard 0.15), damit schlanke/flache
        /// Körper nicht an einem zu kleinen <c>DetailBoxWidthFactor * lenZ</c> verhungern. Werte &lt;= 0 deaktivieren
        /// die Mindestlänge.
        /// </summary>
        public float DetailBoxMinHeightLengthFactor { get; set; } = 0.15f;

        /// <summary>Zellgröße in der Detailbox in mm auf Stufe 1.0; 0 = automatisch 2,5 × Oberflächen-Zellgröße.</summary>
        public float DetailBoxCellSize { get; set; } = 0.0f;

        // --- Krümmungsabhängige Netzverfeinerung ---
        /// <summary>
        /// Mesh.MeshSizeFromCurvature aktivieren (Standard true); verfeinert gekrümmte Oberflächen und Kanten.
        /// </summary>
        public bool MeshSizeFromCurvature { get; set; } = true;

        /// <summary>
        /// Untergrenze für die krümmungsabhängige Verfeinerung als Faktor auf <c>surfaceSize</c> (Standard 0.5,
        /// intern auf [0.1, 1.0] geklammert), damit feine Voxel-Splitter/Fillets die Zellzahl nicht explodieren lassen.
        /// </summary>
        public float CurvatureMinSizeFactor { get; set; } = 0.5f;

        // --- Strukturierte Grenzschicht (TODO-40) ---
        /// <summary>
        /// Modus der Grenzschicht-Vernetzung:
        /// <list type="bullet">
        ///   <item><c>"Prism"</c> (Standard seit 08.11.2026): Strukturierte Prismen-Grenzschicht an der Wand, extrudiert aus Dreieckselementen (y+ ≈ 1, am Manta gemessen).</item>
        ///   <item><c>"Threshold"</c>: Unstrukturiertes Tetraeder-Netz mit Gmsh-Threshold-Abstandsfeld (y+ ≈ 30–70, für das SA-Modell zu grob).</item>
        ///   <item><c>"Hexa"</c>: Strukturierte Quader-Grenzschicht (Hexaeder) an der Wand, extrudiert aus Viereckselementen.</item>
        /// </list>
        /// </summary>
        public string BoundaryLayerMode { get; set; } = "Prism";

        /// <summary>Ziel-y+ für die erste Zellhöhe an der Wand (Standard 1.5).</summary>
        public float TargetYPlus { get; set; } = 1.5f;

        /// <summary>Mindestanzahl an Randschichten (Standard 4).</summary>
        public int BoundaryLayerMinLayers { get; set; } = 4;

        /// <summary>Wachstumsfaktor von Schicht zu Schicht in der Grenzschicht (Standard 1.28).</summary>
        public float BoundaryLayerGrowthRate { get; set; } = 1.28f;

        /// <summary>Feste Schichtanzahl; 0 = automatisch aus Grenzschichtdicke delta berechnen.</summary>
        public int BoundaryLayerLayers { get; set; } = 0;

        /// <summary>
        /// Faktor auf die abzudeckende Grenzschichtdicke: die Wandschicht wird so dick wie <c>Faktor × δ</c> (Standard 1.0).
        /// Die Literatur empfiehlt oft 1.2–1.5, damit die Grenzschicht ganz in der strukturierten Zone liegt.
        /// Wirkt nur bei <see cref="BoundaryLayerLayers"/> = 0 (automatische Schichtzahl).
        /// </summary>
        public float BoundaryLayerCoverage { get; set; } = 1.0f;

        /// <summary>Oberflächen-Zellgröße in mm auf Stufe 1.0; 0 = nutzt BoundaryLayerSizeMin.</summary>
        public float SurfaceCellSize { get; set; } = 0.0f;

        /// <summary>Elemente pro 2*Pi Bogenmaß bei krümmungsabhängiger Netzverfeinerung (Standard 20).</summary>
        public int CurvatureElements { get; set; } = 20;

        /// <summary>Winkelgrenze in Grad für Gmsh ClassifySurfaces bei Neuvernetzung der Bauteiloberfläche (Standard 40.0).</summary>
        public float ClassifyAngleDeg { get; set; } = 40.0f;
    }
}
