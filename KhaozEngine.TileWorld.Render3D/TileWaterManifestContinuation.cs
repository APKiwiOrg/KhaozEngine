using System;
using System.Collections.Generic;
using System.Numerics;
using Vertex = KhaozEngine.TileWorld.TileWaterTrianglePreparation.Vertex;

namespace KhaozEngine.TileWorld;

/// <summary>Completes only absent manifest space with a continuous piecewise-linear clamped bed. The grid
/// contains every consumed boundary breakpoint, so its edges follow the actual mesh profiles, including LOD
/// fans and cut tiles. Shared exterior vertices resolve concave corners without overlapping extrusion strips.
/// Original triangles are never changed. The ordinary field proof still certifies coverage and rounding seams.</summary>
internal sealed class TileWaterManifestContinuation(TileWorldDocument document, TileRect domain,
    IReadOnlyList<RegionCoord> absent)
{
    const double InterpolationSlack = 16d / (1L << 50);
    readonly List<Source> _sources = new();

    readonly record struct Bounds(double MinX, double MinZ, double MaxX, double MaxZ)
    {
        internal bool Contains(double x, double z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    sealed record Source(RegionCoord Region, Bounds Bounds, List<Edge> Edges);

    readonly record struct Edge(Vertex A, Vertex B)
    {
        internal bool Contains(double x, double z) =>
            x >= Math.Min(A.X, B.X) && x <= Math.Max(A.X, B.X) &&
            z >= Math.Min(A.Z, B.Z) && z <= Math.Max(A.Z, B.Z);

        internal Vertex At(double x, double z)
        {
            double t = A.X != B.X ? (x - A.X) / (B.X - A.X) : A.Z != B.Z ? (z - A.Z) / (B.Z - A.Z) : 0;
            double y = t == 0 || A.Y == B.Y ? A.Y : t == 1 ? B.Y : A.Y + t * (B.Y - A.Y);
            double slack = t == 0 || t == 1 || A.Y == B.Y ? 0 :
                InterpolationSlack * (1 + Math.Abs(A.Y) + Math.Abs(B.Y));
            return new Vertex(x, y, z, Math.Max(A.Slack, B.Slack), Math.Max(A.YSlack, B.YSlack) + slack);
        }
    }

    internal void Add(TileGroundSurfaceSnapshot snapshot, List<Vertex> vertices, int first)
    {
        if (absent.Count == 0) return;
        Matrix4x4 m = snapshot.WorldTransform;
        float span = TileRegion.Size * snapshot.TileSize;
        var bounds = new Bounds(m.M41, (double)m.M43 - span, (double)m.M41 + span, m.M43);
        var edges = new List<Edge>();
        for (int i = first; i + 2 < vertices.Count; i += 3)
        {
            Vertex a = vertices[i], b = vertices[i + 1], c = vertices[i + 2];
            // Vertical faces do not supply ground, even when they have an edge on the manifest boundary.
            if ((b.X - a.X) * (c.Z - a.Z) == (b.Z - a.Z) * (c.X - a.X)) continue;
            AddEdge(a, b);
            AddEdge(b, c);
            AddEdge(c, a);
        }
        _sources.Add(new Source(snapshot.Id.Region, bounds, edges));

        void AddEdge(Vertex a, Vertex b)
        {
            if ((a.X == b.X && (a.X == bounds.MinX || a.X == bounds.MaxX)) ||
                (a.Z == b.Z && (a.Z == bounds.MinZ || a.Z == bounds.MaxZ)))
                edges.Add(new Edge(a, b));
        }
    }

    internal void Append(List<Vertex> vertices)
    {
        if (absent.Count == 0 || _sources.Count == 0) return;
        double tileSize = document.TileSize;
        double minX = domain.X * tileSize, maxX = ((long)domain.X + domain.Width) * tileSize;
        double minZ = -((long)domain.Z + domain.Height) * tileSize, maxZ = -(long)domain.Z * tileSize;
        var rectangles = new List<Bounds>();
        var xs = new SortedSet<double>();
        var zs = new SortedSet<double>();
        foreach (RegionCoord region in absent)
        {
            var rect = new Bounds(Math.Max(minX, BoundaryX(region.Rx)), Math.Max(minZ, BoundaryZ(region.Rz + 1)),
                Math.Min(maxX, BoundaryX(region.Rx + 1)), Math.Min(maxZ, BoundaryZ(region.Rz)));
            if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ) continue;
            rectangles.Add(rect);
            xs.Add(rect.MinX);
            xs.Add(rect.MaxX);
            zs.Add(rect.MinZ);
            zs.Add(rect.MaxZ);
        }
        if (rectangles.Count == 0) return;
        // All breakpoints participate, not just tile corners. Otherwise a long synthetic edge could cut
        // across a kink in the consumed boundary profile and introduce a depth discontinuity.
        foreach (Source source in _sources)
            foreach (Edge edge in source.Edges)
            {
                if (edge.A.X >= minX && edge.A.X <= maxX) xs.Add(edge.A.X);
                if (edge.B.X >= minX && edge.B.X <= maxX) xs.Add(edge.B.X);
                if (edge.A.Z >= minZ && edge.A.Z <= maxZ) zs.Add(edge.A.Z);
                if (edge.B.Z >= minZ && edge.B.Z <= maxZ) zs.Add(edge.B.Z);
            }
        double[] xAxis = new double[xs.Count], zAxis = new double[zs.Count];
        xs.CopyTo(xAxis);
        zs.CopyTo(zAxis);
        var samples = new Dictionary<(double X, double Z), Vertex?>();
        for (int z = 0; z + 1 < zAxis.Length; z++)
            for (int x = 0; x + 1 < xAxis.Length; x++)
            {
                double x0 = xAxis[x], x1 = xAxis[x + 1], z0 = zAxis[z], z1 = zAxis[z + 1];
                double cx = (x0 + x1) / 2, cz = (z0 + z1) / 2;
                bool exterior = false;
                foreach (Bounds rect in rectangles)
                    if (rect.Contains(cx, cz)) { exterior = true; break; }
                if (!exterior || Sample(cx, cz) is null) continue;
                // A hole in a boundary profile remains a hole. In particular, two grounded endpoints
                // cannot bridge an empty or NoDraw edge tile between them.
                if (Node(x0, z0) is not { } a || Node(x1, z0) is not { } b ||
                    Node(x1, z1) is not { } c || Node(x0, z1) is not { } d) continue;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(d);
                vertices.Add(b);
                vertices.Add(c);
                vertices.Add(d);
            }

        Vertex? Node(double x, double z)
        {
            if (!samples.TryGetValue((x, z), out Vertex? value))
                samples.Add((x, z), value = Sample(x, z));
            return value;
        }
    }

    Vertex? Sample(double x, double z)
    {
        double distance = double.PositiveInfinity;
        foreach (Source source in _sources)
        {
            double sx = Math.Clamp(x, source.Bounds.MinX, source.Bounds.MaxX);
            double sz = Math.Clamp(z, source.Bounds.MinZ, source.Bounds.MaxZ);
            double candidate = (sx - x) * (sx - x) + (sz - z) * (sz - z);
            if (candidate < distance) distance = candidate;
        }
        // Only equally near sources may supply this seam, so a farther source cannot bridge an empty edge tile.
        foreach (Source source in _sources)
        {
            double px = Math.Clamp(x, source.Bounds.MinX, source.Bounds.MaxX);
            double pz = Math.Clamp(z, source.Bounds.MinZ, source.Bounds.MaxZ);
            double candidate = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (candidate != distance) continue;
            foreach (Edge edge in source.Edges)
                if (edge.Contains(px, pz))
                {
                    Vertex projected = edge.At(px, pz);
                    return projected with { X = x, Z = z };
                }
        }
        return null;
    }

    // Use the uploaded boundary coordinates, rather than recomputing the region's translation in double.
    // This keeps the continuation attached to the actual edge when a binary32 world transform has rounded.
    double BoundaryX(int rx)
    {
        foreach (Source source in _sources)
            if (source.Region.Rx == rx - 1) return source.Bounds.MaxX;
        foreach (Source source in _sources)
            if (source.Region.Rx == rx) return source.Bounds.MinX;
        return (long)rx * TileRegion.Size * (double)document.TileSize;
    }

    double BoundaryZ(int rz)
    {
        foreach (Source source in _sources)
            if (source.Region.Rz == rz) return source.Bounds.MaxZ;
        foreach (Source source in _sources)
            if (source.Region.Rz == rz - 1) return source.Bounds.MinZ;
        return -(long)rz * TileRegion.Size * (double)document.TileSize;
    }
}
