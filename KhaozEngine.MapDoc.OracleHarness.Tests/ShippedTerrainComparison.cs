using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.MapDocOracle;

// One region of the verified source world. SpikePoint is the historical movement spike the private manifest names
// under comparison.spikePoint, null when it names none.
internal sealed record ShippedRegion(RegionCoord Coord, TileWorldDocument Document)
{
    internal ShippedSpikePoint? SpikePoint { get; init; }
}

// A world point in metres on plane 0, the one plane the legacy movement sampler describes.
internal sealed record ShippedSpikePoint(float WorldX, float WorldZ)
{
    internal const string Property = "spikePoint";

    // An absent property names no point. A present but malformed one throws, so the entry fails the run.
    internal static ShippedSpikePoint? From(JsonElement comparison)
    {
        if (comparison.ValueKind != JsonValueKind.Object || !comparison.TryGetProperty(Property, out JsonElement point))
            return null;
        if (point.ValueKind != JsonValueKind.Object ||
            !point.TryGetProperty("worldX", out JsonElement x) || !x.TryGetSingle(out float worldX) ||
            !point.TryGetProperty("worldZ", out JsonElement z) || !z.TryGetSingle(out float worldZ) ||
            !float.IsFinite(worldX) || !float.IsFinite(worldZ))
            throw new InvalidDataException("invalid spike point");
        return new(worldX, worldZ);
    }
}

// The exhaustive shipped comparison. Public code with no data. Every outcome goes through report.Check or
// report.Record, never Assert, so source values reach only the private report.
[UnsupportedOSPlatform("windows")]
internal static class ShippedTerrainComparison
{
    const float VertexTolerance = 0.00001f;
    const float NormalTolerance = 0.000001f;
    const double SupportTolerance = 0.00001;

    internal static void CompareRegion(ShippedRegion region, PrivateOracleReport report)
    {
        TileWorldDocument document = region.Document;
        RegionCoord coord = region.Coord;
        report.Check("tile-size", document.TileSize == 1f, Json(new { coord.Rx, coord.Rz }));
        report.Check("plane-count", document.PlaneCount == 4, Json(new { coord.Rx, coord.Rz, document.PlaneCount }));
        for (int plane = 0; plane < document.PlaneCount; plane++)
            ComparePlane(document, coord, plane, report);

        IReadOnlyList<string> seams = LegacyOracleConverter.SharedCornerMismatches(document, coord);
        report.Check("seams", seams.Count == 0, Json(new { coord.Rx, coord.Rz, seams.Count, first = seams.Take(16) }));
        CompareSpikePoint(region, report);
    }

    static void ComparePlane(TileWorldDocument document, RegionCoord coord, int plane, PrivateOracleReport report)
    {
        TileRegion source = document.GetRegion(coord)!;
        bool derived = plane > 0 && source.Plane(plane).Heights is null;
        var (surface, patch) = LegacyOracleConverter.ToNative(document, coord, plane);

        int[] legacyCorners = LegacyOracleConverter.LegacyCorners(document, coord, plane);
        int cornerMismatches = legacyCorners.Where((h, i) => h != patch.Heights[i]).Count();
        report.Check("corners", legacyCorners.Length == patch.Heights.Length && cornerMismatches == 0,
            Json(new { coord.Rx, coord.Rz, plane, derived, cornerMismatches }));

        byte[] legacyBytes = LegacyOracleConverter.CellBytes(document, coord, plane);
        byte[] nativeBytes = LegacyOracleConverter.CellBytes(patch);
        report.Check("cell-bytes", legacyBytes.AsSpan().SequenceEqual(nativeBytes),
            Json(new { coord.Rx, coord.Rz, plane }));
        report.Check("records", patch.Records.Count == 0, Json(new { coord.Rx, coord.Rz, plane, patch.Records.Count }));

        MapCompiledPatch native = MapSurfaceCompiler.Compile(surface, patch);
        TileGroundMesh legacy = TileGroundTriangles.Build(document, coord, plane);
        int legacyTriangles = legacy.Indices.Length / 3;
        int supportFaces = native.Faces.Count(f => f.Role == MapFaceRole.SupportFloor);
        bool sameCount = legacyTriangles == native.Faces.Count && supportFaces == native.Faces.Count;
        report.Check("triangle-count", sameCount,
            Json(new { coord.Rx, coord.Rz, plane, legacyTriangles, nativeFaces = native.Faces.Count, supportFaces }));

        var (vertexError, normalError) = sameCount ? TriangleErrors(legacy, native) : (double.NaN, double.NaN);
        report.Check("triangle-vertex", vertexError <= VertexTolerance,
            Json(new { coord.Rx, coord.Rz, plane, maxVertexError = Finite(vertexError) }));
        report.Check("triangle-normal", normalError <= NormalTolerance,
            Json(new { coord.Rx, coord.Rz, plane, maxNormalError = Finite(normalError) }));

        int[] legacyFallback = LegacyOracleConverter.FallbackCells(document, coord, plane).ToArray();
        int[] nativeFallback = native.LegacyFallbackCells.Select(c => c.SlotCell).ToArray();
        report.Check("fallback", legacyFallback.AsSpan().SequenceEqual(nativeFallback),
            Json(new { coord.Rx, coord.Rz, plane, legacy = legacyFallback.Length, native = nativeFallback.Length }));

        report.Record("plane-summary", Json(new
        {
            coord.Rx,
            coord.Rz,
            plane,
            derived,
            legacyTriangles,
            nativeFaces = native.Faces.Count,
            fallbackCells = nativeFallback.Length,
            maxVertexError = Finite(vertexError),
            maxNormalError = Finite(normalError),
        }));
    }

