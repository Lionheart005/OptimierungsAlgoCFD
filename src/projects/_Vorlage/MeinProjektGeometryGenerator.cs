using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using PicoGK;

using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.PicoGk;

namespace MyPicoGkProject.Projects.MeinProjekt
{
    /// <summary>
    /// Die Geometrie eines Projekts (TODO-27). Diese Vorlage baut den einfachsten denkbaren
    /// Körper — einen Quader aus <c>Length × Width × Height</c> — und meldet vier Metriken.
    /// Damit läuft die ganze Kette (PicoGK → STL → Gmsh → SU2 → Fitness) einmal durch, bevor du
    /// deine eigene Form einbaust: ein <b>Rauchtest</b>.
    ///
    /// <para>
    /// <b>Hier arbeitest du.</b> Der Quader steht nur als Platzhalter; ersetze
    /// <see cref="BuildVoxelModel"/> durch deine Form. Wie eine richtige Geometrie aussieht, zeigt
    /// <c>src/projects/MantaAuv/MantaGeometryGenerator.cs</c>: dort entsteht aus einem
    /// <c>Lattice</c> ein Rumpf mit Flügelfächer, der über zwei <c>Offset</c>-Schritte organisch
    /// verschmolzen wird.
    /// </para>
    ///
    /// <para>
    /// Das Framework erfährt vom Ergebnis ausschließlich über den
    /// <see cref="GeometryResult"/> — eine STL-Datei und benannte Metriken. Es gibt keine
    /// Metrikliste, die irgendwo gepflegt werden müsste: der Name, den du hier in
    /// <see cref="GeometryResult.AddMetric"/> schreibst, ist derselbe, den die Fitness-Formel
    /// liest, den <c>ReferenceAreaMetric</c> in der su2.json meinen kann und der als Spalte in
    /// der <c>Simulation_Results.csv</c> auftaucht.
    /// </para>
    /// </summary>
    public sealed class MeinProjektGeometryGenerator : IGeometryGenerator
    {
        /// <inheritdoc/>
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            // Die Parameter kommen aus project.json (bzw. CreateDefaults) und sind hier bereits
            // auf die PicoGK-Arbeitsgröße geschrumpft — der Controller rechnet die Metriken
            // hinterher zurück. Deshalb wird hier NICHT mit den Zahlen aus der JSON gerechnet,
            // sondern mit denen, die ankommen.
            float length = Read(parameters, "Length");
            float width = Read(parameters, "Width");
            float height = Read(parameters, "Height");

            Voxels voxels = BuildVoxelModel(length, width, height);

            // Das Netz, das exportiert wird — und aus dem alle Maße kommen.
            Mesh exportModel = new Mesh(voxels);

            // Volumen, Oberfläche und Hüllquader in einem Durchgang (TODO-29). Die Oberfläche
            // ist der Grund, warum es diesen Helfer gibt: PicoGKs CalculateProperties liefert
            // Volumen und Hüllquader, die Oberfläche NICHT — die muss aus dem Dreiecksnetz
            // summiert werden, und das wäre in jedem Projekt dieselbe Schleife.
            //
            // Die Alternative ist voxels.CalculateProperties(out float volume, out BBox3 box) —
            // so macht es MantaAuv. Der Unterschied ist echt, aber klein: PicoGK misst das
            // VOXELFELD, MeshMetrics das daraus gewonnene NETZ.
            MeshMeasurement measurement = PicoGkMeshMetrics.Measure(exportModel);

            // Ein negatives Vorzeichen hiesse: die Dreiecke sind nach innen gedreht. Gmsh
            // rechnet so eine Huelle nicht oder falsch, und im Betrag sieht man es nicht.
            if (measurement.SignedVolume < 0f)
                Console.WriteLine("       -> [WARNUNG] Das Netz ist nach innen gedreht (negatives Volumen).");

            // Der Dateiname ist frei wählbar, das Muster aber praktisch: Iteration und Variante
            // stehen darin, und alle STLs eines Laufs liegen unter Ergebnisse/<Projekt>/.
            string modelPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
            exportModel.SaveToStlFile(modelPath);

