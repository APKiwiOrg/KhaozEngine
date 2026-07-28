using System;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How many mip levels <see cref="Scene3D.LoadTexture(byte[],int,int,TextureMipPolicy)"/> generates for a texture.
    /// The default is <see cref="Full"/>, so <c>default(TextureMipPolicy)</c> and an omitted argument both keep the
    /// full chain every caller had before this type existed.
    /// <para>A full chain is right for a tiled albedo and wrong for an image whose regions are independent: a UI
    /// sheet, a gradient ramp, a lookup texture, or a flipbook atlas all average across content that should never
    /// mix. <see cref="None"/> is the blunt answer (level 0 only, at the cost of minification sparkle) and
    /// <see cref="AtlasGrid"/> is the measured one: keep the chain only as far as a grid cell still has real
    /// texels.</para>
    /// <para>The particle flipbook path does NOT need this. Its fragment shader derives the same cap from the packed
    /// grid and clamps the sampled LOD itself, so an atlas loaded with <see cref="Full"/> still samples correctly
    /// there. <see cref="AtlasGrid"/> is for callers who want to stop paying memory for levels nothing samples, and
    /// for the model pipeline, which has no shader-side clamp of its own.</para>
    /// </summary>
    public readonly struct TextureMipPolicy : IEquatable<TextureMipPolicy>
    {
        /// <summary>Kept private and ordered so <c>default</c> lands on <see cref="Full"/>.</summary>
        enum Kind : byte { Full = 0, None = 1, AtlasGrid = 2 }

        readonly Kind _kind;
        readonly int _columns;
        readonly int _rows;
        readonly int _minCellTexels;

        TextureMipPolicy(Kind kind, int columns, int rows, int minCellTexels)
        {
            _kind = kind;
            _columns = columns;
            _rows = rows;
            _minCellTexels = minCellTexels;
        }

        /// <summary>The full chain, down to 1x1. The default, and what every call site got before this type existed.</summary>
        public static TextureMipPolicy Full => default;

        /// <summary>Level 0 only, no chain at all. For a sheet or lookup table where any averaging is wrong, at the
        /// cost of the minification sparkle a chain exists to remove.</summary>
        public static TextureMipPolicy None => new(Kind.None, 0, 0, 0);

        /// <summary>Cap the chain at the coarsest level where a grid cell still has at least
        /// <paramref name="minCellTexels"/> texels on its shorter side, so the bilinear fringe at a cell edge never
        /// reaches far into the neighbouring cell.</summary>
        /// <param name="columns">Cells across. Values below 1 are treated as 1.</param>
        /// <param name="rows">Cells down. Values below 1 are treated as 1.</param>
        /// <param name="minCellTexels">Texels a cell must keep on its shorter side. 4 caps the fringe at a quarter of
        /// a texel, which is what the particle flipbook shader uses. Values below 1 are treated as 1.</param>
        public static TextureMipPolicy AtlasGrid(int columns, int rows, int minCellTexels = 4) =>
            new(Kind.AtlasGrid, columns, rows, minCellTexels);

        /// <summary>Pure: the mip level count this policy asks for on a <paramref name="width"/> x
        /// <paramref name="height"/> texture. Never returns 0, and never more than the full chain.</summary>
        public uint LevelsFor(int width, int height)
        {
            if (_kind == Kind.None) return 1u;
            uint full = SplatMaterialConfig.MipLevelCount(width, height);
            if (_kind != Kind.AtlasGrid) return full;

            int minTexels = Math.Max(1, _minCellTexels);
            int cell = Math.Max(1, Math.Min(width / Math.Max(_columns, 1), height / Math.Max(_rows, 1)));
            uint levels = 1;
            while (levels < full && (cell >> (int)levels) >= minTexels) levels++;
            return levels;
        }

        /// <inheritdoc/>
        public bool Equals(TextureMipPolicy other) =>
            _kind == other._kind && _columns == other._columns && _rows == other._rows &&
            _minCellTexels == other._minCellTexels;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is TextureMipPolicy other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine((byte)_kind, _columns, _rows, _minCellTexels);

        public static bool operator ==(TextureMipPolicy left, TextureMipPolicy right) => left.Equals(right);

        public static bool operator !=(TextureMipPolicy left, TextureMipPolicy right) => !left.Equals(right);
    }
}
