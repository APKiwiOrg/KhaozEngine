using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>The authored inputs to interior collection, excluding unrelated prop and ground edits.</summary>
internal static class TileInteriorInputs
{
    internal static long Digest(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, int plane)
    {
        if ((uint)plane >= (uint)document.PlaneCount) throw new ArgumentOutOfRangeException(nameof(plane));
        var hash = new InputHash();
        hash.Add(region.Rx);
        hash.Add(region.Rz);
        hash.Add(plane);
        hash.Add(document.PlaneCount);
        hash.Add(BitConverter.SingleToInt32Bits(document.TileSize));
        hash.Add(BitConverter.SingleToInt32Bits(document.PlaneHeight));
        TileRect rect = region.Rect, halo = rect.Expand(1);
        // A neighbouring indoor flag can change whether a wall on the region boundary is safe.
        for (int z = halo.Z; z < halo.Z1; z++)
            for (int x = halo.X; x < halo.X1; x++)
                hash.Add(Indoors(document, x, z, plane) ? 1 : 0);

        var roofFootprints = new List<TileRect>();
        foreach (TileObject o in document.AllObjects().Where(o => o.Plane == plane).OrderBy(o => o.Id))
        {
            if (catalogs.Archetype(o.ArchetypeId) is not { } a) continue;
            bool wall = a.CollisionKind is TileCollisionKind.Wall or TileCollisionKind.WallCorner or TileCollisionKind.Diagonal;
            TileRect footprint = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
            bool roof = a.IsRoof && footprint.Intersects(rect);
            if (!(wall && halo.Contains(o.X, o.Z)) && !roof) continue;
            hash.Add(o.Id);
            hash.Add(o.ArchetypeId);
            hash.Add(o.X);
            hash.Add(o.Z);
            hash.Add(o.Rotation);
            hash.Add((int)a.CollisionKind);
            hash.Add(a.IsRoof ? 1 : 0);
            if (!roof) continue;
            hash.Add(a.SizeX);
            hash.Add(a.SizeZ);
            hash.Add(a.MeshRef ?? string.Empty);
            hash.Add(BitConverter.SingleToInt32Bits(a.YawOffsetDegrees));
            roofFootprints.Add(footprint);
        }

        // Read resolved corners, including the far edge owned by another region and derived upper planes.
        // Ceiling corners also matter when only the plane below carries the indoor flags.
        for (int z = rect.Z; z <= rect.Z1; z++)
            for (int x = rect.X; x <= rect.X1; x++)
            {
                bool relevant = IndoorCorner(document, x, z, plane) ||
                    plane > 0 && IndoorCorner(document, x, z, plane - 1);
                if (!relevant)
                    foreach (TileRect roof in roofFootprints)
                        if (x >= roof.X && x <= roof.X1 && z >= roof.Z && z <= roof.Z1)
                        {
                            relevant = true;
                            break;
                        }
                if (!relevant) continue;
                hash.Add(x);
                hash.Add(z);
                hash.Add(document.CornerHeightCm(x, z, plane));
            }
        return hash.Value;
    }

    static bool IndoorCorner(TileWorldDocument doc, int x, int z, int plane) =>
        Indoors(doc, x, z, plane) || Indoors(doc, x - 1, z, plane) ||
        Indoors(doc, x, z - 1, plane) || Indoors(doc, x - 1, z - 1, plane);

    static bool Indoors(TileWorldDocument doc, int x, int z, int plane) =>
        (doc.GetSettings(x, z, plane) & TileSettings.Indoors) != 0;

    struct InputHash
    {
        ulong _value;
        public InputHash() => _value = 14695981039346656037UL;
        public readonly long Value => unchecked((long)_value);

        public void Add(long value)
        {
            for (int b = 0; b < 8; b++)
            {
                _value = unchecked((_value ^ (byte)value) * 1099511628211UL);
                value >>= 8;
            }
        }

        public void Add(string value)
        {
            Add(value.Length);
            foreach (char c in value) Add(c);
        }
    }
}
