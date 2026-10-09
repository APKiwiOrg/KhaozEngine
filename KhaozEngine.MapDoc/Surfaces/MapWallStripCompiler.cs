using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>An immutable ruled wall strip with one owner and distinct face sides.</summary>
public sealed class MapCompiledStrip
{
    public string StripId { get; }
    public MapSubmissionAnchor Anchor { get; }
    public IReadOnlyList<MapExactPoint> ExactVertices { get; }
    public IReadOnlyList<Vector3> Offsets { get; }
    public IReadOnlyList<MapCompiledFace> Faces { get; }

    internal MapCompiledStrip(string stripId, MapSubmissionAnchor anchor, IEnumerable<MapExactPoint> vertices,
        IEnumerable<Vector3> offsets, IEnumerable<MapCompiledFace> faces)
    {
        StripId = stripId;
        Anchor = anchor;
        ExactVertices = Array.AsReadOnly(vertices.ToArray());
        Offsets = Array.AsReadOnly(offsets.ToArray());
        Faces = Array.AsReadOnly(faces.ToArray());
    }
}

/// <summary>Compiles matching exact world sequences into ruled quads with a fixed diagonal.</summary>
public static class MapWallStripCompiler
{
    public static MapCompiledStrip Compile(MapWallStrip strip, MapChainResolution lower, MapChainResolution upper)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        if (lower.Status != MapResolveStatus.Resolved || upper.Status != MapResolveStatus.Resolved)
            throw new MapDocumentException(lower.Status != MapResolveStatus.Resolved
                ? lower.Detail ?? "unresolved lower chain" : upper.Detail ?? "unresolved upper chain");
        if (!Enum.IsDefined(strip.Facing)) throw new MapDocumentException("invalid strip facing");
        if (lower.Points.Count is < 2 or > 4097 || lower.Points.Count != upper.Points.Count)
            throw new MapDocumentException("wall strip vertex sequence mismatch");
        try
        {
            int count = lower.Points.Count;
            for (int i = 0; i < count; i++)
            {
                MapExactPoint l = lower.Points[i], u = upper.Points[i];
                if (l.X != u.X || l.Z != u.Z) throw new MapDocumentException("wall strip vertex sequence mismatch");
                if (l.Y.CompareTo(u.Y) > 0) throw new MapDocumentException("wall strip lower chain is above upper chain");
            }
            MapExactPoint[] vertices = lower.Points.Concat(upper.Points).ToArray();
            MapExactValue minX = vertices.Min(p => p.X), maxX = vertices.Max(p => p.X);
            MapExactValue minZ = vertices.Min(p => p.Z), maxZ = vertices.Max(p => p.Z);
            MapExactValue minY = vertices.Min(p => p.Y), maxY = vertices.Max(p => p.Y);
            MapSubmissionAnchor anchor = MapSubmissionGeometry.Anchor(new(minX, minZ), new(maxX, maxZ),
                minY.Add(maxY.Subtract(minY).Divide(new(2, 1))));
            Vector3[] offsets = vertices.Select(point => MapSubmissionGeometry.Offset(point, anchor)).ToArray();
            var faces = new List<MapCompiledFace>();
            for (int i = 0; i < count - 1; i++)
            {
                MapExactValue dx = lower.Points[i + 1].X.Subtract(lower.Points[i].X);
                MapExactValue dz = lower.Points[i + 1].Z.Subtract(lower.Points[i].Z);
                if (dx.Sign == 0 && dz.Sign == 0) continue;
                Vector3 normal = FrontNormal(dx, dz);
                // Matching XZ makes each triangle degenerate exactly when its vertical end is zero.
                if (lower.Points[i + 1].Y != upper.Points[i + 1].Y) Emit(i, 0, i, i + 1, count + i + 1, normal);
                if (lower.Points[i].Y != upper.Points[i].Y) Emit(i, 1, i, count + i + 1, count + i, normal);
            }
            return new(strip.Id, anchor, vertices, offsets, faces);

            void Emit(int segment, byte triangle, int a, int b, int c, Vector3 normal)
            {
                // The specified diagonal is unchanged. Front winding follows (dz, 0, -dx).
                if (strip.Facing is MapStripFacing.Front or MapStripFacing.TwoSided)
                    faces.Add(new(new(strip.Id, null, segment, triangle, 0, MapSide.Front), MapFaceRole.Wall, a, c, b, normal));
                if (strip.Facing is MapStripFacing.Back or MapStripFacing.TwoSided)
                    faces.Add(new(new(strip.Id, null, segment, triangle, 0, MapSide.Back), MapFaceRole.Wall, a, b, c, -normal));
            }
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("wall strip geometry is not representable", error);
        }
    }

    static Vector3 FrontNormal(MapExactValue dx, MapExactValue dz)
    {
        double x = dz.ToDouble(), z = -dx.ToDouble();
        double scale = Math.Max(Math.Abs(x), Math.Abs(z));
        Vector3 normal = Vector3.Normalize(new((float)(x / scale), 0, (float)(z / scale)));
        if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Z))
            throw new MapDocumentException("wall strip normal is not representable");
        return normal;
    }
}
