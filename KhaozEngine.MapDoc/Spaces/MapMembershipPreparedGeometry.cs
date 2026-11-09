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
    internal readonly struct Cell
    {
        readonly MapMembershipGeometryArena _arena;
        readonly int _index;
        internal Cell(MapMembershipGeometryArena arena, int index) { _arena = arena; _index = index; }
        ref readonly MapMembershipGeometryArena.CellRow Row => ref _arena.Cells[_index];
        internal int Id => Row.Rank;
        internal int PatchIndex => Row.Patch;
        internal int FaceSet => Row.FaceSet;
        internal MapSurfaceRef Surface => _arena.Surfaces[Row.Surface];
        internal MapSurfacePatch Patch => _arena.Patches[Row.Patch];
        internal MapCellDemand Demand => new(Patch.Key, Row.Slot);
        internal MapMembershipPolygon Polygon => new(_arena, Row.Polygon);
        internal MapMembershipTriangleRange Triangles => new(_arena, Row.Triangle, Row.Count);
        internal MapMembershipTriangleRange SpanLower => new(_arena, Row.SpanLower, Row.SpanLower < 0 ? 0 : Row.SpanCount);
        internal MapMembershipTriangleRange SpanUpper => new(_arena, Row.SpanUpper, Row.SpanUpper < 0 ? 0 : Row.SpanCount);
        internal MapMembershipTriangleRange LegacyX => new(_arena, Row.LegacyX, Row.LegacyX < 0 ? 0 : 2);
        internal MapMembershipTriangleRange LegacyZ => new(_arena, Row.LegacyZ, Row.LegacyZ < 0 ? 0 : 2);
        internal int PhysicalFaces => Row.Physical;
        internal bool Indoor => (Row.Flags & 1) != 0;
        internal MapResourceTransform? Transform => _arena.Transforms[Row.Surface];
        internal MapExactXz Centre => _arena.Centre(Row.Polygon);
        internal Cell(int id, int patchIndex, int slot, MapScopedSurfaces view, MapSurfaceRef surface,
            MapSurfacePatch patch, MapCompiledPatch local, MapCompiledPatch compiled, MapCompiledFace[] localFaces, MapCompiledFace[] worldFaces)
        {
            _arena = MapMembershipArenaBuilder.Current;
            var arena = _arena;
            int x = slot % 64 - patch.CellMinX, z = slot / 64 - patch.CellMinZ;
            int surfaceIndex = arena.Surface(view, surface);
            if (arena.Patch(patch) != patchIndex) throw new InvalidOperationException("Membership patch rank changed.");
            var polygon = MapEffectiveGeometry.CellPolygon(view, patch.Key, slot);
            int polygonIndex = new MapMembershipPolygon(polygon, cell: true).Index;
            // Preserve the released eager precision refusal even for cells with no bound association.
            using (var centre = MapMembershipBuild.Kernel()) _ = arena.Centre(polygonIndex);
            int first = arena.Triangles.Count, face = arena.Faces.Count;
            foreach (var f in worldFaces)
            { int triangle = MapMembershipArenaBuilder.Triangle(compiled.ExactTriangle(f)); arena.Face(f.Key, triangle); }
            int lower = -1, upper = -1, legacyX = -1, legacyZ = -1;
            var transform = view.EffectiveContext?.Transform(MapPrefabChildKind.Surface, surface.Id);
            if (surface.IndoorSpan is { } span)
            {
                lower = Raised(span.LowerOffsetUnits); upper = Raised(span.UpperOffsetUnits);
                if (MapMembershipBuild.Current is { } build) build.Counts.SpanTriangles += 2 * localFaces.Length;
            }
            if (MapSurfaceCompiler.Fallback(surface, patch.Cells[z * patch.Width + x]) && transform is not null)
            {
                MapExactPoint P(int i, long y) => new(polygon[i].X, new(y, 1), polygon[i].Z);
                legacyX = arena.Triangles.Count;
                _ = new MapMembershipTriangle(new(P(0, 0), P(1, 1), P(2, 1)));
                _ = new MapMembershipTriangle(new(P(0, 0), P(2, 1), P(3, 0)));
                legacyZ = arena.Triangles.Count;
                _ = new MapMembershipTriangle(new(P(0, 0), P(1, 0), P(2, 1)));
                _ = new MapMembershipTriangle(new(P(0, 0), P(2, 1), P(3, 1)));
                if (MapMembershipBuild.Current is { } build) build.Counts.LegacyTriangles += 4;
            }
            _index = arena.Cells.Append(new(surfaceIndex, patchIndex, slot,
                checked((int)MapSurfaceCompiler.CountFaces(surface, patch, slot)), polygonIndex, first, worldFaces.Length,
                face, lower, upper, legacyX, legacyZ, id, (patch.Cells[z * patch.Width + x].Flags & MapCellFlags.Indoor) != 0 ? 1 : 0, id, localFaces.Length));
            if (MapMembershipBuild.Current is { } completed) completed.Counts.UniqueCellProducts++;
            int Raised(long units)
            {
                int start = arena.Triangles.Count;
                var offset = surface.Frame.Metres(new(units, 1));
                MapExactPoint Raise(MapExactPoint p) => transform?.Point(p with { Y = p.Y.Add(offset) }) ?? p with { Y = p.Y.Add(offset) };
                foreach (var f in localFaces)
                { var t = local.ExactTriangle(f); _ = new MapMembershipTriangle(new(Raise(t.A), Raise(t.B), Raise(t.C))); }
                return start;
            }
        }
        internal MapExactValue? LegacyHeight(MapExactXz point, MapMembershipExact exact)
        {
            if (Transform is null)
            {
                if (MapMembershipCoordinates.Single(MapLegacyBilinear.HeightMetres(Surface, Patch, point), out var sampled)) return sampled;
                exact.NotRepresentable = true; return null;
            }
            int index = LegacyX[0].Contains(point, exact) ? 0 : 1;
            float x = (float)LegacyX[index].HeightDouble(point, exact), z = (float)LegacyZ[index].HeightDouble(point, exact);
            int cx = Row.Slot % 64 - Patch.CellMinX, cz = Row.Slot / 64 - Patch.CellMinZ;
            float height = MapLegacyBilinear.Evaluate(Patch.Height(cx, cz), Patch.Height(cx + 1, cz),
                Patch.Height(cx, cz + 1), Patch.Height(cx + 1, cz + 1), x, z);
            if (MapMembershipCoordinates.Single(height, out var value) && MapMembershipCoordinates.Height(Transform.Transform, value, out var world)) return world;
            exact.NotRepresentable = true; return null;
        }
    }
    internal sealed class Surface
    {
        internal readonly MapSurfaceRef Reference;
        internal readonly int[] Cells;
        readonly MapMembershipCellIndex _index;
        internal long InventoryBytes => checked(128L + 4L * Cells.Length + _index.InventoryBytes);
        internal Surface(MapSurfaceRef reference, int[] cells, MapMembershipGeometryArena arena)
        { Reference = reference; Cells = cells; _index = new(arena, cells); }
        internal Cell? Find(MapExactXz point, MapMembershipExact exact) => _index.Find(point, exact);
    }
    internal long InventoryBytes => checked(768L + Bounds.InventoryBytes + (_arenaOwner?.InventoryBytes ?? 0) +
        Surfaces.Values.Sum(s => s.InventoryBytes) + 128L * (ByCell.PatchCount + Validated.Count) +
        FaceSets.InventoryBytes + OpeningSets.Values.Sum(s => 96L + 64L * s.Count));
    readonly MapMembershipGeometryArena? _arenaOwner;
    internal readonly MapMembershipCellRange Cells;
    internal readonly MapMembershipFrozenBoundStore Bounds;
    internal readonly Dictionary<string, Surface> Surfaces = new(StringComparer.Ordinal);
    internal readonly MapMembershipCellLookup ByCell;
    internal readonly Dictionary<MapPatchKey, MapValidatedSurfacePatch> Validated;
    internal readonly int PatchCount;
    internal readonly MapMembershipFaceSets FaceSets;
    internal readonly Dictionary<MapRecordRef, Dictionary<int, int>> OpeningSets = new();

    internal MapMembershipPreparedGeometry(MapScopedSurfaces view, MapTopologyRecord[] records, MapMembershipDemandPlan plan)
    {
        using var building = MapMembershipArenaBuilder.Begin();
        var arena = building.Arena;
        _arenaOwner = building.Owns ? arena : null;
        MapMembershipBuild.Inventory(checked(768L + 256L * plan.Patches.Count));
        ByCell = new(arena, plan.Patches.Count); FaceSets = new(arena);
        Bounds = MapMembershipFrozenBoundStore.Create(this);
        Validated = new(plan.Patches.Count);
        using var collections = MapMembershipBuild.Kernel(checked(1024L + 32L * plan.Cells + 96L * view.Surfaces.Count));
        var bySurface = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int s = 0; s < view.Surfaces.Count; s++) bySurface.Add(view.Surfaces[s].Id, new());
        for (int p = 0; p < view.Witness.Present.Count; p++)
        {
            var key = view.Witness.Present[p].Key;
            if (!plan.Patches.TryGetValue(key, out var mask)) continue;
            if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null) continue;
            var surface = MapMembershipPreparation.Surface(view, key.SurfaceId);
            if (surface.Role is not (MapSurfaceRole.SupportFloor or MapSurfaceRole.Ceiling)) continue;
            using var patchPreparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
            if (arena.Patch(patch) != PatchCount) throw new InvalidOperationException("Membership patch rank changed.");
            var validated = new MapValidatedSurfacePatch(surface, patch);
            Validated.Add(key, validated);
            var local = MapSurfaceCompiler.Compile(validated, mask, MapSurfaceCompiler.MaxFacesPerPatch);
            var compiled = MapEffectiveGeometry.TransformPatch(view, local);
            using var faceGroups = new MapMembershipFaceGroups(local, compiled, mask.Count);
            var localCells = faceGroups.Local;
            var worldCells = faceGroups.World;
            if (MapMembershipBuild.Current is { } build)
            {
                build.Counts.PeakPatchBytes = Math.Max(build.Counts.PeakPatchBytes, 16L * patch.Width * patch.Depth);
                build.Counts.PeakCompilerBytes = Math.Max(build.Counts.PeakCompilerBytes,
                    Math.Max(checked(16L * patch.Width * patch.Depth + 1280L * local.Faces.Count +
                        (ReferenceEquals(local, compiled) ? 0 : 3L * local.InventoryBytes)),
                        checked(local.InventoryBytes + (ReferenceEquals(local, compiled) ? 0 : compiled.InventoryBytes) + faceGroups.WorkspaceBytes)));
            }
            int cellStart = arena.Cells.Count;
            for (int z = 0; z < patch.Depth; z++)
                for (int x = 0; x < patch.Width; x++)
                {
                    int slot = (z + patch.CellMinZ) * 64 + x + patch.CellMinX;
                    if (!mask.Contains(slot)) continue;
                    var cell = new Cell(arena.Cells.Count, PatchCount, slot, view, surface, patch, local, compiled,
                        localCells.GetValueOrDefault(slot) ?? [], worldCells.GetValueOrDefault(slot) ?? []);
                    bySurface[surface.Id].Add(cell.Id);
                }
            ByCell.Add(key, cellStart, arena.Cells.Count - cellStart);
            PatchCount++;
        }
        Cells = new(arena, 0, arena.Cells.Count);
        for (int s = 0; s < view.Surfaces.Count; s++)
        {
            var cells = bySurface[view.Surfaces[s].Id];
            MapMembershipBuild.Inventory(checked(4L * cells.Count));
            Surfaces.Add(view.Surfaces[s].Id, new(view.Surfaces[s], cells.ToArray(), arena));
        }
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
            var sets = new Dictionary<int, int>();
            foreach (var entry in faces)
            {
                int first = arena.Triangles.Count;
                foreach (var f in entry.Value)
                { int triangle = MapMembershipArenaBuilder.Triangle(f.Triangle); arena.Face(f.Key, triangle); }
                sets.Add(entry.Key, FaceSets.Add(new(arena, first, entry.Value.Length)));
            }
            OpeningSets.Add(reference, sets);
            if (MapMembershipBuild.Current is { } completed) completed.Counts.OpeningTriangles += faces.Values.Sum(t => t.Length);
        }
    }
    internal static MapExactValue? Height(MapMembershipTriangleRange faces, MapExactXz point, MapMembershipExact exact)
    {
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i].Height(point, exact) is { } height) return height;
            if (exact.NotRepresentable) return null;
        }
        return null;
    }
    internal static int Direction(MapMembershipTriangleRange faces, MapExactXz point, MapMembershipExact exact)
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
