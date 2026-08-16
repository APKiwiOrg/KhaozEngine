using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>The object-placing half of the high-level factories: a line of objects, a deterministic scatter,
/// and a prefab stamp. Like the height factories in the other part of this class, nothing here mutates. Each
/// one READS the document and returns the command that expresses the edit, and the caller hands that to
/// <see cref="TileEditingDocument.Execute"/>.</summary>
public static partial class TileEditOps
{
    /// <summary>One object per tile of the Bresenham line from <paramref name="from"/> to
    /// <paramref name="to"/>, both ends included, as a single undo step. Every object takes the same archetype
    /// and the same rotation, which is what a run of fence or wall pieces wants.</summary>
    public static CompositeCommand Line(TileWorldCatalogs catalogs, string archetypeId, (int X, int Z) from,
        (int X, int Z) to, int plane, int rotation = 0)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var commands = new List<ITileCommand>();
        foreach ((int x, int z) in Bresenham(from, to))
            commands.Add(new PlaceObjectCommand(catalogs, archetypeId, x, z, plane, rotation));
        return new CompositeCommand("Line", commands);
    }

    /// <summary>A deterministic scatter of one archetype over a rect: a grid at <paramref name="spacing"/>,
    /// each point pushed by up to <paramref name="jitter"/> tiles on each axis, the offset coming from a hash
    /// of the grid point and <paramref name="seed"/> rather than a random source, so the same arguments always
    /// produce the same world. A point is skipped when the jitter carries it out of the rect, when its tile
    /// reads blocked (which covers a region that does not exist, since the collision map answers blocked for
    /// one it does not hold), when an object is already anchored there, or when an earlier point of this same
    /// scatter already claimed it. The result can legitimately be empty.</summary>
    public static CompositeCommand Scatter(TileEditingDocument editing, string archetypeId, TileRect rect, int plane,
        int spacing, int jitter, int seed)
    {
        ArgumentNullException.ThrowIfNull(editing);
        ArgumentOutOfRangeException.ThrowIfLessThan(spacing, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        var commands = new List<ITileCommand>();
        if (rect.IsEmpty) return new CompositeCommand("Scatter", commands);

        // Every anchor already in the rect, taken once up front. The set then doubles as the claimed-tile
        // record, so a point the jitter pushes onto a tile an earlier point took is skipped by the same test.
        var taken = new HashSet<(int X, int Z)>();
        foreach (TileObject o in editing.Document.ObjectsIn(rect, plane)) taken.Add((o.X, o.Z));

        for (int gz = rect.Z; gz < rect.Z1; gz += spacing)
            for (int gx = rect.X; gx < rect.X1; gx += spacing)
            {
                (int x, int z) = Jitter(gx, gz, seed, jitter);
                if (!rect.Contains(x, z)) continue;
                if ((editing.Collision.Get(x, z, plane) & TileCollisionFlags.Blocked) != 0) continue;
                if (!taken.Add((x, z))) continue;
                commands.Add(new PlaceObjectCommand(editing.Catalogs, archetypeId, x, z, plane, 0));
            }
        return new CompositeCommand("Scatter", commands);
    }

    /// <summary>Stamps a prefab as one undo step. The command is a <see cref="SnapshotRectCommand"/> over the
    /// exact rect <see cref="TilePrefabs.Place"/> touches (its tile rect grown by one tile to the west and
    /// south and one row and column to the north and east, for the corner writes on its far edges) across the
    /// planes the prefab carries, so the undo restores the layers, heights, objects and markers the stamp
    /// overwrote rather than trying to unpick the stamp write by write.
    ///
    /// The planes are <paramref name="plane"/> through <paramref name="plane"/> + the prefab's plane count - 1,
    /// unclipped. A prefab that reaches above the world's planes is refused by
    /// <see cref="TileEditingDocument.Execute"/> rather than clipped the way <see cref="TilePrefabs.Place"/>
    /// clips it, because a snapshot must not claim a rect it cannot capture or restore.</summary>
    public static SnapshotRectCommand PlacePrefab(TilePrefab prefab, int x, int z, int plane, int rotation)
    {
        ArgumentNullException.ThrowIfNull(prefab);
        // Rotated the same way the stamp will rotate it, so the width and height below are the ones Place
        // measures its own touched rect from and the two cannot drift apart.
        TilePrefab rotated = TilePrefabs.Rotate(prefab, rotation);
        TileRect rect = TileRect.FromCorners(x - 1, z - 1, x + rotated.Width, z + rotated.Height);
        var planes = new List<int>();
        for (int i = 0; i < prefab.PlaneCount; i++) planes.Add(plane + i);
        return new SnapshotRectCommand("Place prefab", rect, planes,
            doc => TilePrefabs.Place(doc, prefab, x, z, plane, rotation));
    }

    // The integer Bresenham walk, both ends inclusive, in one enumerator so the line factory and its tests read
    // the same tile order.
    static IEnumerable<(int X, int Z)> Bresenham((int X, int Z) from, (int X, int Z) to)
    {
        int x = from.X, z = from.Z;
        int dx = Math.Abs(to.X - x), sx = x < to.X ? 1 : -1;
        int dz = -Math.Abs(to.Z - z), sz = z < to.Z ? 1 : -1;
        int err = dx + dz;
        while (true)
        {
            yield return (x, z);
            if (x == to.X && z == to.Z) yield break;
            int e2 = 2 * err;
            if (e2 >= dz) { err += dz; x += sx; }
            if (e2 <= dx) { err += dx; z += sz; }
        }
    }

    // The jittered tile for one grid point. Both offsets come out of ONE mix, the low half driving x and the
    // high half z, so a point costs a single hash. A jitter of 0 gives a span of 1 and lands the point back on
    // its own grid coordinate.
    static (int X, int Z) Jitter(int gx, int gz, int seed, int jitter)
    {
        if (jitter == 0) return (gx, gz);
        ulong h = Mix(((ulong)(uint)gx << 32) | (uint)gz, (ulong)(uint)seed);
        ulong span = (ulong)(2 * jitter + 1);
        return (gx + (int)(h % span) - jitter, gz + (int)((h >> 32) % span) - jitter);
    }

    // splitmix64's finaliser, run over the point and then over the seeded result, which is enough avalanche for
    // a placement offset and, unlike a Random, gives the same answer on every machine and every run forever.
    // Written out here rather than taken from a library on purpose: this IS the world's content.
    static ulong Mix(ulong point, ulong seed)
    {
        unchecked
        {
            ulong v = point + 0x9E3779B97F4A7C15UL + (seed * 0xD1B54A32D192ED03UL);
            v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL;
            v = (v ^ (v >> 27)) * 0x94D049BB133111EBUL;
            return v ^ (v >> 31);
        }
    }
}
