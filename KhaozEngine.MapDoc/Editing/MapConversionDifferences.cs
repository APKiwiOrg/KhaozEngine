using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Accounts for authored replacements separately from exact rim retessellation.</summary>
internal static class MapConversionDifferences
{
    internal static IReadOnlyList<MapAuthoredDifference> Collect(MapSurfaceSet before, MapSurfaceSet candidate,
        MapSurfaceRef source, MapSurfaceRef fine, IReadOnlyList<MapConversionCellGeometry> cells,
        IReadOnlyList<MapCellConversion> classifications, IReadOnlyList<(long X, long Z)> rim, int k)
    {
        var differences = new List<MapAuthoredDifference>();
        var fineCompiles = new Dictionary<MapPatchKey, MapCompiledPatch>();
        for (int i = 0; i < cells.Count; i++)
        {
            MapConversionCellGeometry cell = cells[i];
            MapCellConversionClass kind = classifications[i].Class;
            bool legacy = source.PresencePolicy == MapPresencePolicy.LegacyTileWorld;
            bool replacement = kind is MapCellConversionClass.UnsupportedEncoding or MapCellConversionClass.NotRepresentable;
            if (legacy)
                differences.Add(new(MapDifferenceKind.ArithmeticPolicy, cell.CellX, cell.CellZ, null, null,
                    $"LegacyTileWorld to Native arithmetic, max delta {ArithmeticDelta(cell, before.Patches[cell.Patch.Key])} m in submitted offsets"));
            if (kind == MapCellConversionClass.NotRepresentable)
                differences.Add(new(MapDifferenceKind.FallbackRemoved, cell.CellX, cell.CellZ, null, null,
                    "legacy non-capture fallback becomes physical authored faces"));
            if ((cell.Cell.Flags & MapCellFlags.NoDraw) != 0)
                differences.Add(new(MapDifferenceKind.FlagRemoved, cell.CellX, cell.CellZ, null, null,
                    "NoDraw removed for Native presence"));
            if (!legacy && !replacement) continue;
            MapCompiledPatch old = MapSurfaceCompiler.Compile(source, before.Patches[cell.Patch.Key],
                MapSlotCellMask.Of(new[] { cell.SlotCell }), MapSurfaceCompiler.MaxFacesPerPatch);
            foreach (MapCompiledFace face in old.Faces)
            {
                differences.Add(new(MapDifferenceKind.Geometry, cell.CellX, cell.CellZ, face.Key, null, "removed authored face"));
                if (replacement) differences.Add(new(MapDifferenceKind.Paint, cell.CellX, cell.CellZ, face.Key, null, "removed authored paint face"));
            }
            long minX = checked(cell.CellX * k), minZ = checked(cell.CellZ * k);
            foreach (MapSurfacePatch patch in candidate.Patches.Values.Where(p => p.Key.SurfaceId == fine.Id))
            {
                long originX = checked(patch.Key.SlotX * 64), originZ = checked(patch.Key.SlotZ * 64);
                if (originX + patch.CellMinX >= checked(minX + k) || originX + patch.CellMinX + patch.Width <= minX ||
                    originZ + patch.CellMinZ >= checked(minZ + k) || originZ + patch.CellMinZ + patch.Depth <= minZ) continue;
                if (!fineCompiles.TryGetValue(patch.Key, out MapCompiledPatch? compiled))
                {
                    compiled = MapSurfaceCompiler.Compile(fine, patch);
                    fineCompiles.Add(patch.Key, compiled);
                }
                foreach (MapCompiledFace face in compiled.Faces)
                {
                    long x = checked(originX + face.Key.Primitive % 64), z = checked(originZ + face.Key.Primitive / 64);
                    if (x < minX || x >= checked(minX + k) || z < minZ || z >= checked(minZ + k)) continue;
                    differences.Add(new(MapDifferenceKind.Geometry, cell.CellX, cell.CellZ, null, face.Key, "added fine authored face"));
                    if (replacement) differences.Add(new(MapDifferenceKind.Paint, cell.CellX, cell.CellZ, null, face.Key,
                        kind == MapCellConversionClass.UnsupportedEncoding ? "added fine-cell-centroid paint" : "added physical legacy paint"));
                }
            }
        }
        foreach (var cell in rim)
        {
            _ = MapFinePatchConversion.TryCell(before, source.Id, cell.X, cell.Z, out MapSurfacePatch? oldPatch, out int x, out int z);
            MapSurfacePatch newPatch = candidate.Patches[oldPatch!.Key];
            int slotCell = (oldPatch.CellMinZ + z) * 64 + oldPatch.CellMinX + x;
            MapSlotCellMask mask = MapSlotCellMask.Of(new[] { slotCell });
            MapCompiledPatch old = MapSurfaceCompiler.Compile(source, oldPatch, mask, MapSurfaceCompiler.MaxFacesPerPatch);
            MapCompiledPatch updated = MapSurfaceCompiler.Compile(source, newPatch, mask, MapSurfaceCompiler.MaxFacesPerPatch);
            var unchanged = old.Faces.Select(f => f.Key).Intersect(updated.Faces.Select(f => f.Key)).ToHashSet();
            // Child 0 can keep its key while changing from a whole parent to one fan child.
            var retessellated = updated.Faces.Where(f => f.Key.Child != 0).Select(f => f.Key.ParentTriangle).ToHashSet();
            foreach (MapCompiledFace face in old.Faces)
                if (!unchanged.Contains(face.Key) || retessellated.Contains(face.Key.ParentTriangle))
                    differences.Add(new(MapDifferenceKind.Retessellated, cell.X, cell.Z, face.Key, null,
                        "rim parent removed with unchanged plane and paint"));
            foreach (MapCompiledFace face in updated.Faces)
                if (!unchanged.Contains(face.Key) || retessellated.Contains(face.Key.ParentTriangle))
                    differences.Add(new(MapDifferenceKind.Retessellated, cell.X, cell.Z, null, face.Key,
                        "rim fan child added with unchanged plane and paint"));
        }
        return differences.AsReadOnly();
    }

