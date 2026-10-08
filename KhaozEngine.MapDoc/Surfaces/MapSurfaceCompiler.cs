using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Compiles authored cells into bounded canonical faces without changing the document.</summary>
public static class MapSurfaceCompiler
{
    public const int MaxFacesPerPatch = 65_536;
    public const int MaxChildrenPerParent = 192;

    public static MapCompiledPatch Compile(MapSurfaceRef surface, MapSurfacePatch patch) =>
        Compile(surface, patch, MapSlotCellMask.All, MaxFacesPerPatch);

    internal static MapCompiledPatch Compile(MapSurfaceRef surface, MapSurfacePatch patch, int maxFaces) =>
        Compile(surface, patch, MapSlotCellMask.All, maxFaces);

    internal static MapCompiledPatch Compile(MapSurfaceRef surface, MapSurfacePatch patch, MapSlotCellMask cells, int maxFaces)
    {
        Validate(surface, patch);
        ArgumentNullException.ThrowIfNull(cells);
        if (maxFaces < 0) throw new ArgumentOutOfRangeException(nameof(maxFaces));
        try
        {
            long count = CountFaces(surface, patch, cells);
            if (count > maxFaces) throw new MapDocumentException($"compiled patch exceeds face budget {maxFaces}");
            if (surface.Role == MapSurfaceRole.PaintOverride)
                throw new MapDocumentException("paint override has no physical faces. Use ApplyPaintOverride.");
            return CompileCore(surface, patch, cells, (int)count);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("surface geometry is not representable", error);
        }
    }