    // The same per-vertex and per-normal comparison as LegacyOracleConverter.AssertSameTriangles, returning the
    // largest component errors instead of asserting. A non-finite component makes the maximum NaN, which fails.
    static (double Vertex, double Normal) TriangleErrors(TileGroundMesh legacy, MapCompiledPatch native)
    {
        var legacyOrigin = new Vector3(legacy.Region.OriginX, 0, -legacy.Region.OriginZ);
        var nativeOrigin = new Vector3((float)native.Anchor.X, (float)native.Anchor.Y, (float)native.Anchor.Z);
        double vertex = 0, normal = 0;
        for (int i = 0; i < native.Faces.Count; i++)
        {
            Vector3 a = legacy.Positions[legacy.Indices[3 * i]],
                b = legacy.Positions[legacy.Indices[3 * i + 1]], c = legacy.Positions[legacy.Indices[3 * i + 2]];
            MapCompiledFace face = native.Faces[i];
            vertex = Max(vertex, Error(legacyOrigin + a, nativeOrigin + native.Offsets[face.A]));
            vertex = Max(vertex, Error(legacyOrigin + b, nativeOrigin + native.Offsets[face.B]));
            vertex = Max(vertex, Error(legacyOrigin + c, nativeOrigin + native.Offsets[face.C]));
            normal = Max(normal, Error(LegacyOracleConverter.LegacyNormal(a, b, c), face.Normal));
        }
        return (vertex, normal);
    }

    // When the source still draws ground under the named point, native support height on the canonical faces must
    // equal the legacy movement triangle the plane-0 ground sampler reads there.
    static void CompareSpikePoint(ShippedRegion region, PrivateOracleReport report)
    {
        if (region.SpikePoint is not { } spike) return;
        TileWorldDocument document = region.Document;
        float tileX = TileWorldSpace.TileX(spike.WorldX, document.TileSize);
        float tileZ = TileWorldSpace.TileZ(spike.WorldZ, document.TileSize);
        int x = (int)MathF.Floor(tileX), z = (int)MathF.Floor(tileZ);
        if (RegionCoord.Of(x, z) != region.Coord) return;

        if (!TryMovementHeight(document, tileX, tileZ, out float legacy))
        {
            report.Record("spike-point", Json(new { drawable = false }));
            return;
        }
        var (surface, patch) = LegacyOracleConverter.ToNative(document, region.Coord, 0);
        MapExactValue? native = MapSurfaceCompiler.ExactHeight(surface, patch,
            MapExactValue.FromSingle(spike.WorldX), MapExactValue.FromSingle(spike.WorldZ));
        double error = native is { } height ? Math.Abs(height.ToDouble() - legacy) : double.NaN;
        report.Check("spike-support", error <= SupportTolerance,
            Json(new { drawable = true, legacy = Finite(legacy), native = native?.ToDouble(), error = Finite(error) }));
        report.Record("spike-point", Json(new { drawable = true, error = Finite(error) }));
    }

    // The released plane-0 movement triangle: among the tile's drawn triangles, the one whose smallest barycentric
    // weight is largest, its height interpolated from lattice positions placed relative to the tile corner.
    static bool TryMovementHeight(TileWorldDocument document, float tileX, float tileZ, out float height)
    {
        int x = (int)MathF.Floor(tileX), z = (int)MathF.Floor(tileZ);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        height = 0f;
        if (!TileGroundTriangles.TryDescribe(document, x, z, 0, out TileGroundCell cell, triangles)) return false;

        var local = new Vector2(tileX - x, tileZ - z);
        int best = 0;
        Vector3 weights = default;
        float bestMin = float.NegativeInfinity;
        for (int i = 0; i < cell.TriangleCount; i++)
        {
            Vector3 w = Weights(local, TileTriangulation.Local(triangles[i].A), TileTriangulation.Local(triangles[i].B),
                TileTriangulation.Local(triangles[i].C));
            float min = MathF.Min(w.X, MathF.Min(w.Y, w.Z));
            if (min > bestMin)
            {
                bestMin = min;
                best = i;
                weights = w;
            }
        }
        TileLatticeTriangle t = triangles[best];
        Vector3 a = TileGroundTriangles.LatticePosition(document, x, z, 0, t.A, x, z);
        Vector3 b = TileGroundTriangles.LatticePosition(document, x, z, 0, t.B, x, z);
        Vector3 c = TileGroundTriangles.LatticePosition(document, x, z, 0, t.C, x, z);
        height = weights.X * a.Y + weights.Y * b.Y + weights.Z * c.Y;
        return true;
    }

    static Vector3 Weights(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a, ac = c - a, ap = p - a;
        float determinant = ab.X * ac.Y - ac.X * ab.Y;
        float wb = (ap.X * ac.Y - ac.X * ap.Y) / determinant;
        float wc = (ab.X * ap.Y - ap.X * ab.Y) / determinant;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    static double Error(Vector3 expected, Vector3 actual) => Math.Max(Math.Abs(expected.X - actual.X),
        Math.Max(Math.Abs(expected.Y - actual.Y), Math.Abs(expected.Z - actual.Z)));

    static double Max(double current, double next) => double.IsNaN(next) ? double.NaN : Math.Max(current, next);

    static double? Finite(double value) => double.IsFinite(value) ? value : null;

    static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
