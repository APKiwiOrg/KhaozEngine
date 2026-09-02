using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>ONE interior: the 4-connected flood fill of tiles carrying <see cref="TileSettings.Indoors"/> on a
/// single plane, seeded from the tile an observer stands on. This is what makes the roof rule local, so walking
/// into a house hides that building's roofs and leaves every other roof in view alone.
/// <para>The walk is BOUNDED by <see cref="MaxTiles"/>. An authoring mistake that flags a whole region indoors
/// would otherwise cost tens of thousands of settings reads on the frame the observer steps into it, so the walk
/// stops at the cap and everything it never reached is simply not part of the interior. The failure direction is
/// a roof left visible, never a stalled frame and never a throw. <see cref="Truncated"/> reports it happened, and
/// the view turns that into one log line.</para>
/// <para>Internal because the roof rule is the only reader. What a caller needs is on the view:
/// <c>TileWorldView.IsRoofHidden</c>, <c>TileWorldView.InteriorTileCount</c> and
/// <c>TileWorldView.InteriorTruncated</c>.</para></summary>
sealed class TileInteriorFill
{
    /// <summary>Most tiles one interior may hold. A generous building and then some: the largest OSRS-style
    /// interior is a bank or a multi-room castle floor, which is hundreds of tiles, so a fill that reaches this
    /// is content saying "indoors" over ground that is not one room.</summary>
    public const int MaxTiles = 4096;

    // Row-major deltas of the four edge neighbours. 4-connected on purpose: two rooms touching only at a corner
    // are two buildings to look at, so they are two interiors.
    static readonly int[] NeighbourX = { -1, 1, 0, 0 };
    static readonly int[] NeighbourZ = { 0, 0, -1, 1 };

    readonly HashSet<long> _tiles = new();
    readonly Queue<long> _frontier = new();

    /// <summary>How many tiles the interior holds. 0 when the observer is not indoors.</summary>
    public int Count => _tiles.Count;

    /// <summary>The plane the fill ran on, which is the observer's own.</summary>
    public int Plane { get; private set; }

    /// <summary>True when the last rebuild stopped at <see cref="MaxTiles"/> with tiles still to walk.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The tile rect the interior fits inside, empty when it holds nothing. The cheap reject
    /// <see cref="Intersects"/> opens with.</summary>
    public TileRect Bounds { get; private set; }

    /// <summary>Refills the set from the document. <paramref name="seedIndoors"/> is the caller's already-read
    /// answer for the seed tile, so the view does not pay the lookup twice: false clears the interior and returns,
    /// which is the outdoor case and the common one.</summary>
    public void Rebuild(TileWorldDocument doc, TileCoord seed, bool seedIndoors)
    {
        ArgumentNullException.ThrowIfNull(doc);

        _tiles.Clear();
        _frontier.Clear();
        Truncated = false;
        Plane = seed.Plane;
        Bounds = default;
        if (!seedIndoors) return;

        int minX = seed.X, maxX = seed.X, minZ = seed.Z, maxZ = seed.Z;
        _tiles.Add(Pack(seed.X, seed.Z));
        _frontier.Enqueue(Pack(seed.X, seed.Z));

        while (_frontier.Count > 0)
        {
            long at = _frontier.Dequeue();
            int x = (int)(at >> 32), z = (int)at;
            for (int i = 0; i < NeighbourX.Length; i++)
            {
                int nx = x + NeighbourX[i], nz = z + NeighbourZ[i];
                long key = Pack(nx, nz);
                if (_tiles.Contains(key)) continue;
                // A tile outside every existing region reads None, so an interior that runs off the authored
                // world stops at its edge rather than walking empty space to the cap.
                if ((doc.GetSettings(nx, nz, Plane) & TileSettings.Indoors) == 0) continue;
                // Checked here rather than at the head of the loop so an interior that is EXACTLY the cap, with
                // nothing left to reach, is a complete fill rather than a truncated one.
                if (_tiles.Count >= MaxTiles) { Truncated = true; break; }

                _tiles.Add(key);
                _frontier.Enqueue(key);
                if (nx < minX) minX = nx;
                if (nx > maxX) maxX = nx;
                if (nz < minZ) minZ = nz;
                if (nz > maxZ) maxZ = nz;
            }

            if (Truncated) break;
        }

        if (Truncated) _frontier.Clear();
        Bounds = TileRect.FromCorners(minX, minZ, maxX, maxZ);
    }

    /// <summary>True when world tile (x, z) is part of the interior. The plane is the fill's own.</summary>
    public bool Contains(int x, int z) => _tiles.Contains(Pack(x, z));

    /// <summary>True when any tile of <paramref name="footprint"/> is part of the interior, which is how an
    /// object standing over several tiles is judged: a roof belongs to this building when it covers any of it.
    /// <para>Walks whichever side is smaller, the clipped footprint or the set, so a caller cannot make this
    /// expensive by handing over an enormous rect.</para></summary>
    public bool Intersects(TileRect footprint)
    {
        if (_tiles.Count == 0 || footprint.IsEmpty) return false;
        TileRect probe = Bounds.Intersect(footprint);
        if (probe.IsEmpty) return false;

        if ((long)probe.Width * probe.Height > _tiles.Count)
        {
            foreach (long key in _tiles)
                if (footprint.Contains((int)(key >> 32), (int)key)) return true;
            return false;
        }

        for (int z = probe.Z; z < probe.Z1; z++)
            for (int x = probe.X; x < probe.X1; x++)
                if (_tiles.Contains(Pack(x, z))) return true;
        return false;
    }

    // x in the high half, z in the low half, both signed. The shift back out is arithmetic for x and a plain
    // truncation for z, so negative world coordinates survive the round trip.
    static long Pack(int x, int z) => ((long)x << 32) | (uint)z;
}
