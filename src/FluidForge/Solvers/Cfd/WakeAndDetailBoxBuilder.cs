using System;
using System.Collections.Generic;
using System.Globalization;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Gemeinsamer Builder für Wake-Box- und Detail-Box-Verfeinerungsfelder in 2D- und 3D-<c>.geo</c>-Skripten.
    /// </summary>
    internal static class WakeAndDetailBoxBuilder
    {
        /// <summary>Zahlen fürs <c>.geo</c>-Skript immer mit Punkt als Dezimaltrenner.</summary>
        public static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Zahlen fürs <c>.geo</c>-Skript immer mit Punkt als Dezimaltrenner.</summary>
        public static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

        public static void AppendSectionHeader(List<string> geoLines, string headerLine, bool wideBanner = false, bool leadingBlankLine = false)
        {
            if (leadingBlankLine) geoLines.Add(string.Empty);
            string bar = wideBanner
                ? "// ============================================================================="
                : "// ==========================================";
            geoLines.Add(bar);
            geoLines.Add(headerLine);
            geoLines.Add(bar);
        }

        public static void AppendNumericBoxField(
            List<string> geoLines,
            int fieldIndex,
            float xMin, float xMax,
            float yMin, float yMax,
            float zMin, float zMax,
            float vIn, float vOut,
            float? thickness = null)
        {
            geoLines.Add($"Field[{fieldIndex}] = Box;");
            geoLines.Add($"Field[{fieldIndex}].XMin = {Format(xMin)};");
            geoLines.Add($"Field[{fieldIndex}].XMax = {Format(xMax)};");
            geoLines.Add($"Field[{fieldIndex}].YMin = {Format(yMin)};");
            geoLines.Add($"Field[{fieldIndex}].YMax = {Format(yMax)};");
            geoLines.Add($"Field[{fieldIndex}].ZMin = {Format(zMin)};");
            geoLines.Add($"Field[{fieldIndex}].ZMax = {Format(zMax)};");
            geoLines.Add($"Field[{fieldIndex}].VIn = {Format(vIn)};");
            geoLines.Add($"Field[{fieldIndex}].VOut = {Format(vOut)};");
            if (thickness.HasValue)
            {
                geoLines.Add($"Field[{fieldIndex}].Thickness = {Format(thickness.Value)};");
            }
        }

        public static void Append3DWakeBox(
            List<string> geoLines,
            GmshMesherOptions options,
            string surfaces,
            float factor,
            float safeScale,
            float sizeMaxGeo,
            string headerLine)
        {
            AppendSectionHeader(geoLines, headerLine);
            geoLines.Add($"bbox() = BoundingBox Surface {{{surfaces}}};");
            geoLines.Add("minX = bbox(0); minY = bbox(1); minZ = bbox(2);");
            geoLines.Add("maxX = bbox(3); maxY = bbox(4); maxZ = bbox(5);");
            geoLines.Add("lenX = maxX - minX;");
            geoLines.Add("lenY = maxY - minY;");
            geoLines.Add("lenZ = maxZ - minZ;");
            geoLines.Add("Field[3] = Box;");
            geoLines.Add("Field[3].XMin = minX - (0.1 * lenX);");
            geoLines.Add($"Field[3].XMax = maxX + ({Format(options.WakeLengthFactor)} * lenX);");
            geoLines.Add($"Field[3].YMin = minY - ({Format(options.WakeWidthFactor)} * lenY);");
            geoLines.Add($"Field[3].YMax = maxY + ({Format(options.WakeWidthFactor)} * lenY);");
            geoLines.Add($"Field[3].ZMin = minZ - ({Format(options.WakeWidthFactor)} * lenZ);");
            geoLines.Add($"Field[3].ZMax = maxZ + ({Format(options.WakeWidthFactor)} * lenZ);");
            string vIn = options.WakeCellSize > 0f
                ? Format(options.WakeCellSize * factor * safeScale)
                : $"(lenX / 6.0) * {Format(factor)}";
            geoLines.Add($"Field[3].VIn = {vIn};");
            geoLines.Add($"Field[3].VOut = {Format(sizeMaxGeo)};");
        }

        public static void Append3DDetailBox(
            List<string> geoLines,
            GmshMesherOptions options,
            string surfaces,
            float factor,
            float safeScale)
        {
            float baseSizeGeo = (options.SurfaceCellSize > 0f ? options.SurfaceCellSize : options.BoundaryLayerSizeMin) * safeScale;
            float cell = (options.DetailBoxCellSize > 0f ? options.DetailBoxCellSize * safeScale : 2.5f * baseSizeGeo) * factor;
            float minHeightLengthFactor = Math.Max(0f, options.DetailBoxMinHeightLengthFactor);

            AppendSectionHeader(geoLines, "// F2) DETAILBOX UM UND HINTER DEM MODELL (TODO-W6)");
            geoLines.Add($"dbbox() = BoundingBox Surface {{{surfaces}}};");
            geoLines.Add("dminX = dbbox(0); dminY = dbbox(1); dminZ = dbbox(2);");
            geoLines.Add("dmaxX = dbbox(3); dmaxY = dbbox(4); dmaxZ = dbbox(5);");
            geoLines.Add("dlenX = dmaxX - dminX; dlenY = dmaxY - dminY; dlenZ = dmaxZ - dminZ;");
            geoLines.Add("Field[5] = Box;");
            geoLines.Add("Field[5].XMin = dminX - (0.1 * dlenX);");
            geoLines.Add($"Field[5].XMax = dmaxX + ({Format(options.DetailBoxLengthFactor)} * dlenX);");
            geoLines.Add($"Field[5].YMin = dminY - ({Format(options.DetailBoxWidthFactor)} * dlenY);");
            geoLines.Add($"Field[5].YMax = dmaxY + ({Format(options.DetailBoxWidthFactor)} * dlenY);");
            geoLines.Add($"dz_width = {Format(options.DetailBoxWidthFactor)} * dlenZ;");
            geoLines.Add($"dz_min_len = {Format(minHeightLengthFactor)} * dlenX;");
            geoLines.Add("dz_pad = (dz_width > dz_min_len) ? dz_width : dz_min_len;");
            geoLines.Add("Field[5].ZMin = dminZ - dz_pad;");
            geoLines.Add("Field[5].ZMax = dmaxZ + dz_pad;");
            geoLines.Add($"Field[5].VIn = {Format(cell)};");
            geoLines.Add($"Field[5].VOut = {Format(options.BoundaryLayerSizeMax * factor * safeScale)};");
            geoLines.Add($"Field[5].Thickness = {Format(Math.Max(cell * 5f, 1.0f * safeScale))};");
        }

        public static void Append2DWakeBox(
            List<string> geoLines,
            Gmsh2DMesherOptions options,
            BBox2 bounds,
            float chordGeo,
            float wakeSize,
            float farfieldSize)
        {
            AppendSectionHeader(geoLines, "// F) DYNAMISCHE WAKE-BOX (NACHLAUF)", wideBanner: true, leadingBlankLine: true);
            AppendNumericBoxField(
                geoLines, 4,
                bounds.Min.X - (0.05f * chordGeo),
                bounds.Max.X + (options.WakeLengthFactor * chordGeo),
                bounds.Center.Y - (0.5f * options.WakeWidthFactor * chordGeo),
                bounds.Center.Y + (0.5f * options.WakeWidthFactor * chordGeo),
                -1.0f, 1.0f,
                wakeSize, farfieldSize);
        }

        public static void Append2DDetailBox(
            List<string> geoLines,
            Gmsh2DMesherOptions options,
            BBox2 bounds,
            float chordGeo,
            float factor,
            float farfieldSize)
        {
            float detailSize = options.DetailBoxCellSizeFactor * chordGeo * factor;
            AppendSectionHeader(geoLines, "// F2) DETAILBOX UM UND HINTER DEM PROFIL (TODO-W6)", wideBanner: true, leadingBlankLine: true);
            AppendNumericBoxField(
                geoLines, 6,
                bounds.Min.X - (0.1f * chordGeo),
                bounds.Max.X + (options.DetailBoxLengthFactor * chordGeo),
                bounds.Min.Y - (options.DetailBoxWidthFactor * chordGeo),
                bounds.Max.Y + (options.DetailBoxWidthFactor * chordGeo),
                -1.0f, 1.0f,
                detailSize, farfieldSize,
                thickness: 0.3f * chordGeo);
        }
    }
}