            // Glättung ist ein abschaltbarer Framework-Baustein (Entscheidung 4): steht
            // UseStlSmoothing in der simulation.json auf false, bleibt die STL, wie sie aus dem
            // Voxel-Modell kommt. Eine fehlgeschlagene Glättung darf den Lauf nicht kosten —
            // die ungeglättete Datei ist immer noch rechenbar.
            if (config.UseStlSmoothing)
            {
                try
                {
                    StlSmoother.SmoothStl(
                        modelPath, modelPath,
                        config.VoxelSmoothingIterations,
                        config.VoxelSmoothingPremeltingSteps);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"       -> [WARNUNG] Glättung fehlgeschlagen: {ex.Message}");
                }
            }

            var result = new GeometryResult { StlPath = modelPath };

            // ==============================================================
            //  METRIKEN -- die Schnittstelle zum Rest des Frameworks
            // ==============================================================
            //
            // Der zweite Parameter von AddMetric ist die DIMENSION. Sie entscheidet, wie der
            // RubberBandScaler von der PicoGK-Arbeitsgröße zurückrechnet:
            //
            //   Volume  kubisch      Area  quadratisch      Linear  einfach      None  gar nicht
            //
            // EIN FALSCHER EINTRAG FÄLLT NICHT AUF. Der Wert ist dann um den Skalierungsfaktor
            // hoch 1, 2 oder 3 daneben — und sieht trotzdem plausibel aus. Welche Dimension eine
            // Metrik bekam, hält Ergebnisse/<Projekt>/effective-config.json fest (TODO-25).
            result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
            result.AddMetric("SurfaceArea", measurement.SurfaceArea, MetricScaling.Area);

            // FrontalArea ist die angeströmte Fläche (quer zur X-Achse) und hat einen zweiten
            // Abnehmer: solvers/su2.json nennt sie unter "ReferenceAreaMetric", SU2 bildet daraus
            // REF_AREA und normiert damit alle Beiwerte. Wer sie umbenennt, muss den Namen dort
            // mitziehen — sonst warnt der Solver und rechnet mit einem Ersatzwert.
            result.AddMetric("FrontalArea", measurement.FrontalBoxArea, MetricScaling.Area);

            // Spannweite: die Ausdehnung quer zur Strömung. Eine Länge, also Linear.
            result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);

            // Ein Verhältnis ist dimensionslos und darf NICHT mitskaliert werden — das ist der
            // Fall für None. (Beispiel, die Fitness dieser Vorlage benutzt es nicht.)
            result.AddMetric("LengthToWidth", length / Math.Max(width, 0.001f), MetricScaling.None);

            return result;
        }

        /// <summary>
        /// Der Platzhalter-Körper: ein Quader, zentriert im Ursprung, Länge entlang X (=
        /// Strömungsrichtung). <b>Das ist die Methode, die du ersetzt.</b>
        ///
        /// <para>
        /// Wer Kanten verrunden will, ruft auf dem Voxelmodell <c>Offset(r)</c> und danach
        /// <c>Offset(-r)</c> auf — so verschmilzt MantaAuv seine Flügel mit dem Rumpf.
        /// </para>
        /// </summary>
        private static Voxels BuildVoxelModel(float length, float width, float height)
        {
            Mesh box = Utils.mshCreateCube(new Vector3(length, width, height), Vector3.Zero);
            return new Voxels(box);
        }

        /// <summary>
        /// Einen Parameter lesen — und abbrechen, wenn er fehlt.
        ///
        /// <para>
        /// Bewusst <b>kein</b> Ersatzwert: ein Tippfehler in <c>project.json</c> würde sonst
        /// stundenlang eine Geometrie rechnen, die nichts mit den eingestellten Zahlen zu tun hat.
        /// Die Geometrie-Erzeugung läuft außerhalb des <c>try</c> im
        /// <see cref="WorkflowController"/> — der Lauf endet hier also sofort, mit dieser Meldung.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Der Parameter steht nicht in der Konfiguration.</exception>
        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRIE] Der Parameter '{name}' fehlt. Vorhanden ist: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(keiner)")
                + ". Er gehört in BaseParameters, MaxDeviations und ParameterBounds der "
                + "project.json dieses Projekts — und in dieselben drei Listen in "
                + "CreateDefaults().");
        }
    }
}
