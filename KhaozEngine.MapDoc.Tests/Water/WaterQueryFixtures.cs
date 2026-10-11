using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Water;

internal static partial class WaterFixtures
{
    internal static MapFramePoint Point(float x, float y, float z) => new(WorldFrame.Origin, new(x, y, z));

    internal static MapWaterQuery Query(MapDocument doc) => new(CaveFixtures.View(doc), doc.Water!);

    /// <summary>The released peer probe of <c>SpaceMembershipTests</c>, where the alias-normalized column is
    /// <c>chamber</c> [5, 9) and <c>outside</c> [10, open).</summary>
    internal static MapFramePoint PeerPoint() => Point(2.5f, 5.5f, 4.5f);

    /// <summary><see cref="CaveFixtures.WithPeerChamber"/> without an alias, with resolver-2 identity, one medium and
    /// body <c>chamber-pool</c> covering <c>chamber</c>.</summary>
    internal static MapDocument PeerChambersWithWater()
    {
        MapDocument peers = CaveFixtures.WithPeerChamber(null);
        MapLatticeFrame frame = peers.Surfaces.Refs.Single(s => s.Id == "cave-floor").Frame;
        var water = new MapWaterSet();
        water.Media.Add(new MapWaterMediumDoc(Medium, MapWaterMediumKind.Water, 1f));
        water.Bodies.Add(new MapWaterBodyDoc("chamber-pool", 8f, Medium, Material,
            new MapWaterDomainDoc(frame, new[] { Rect(2, 3, 6, 8) }, Array.Empty<MapWaterRing>()),
            new[] { new MapRecordRef("chamber", CaveFixtures.Key("cave-floor")) }));
        return Document(peers.Surfaces, water);
    }

    /// <summary>Points every 0.5 m in x and z over the fixture's surface extent, edges included, and every 1 m in y
    /// from -62 to 4, plus each body's surface height and one float step below it. Subnormal steps, such as the one
    /// below zero, are outside <see cref="MapExactValue.FromSingle"/> and are left out.</summary>
    internal static IReadOnlyList<MapFramePoint> ProbeGrid(MapDocument doc)
    {
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (var (key, patch) in doc.Surfaces.Patches)
        {
            MapLatticeFrame frame = doc.Surfaces.Refs.Single(s => s.Id == key.SurfaceId).Frame;
            long x0 = key.SlotX * MapPatchKey.SlotCells + patch.CellMinX, z0 = key.SlotZ * MapPatchKey.SlotCells + patch.CellMinZ;
            foreach (MapLatticeAddress corner in new[] { MapLatticeAddress.Corner(x0, z0), MapLatticeAddress.Corner(x0 + patch.Width, z0 + patch.Depth) })
            {
                MapExactXz world = frame.WorldXz(corner);
                minX = MathF.Min(minX, world.X.ToSingle()); maxX = MathF.Max(maxX, world.X.ToSingle());
                minZ = MathF.Min(minZ, world.Z.ToSingle()); maxZ = MathF.Max(maxZ, world.Z.ToSingle());
            }
        }
        IEnumerable<float> heights = Enumerable.Range(-62, 67).Select(y => (float)y);
        if (doc.Water is { } water)
            heights = heights.Concat(water.Bodies.SelectMany(b => new[] { b.SurfaceY, MathF.BitDecrement(b.SurfaceY) }));
        float[] ys = heights.Where(y => y == 0 || float.IsNormal(y)).Distinct().Order().ToArray();
        var points = new List<MapFramePoint>();
        for (float x = minX; x <= maxX; x += 0.5f)
            for (float z = minZ; z <= maxZ; z += 0.5f)
                points.AddRange(ys.Select(y => Point(x, y, z)));
        return points;
    }
}
