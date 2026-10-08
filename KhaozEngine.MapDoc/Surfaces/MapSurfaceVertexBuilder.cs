using System;
using System.Numerics;

namespace KhaozEngine.MapDoc.Surfaces;

internal readonly record struct MapSurfaceVertex(MapLatticeAddress Address, MapExactPoint Exact, Vector3 Offset);

/// <summary>Owns exact lattice positions and the policy-specific float conversion.</summary>
internal sealed class MapSurfaceVertexBuilder(MapSurfaceRef surface, MapSurfacePatch patch, MapSubmissionAnchor anchor)
{
    internal MapSurfaceVertex Point(int cellX, int cellZ, MapLatticePoint point)
    {
        (MapLatticePoint first, MapLatticePoint second) = MapSurfaceTopology.Ends(point);
        MapSurfaceVertex a = Corner(cellX, cellZ, first);
        if (first == second) return a;
        MapSurfaceVertex b = Corner(cellX, cellZ, second);
        (int x, int z) = MapSurfaceTopology.LocalTwice(point);
        MapLatticeAddress origin = patch.CornerAddress(cellX, cellZ);
        MapLatticeAddress address = Address(origin, x, z, 2);
        MapExactValue y = a.Exact.Y.Add(b.Exact.Y.Subtract(a.Exact.Y).Divide(new(2, 1)));
        if (surface.PresencePolicy != MapPresencePolicy.LegacyTileWorld) return Exact(address, y);
        // Only submission retains the released midpoint float operation.
        Vector3 offset = (a.Offset + b.Offset) * 0.5f;
        return Exact(address, y) with { Offset = offset };
    }

    MapSurfaceVertex Corner(int cellX, int cellZ, MapLatticePoint point)
    {
        (int x, int z) = MapSurfaceTopology.LocalTwice(point);
        x = cellX + x / 2;
        z = cellZ + z / 2;
        MapLatticeAddress address = patch.CornerAddress(x, z);
        int height = patch.Height(x, z);
        if (surface.PresencePolicy != MapPresencePolicy.LegacyTileWorld)
            return Exact(address, surface.Frame.Metres(new(height, 1)));
        long originZ = checked(-anchor.Z);
        var offset = new Vector3((float)checked(address.X - anchor.X), height * 0.01f,
            -(float)checked(address.Z - originZ));
        return Exact(address, surface.Frame.Metres(new(height, 1))) with { Offset = offset };
    }

    internal MapSurfaceVertex Insert(int cellX, int cellZ, MapCellEdge edge, int step, int segments,
        MapSurfaceVertex from, MapSurfaceVertex to, int fromTwice, int toTwice)
    {
        MapLatticeAddress origin = patch.CornerAddress(cellX, cellZ);
        (int x, int z) = edge switch
        {
            MapCellEdge.South => (step, 0),
            MapCellEdge.East => (segments, step),
            MapCellEdge.North => (step, segments),
            _ => (0, step),
        };
        var t = new MapExactValue(2L * step - (long)fromTwice * segments,
            (long)(toTwice - fromTwice) * segments);
        MapExactValue y = from.Exact.Y.Add(to.Exact.Y.Subtract(from.Exact.Y).Multiply(t));
        MapExactValue submittedY = surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld
            ? MapExactValue.FromSingle(from.Offset.Y).Add(MapExactValue.FromSingle(to.Offset.Y)
                .Subtract(MapExactValue.FromSingle(from.Offset.Y)).Multiply(t)) : y;
        return Exact(Address(origin, x, z, segments), y, submittedY);
    }

    internal MapSurfaceVertex Centre(int cellX, int cellZ, MapLatticeTriangle parent,
        MapSurfaceVertex a, MapSurfaceVertex b, MapSurfaceVertex c)
    {
        (int ax, int az) = MapSurfaceTopology.LocalTwice(parent.A);
        (int bx, int bz) = MapSurfaceTopology.LocalTwice(parent.B);
        (int cx, int cz) = MapSurfaceTopology.LocalTwice(parent.C);
        MapLatticeAddress address = Address(patch.CornerAddress(cellX, cellZ), ax + bx + cx, az + bz + cz, 6);
        MapExactValue y = a.Exact.Y.Add(b.Exact.Y.Subtract(a.Exact.Y).Divide(new(3, 1)))
            .Add(c.Exact.Y.Subtract(a.Exact.Y).Divide(new(3, 1)));
        MapExactValue submittedY = y;
        if (surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld)
        {
            MapExactValue first = MapExactValue.FromSingle(a.Offset.Y);
            submittedY = first.Add(MapExactValue.FromSingle(b.Offset.Y).Subtract(first).Divide(new(3, 1)))
                .Add(MapExactValue.FromSingle(c.Offset.Y).Subtract(first).Divide(new(3, 1)));
        }
        return Exact(address, y, submittedY);
    }

    MapSurfaceVertex Exact(MapLatticeAddress address, MapExactValue y, MapExactValue? submittedY = null)
    {
        MapExactXz xz = surface.Frame.WorldXz(address);
        var exact = new MapExactPoint(xz.X, y, xz.Z);
        // Legacy fan and subdivision offsets interpolate the released float endpoints independently of exact geometry.
        return new(address, exact, MapSubmissionGeometry.Offset(exact with { Y = submittedY ?? y }, anchor));
    }

    static MapLatticeAddress Address(MapLatticeAddress origin, int x, int z, int denominator)
    {
        Int128 nx = (Int128)origin.X * denominator + x;
        Int128 nz = (Int128)origin.Z * denominator + z;
        Int128 gcd = MapExactValue.Gcd(MapExactValue.Gcd(nx, nz), denominator);
        nx /= gcd;
        nz /= gcd;
        denominator /= (int)gcd;
        if (nx < long.MinValue || nx > long.MaxValue || nz < long.MinValue || nz > long.MaxValue)
            throw new MapExactOverflowException();
        return MapLatticeAddress.Create((long)nx, (long)nz, denominator);
    }
}
