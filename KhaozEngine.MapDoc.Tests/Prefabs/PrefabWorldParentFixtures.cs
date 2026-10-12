using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    // T5-R1. The instance rotates freely inside an independently authored world-aligned exterior.
    internal static void AuthorWorldParent(PrefabFixture fixture, MapTransform pose, Func<double, double, double>? localFloor = null, MapRational? cellUnit = null)
    {
        var document = fixture.Document;
        var frame = MapLatticeFrame.ImportedMetreCentimetre with { CellUnitMetres = cellUnit ?? new(1, 1) };
        double cellMetres = frame.CellUnitMetres.Numerator / (double)frame.CellUnitMetres.Denominator;
        var (sin, cos) = Math.SinCos(pose.YawRadians);
        var corners = new[] { Vector2.Zero, new Vector2(16, 0), new Vector2(16, -16), new Vector2(0, -16) }
            .Select(p => new Vector2((float)(pose.Position.X + pose.Scale * (cos * p.X + sin * p.Y)),
                (float)(pose.Position.Z + pose.Scale * (-sin * p.X + cos * p.Y)))).ToArray();
        int x0 = (int)Math.Floor(corners.Min(p => p.X) / cellMetres) - 2, x1 = (int)Math.Ceiling(corners.Max(p => p.X) / cellMetres) + 2;
        int z0 = (int)Math.Floor(-corners.Max(p => p.Y) / cellMetres) - 2, z1 = (int)Math.Ceiling(-corners.Min(p => p.Y) / cellMetres) + 2;
        document.Surfaces = new();
        Add("exterior", MapSurfaceRole.SupportFloor, (int)Math.Round((pose.Position.Y - 2 * pose.Scale) * 100));
        Add("exterior-ceiling", MapSurfaceRole.Ceiling, (int)Math.Round((pose.Position.Y + 8 * pose.Scale) * 100));
        var anchor = document.Surfaces.Patches.Keys.Where(k => k.SurfaceId == "exterior").Order().First();
        document.Surfaces.Patches[anchor].Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        foreach (var patch in document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "exterior"))
        {
            string id = patch.Key == anchor ? "exterior-domain" : $"exterior-domain-{patch.Key.SlotX}-{patch.Key.SlotZ}";
            patch.Records.Add(new MapSpaceFootprint(id, new("exterior", anchor), patch.Key,
                Enumerable.Range(patch.CellMinZ, patch.Depth).SelectMany(z => Enumerable.Range(patch.CellMinX, patch.Width).Select(x => z * 64 + x)).ToArray(),
                new(MapBoundKind.SupportFloor, "exterior", null), new(MapBoundKind.Ceiling, "exterior-ceiling", null)));
        }
        if (localFloor is not null)
            foreach (var patch in document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "exterior"))
                for (int z = 0; z <= patch.Depth; z++)
                    for (int x = 0; x <= patch.Width; x++)
                    {
                        double dx = (patch.Key.SlotX * 64 + patch.CellMinX + x) * cellMetres - pose.Position.X;
                        double dz = -(patch.Key.SlotZ * 64 + patch.CellMinZ + z) * cellMetres - pose.Position.Z;
                        double localX = (cos * dx - sin * dz) / pose.Scale;
                        double localZ = (sin * dx + cos * dz) / pose.Scale;
                        patch.Heights[z * (patch.Width + 1) + x] = (int)Math.Round(100 * (pose.Position.Y + pose.Scale * localFloor(localX, localZ)));
                    }
        var instance = document.Prefabs!.Instances.Single();
        document.Prefabs = new(1, 1, new[] { instance with { ParentSpace = new("exterior", anchor) } }, []);
        document.NativeResourceTransforms = null;
        document.Bounds = new() { MinX = pose.Position.X - 64, MinZ = pose.Position.Z - 64, MaxX = pose.Position.X + 64, MaxZ = pose.Position.Z + 64 };
        document.PlayableBounds = new() { MinX = document.Bounds.MinX, MinZ = document.Bounds.MinZ, MaxX = document.Bounds.MaxX, MaxZ = document.Bounds.MaxZ };
        SealWorldParent(document);

        void Add(string id, MapSurfaceRole role, int height)
        {
            document.Surfaces.Refs.Add(Surface(id, role) with { Frame = frame });
            for (int sz = (int)Math.Floor(z0 / 64d); sz * 64 < z1; sz++)
                for (int sx = (int)Math.Floor(x0 / 64d); sx * 64 < x1; sx++)
                {
                    int minX = Math.Max(x0, sx * 64), maxX = Math.Min(x1, (sx + 1) * 64);
                    int minZ = Math.Max(z0, sz * 64), maxZ = Math.Min(z1, (sz + 1) * 64);
                    var patch = Patch(id, maxX - minX, maxZ - minZ, height);
                    patch.Key = new(id, sx, sz);
                    patch.CellMinX = minX - sx * 64; patch.CellMinZ = minZ - sz * 64;
                    document.Surfaces.Patches.Add(patch.Key, patch);
                }
        }
    }

    internal static void SealWorldParent(MapDocument document)
    {
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                document.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id)
                    .Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
    }
}
