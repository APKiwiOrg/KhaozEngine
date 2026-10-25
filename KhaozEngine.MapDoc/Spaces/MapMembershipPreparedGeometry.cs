using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Canonical acquired cells and exact predicates retained once by the prepared point adapter.</summary>
internal sealed class MapMembershipPreparedGeometry
{
    internal sealed class Cell
    {
        internal long InventoryBytes => checked(256L + Polygon.InventoryBytes + 256L * Faces.Length +
            Triangles.Sum(t => 8L + t.InventoryBytes) + (SpanLower?.Sum(t => 8L + t.InventoryBytes) ?? 0) +
            (SpanUpper?.Sum(t => 8L + t.InventoryBytes) ?? 0) + (LegacyX?.Sum(t => 8L + t.InventoryBytes) ?? 0) +
            (LegacyZ?.Sum(t => 8L + t.InventoryBytes) ?? 0));
        internal readonly int Id, PatchIndex;
        internal int FaceSet;
        internal readonly MapCellDemand Demand;
        internal readonly MapSurfaceRef Surface;
        internal readonly MapSurfacePatch Patch;
        internal readonly MapMembershipPolygon Polygon;
        internal readonly MapExactXz Centre;
        internal readonly MapBoundFace[] Faces;
        internal readonly MapMembershipTriangle[] Triangles;
        internal readonly int PhysicalFaces;
        internal readonly bool Indoor;
        internal readonly MapResourceTransform? Transform;
        internal readonly MapMembershipTriangle[]? SpanLower, SpanUpper, LegacyX, LegacyZ;
        readonly int _x, _z;
        internal Cell(int id, int patchIndex, int slot, MapScopedSurfaces view, MapSurfaceRef surface,
            MapSurfacePatch patch, MapCompiledPatch local, MapCompiledPatch compiled, MapCompiledFace[] localFaces, MapCompiledFace[] worldFaces)
        {
            MapMembershipBuild.Inventory(checked(256L + 256L * worldFaces.Length));
            Id = id; PatchIndex = patchIndex; Demand = new(patch.Key, slot); Surface = surface; Patch = patch;
            _x = slot % 64 - patch.CellMinX; _z = slot / 64 - patch.CellMinZ;
            var polygon = MapEffectiveGeometry.CellPolygon(view, patch.Key, slot);
            Polygon = new(polygon, cell: true);
            Centre = new(polygon.Aggregate(default(MapExactValue), (sum, p) => sum.Add(p.X)).Divide(new(4, 1)),
                polygon.Aggregate(default(MapExactValue), (sum, p) => sum.Add(p.Z)).Divide(new(4, 1)));
            Faces = worldFaces.Select(f => new MapBoundFace(f.Key, compiled.ExactTriangle(f))).ToArray();
            Triangles = Faces.Select(f => new MapMembershipTriangle(f.Triangle)).ToArray();
            PhysicalFaces = checked((int)MapSurfaceCompiler.CountFaces(surface, patch, slot));
            Indoor = (patch.Cells[_z * patch.Width + _x].Flags & MapCellFlags.Indoor) != 0;
            Transform = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surface.Id);
            if (surface.IndoorSpan is { } span)
            {
                SpanLower = Raised(span.LowerOffsetUnits); SpanUpper = Raised(span.UpperOffsetUnits);
            }
            if (MapSurfaceCompiler.Fallback(surface, patch.Cells[_z * patch.Width + _x]) && Transform is not null)
            {
                MapExactPoint P(int i, long y) => new(polygon[i].X, new(y, 1), polygon[i].Z);
                LegacyX = [new(new(P(0, 0), P(1, 1), P(2, 1))), new(new(P(0, 0), P(2, 1), P(3, 0)))];
                LegacyZ = [new(new(P(0, 0), P(1, 0), P(2, 1))), new(new(P(0, 0), P(2, 1), P(3, 1)))];
            }
            MapMembershipTriangle[] Raised(long units)
            {
                var offset = surface.Frame.Metres(new(units, 1));
                MapExactPoint Raise(MapExactPoint p) => Transform?.Point(p with { Y = p.Y.Add(offset) }) ?? p with { Y = p.Y.Add(offset) };
                return localFaces.Select(f =>
                {
                    var t = local.ExactTriangle(f);
                    return new MapMembershipTriangle(new(Raise(t.A), Raise(t.B), Raise(t.C)));
                }).ToArray();
            }
        }
        internal MapExactValue? LegacyHeight(MapExactXz point, MapMembershipExact exact)
        {
            if (Transform is null)
            {
                if (MapMembershipCoordinates.Single(MapLegacyBilinear.HeightMetres(Surface, Patch, point), out var sampled)) return sampled;
                exact.NotRepresentable = true; return null;
            }
            int index = LegacyX![0].Contains(point, exact) ? 0 : 1;
            float x = (float)LegacyX[index].HeightDouble(point, exact);
            float z = (float)LegacyZ![index].HeightDouble(point, exact);
            float height = MapLegacyBilinear.Evaluate(Patch.Height(_x, _z), Patch.Height(_x + 1, _z),
                Patch.Height(_x, _z + 1), Patch.Height(_x + 1, _z + 1), x, z);
            if (MapMembershipCoordinates.Single(height, out var local) && MapMembershipCoordinates.Height(Transform.Transform, local, out var world)) return world;
            exact.NotRepresentable = true; return null;
        }
    }
    internal sealed class Surface
    {
        internal readonly MapSurfaceRef Reference;
        internal readonly Cell[] Cells;
        readonly MapMembershipCellIndex _index;
        internal long InventoryBytes => checked(128L + 8L * Cells.Length + _index.InventoryBytes);
        internal Surface(MapSurfaceRef reference, Cell[] cells)
        { Reference = reference; Cells = cells; _index = new(cells); }
        internal Cell? Find(MapExactXz point, MapMembershipExact exact) => _index.Find(point, exact);
    }
    internal long InventoryBytes => checked(512L + Cells.Sum(c => 8L + c.InventoryBytes) +
        Surfaces.Values.Sum(s => s.InventoryBytes) + 64L * (ByCell.Count + Validated.Count) +
        8L * FaceSets.Count + _openingBytes + OpeningSets.Values.Sum(s => 96L + 64L * s.Count));
    long _openingBytes;
    internal readonly Cell[] Cells;
    internal readonly Dictionary<string, Surface> Surfaces = new(StringComparer.Ordinal);
    internal readonly Dictionary<MapCellDemand, Cell> ByCell = new();
    internal readonly Dictionary<MapPatchKey, MapValidatedSurfacePatch> Validated = new();
    internal readonly int PatchCount;
    internal readonly List<MapMembershipTriangle[]> FaceSets = new();
    internal readonly Dictionary<MapRecordRef, Dictionary<int, int>> OpeningSets = new();

    internal MapMembershipPreparedGeometry(MapScopedSurfaces view, MapTopologyRecord[] records, MapMembershipDemandPlan plan)
    {
        MapMembershipBuild.Inventory(checked(512L + 256L * plan.Cells + 64L * plan.Patches.Count));
        using var collections = MapMembershipBuild.Kernel(checked(1024L + 32L * plan.Cells + 96L * view.Surfaces.Count));
        var cells = new List<Cell>();
        var bySurface = new Dictionary<string, List<Cell>>(StringComparer.Ordinal);
        for (int s = 0; s < view.Surfaces.Count; s++) bySurface.Add(view.Surfaces[s].Id, new());
        for (int p = 0; p < view.Witness.Present.Count; p++)
        {
            var key = view.Witness.Present[p].Key;
            if (!plan.Patches.TryGetValue(key, out var mask)) continue;
            if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null) continue;
            var surface = MapMembershipPreparation.Surface(view, key.SurfaceId);
            if (surface.Role is not (MapSurfaceRole.SupportFloor or MapSurfaceRole.Ceiling)) continue;
            using var patchPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
            var validated = new MapValidatedSurfacePatch(surface, patch);
            Validated.Add(key, validated);
            var local = MapSurfaceCompiler.Compile(validated, mask, MapSurfaceCompiler.MaxFacesPerPatch);
            var compiled = MapEffectiveGeometry.TransformPatch(view, local);
            var localCells = local.Faces.GroupBy(f => f.Key.Primitive).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Key).ToArray());
            var worldCells = ReferenceEquals(local, compiled) ? localCells :
                compiled.Faces.GroupBy(f => f.Key.Primitive).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Key).ToArray());
            if (MapMembershipBuild.Current is { } build)
            {
                build.Counts.PeakPatchBytes = Math.Max(build.Counts.PeakPatchBytes, 16L * patch.Width * patch.Depth);
                build.Counts.PeakCompilerBytes = Math.Max(build.Counts.PeakCompilerBytes,
                    checked(16L * patch.Width * patch.Depth + 1280L * local.Faces.Count +
                        (ReferenceEquals(local, compiled) ? 0 : 3L * local.InventoryBytes)));
            }
            for (int z = 0; z < patch.Depth; z++)
                for (int x = 0; x < patch.Width; x++)
                {
                    int slot = (z + patch.CellMinZ) * 64 + x + patch.CellMinX;
                    if (!mask.Contains(slot)) continue;
                    var cell = new Cell(cells.Count, PatchCount, slot, view, surface, patch, local, compiled,
                        localCells.GetValueOrDefault(slot) ?? [], worldCells.GetValueOrDefault(slot) ?? []);
                    cell.FaceSet = FaceSets.Count; FaceSets.Add(cell.Triangles);
                    cells.Add(cell); bySurface[surface.Id].Add(cell); ByCell.Add(cell.Demand, cell);
                }
            PatchCount++;
        }
        Cells = cells.ToArray();
        for (int s = 0; s < view.Surfaces.Count; s++)
            Surfaces.Add(view.Surfaces[s].Id, new(view.Surfaces[s], bySurface[view.Surfaces[s].Id].ToArray()));
        foreach (var opening in records.OfType<MapHorizontalOpening>())
        {
            var reference = view.Witness.Records.Single(r => r.Id == opening.Id);
            var selected = plan.Bounds.Where(p => records.OfType<MapSpaceFootprint>().Any(f => f.Id == p.Key.Footprint &&
                (p.Key.Upper ? f.Upper : f.Lower).Opening == reference)).SelectMany(p => p.Value)
                .Where(d => d.Patch == opening.Patch && opening.SlotCells.Contains(d.SlotCell)).Select(d => d.SlotCell).Distinct().ToArray();
            if (selected.Length == 0 || !Validated.TryGetValue(opening.Patch, out var validated)) continue;
            using var openingPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
            var plane = MapOpeningBoundary.Compile(validated, opening with { SlotCells = selected });
            if (MapMembershipBuild.Current is { } build) build.Counts.OpeningFaces += plane.Keys.Count;
            var faces = plane.Keys.Select((key, i) =>
            {
                var t = plane.ExactTriangles[i];
                MapExactPoint Transform(MapExactPoint p) => MapEffectiveGeometry.Point(view, opening.Patch.SurfaceId, p);
                return new MapBoundFace(key, new(Transform(t.A), Transform(t.B), Transform(t.C)));
            }).GroupBy(f => f.Key.Primitive).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Key).ToArray());
            var triangles = faces.ToDictionary(p => p.Key, p => p.Value.Select(f => new MapMembershipTriangle(f.Triangle)).ToArray());
            var sets = new Dictionary<int, int>();
            foreach (var entry in triangles)
            {
                sets.Add(entry.Key, FaceSets.Count); FaceSets.Add(entry.Value);
                _openingBytes += entry.Value.Sum(t => 8L + t.InventoryBytes);
            }
            OpeningSets.Add(reference, sets);
        }
    }
    internal static MapExactValue? Height(MapMembershipTriangle[] faces, MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].Height(point, exact) is { } height) return height;
            if (exact.NotRepresentable) return null;
        }
        return null;
    }
    internal static int Direction(MapMembershipTriangle[] faces, MapExactXz point, MapMembershipExact exact)
    {
        MapMembershipTriangle? selected = null;
        for (int i = 0; i < faces.Length; i++)
        {
            if (!faces[i].Contains(point, exact)) continue;
            selected ??= faces[i];
            if (faces[i].Contains(point, exact, halfOpen: true)) { selected = faces[i]; break; }
        }
        return selected?.Direction ?? 0;
    }
}
