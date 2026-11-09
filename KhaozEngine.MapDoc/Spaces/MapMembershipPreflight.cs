using System;
using System.Numerics;
using static KhaozEngine.MapDoc.Spaces.MapMembershipCoordinates;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Checks borrowed immutable inputs before allocating a prepared product or spending its admission.</summary>
internal static class MapMembershipPreflight
{
    internal static MapSceneStatus Check(MapScopedScene scene, MapMembershipPreparation demand)
    {
        var view = scene.SurfaceView;
        var intent = scene.CapturedBuildingIntent;
        for (int i = 0; i < intent.Roofs.Count; i++)
            for (int j = 0; j < i; j++) if (intent.Roofs[i].Id == intent.Roofs[j].Id) return MapSceneStatus.Invalid;
        for (int i = 0; i < intent.RoofGroups.Count; i++)
            for (int j = 0; j < i; j++) if (intent.RoofGroups[i].Id == intent.RoofGroups[j].Id) return MapSceneStatus.Invalid;
        for (int i = 0; i < intent.RoofLinks.Count; i++)
        {
            var link = intent.RoofLinks[i];
            if (!Single(link.AboveY, out _)) return MapSceneStatus.NotRepresentable;
            bool roof = false;
            for (int r = 0; r < intent.Roofs.Count; r++) roof |= intent.Roofs[r].Id == link.Roof.Id;
            if (!roof) return MapSceneStatus.Invalid;
            for (int g = 0; g < link.Groups.Count; g++)
            {
                bool group = false;
                for (int j = 0; j < intent.RoofGroups.Count; j++) group |= intent.RoofGroups[j].Id == link.Groups[g].Id;
                if (!group) return MapSceneStatus.Invalid;
            }
        }
        for (int i = 0; i < view.Witness.Records.Count; i++)
        {
            if (!view.TryRecord(view.Witness.Records[i], out var record, out _, borrow: true)) return MapSceneStatus.Invalid;
            if (record is MapSpaceDoc space && (!Ancestry(view, space, false) || !Ancestry(view, space, true)))
                return MapSceneStatus.Invalid;
            if (record is MapSpaceFootprint footprint &&
                (!Space(view, footprint.Space, out _) || FindSurface(view, footprint.Lattice.SurfaceId) is null))
                return MapSceneStatus.Invalid;
            if (record is MapHorizontalOpening opening && !Opening(view, opening)) return MapSceneStatus.Invalid;
            if (record is MapPrismDomain prism)
            {
                if (!Space(view, prism.Space, out var room) || !Space(view, prism.Parent, out _) ||
                    room!.Kind != MapSpaceKind.Room || room.Parent != prism.Parent || !Storey(view, prism)) return MapSceneStatus.Invalid;
                if (!Single(prism.LowerY, out var low) || !Single(prism.UpperY, out var high)) return MapSceneStatus.NotRepresentable;
                var pose = Pose(view, MapPrefabChildKind.Record, prism.Id);
                if (!Height(pose, low, out low) || !Height(pose, high, out high)) return MapSceneStatus.NotRepresentable;
                if (low.CompareTo(high) >= 0) return MapSceneStatus.Invalid;
                var status = Domain(prism, pose);
                if (status != MapSceneStatus.CompleteForScope) return status;
            }
            if (record is MapCavePortal portal && (portal.Interval.Count < 2 || !Space(view, portal.FromSpace, out _) ||
                !Space(view, portal.ToSpace, out _) || !Chain(view, portal.BandBottom) || portal.BandTop is { } top && !Chain(view, top)))
                return MapSceneStatus.Invalid;
        }
        for (int p = 0; demand.Cells != 0 && p < view.Witness.Present.Count; p++)
        {
            var key = view.Witness.Present[p].Key;
            if (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null) return MapSceneStatus.Invalid;
            var surface = FindSurface(view, key.SurfaceId);
            if (surface is null) return MapSceneStatus.Invalid;
            if (surface.Role is not (MapSurfaceRole.SupportFloor or MapSurfaceRole.Ceiling)) continue;
            var status = Patch(view, surface, patch);
            if (status != MapSceneStatus.CompleteForScope) return status;
        }
        return MapSceneStatus.CompleteForScope;
    }
    static bool Ancestry(MapScopedSurfaces view, MapSpaceDoc space, bool alias)
    {
        for (int depth = 0; depth <= view.Witness.Records.Count; depth++)
        {
            var next = alias ? space.AliasOf : space.Parent;
            if (next is null) return true;
            if (!Space(view, next, out var parent)) return false;
            space = parent!;
        }
        return false;
    }
    static bool Space(MapScopedSurfaces view, MapRecordRef reference, out MapSpaceDoc? space)
    {
        view.TryRecord(reference, out var record, out _, borrow: true); space = record as MapSpaceDoc;
        return space is not null;
    }
    static bool Storey(MapScopedSurfaces view, MapPrismDomain prism)
    {
        var storeys = view.EffectiveContext?.Storeys;
        for (int i = 0; storeys is not null && i < storeys.Count; i++)
            if (storeys[i].Id == prism.StoreyId) return storeys[i].PayloadVersion == 1 && (storeys[i].InstanceId ?? "") == prism.InstanceId;
        return false;
    }
    static bool Chain(MapScopedSurfaces view, MapRecordRef reference)
    {
        if (!view.TryRecord(reference, out var record, out _, borrow: true) || record is not MapBoundaryChain chain ||
            chain.Vertices.Count is < 2 or > 4097 || (chain.Kind == MapChainKind.SurfaceEdge) != chain.SourcePatch.HasValue) return false;
        if (chain.SourcePatch is { } key && (!view.TryAcquiredPatch(key, out var patch, out _) || patch is null)) return false;
        for (int i = 0; i < chain.Vertices.Count; i++)
        {
            var vertex = chain.Vertices[i]; var surface = FindSurface(view, vertex.Vertex.SurfaceId);
            if (surface is null || (chain.Kind == MapChainKind.Authored) != vertex.HeightUnits.HasValue ||
                !World(surface.Frame, vertex.Vertex.Address, out var point)) return false;
            if (!Point(Pose(view, MapPrefabChildKind.Surface, surface.Id), new(point.X,
                surface.Frame.Metres(new(vertex.HeightUnits ?? 0, 1)), point.Z), out _)) return false;
        }
        return true;
    }
    static bool Opening(MapScopedSurfaces view, MapHorizontalOpening opening)
    {
        if (opening.SlotCells.Count is < 1 or > 4096 || !view.TryAcquiredPatch(opening.Patch, out var patch, out _) || patch is null) return false;
        for (int i = 0; i < opening.SlotCells.Count; i++)
        {
            int slot = opening.SlotCells[i], x = slot % 64 - patch.CellMinX, z = slot / 64 - patch.CellMinZ;
            if (slot is < 0 or >= 4096 || x < 0 || z < 0 || x >= patch.Width || z >= patch.Depth || patch.IsPresent(x, z)) return false;
            for (int j = 0; j < i; j++) if (opening.SlotCells[j] == slot) return false;
        }
        return true;
    }
    static MapSceneStatus Domain(MapPrismDomain prism, MapEffectiveTransform? pose)
    {
        var domain = prism.Footprint;
        if (domain.Rects.Count + domain.Rings.Count == 0) return MapSceneStatus.Invalid;
        if ((long)domain.Rects.Count + domain.Rings.Count > Water.MapWaterDomain.MaxPieces) return MapSceneStatus.CapacityExceeded;
        long pairs = 0;
        for (int piece = 0; piece < domain.Rects.Count + domain.Rings.Count; piece++)
        {
            int count = piece < domain.Rects.Count ? 4 : domain.Rings[piece - domain.Rects.Count].Vertices.Count;
            if (count < 3) return MapSceneStatus.Invalid;
            if (count > Water.MapWaterDomain.MaxPieceVertices || (pairs += (long)count * count) > Water.MapWaterDomain.MaxPairChecks)
                return MapSceneStatus.CapacityExceeded;
            if (piece < domain.Rects.Count && domain.Rects[piece] is var rect && (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ))
                return MapSceneStatus.Invalid;
            bool noncollinear = false;
            for (int i = 0; i < count; i++)
            {
                if (!DomainVertex(domain, pose, piece, i, out var a) || !DomainVertex(domain, pose, piece, (i + 1) % count, out var b) ||
                    !DomainVertex(domain, pose, piece, (i + 2) % count, out var c)) return MapSceneStatus.NotRepresentable;
                if (a == b) return MapSceneStatus.Invalid;
                int turn = MapMembershipExact.Orientation(a, b, c);
                if (turn == 0 && (a == c || Between(c, a, b) || Between(a, b, c))) return MapSceneStatus.Invalid;
                noncollinear |= turn != 0;
                for (int j = i + 2; j < count; j++)
                {
                    if (i == 0 && j == count - 1) continue;
                    if (!DomainVertex(domain, pose, piece, j, out var d) || !DomainVertex(domain, pose, piece, (j + 1) % count, out var e)) return MapSceneStatus.NotRepresentable;
                    if (Intersects(a, b, d, e)) return MapSceneStatus.Invalid;
                }
            }
            if (!noncollinear) return MapSceneStatus.Invalid;
        }
        return MapSceneStatus.CompleteForScope;

    }
    static bool DomainVertex(Water.MapWaterDomainDoc domain, MapEffectiveTransform? pose, int piece, int vertex, out MapExactXz point)
    {
        MapLatticeAddress address;
        if (piece < domain.Rects.Count)
        {
            var rect = domain.Rects[piece];
            address = MapLatticeAddress.Corner(vertex is 0 or 3 ? rect.MinX : rect.MaxX, vertex < 2 ? rect.MinZ : rect.MaxZ);
        }
        else address = domain.Rings[piece - domain.Rects.Count].Vertices[vertex];
        if (!World(domain.Frame, address, out point)) return false;
        return Xz(pose, point, out point);
    }
    static bool Intersects(MapExactXz a, MapExactXz b, MapExactXz c, MapExactXz d)
    {
        int abC = MapMembershipExact.Orientation(a, b, c), abD = MapMembershipExact.Orientation(a, b, d);
        int cdA = MapMembershipExact.Orientation(c, d, a), cdB = MapMembershipExact.Orientation(c, d, b);
        return abC * abD < 0 && cdA * cdB < 0 || abC == 0 && Between(c, a, b) || abD == 0 && Between(d, a, b) ||
            cdA == 0 && Between(a, c, d) || cdB == 0 && Between(b, c, d);
    }
    static bool Between(MapExactXz p, MapExactXz a, MapExactXz b) => MapMembershipPolygon.Between(p.X, a.X, b.X) && MapMembershipPolygon.Between(p.Z, a.Z, b.Z);
    internal static MapSurfaceRef? FindSurface(MapScopedSurfaces view, string id)
    {
        for (int i = 0; i < view.Surfaces.Count; i++) if (view.Surfaces[i].Id == id) return view.Surfaces[i];
        return null;
    }
    static MapEffectiveTransform? Pose(MapScopedSurfaces view, MapPrefabChildKind kind, string id) =>
        view.EffectiveContext is { } context && context.TryGeometryTransform(kind, id, out var pose) ? pose : null;
    static MapSceneStatus Patch(MapScopedSurfaces view, MapSurfaceRef surface, MapSurfacePatch patch)
    {
        var pose = Pose(view, MapPrefabChildKind.Surface, surface.Id);
        // Validate every exact endpoint and interpolation before the shared compiler's stack-only emission.
        for (int z = 0; z <= patch.Depth; z++)
            for (int x = 0; x <= patch.Width; x++)
                if (!World(surface.Frame, patch.CornerAddress(x, z), out _) ||
                    !Rational((Int128)patch.Height(x, z) * surface.Frame.HeightUnitMetres.Numerator,
                        surface.Frame.HeightUnitMetres.Denominator, out _)) return MapSceneStatus.NotRepresentable;
        if (!Anchor(surface, patch, out var anchor)) return MapSceneStatus.NotRepresentable;
        var builder = new MapSurfaceVertexBuilder(surface, patch, anchor);
        Span<MapLatticeTriangle> parents = stackalloc MapLatticeTriangle[4];
        Span<MapSurfaceVertex> boundary = stackalloc MapSurfaceVertex[MapSurfaceCompiler.MaxChildrenPerParent];
        Span<int> segments = stackalloc int[4];
        for (int z = 0; z < patch.Depth; z++)
            for (int x = 0; x < patch.Width; x++)
            {
                segments.Clear();
                for (int e = 0; e < patch.EdgeSubdivisions.Count; e++)
                {
                    var edge = patch.EdgeSubdivisions[e];
                    if (edge.CellX == x && edge.CellZ == z) segments[(int)edge.Edge] = edge.Segments;
                }
                int count = MapSurfaceCompiler.Describe(patch, x, z, parents);
                for (int p = 0; p < count; p++)
                {
                    var parent = parents[p];
                    if (!Endpoint(surface, patch, x, z, parent.A, anchor) || !Endpoint(surface, patch, x, z, parent.B, anchor) ||
                        !Endpoint(surface, patch, x, z, parent.C, anchor)) return MapSceneStatus.NotRepresentable;
                    var a = builder.Point(x, z, parent.A); var b = builder.Point(x, z, parent.B); var c = builder.Point(x, z, parent.C);
                    if (!Inserted(surface, patch, x, z, parent.A, parent.B, a, b, segments, anchor) ||
                        !Inserted(surface, patch, x, z, parent.B, parent.C, b, c, segments, anchor) ||
                        !Inserted(surface, patch, x, z, parent.C, parent.A, c, a, segments, anchor)) return MapSceneStatus.NotRepresentable;
                    if (MapSubdividedTriangle.CountChildren(parent, segments) != 1)
                    {
                        if (!Add(b.Exact.Y, a.Exact.Y, out var ba, true) || !Divide(ba, 3, out ba) || !Add(a.Exact.Y, ba, out var sum) ||
                            !Add(c.Exact.Y, a.Exact.Y, out var ca, true) || !Divide(ca, 3, out ca) || !Add(sum, ca, out var centreY))
                            return MapSceneStatus.NotRepresentable;
                        (int ax, int az) = MapSurfaceTopology.LocalTwice(parent.A);
                        (int bx, int bz) = MapSurfaceTopology.LocalTwice(parent.B);
                        (int cx, int cz) = MapSurfaceTopology.LocalTwice(parent.C);
                        if (!Address(patch.CornerAddress(x, z), ax + bx + cx, az + bz + cz, 6, out var centreAddress) ||
                            !Vertex(surface, centreAddress, centreY, anchor)) return MapSceneStatus.NotRepresentable;
                    }
                    int vertices = MapSubdividedTriangle.Boundary(builder, x, z, parent, segments, boundary);
                    var centre = vertices == 3 ? default : builder.Centre(x, z, parent, a, b, c);
                    for (int v = 0; v < (vertices == 3 ? 1 : vertices); v++)
                    {
                        a = vertices == 3 ? boundary[0] : centre;
                        b = vertices == 3 ? boundary[1] : boundary[v];
                        c = vertices == 3 ? boundary[2] : boundary[(v + 1) % vertices];
                        var normal = Vector3.Normalize(Vector3.Cross(b.Offset - a.Offset, c.Offset - a.Offset));
                        if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z) ||
                            !Point(pose, a.Exact, out var wa) || !Point(pose, b.Exact, out var wb) || !Point(pose, c.Exact, out var wc) ||
                            MapMembershipExact.Orientation(new(wa.X, wa.Z), new(wb.X, wb.Z), new(wc.X, wc.Z)) == 0 ||
                            !Raised(surface, pose, a.Exact) || !Raised(surface, pose, b.Exact) || !Raised(surface, pose, c.Exact))
                            return MapSceneStatus.NotRepresentable;
                    }
                }
            }
        return MapSceneStatus.CompleteForScope;
    }
    internal static bool CellValid(MapSurfaceRef surface, MapSurfaceCell cell) =>
        cell.Rotation <= 3 && cell.Cut <= MapOverlayCut.CornerThreeQuarter && cell.Topology <= MapCellTopology.ForceNwSe &&
        !(cell.Topology != MapCellTopology.Auto && cell.Cut == MapOverlayCut.DiagonalHalf &&
            (cell.Topology == MapCellTopology.ForceSwNe) != (cell.Rotation % 2 == 0)) &&
        !(surface.PresencePolicy == MapPresencePolicy.Native && (cell.Flags & MapCellFlags.NoDraw) != 0) &&
        !(surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld && surface.Frame != MapLatticeFrame.ImportedMetreCentimetre);
    static bool Raised(MapSurfaceRef surface, MapEffectiveTransform? pose, MapExactPoint point)
    {
        if (surface.IndoorSpan is not { } span) return true;
        return Rational((Int128)span.LowerOffsetUnits * surface.Frame.HeightUnitMetres.Numerator, surface.Frame.HeightUnitMetres.Denominator, out var low) &&
            Rational((Int128)span.UpperOffsetUnits * surface.Frame.HeightUnitMetres.Numerator, surface.Frame.HeightUnitMetres.Denominator, out var high) &&
            Add(point.Y, low, out low) && Add(point.Y, high, out high) &&
            Height(pose, low, out _) && Height(pose, high, out _);
    }
    static bool Anchor(MapSurfaceRef surface, MapSurfacePatch patch, out MapSubmissionAnchor anchor)
    {
        anchor = default;
        if (surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld)
        { anchor = new(patch.Key.SlotX * 64, 0, -patch.Key.SlotZ * 64); return true; }
        int min = patch.Heights[0], max = min;
        for (int i = 1; i < patch.Heights.Length; i++) { min = Math.Min(min, patch.Heights[i]); max = Math.Max(max, patch.Heights[i]); }
        if (!Rational(((Int128)min + max) * surface.Frame.HeightUnitMetres.Numerator,
                2L * surface.Frame.HeightUnitMetres.Denominator, out var middle) ||
            !World(surface.Frame, patch.CornerAddress(0, 0), out var first) ||
            !World(surface.Frame, patch.CornerAddress(patch.Width, patch.Depth), out var last)) return false;
        anchor = MapSubmissionGeometry.Anchor(first, last, middle); return true;
    }
    static bool Address(MapLatticeAddress origin, int x, int z, int d, out MapLatticeAddress address)
    {
        address = default;
        Int128 nx = (Int128)origin.X * d + x, nz = (Int128)origin.Z * d + z;
        Int128 gcd = MapExactValue.Gcd(MapExactValue.Gcd(nx, nz), d); nx /= gcd; nz /= gcd; d /= (int)gcd;
        if (d > MapLatticeAddress.MaxDenominator || nx < long.MinValue || nx > long.MaxValue || nz < long.MinValue || nz > long.MaxValue) return false;
        address = MapLatticeAddress.Create((long)nx, (long)nz, d); return true;
    }
    static bool Vertex(MapSurfaceRef surface, MapLatticeAddress address, MapExactValue height, MapSubmissionAnchor anchor) =>
        World(surface.Frame, address, out var xz) && Add(xz.X, new(anchor.X, 1), out _, true) &&
        Add(xz.Z, new(anchor.Z, 1), out _, true) && Add(height, new(anchor.Y, 1), out _, true);
    static bool Endpoint(MapSurfaceRef surface, MapSurfacePatch patch, int x, int z, MapLatticePoint point, MapSubmissionAnchor anchor)
    {
        var (first, second) = MapSurfaceTopology.Ends(point);
        var (fx, fz) = MapSurfaceTopology.LocalTwice(first); var (sx, sz) = MapSurfaceTopology.LocalTwice(second);
        if (!Rational((Int128)patch.Height(x + fx / 2, z + fz / 2) * surface.Frame.HeightUnitMetres.Numerator,
                surface.Frame.HeightUnitMetres.Denominator, out var a) ||
            !Rational((Int128)patch.Height(x + sx / 2, z + sz / 2) * surface.Frame.HeightUnitMetres.Numerator,
                surface.Frame.HeightUnitMetres.Denominator, out var b)) return false;
        var (px, pz) = MapSurfaceTopology.LocalTwice(point);
        return Address(patch.CornerAddress(x, z), px, pz, 2, out var address) &&
            Add(b, a, out var delta, true) && Divide(delta, 2, out delta) && Add(a, delta, out var height) && Vertex(surface, address, height, anchor);
    }
    static bool Inserted(MapSurfaceRef surface, MapSurfacePatch patch, int x, int z, MapLatticePoint fromPoint,
        MapLatticePoint toPoint, MapSurfaceVertex from, MapSurfaceVertex to, ReadOnlySpan<int> segments, MapSubmissionAnchor anchor)
    {
        if (!MapSubdividedTriangle.Edge(fromPoint, toPoint, out var edge, out int start, out int end)) return true;
        int count = segments[(int)edge];
        for (int i = 1; i < count; i++)
        {
            if (!MapSubdividedTriangle.Between(i, count, start, end)) continue;
            var t = new MapExactValue(2L * i - (long)start * count, (long)(end - start) * count);
            var (px, pz) = edge switch
            { MapCellEdge.South => (i, 0), MapCellEdge.East => (count, i), MapCellEdge.North => (i, count), _ => (0, i) };
            if (!Address(patch.CornerAddress(x, z), px, pz, count, out var address) ||
                !Add(to.Exact.Y, from.Exact.Y, out var delta, true) ||
                !Rational((Int128)delta.Numerator * t.Numerator, (Int128)delta.Denominator * t.Denominator, out delta) ||
                !Add(from.Exact.Y, delta, out var height) || !Vertex(surface, address, height, anchor)) return false;
        }
        return true;
    }
}
