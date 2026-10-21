using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Scene;

internal static partial class NativeSceneFixtures
{
    static NativeSceneFixture Water(string variant)
    {
        var fixture = Create("empty");
        var doc = fixture.Document;
        var frame = new MapLatticeFrame(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        int width = variant == "water-bent-border" ? 26 : variant.StartsWith("water-bent", StringComparison.Ordinal) ? 10 : 2;
        int depth = width >= 10 ? 7 : 2;
        bool negative = variant == "water-negative";
        var floorKey = new MapPatchKey("wet-floor", negative ? -1 : 0, negative ? -1 : 0);
        Surface(floorKey, MapSurfaceRole.SupportFloor, -200);
        var upper = new MapBoundRef(MapBoundKind.OpenTop, null, null);
        if (variant == "water-closed")
        {
            Surface(new("wet-roof", 0, 0), MapSurfaceRole.Ceiling, -100);
            upper = new(MapBoundKind.Ceiling, "wet-roof", null);
        }
        var floor = doc.Surfaces.Patches[floorKey];
        floor.Records.Add(new MapSpaceDoc("wet-space", MapSpaceKind.Exterior, null, null, [], [], [], []));
        floor.Records.Add(new MapSpaceFootprint("wet-footprint", new("wet-space", floorKey), floorKey,
            Enumerable.Range(0, depth).SelectMany(z => Enumerable.Range(0, width).Select(x => 64 * (z + floor.CellMinZ) + x + floor.CellMinX)).ToArray(),
            new(MapBoundKind.SupportFloor, "wet-floor", null), upper));
        if (variant == "water-missing") floor.SetPresent(1, 1, false);
        if (variant == "water-bent-missing-halo") floor.SetPresent(2, 0, false);
        foreach (var surface in doc.Surfaces.Refs.ToArray())
        {
            int index = doc.Surfaces.Refs.FindIndex(s => s.Id == surface.Id);
            doc.Surfaces.Refs[index] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                doc.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
        MapWaterDomainDoc domain = variant switch
        {
            "water-bent-one" or "water-bent-missing-halo" => new(frame, [], [new(new[] { (0L, 0L), (10L, 0L), (10L, 1L), (1L, 1L), (1L, 7L), (0L, 7L) }
                .Select(p => MapLatticeAddress.Corner(p.Item1, p.Item2)).ToArray())]),
            "water-bent-sixteen" => new(frame, Enumerable.Range(0, 10).Select(x => new MapWaterRect(x, 0, x + 1, 1))
                .Concat(Enumerable.Range(1, 6).Select(z => new MapWaterRect(0, z, 1, z + 1))).ToArray(), []),
            "water-bent-border" => new(frame, [new(16, 0, 26, 1), new(16, 1, 17, 7)], []),
            "water-notch" => new(frame, [new(0, 0, 2, 1), new(0, 1, 1, 2)], []),
            "water-negative" => new(frame, [new(-2, -2, 0, 0)], []),
            _ => new(frame, [new(0, 0, 2, 2)], []),
        };
        doc.Water = new();
        doc.Water.Media.Add(new("fresh", MapWaterMediumKind.Water, .75f));
        doc.Water.Bodies.Add(new("bent-water", .25f, "fresh", "water-look", domain, [new("wet-space", floorKey)]));
        return fixture;

        void Surface(MapPatchKey key, MapSurfaceRole role, int height)
        {
            var patch = new MapSurfacePatch
            {
                Key = key,
                Width = width,
                Depth = depth,
                CellMinX = negative ? 62 : 0,
                CellMinZ = negative ? 62 : 0,
                Heights = Enumerable.Repeat(height, (width + 1) * (depth + 1)).ToArray(),
                Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.ForceSwNe), width * depth).ToArray(),
                Presence = Enumerable.Repeat(ulong.MaxValue, (width * depth + 63) / 64).ToArray()
            };
            // Clear unused presence bits, as the persisted patch contract requires.
            if (width * depth % 64 != 0) patch.Presence[^1] = (1UL << (width * depth % 64)) - 1;
            doc.Surfaces.Patches.Add(key, patch);
            doc.Surfaces.Refs.Add(new(key.SurfaceId, frame, role, MapPresencePolicy.Native, null, null, ""));
        }
    }

    internal static MapSceneScope WaterWindow(float minX = 0, float maxX = 2, float maxZ = 2, float minZ = 0) => new(
        new(WorldFrame.Origin, new(minX, minZ), new(maxX, maxZ), null, null, Enum.GetValues<MapSurfaceRole>(), null, new()),
        new(minX, -10, minZ, maxX, 10, maxZ), MapSceneChannels.Water);
}