    internal static long CountFaces(MapSurfaceRef surface, MapSurfacePatch patch, MapSlotCellMask cells)
    {
        Validate(surface, patch);
        ArgumentNullException.ThrowIfNull(cells);
        int[] subdivisions = Subdivisions(patch);
        Span<MapLatticeTriangle> parents = stackalloc MapLatticeTriangle[4];
        long count = 0;
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                if (!cells.Contains(SlotCell(patch, x, z)) || !patch.IsPresent(x, z)) continue;
                MapSurfaceCell cell = patch.Cells[z * patch.Width + x];
                if (Fallback(surface, cell)) continue;
                int parentCount = Describe(patch, x, z, parents);
                ReadOnlySpan<int> segments = subdivisions.AsSpan(4 * (z * patch.Width + x), 4);
                for (int i = 0; i < parentCount; i++)
                    count = checked(count + MapSubdividedTriangle.CountChildren(parents[i], segments));
            }
        return count;
    }

    static MapCompiledPatch CompileCore(MapSurfaceRef surface, MapSurfacePatch patch, MapSlotCellMask cells, int count)
    {
        MapSubmissionAnchor anchor = Anchor(surface, patch);
        var vertexBuilder = new MapSurfaceVertexBuilder(surface, patch, anchor);
        var vertices = new List<MapVertexId>();
        var exact = new List<MapExactPoint>();
        var offsets = new List<Vector3>();
        var indices = new Dictionary<MapVertexId, int>();
        var faces = new List<MapCompiledFace>(count);
        var paint = new List<MapPaintCoverage>(count);
        var fallback = new List<MapFallbackCell>();
        int[] subdivisions = Subdivisions(patch);
        Span<MapLatticeTriangle> parents = stackalloc MapLatticeTriangle[4];
        Span<MapSurfaceVertex> boundary = stackalloc MapSurfaceVertex[MaxChildrenPerParent];
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                int primitive = SlotCell(patch, x, z);
                if (!cells.Contains(primitive) || !patch.IsPresent(x, z)) continue;
                MapSurfaceCell cell = patch.Cells[z * patch.Width + x];
                if (Fallback(surface, cell))
                {
                    fallback.Add(new(primitive, patch.Height(x, z), patch.Height(x + 1, z),
                        patch.Height(x, z + 1), patch.Height(x + 1, z + 1)));
                    continue;
                }
                int parentCount = Describe(patch, x, z, parents);
                ReadOnlySpan<int> segments = subdivisions.AsSpan(4 * (z * patch.Width + x), 4);
                for (byte p = 0; p < parentCount; p++)
                {
                    MapLatticeTriangle parent = parents[p];
                    // Normalize to the floor winding before walking the D4 boundary sequence.
                    if (surface.Frame.RowDirection == MapRowDirection.PositiveZ)
                        parent = new(parent.A, parent.C, parent.B, parent.Overlay);
                    int boundaryCount = MapSubdividedTriangle.Boundary(vertexBuilder, x, z, parent, segments, boundary);
                    if (boundaryCount == 3)
                        Emit(boundary[0], boundary[1], boundary[2], p, 0, parent.Overlay, primitive, cell);
                    else
                    {
                        MapSurfaceVertex centre = vertexBuilder.Centre(x, z, parent,
                            vertexBuilder.Point(x, z, parent.A), vertexBuilder.Point(x, z, parent.B),
                            vertexBuilder.Point(x, z, parent.C));
                        for (ushort child = 0; child < boundaryCount; child++)
                            Emit(centre, boundary[child], boundary[(child + 1) % boundaryCount], p, child,
                                parent.Overlay, primitive, cell);
                    }
                }
            }
        if (faces.Count != count) throw new InvalidOperationException("surface face count disagrees with emission");
        return new(patch.Key, surface.Role, anchor, vertices, exact, offsets, faces, paint, fallback);

        int Vertex(MapSurfaceVertex vertex)
        {
            var id = new MapVertexId(surface.Id, vertex.Address);
            if (indices.TryGetValue(id, out int index))
            {
                if (exact[index] != vertex.Exact)
                    throw new MapDocumentException("inconsistent exact height for shared surface vertex");
                return index;
            }
            index = vertices.Count;
            indices.Add(id, index);
            vertices.Add(id);
            exact.Add(vertex.Exact);
            offsets.Add(vertex.Offset);
            return index;
        }

        void Emit(MapSurfaceVertex a, MapSurfaceVertex b, MapSurfaceVertex c, byte parent, ushort child,
            bool overlay, int primitive, MapSurfaceCell cell)
        {
            int ia = Vertex(a), ib = Vertex(b), ic = Vertex(c);
            bool ceiling = surface.Role == MapSurfaceRole.Ceiling;
            if (ceiling) (ib, ic) = (ic, ib);
            Vector3 normal = Vector3.Normalize(Vector3.Cross(offsets[ib] - offsets[ia], offsets[ic] - offsets[ia]));
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
                throw new MapDocumentException("surface submission normal is not representable");
            var key = new MapFaceKey(surface.Id, patch.Key, primitive, parent, child, MapSide.Front);
            faces.Add(new(key, ceiling ? MapFaceRole.Ceiling : MapFaceRole.SupportFloor, ia, ib, ic, normal));
            paint.Add(new(key, cell.Underlay, cell.Overlay, overlay && cell.Overlay != 0,
                (cell.Flags & MapCellFlags.FeatherOverlay) != 0));
        }
    }

    internal static int Describe(MapSurfacePatch patch, int x, int z, Span<MapLatticeTriangle> parents)
    {
        MapSurfaceCell cell = patch.Cells[z * patch.Width + x];
        bool split = MapSurfaceTopology.SplitSwNe(patch.Height(x, z), patch.Height(x + 1, z),
            patch.Height(x, z + 1), patch.Height(x + 1, z + 1), cell.Cut, cell.Rotation, cell.Topology);
        return MapSurfaceTopology.Triangulate(cell.Overlay == 0 ? MapOverlayCut.Full : cell.Cut,
            cell.Rotation, split, parents);
    }

    static int[] Subdivisions(MapSurfacePatch patch)
    {
        var segments = new int[patch.Cells.Length * 4];
        foreach (MapEdgeSubdivision edge in patch.EdgeSubdivisions)
            segments[4 * (edge.CellZ * patch.Width + edge.CellX) + (int)edge.Edge] = edge.Segments;
        return segments;
    }

    static int SlotCell(MapSurfacePatch patch, int x, int z) =>
        (patch.CellMinZ + z) * MapPatchKey.SlotCells + patch.CellMinX + x;

    static bool Fallback(MapSurfaceRef surface, MapSurfaceCell cell) =>
        surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld &&
        (cell.Underlay == 0 || (cell.Flags & MapCellFlags.NoDraw) != 0);

    internal static void Validate(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        IReadOnlyList<string> findings = patch.ValidateLocal();
        if (findings.Count != 0) throw new MapDocumentException(findings[0]);
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Id != patch.Key.SurfaceId || surface.Frame is null ||
            !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
            throw new MapDocumentException("invalid surface metadata or patch surface id");
        if (surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld &&
            surface.Frame != MapLatticeFrame.ImportedMetreCentimetre)
            throw new MapDocumentException("LegacyTileWorld requires ImportedMetreCentimetre");
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                MapSurfaceCell cell = patch.Cells[z * patch.Width + x];
                if (surface.PresencePolicy == MapPresencePolicy.Native && (cell.Flags & MapCellFlags.NoDraw) != 0)
                    throw new MapDocumentException("Native surfaces refuse NoDraw");
                _ = MapSurfaceTopology.SplitSwNe(patch.Height(x, z), patch.Height(x + 1, z),
                    patch.Height(x, z + 1), patch.Height(x + 1, z + 1), cell.Cut, cell.Rotation, cell.Topology);
            }
    }

    internal static MapSubmissionAnchor Anchor(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        if (surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld)
            return new(checked(patch.Key.SlotX * 64), 0, checked(-patch.Key.SlotZ * 64));
        MapExactXz first = surface.Frame.WorldXz(patch.CornerAddress(0, 0));
        MapExactXz last = surface.Frame.WorldXz(patch.CornerAddress(patch.Width, patch.Depth));
        int min = patch.Heights[0], max = min;
        foreach (int height in patch.Heights) { min = Math.Min(min, height); max = Math.Max(max, height); }
        return MapSubmissionGeometry.Anchor(first, last,
            surface.Frame.Metres(new MapExactValue((long)min + max, 2)));
    }

    public static MapExactValue? ExactHeight(MapSurfaceRef surface, MapSurfacePatch patch,
        MapExactValue worldX, MapExactValue worldZ)
    {
        MapCompiledPatch compiled = Compile(surface, patch);
        try
        {
            // Emission is in face-key order, including on shared boundaries.
            foreach (MapCompiledFace face in compiled.Faces)
                if (MapSubdividedTriangle.Height(compiled.ExactTriangle(face), worldX, worldZ) is { } height)
                    return height;
            return null;
        }
        catch (MapExactOverflowException error)
        {
            throw new MapDocumentException("surface height is not representable", error);
        }
    }

    public static MapCompiledPatch ApplyPaintOverride(MapCompiledPatch target, MapSurfaceRef overrideSurface,
        MapSurfacePatch overridePatch) => MapSurfacePaintOverride.Apply(target, overrideSurface, overridePatch);
}