    static MapExactValue ArithmeticDelta(MapConversionCellGeometry cell, MapSurfacePatch original)
    {
        MapSurfacePatch physical = original.Clone();
        for (int i = 0; i < physical.Cells.Length; i++)
            physical.Cells[i] = physical.Cells[i] with
            {
                Underlay = physical.Cells[i].Underlay == 0 ? (ushort)1 : physical.Cells[i].Underlay,
                Flags = physical.Cells[i].Flags & ~MapCellFlags.NoDraw,
            };
        MapCompiledPatch legacy = MapSurfaceCompiler.Compile(cell.Surface, physical,
            MapSlotCellMask.Of(new[] { cell.SlotCell }), MapSurfaceCompiler.MaxFacesPerPatch);
        var native = cell.Physical.VertexIds.Select((v, i) => (v.Address, Point: cell.Physical.ExactVertices[i]))
            .ToDictionary(v => v.Address, v => v.Point);
        MapExactValue maximum = default;
        for (int i = 0; i < legacy.VertexIds.Count; i++)
        {
            Vector3 exact = MapSubmissionGeometry.Offset(native[legacy.VertexIds[i].Address], legacy.Anchor);
            Vector3 old = legacy.Offsets[i];
            MaxDelta(exact.X, old.X);
            MaxDelta(exact.Y, old.Y);
            MaxDelta(exact.Z, old.Z);
        }
        return maximum;

        void MaxDelta(float nativeValue, float legacyValue)
        {
            MapExactValue delta = MapExactValue.FromSingle(nativeValue).Subtract(MapExactValue.FromSingle(legacyValue));
            if (delta.Sign < 0) delta = delta.Negate();
            if (delta.CompareTo(maximum) > 0) maximum = delta;
        }
    }
}
