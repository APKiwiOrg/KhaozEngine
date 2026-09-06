using System;
using System.Numerics;

namespace KhaozEngine.TileWorld;

// The material half of the ground mesher. TileGroundMesher.cs decides which tiles are drawn and
// TileGroundMesher.Overlays.cs decides what each lattice point of a tile becomes. This file answers the one
// question both of them ask: which material a lattice corner is, and how bright the ground is there. Both
// answers are read from the GLOBAL tile grid rather than from the region being meshed, which is what lets two
// regions agree exactly at the border they share.
public static partial class TileGroundMesher
{
    // What every vertex of an overlay-painted triangle carries: all of its weight on slot 0, which the painted
    // triangle fills with the overlay's own material.
    static readonly Vector4 OverlayWeights = new(1f, 0f, 0f, 0f);

    /// <summary>The material at a lattice corner: the underlay id shared by the most of the up-to-four tiles
    /// that touch it, ties broken by the LOWER id, and 0 when none of them has a visible underlay. Void tiles and
    /// underlays hidden by drawn full overlays are excluded, exactly as <see cref="CornerColor"/> excludes them.
    /// A <see cref="TileSettings.NoDraw"/> tile still decides the material at the corners it touches and the ground
    /// stays continuous across a hole punched for an object floor. The rule reads the global grid and breaks its
    /// ties without reference to which tile is asking, so every tile sharing a corner picks the same material
    /// there and the corner cannot seam.</summary>
    public static ushort CornerMaterial(TileWorldDocument doc, int worldX, int worldZ, int plane)
    {
        ArgumentNullException.ThrowIfNull(doc);

        Span<ushort> ids = stackalloc ushort[4];
        Span<int> counts = stackalloc int[4];
        int distinct = 0;
        for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
            {
                ushort underlay = VisibleUnderlay(doc, worldX + dx, worldZ + dz, plane);
                if (underlay == 0) continue;
                int at = 0;
                while (at < distinct && ids[at] != underlay) at++;
                if (at == distinct)
                {
                    ids[distinct] = underlay;
                    counts[distinct] = 0;
                    distinct++;
                }
                counts[at]++;
            }

        ushort best = 0;
        int bestCount = 0;
        for (int i = 0; i < distinct; i++)
        {
            if (counts[i] < bestCount) continue;
            if (counts[i] == bestCount && ids[i] >= best) continue;
            best = ids[i];
            bestCount = counts[i];
        }
        return best;
    }

    /// <summary>The brightness multiplier at a lattice corner: the mean of <see cref="TileColors.Jitter"/> over
    /// the same visible underlays <see cref="CornerMaterial"/> counts, so the ground varies softly across a corner instead of
    /// stepping at every tile edge, and 1 when none of them has an underlay. It is a MULTIPLIER the shader
    /// applies to the sampled albedo, so no jitter is 1 and never 0: a vertex carrying 0 renders black. That is
    /// why <paramref name="amplitude"/> is refused at 1 and above, which leaves every answer inside (0, 2).</summary>
    public static float CornerJitter(
        TileWorldDocument doc,
        int worldX,
        int worldZ,
        int plane,
        float amplitude = TileColors.DefaultJitterAmplitude)
    {
        ArgumentNullException.ThrowIfNull(doc);
        CheckedAmplitude(amplitude, nameof(amplitude));

        float sum = 0f;
        int count = 0;
        for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
            {
                int tx = worldX + dx;
                int tz = worldZ + dz;
                if (VisibleUnderlay(doc, tx, tz, plane) == 0) continue;
                sum += TileColors.Jitter(tx, tz, plane, amplitude);
                count++;
            }
        return count == 0 ? 1f : sum / count;
    }

    // The underlay seen by its neighbours. NoDraw remains visible for blending because another object supplies
    // that tile's surface. A drawn full overlay hides the underlay, so carrying it into a neighbouring tile would
    // make an exact overlay boundary grade into the material below it.
    static ushort VisibleUnderlay(TileWorldDocument doc, int x, int z, int plane)
    {
        ushort underlay = doc.GetUnderlay(x, z, plane);
        if (underlay == 0) return 0;
        bool drawnFullOverlay = (doc.GetSettings(x, z, plane) & TileSettings.NoDraw) == 0
            && doc.GetOverlay(x, z, plane) != 0
            && doc.GetOverlayShape(x, z, plane) == TileOverlayShape.Full;
        return drawnFullOverlay ? (ushort)0 : underlay;
    }

    /// <summary>The amplitude back, or a throw: it runs from 0 (no jitter) up to but not including 1, so the
    /// multiplier stays strictly positive and no vertex can carry the 0 that renders black. NaN is refused by the
    /// same test.</summary>
    internal static float CheckedAmplitude(float amplitude, string parameterName) =>
        amplitude >= 0f && amplitude < 1f
            ? amplitude
            : throw new ArgumentOutOfRangeException(
                parameterName, amplitude, "the jitter amplitude runs from 0 up to but not including 1.");

    /// <summary>The four slots of the tile at region-local (lx, lz), one per corner.</summary>
    static TileCornerSlots TileSlots(in TileMeshContext c, int lx, int lz)
    {
        int x = c.OriginX + lx;
        int z = c.OriginZ + lz;
        return new TileCornerSlots(
            SlotAt(c, x, z),
            SlotAt(c, x + 1, z),
            SlotAt(c, x, z + 1),
            SlotAt(c, x + 1, z + 1));
    }

    /// <summary>The slot the material set holds this lattice corner's material in.</summary>
    static int SlotAt(in TileMeshContext c, int cornerX, int cornerZ) =>
        c.Options.Slots.SlotOf(CornerMaterial(c.Doc, cornerX, cornerZ, c.Plane));

    /// <summary>A corner point's weights: all of them on its own corner, none on the other three. The corner is
    /// numbered the way the slots are, SW 0, SE 1, NW 2, NE 3, which is dz * 2 + dx over the corner's own
    /// 0-or-1 step on each axis.</summary>
    static Vector4 CornerWeights(int corner) => corner switch
    {
        1 => new Vector4(0f, 1f, 0f, 0f),
        2 => new Vector4(0f, 0f, 1f, 0f),
        3 => new Vector4(0f, 0f, 0f, 1f),
        _ => new Vector4(1f, 0f, 0f, 0f),
    };

    /// <summary>The four material slots of one tile, in corner order SW, SE, NW, NE. Identical on every lattice
    /// point and every triangle of the tile, which is what makes the ground continuous: a corner shared by four
    /// tiles is one-hot on the same material from all of them, and an edge shared by two interpolates the same
    /// pair of materials from either side.</summary>
    internal readonly struct TileCornerSlots
    {
        internal TileCornerSlots(int sw, int se, int nw, int ne)
        {
            Sw = sw;
            Se = se;
            Nw = nw;
            Ne = ne;
        }

        /// <summary>Slot 0, the south west corner's material.</summary>
        public int Sw { get; }
        /// <summary>Slot 1, the south east corner's material.</summary>
        public int Se { get; }
        /// <summary>Slot 2, the north west corner's material.</summary>
        public int Nw { get; }
        /// <summary>Slot 3, the north east corner's material.</summary>
        public int Ne { get; }

        /// <summary>The same slot in all four corners, which is what an overlay-painted triangle carries.</summary>
        public static TileCornerSlots Uniform(int slot) => new(slot, slot, slot, slot);
    }
}
