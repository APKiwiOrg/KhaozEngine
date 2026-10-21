using System;

namespace KhaozEngine.WorldMap;

/// <summary>A square RGBA8 world-map raster with row zero at the north edge.</summary>
/// <remarks>
/// The interior covers exactly the chunk rectangle. A non-zero <see cref="Border"/> surrounds it with
/// gutter pixels sampled from the adjacent map area, so filtered sampling at a chunk edge reads real
/// neighbouring imagery. <see cref="Rgba"/> holds <see cref="TextureSize"/> squared pixels.
/// </remarks>
/// <param name="Coord">The map-space chunk address.</param>
/// <param name="Size">The interior edge length in pixels, covering the chunk rectangle.</param>
/// <param name="Rgba">Row-major RGBA bytes over the full texture, with columns running west to east.</param>
/// <param name="Revision">The source revision captured before sampling began.</param>
public sealed record WorldMapChunkImage(WorldMapChunkCoord Coord, int Size, byte[] Rgba, long Revision)
{
    readonly int _border;

    /// <summary>The gutter width in pixels on each side of the interior. Defaults to zero.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Border
    {
        get => _border;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _border = value;
        }
    }

    /// <summary>The physical edge length of <see cref="Rgba"/> in pixels, the interior plus both gutters.</summary>
    public int TextureSize => Size + 2 * Border;

    /// <summary>Returns the byte offset of an interior-relative pixel.</summary>
    /// <param name="column">The column from the interior's west edge, from <c>-Border</c> to <c>Size + Border - 1</c>.</param>
    /// <param name="row">The row from the interior's north edge, from <c>-Border</c> to <c>Size + Border - 1</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The pixel lies outside the texture.</exception>
    public int ByteOffset(int column, int row)
    {
        if (column < -Border || column >= Size + Border) throw new ArgumentOutOfRangeException(nameof(column));
        if (row < -Border || row >= Size + Border) throw new ArgumentOutOfRangeException(nameof(row));
        return ((row + Border) * TextureSize + column + Border) * 4;
    }
}
