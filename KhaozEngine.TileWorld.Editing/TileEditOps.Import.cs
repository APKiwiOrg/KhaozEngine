using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>The heightmap-import half of the high-level factories: a greyscale image resampled onto a rect of
/// the corner lattice. Like the other parts of this class, nothing here mutates. The returned command is what
/// <see cref="TileEditingDocument.Execute"/> applies, so an import undoes in one step like any other edit.
/// </summary>
public static partial class TileEditOps
{
    /// <summary>Resamples <paramref name="image"/> onto the corner rect and returns the write, one height per
    /// corner.
    ///
    /// The image is stretched over the rect with bilinear interpolation, its first and last columns landing on
    /// the rect's west and east corners and its first and last rows on the north and south ones. Image row 0 is
    /// the NORTH edge, which is the flip this method exists to get right: an image is written top row first
    /// while tile z grows northward, so the top of the heightmap belongs on the rect's HIGHEST z. An image
    /// exactly the size of the rect therefore maps one sample to one corner with nothing interpolated.
    ///
    /// Each interpolated sample is then mapped linearly from 0..<see cref="PgmImage.MaxValue"/> onto
    /// <paramref name="minCm"/>..<paramref name="maxCm"/> and rounded to whole centimetres away from zero. The
    /// mapping runs AFTER the interpolation, so a heightmap rounds once at the end rather than at every sample
    /// that fed a corner. A sample above maxval (which a malformed file can carry) is treated as white rather
    /// than allowed to push a corner past <paramref name="maxCm"/>.
    ///
    /// Corners the world has no region for are skipped by the command itself, and
    /// <see cref="SetCornerHeightsCommand.WrittenCount"/> reports how many of the rect actually landed.</summary>
    public static SetCornerHeightsCommand ImportHeights(PgmImage image, TileRect cornerRect, int plane,
        short minCm, short maxCm)
    {
        if (image.Samples is null || image.Width <= 0 || image.Height <= 0 || image.MaxValue <= 0
            || image.Samples.Length != image.Width * image.Height)
            throw new ArgumentException(
                "the image carries no samples, read one with PgmReader.Read first.", nameof(image));
        if (maxCm < minCm)
            throw new ArgumentException(
                $"the height range runs from {minCm} to {maxCm} cm, which is backwards.", nameof(maxCm));
        if (cornerRect.IsEmpty) return new SetCornerHeightsCommand(cornerRect, plane, Array.Empty<short>());

        var cm = new short[cornerRect.Width * cornerRect.Height];
        double span = maxCm - minCm;
        int i = 0;
        // Row-major with z outer and rising, the order every command and factory of this family indexes its
        // value array in. The image row is the mirror of that walk, hence the flipped second argument.
        for (int oz = 0; oz < cornerRect.Height; oz++)
            for (int ox = 0; ox < cornerRect.Width; ox++, i++)
            {
                double u = SampleAxis(ox, cornerRect.Width, image.Width);
                double v = SampleAxis(cornerRect.Height - 1 - oz, cornerRect.Height, image.Height);
                double t = Math.Clamp(Bilinear(image, u, v) / image.MaxValue, 0.0, 1.0);
                cm[i] = ClampCm((long)Math.Round(minCm + span * t, MidpointRounding.AwayFromZero));
            }
        return new SetCornerHeightsCommand(cornerRect, plane, cm);
    }

    /// <summary>Reads the binary PGM at <paramref name="pgmPath"/> and resamples it onto the corner rect, the
    /// whole heightmap import in one call.</summary>
    public static SetCornerHeightsCommand ImportHeights(string pgmPath, TileRect cornerRect, int plane,
        short minCm, short maxCm) =>
        ImportHeights(PgmReader.Read(pgmPath), cornerRect, plane, minCm, maxCm);

    // One output index to its position along an image axis: index 0 sits on the first sample and the last index
    // on the last, which is what makes an image the size of the rect land sample on corner. A rect one corner
    // wide or tall has no span to stretch across and takes the first sample of that axis.
    static double SampleAxis(int index, int outCount, int inCount) =>
        outCount > 1 ? index * (double)(inCount - 1) / (outCount - 1) : 0.0;

    // The bilinear sample at a fractional position in the image's grid. The upper index is clamped rather than
    // wrapped, so the far edge of the image (where the fraction is 0 anyway) reads itself twice instead of
    // folding round to column 0.
    static double Bilinear(PgmImage image, double u, double v)
    {
        int x0 = Math.Clamp((int)Math.Floor(u), 0, image.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(v), 0, image.Height - 1);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        double fx = u - x0, fy = v - y0;
        double top = image.Sample(x0, y0) + (image.Sample(x1, y0) - image.Sample(x0, y0)) * fx;
        double bottom = image.Sample(x0, y1) + (image.Sample(x1, y1) - image.Sample(x0, y1)) * fx;
        return top + (bottom - top) * fy;
    }
}
