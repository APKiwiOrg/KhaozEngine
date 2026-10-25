using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>One certified field for one reference level. Origin XZ is its absolute minimum and Y is the exact
/// datum 0, so rest depth is <c>-y</c>. Level is the water surface Y the field serves, the key planes bind by and
/// refusals report. Domain is the tile-space extent of its certificates.</summary>
internal sealed record TileWaterTriangleBuild(WaterOpticsTriangleField Field, Vector3 Origin, TileRect Domain,
    float Level);

/// <summary>Document inputs that witness validation reads. Never captured by a worker. The registry owns the
/// availability epochs of every region a consumed strict read inspected.</summary>
internal readonly record struct TileWaterSourceContext(TileWorldDocument Document, TileWorldCatalogs Catalogs,
    TileGroundMesherOptions Options, TileWaterDependencyRegistry? Registry);

/// <summary>Every reference level of one region-plane prepared against one candidate source table. A refusal
/// carries every source key of its certified domain, so any of those sources publishing retries it. Availability
/// stamps every region the consumed strict reads inspected, so an arrival or departure invalidates the field
/// even when no corner height changed.</summary>
internal sealed class TileWaterTriangleOutcome
{
    internal static readonly TileWaterTriangleOutcome Empty = new([], [], [], [], null, null);

    /// <summary>A consumed range of one source snapshot. Staging only, it is never retained after the swap.</summary>
    internal readonly record struct Support(TileGroundSurfaceSnapshot Snapshot, int Range);

    readonly Support[] _support;

    internal TileWaterTriangleOutcome(TileWaterTriangleBuild[] fields, TileGroundSurfaceId[] sources,
        (RegionCoord Region, int Plane)[] sourceKeys, Support[] support, TileWaterOpticsFailure? failure,
        TileWaterSourceStamp? availability)
    {
        Availability = availability;
        Fields = fields;
        Sources = sources;
        SourceKeys = sourceKeys;
        _support = support;
        Failure = failure;
    }

    internal TileWaterTriangleBuild[] Fields { get; }
    internal TileGroundSurfaceId[] Sources { get; }
    internal (RegionCoord Region, int Plane)[] SourceKeys { get; }
    internal ReadOnlySpan<Support> Supporting => _support;
    internal TileWaterOpticsFailure? Failure { get; }
    internal TileWaterSourceStamp? Availability { get; }

    internal TileWaterTriangleOutcome Refused(TileWaterOpticsFailure failure) =>
        new([], [], SourceKeys, [], failure, Availability);
}

/// <summary>Builds immutable triangle fields from actual completed mesh snapshots. Source positions are converted
/// through each snapshot's exact uploaded transform in binary64. Rest depth is stored against the exact datum 0 as
/// <c>q = -y</c>, so binary32 heights that are collinear in the drawn mesh stay collinear in the field. Conversion,
/// render-origin and reconstruction error share the single budget <c>min(0.0001 m, TileSize / 1024)</c>.</summary>
internal static class TileWaterTrianglePreparation
{
    // Every bin axis doubles per attempt, from one bin per field tile up to the 1024 axis limit.
    internal const int MaxPackingAttempts = 11;
    const double Binary64Slack = 1d / (1L << 50);
    // Binding origins at or beyond 2^23 m are refused. WorldFrame anchors reach at most 32768 * 128 m = 2^22 m, so
    // a whole-metre origin minus any anchor stays an integer below 2^24 m, exact in binary32.
    internal const double MaxOriginMetres = 8388608d;

    internal static TileWaterOpticsFailureKind Kind(WaterOpticsSurfaceFailure reason) => reason switch
    {
        WaterOpticsSurfaceFailure.IncompleteCoverage => TileWaterOpticsFailureKind.IncompleteSurfaceCoverage,
        WaterOpticsSurfaceFailure.AmbiguousSurface => TileWaterOpticsFailureKind.AmbiguousSurface,
        WaterOpticsSurfaceFailure.CapacityExceeded => TileWaterOpticsFailureKind.SurfaceCapacityExceeded,
        WaterOpticsSurfaceFailure.UncertifiedDisplacement => TileWaterOpticsFailureKind.UncertifiedDisplacement,
        _ => TileWaterOpticsFailureKind.UnrepresentableSurface,
    };

    internal static TileWaterTriangleOutcome Prepare(TileWaterPreparation water, RegionCoord region, int plane,
        Func<RegionCoord, TileGroundSurfaceSnapshot?> sources, in TileWaterSourceContext context)
    {
        if (water.Planes.Length == 0) return TileWaterTriangleOutcome.Empty;
        var fields = new List<TileWaterTriangleBuild>();
        var ids = new List<TileGroundSurfaceId>();
        var keys = new List<(RegionCoord Region, int Plane)>();
        var support = new List<TileWaterTriangleOutcome.Support>();
        var inspected = new List<TileWaterOpticsDependency>();
        var levels = new HashSet<float>();
        var rects = new List<WaterPlane>();
        foreach (WaterPlane first in water.Planes)
        {
            if (!levels.Add(first.SurfaceY)) continue;
            rects.Clear();
            foreach (WaterPlane p in water.Planes)
                if (p.SurfaceY == first.SurfaceY) rects.Add(p);
            var level = new Level(region, plane, first.SurfaceY, water.Padding, sources, context, keys, ids, support,
                inspected);
            if (level.Build(rects) is { } failure)
                return new TileWaterTriangleOutcome([], [], Distinct(keys), [], failure, Stamp(inspected, context));
            fields.Add(level.Result!);
        }
        return new TileWaterTriangleOutcome(fields.ToArray(), Distinct(ids), Distinct(keys), support.ToArray(), null,
            Stamp(inspected, context));
    }

    /// <summary>The second validation before a swap. Every consumed range must still belong to the table's current
    /// snapshot and match live source data.</summary>
    internal static bool SourcesCurrent(TileWaterTriangleOutcome outcome,
        Func<RegionCoord, TileGroundSurfaceSnapshot?> sources, in TileWaterSourceContext context)
    {
        foreach (TileWaterTriangleOutcome.Support consumed in outcome.Supporting)
            if (!ReferenceEquals(sources(consumed.Snapshot.Id.Region), consumed.Snapshot) ||
                !consumed.Snapshot.SourceWitness.IsRangeCurrent(consumed.Range, context.Document, context.Catalogs,
                    context.Options))
                return false;
        return true;
    }

    static TileWaterSourceStamp? Stamp(List<TileWaterOpticsDependency> inspected, in TileWaterSourceContext context) =>
        context.Registry is null ? null : new TileWaterSourceStamp(context.Document, Distinct(inspected), context.Registry);

    static T[] Distinct<T>(List<T> values)
    {
        var seen = new HashSet<T>();
        var result = new List<T>(values.Count);
        foreach (T value in values)
            if (seen.Add(value)) result.Add(value);
        return result.ToArray();
    }

    sealed class Level(RegionCoord region, int plane, float reference, float padding,
        Func<RegionCoord, TileGroundSurfaceSnapshot?> sources, TileWaterSourceContext context,
        List<(RegionCoord Region, int Plane)> keys, List<TileGroundSurfaceId> ids,
        List<TileWaterTriangleOutcome.Support> support, List<TileWaterOpticsDependency> inspected)
    {
        readonly record struct Bounds(double MinX, double MinZ, double MaxX, double MaxZ);
        // YSlack is zero when the transform's Y row is the identity, as TileGroundMesher.WorldMatrix is.
        readonly record struct Vertex(double X, double Y, double Z, double Slack, double YSlack);

        TileRect _domain;
        internal TileWaterTriangleBuild? Result { get; private set; }

        TileWaterOpticsFailure Fail(TileWaterOpticsFailureKind kind, RegionCoord? missing = null, int columns = 0,
            int rows = 0) => new(region, plane, reference, kind, missing, _domain, columns, rows);

        internal TileWaterOpticsFailure? Build(List<WaterPlane> rects)
        {
            TileWorldDocument document = context.Document;
            float tileSize = document.TileSize;
            double budget = Math.Min(0.0001, (double)tileSize / 1024);
            var certificates = new Bounds[rects.Count];
            var tiles = new Bounds[rects.Count];
            double domainX0 = double.PositiveInfinity, domainZ0 = double.PositiveInfinity;
            double domainX1 = double.NegativeInfinity, domainZ1 = double.NegativeInfinity;
            // Expand outward by displacement and the positional budget before narrowing anything.
            double reach = (double)padding + budget;
            for (int i = 0; i < rects.Count; i++)
            {
                WaterPlane p = rects[i];
                var world = new Bounds((double)p.CenterX - p.HalfExtentX - reach, (double)p.CenterZ - p.HalfExtentZ - reach,
                    (double)p.CenterX + p.HalfExtentX + reach, (double)p.CenterZ + p.HalfExtentZ + reach);
                // World Z is minus tile Z, so the tile-space Z interval is reversed.
                var tile = new Bounds(world.MinX / tileSize, -world.MaxZ / tileSize, world.MaxX / tileSize,
                    -world.MinZ / tileSize);
                certificates[i] = world;
                tiles[i] = tile;
                domainX0 = Math.Min(domainX0, Math.Ceiling(tile.MinX) - 1);
                domainZ0 = Math.Min(domainZ0, Math.Ceiling(tile.MinZ) - 1);
                domainX1 = Math.Max(domainX1, Math.Floor(tile.MaxX));
                domainZ1 = Math.Max(domainZ1, Math.Floor(tile.MaxZ));
            }
            if (!(domainX0 >= int.MinValue && domainZ0 >= int.MinValue && domainX1 <= int.MaxValue &&
                domainZ1 <= int.MaxValue && domainX1 - domainX0 < WaterOpticsTriangleField.MaxDimension &&
                domainZ1 - domainZ0 < WaterOpticsTriangleField.MaxDimension))
                return Fail(TileWaterOpticsFailureKind.FieldTooLarge);
            _domain = new TileRect((int)domainX0, (int)domainZ0, (int)(domainX1 - domainX0) + 1,
                (int)(domainZ1 - domainZ0) + 1);

            var selected = new List<(TileGroundSurfaceSnapshot Snapshot, List<int> Ranges)>();
            RegionCoord? missingSurface = null;
            RegionCoord min = RegionCoord.Of((int)domainX0, (int)domainZ0);
            RegionCoord max = RegionCoord.Of((int)domainX1, (int)domainZ1);
            for (int rz = min.Rz; rz <= max.Rz; rz++)
                for (int rx = min.Rx; rx <= max.Rx; rx++)
                {
                    var source = new RegionCoord(rx, rz);
                    if (!Touches(source.OriginX, source.OriginZ, (long)source.OriginX + TileRegion.Size,
                        (long)source.OriginZ + TileRegion.Size, tiles)) continue;
                    keys.Add((source, plane));
                    TileGroundSurfaceSnapshot? snapshot = sources(source);
                    if (snapshot is null || snapshot.Id.Region != source || snapshot.Id.Plane != plane ||
                        snapshot.TileSize != tileSize)
                    {
                        missingSurface ??= source;
                        continue;
                    }
                    TileGroundSurfaceAudit audit = snapshot.SourceWitness;
                    var ranges = new List<int>();
                    for (int k = 0; k < audit.Ranges.Length; k++)
                    {
                        TileGroundSurfaceRange range = audit.Ranges[k];
                        if (!Touches(range.Tiles.X, range.Tiles.Z, range.Tiles.X1, range.Tiles.Z1, tiles)) continue;
                        if (!audit.IsRangeRepresentable(k)) return Fail(TileWaterOpticsFailureKind.FieldTooLarge);
                        // A strict-read fallback emitted a plausible mesh that cannot certify an optical bed.
                        if (!audit.IsRangeAvailable(k, out RegionCoord unavailable))
                        {
                            inspected.Add(new TileWaterOpticsDependency(unavailable, null));
                            return Fail(TileWaterOpticsFailureKind.MissingHeightData, unavailable);
                        }
                        if (!audit.IsRangeCurrent(k, document, context.Catalogs, context.Options))
                        {
                            missingSurface ??= source;
                            break;
                        }
                        ranges.Add(k);
                        foreach (int height in audit.HeightIndices(k))
                            foreach (RegionCoord candidate in audit.HeightReads[height].InspectedRegions)
                                inspected.Add(new TileWaterOpticsDependency(candidate, null));
                    }
                    selected.Add((snapshot, ranges));
                }
            if (missingSurface is { } absent) return Fail(TileWaterOpticsFailureKind.MissingSurfaceData, absent);

            var vertices = new List<Vertex>();
            double minX = double.PositiveInfinity, minZ = double.PositiveInfinity;
            foreach ((TileGroundSurfaceSnapshot snapshot, List<int> ranges) in selected)
            {
                ids.Add(snapshot.Id);
                Matrix4x4 m = snapshot.WorldTransform;
                ReadOnlySpan<Vector3> positions = snapshot.Payload.Positions;
                ReadOnlySpan<uint> indices = snapshot.Payload.Indices;
                foreach (int k in ranges)
                {
                    support.Add(new TileWaterTriangleOutcome.Support(snapshot, k));
                    TileGroundSurfaceRange range = snapshot.SourceWitness.Ranges[k];
                    int end = TriangleEnd(range);
                    for (int i = range.FirstIndex; i < end; i++)
                    {
                        Vertex v = Transform(positions[(int)indices[i]], m);
                        if (!double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z))
                            return Fail(TileWaterOpticsFailureKind.UnrepresentableSurface);
                        minX = Math.Min(minX, v.X);
                        minZ = Math.Min(minZ, v.Z);
                        vertices.Add(v);
                    }
                }
            }
            if (vertices.Count == 0) return Fail(TileWaterOpticsFailureKind.IncompleteSurfaceCoverage);
            // Whole-metre binding origin below 2^23 m. Every WorldFrame anchor is an integer multiple of 128 m of
            // magnitude at most 2^22 m, so origin minus anchor is an integer below 2^24 m, exact in binary32.
            double flooredX = Math.Floor(minX), flooredZ = Math.Floor(minZ);
            if (!(Math.Abs(flooredX) < MaxOriginMetres && Math.Abs(flooredZ) < MaxOriginMetres))
                return Fail(TileWaterOpticsFailureKind.UnrepresentableSurface);
            float originX = (float)flooredX, originZ = (float)flooredZ;

            var triangles = new List<WaterOpticsTriangle>(vertices.Count / 3);
            var errors = new List<(double Depth, double Horizontal, double Slope)>(vertices.Count / 3);
            float sizeX = 0f, sizeZ = 0f;
            Span<Vector3> local = stackalloc Vector3[3];
            for (int t = 0; t + 2 < vertices.Count; t += 3)
            {
                double depthError = 0, horizontalError = 0;
                for (int j = 0; j < 3; j++)
                {
                    Vertex v = vertices[t + j];
                    // Negation is exact, so depth error is only the binary32 rounding of y, zero for binary32 y.
                    double exactX = v.X - originX, exactZ = v.Z - originZ, exactDepth = -v.Y;
                    local[j] = new Vector3((float)exactX, (float)exactDepth, (float)exactZ);
                    // Binary64 evaluation slack covers the transform sum and these subtractions.
                    double slackX = v.Slack + Binary64Slack * (Math.Abs(v.X) + Math.Abs(originX));
                    double slackZ = v.Slack + Binary64Slack * (Math.Abs(v.Z) + Math.Abs(originZ));
                    horizontalError = Math.Max(horizontalError, Math.Max(
                        Math.Abs(local[j].X - exactX) + slackX, Math.Abs(local[j].Z - exactZ) + slackZ));
                    depthError = Math.Max(depthError, Math.Abs(local[j].Y - exactDepth) + v.YSlack);
                }
                if (!float.IsFinite(local[0].Y) || !float.IsFinite(local[1].Y) || !float.IsFinite(local[2].Y))
                    return Fail(TileWaterOpticsFailureKind.UnrepresentableSurface);
                // Vertical faces have no projected area and support no bed. They are not candidates.
                if (!HasProjectedArea(local[0], local[1], local[2])) continue;
                triangles.Add(new WaterOpticsTriangle(local[0], local[1], local[2]));
                errors.Add((depthError, horizontalError, Slope(local[0], local[1], local[2])));
                sizeX = MathF.Max(sizeX, MathF.Max(local[0].X, MathF.Max(local[1].X, local[2].X)));
                sizeZ = MathF.Max(sizeZ, MathF.Max(local[0].Z, MathF.Max(local[1].Z, local[2].Z)));
            }
            if (triangles.Count == 0 || sizeX <= 0f || sizeZ <= 0f)
                return Fail(TileWaterOpticsFailureKind.IncompleteSurfaceCoverage);

            // Render-origin reduction is charged zero only for the default quantized render origin and any other
            // 128 m WorldFrame anchor, where origin minus anchor is exact. An explicit non-grid Scene3D.RenderOrigin
            // override rounds the reduced origin like all other submitted geometry and is outside this certificate.
            // Source conversion error against the origin costs depth directly and through each triangle's slope.
            double conversion = 0;
            foreach ((double depth, double horizontal, double slope) in errors)
                conversion = Math.Max(conversion, depth + slope * horizontal);
            if (!(conversion <= budget)) return Fail(TileWaterOpticsFailureKind.UnrepresentableSurface);

            var size = new Vector2(sizeX, sizeZ);
            var local2 = new WaterOpticsRectangle[certificates.Length];
            for (int i = 0; i < certificates.Length; i++)
            {
                Bounds c = certificates[i];
                var lo = new Vector2(Down(c.MinX - originX), Down(c.MinZ - originZ));
                var hi = new Vector2(Up(c.MaxX - originX), Up(c.MaxZ - originZ));
                // Support never reaches the certificate. Complete triangles are never moved or clipped to fit.
                if (!(lo.X >= 0f && lo.Y >= 0f && hi.X <= size.X && hi.Y <= size.Y && lo.X < hi.X && lo.Y < hi.Y))
                    return Fail(TileWaterOpticsFailureKind.IncompleteSurfaceCoverage);
                local2[i] = new WaterOpticsRectangle(lo, hi);
            }

            int baseColumns = Math.Clamp((int)Math.Min(WaterOpticsTriangleField.MaxDimension, Math.Ceiling(size.X / tileSize)),
                1, WaterOpticsTriangleField.MaxDimension);
            int baseRows = Math.Clamp((int)Math.Min(WaterOpticsTriangleField.MaxDimension, Math.Ceiling(size.Y / tileSize)),
                1, WaterOpticsTriangleField.MaxDimension);
            WaterOpticsTriangle[] packed = triangles.ToArray();
            WaterOpticsTriangleField? field = null;
            int columns = 0, rows = 0;
            for (int attempt = 0; attempt < MaxPackingAttempts && field is null; attempt++)
            {
                columns = Math.Min(WaterOpticsTriangleField.MaxDimension, baseColumns << attempt);
                rows = Math.Min(WaterOpticsTriangleField.MaxDimension, baseRows << attempt);
                try
                {
                    field = new WaterOpticsTriangleField(size, columns, rows, packed, local2);
                }
                catch (WaterOpticsSurfaceException capacity) when (capacity.Reason == WaterOpticsSurfaceFailure.CapacityExceeded)
                {
                    if (columns == WaterOpticsTriangleField.MaxDimension && rows == WaterOpticsTriangleField.MaxDimension) break;
                }
                catch (WaterOpticsSurfaceException refused)
                {
                    return Fail(Kind(refused.Reason), columns: columns, rows: rows);
                }
            }
            if (field is null) return Fail(TileWaterOpticsFailureKind.SurfaceCapacityExceeded, columns: columns, rows: rows);
            if (!(conversion + field.ReconstructionErrorBoundMetres <= budget))
                return Fail(TileWaterOpticsFailureKind.UnrepresentableSurface, columns: columns, rows: rows);
            Result = new TileWaterTriangleBuild(field, new Vector3(originX, 0f, originZ), _domain, reference);
            return null;
        }

        // Closed support: a block touching a certificate boundary participates.
        static bool Touches(double x0, double z0, double x1, double z1, Bounds[] tiles)
        {
            foreach (Bounds tile in tiles)
                if (x0 <= tile.MaxX && x1 >= tile.MinX && z0 <= tile.MaxZ && z1 >= tile.MinZ) return true;
            return false;
        }

        static Vertex Transform(Vector3 p, Matrix4x4 m)
        {
            double x = (double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31 + m.M41;
            double y = (double)p.X * m.M12 + (double)p.Y * m.M22 + (double)p.Z * m.M32 + m.M42;
            double z = (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33 + m.M43;
            double magnitude = Math.Abs((double)p.X) * (Math.Abs(m.M11) + Math.Abs(m.M12) + Math.Abs(m.M13)) +
                Math.Abs((double)p.Y) * (Math.Abs(m.M21) + Math.Abs(m.M22) + Math.Abs(m.M23)) +
                Math.Abs((double)p.Z) * (Math.Abs(m.M31) + Math.Abs(m.M32) + Math.Abs(m.M33)) +
                Math.Abs(m.M41) + Math.Abs(m.M42) + Math.Abs(m.M43);
            // The identity Y row evaluates y as exactly p.Y, otherwise y carries the transform slack.
            bool exactY = m.M12 == 0f && m.M22 == 1f && m.M32 == 0f && m.M42 == 0f;
            double slack = Binary64Slack * magnitude;
            return new Vertex(x, y, z, slack, exactY ? 0d : slack);
        }

        static double Slope(Vector3 a, Vector3 b, Vector3 c)
        {
            double dx = (double)b.X - a.X, dz = (double)b.Z - a.Z, ex = (double)c.X - a.X, ez = (double)c.Z - a.Z;
            double db = (double)b.Y - a.Y, dc = (double)c.Y - a.Y;
            double determinant = dx * ez - dz * ex;
            double gx = (db * ez - dc * dz) / determinant, gz = (dx * dc - ex * db) / determinant;
            // A small relative margin covers this binary64 gradient evaluation.
            return (Math.Abs(gx) + Math.Abs(gz)) * (1 + 1e-9);
        }
    }

    /// <summary>The exclusive index end of a range's whole triangles. A partial triangle is a producer defect and
    /// is refused, never trimmed.</summary>
    internal static int TriangleEnd(TileGroundSurfaceRange range) => range.IndexCount % 3 == 0
        ? range.FirstIndex + range.IndexCount
        : throw new InvalidOperationException(
            $"Surface range at index {range.FirstIndex} holds {range.IndexCount} indices, not whole triangles.");

    static float Down(double value)
    {
        float f = (float)value;
        return f > value ? MathF.BitDecrement(f) : f;
    }

    static float Up(double value)
    {
        float f = (float)value;
        return f < value ? MathF.BitIncrement(f) : f;
    }

    // The projected cross product as six binary32 products, each exact in binary64. A sum clear of its rounding
    // bound has an exact sign. Otherwise the products are summed exactly over their integer significands.
    internal static bool HasProjectedArea(Vector3 a, Vector3 b, Vector3 c)
    {
        Span<double> terms = stackalloc double[6]
        {
            (double)b.X * c.Z, (double)b.Z * a.X, (double)a.Z * c.X,
            -((double)b.X * a.Z), -((double)a.X * c.Z), -((double)b.Z * c.X),
        };
        double sum = 0, magnitude = 0;
        foreach (double term in terms)
        {
            sum += term;
            magnitude += Math.Abs(term);
        }
        if (Math.Abs(sum) > Binary64Slack * magnitude) return true;
        Span<(long Significand, int Exponent)> products = stackalloc (long, int)[6]
        {
            Product(b.X, c.Z, 1), Product(b.Z, a.X, 1), Product(a.Z, c.X, 1),
            Product(b.X, a.Z, -1), Product(a.X, c.Z, -1), Product(b.Z, c.X, -1),
        };
        int lowest = int.MaxValue;
        foreach ((long significand, int exponent) in products)
            if (significand != 0) lowest = Math.Min(lowest, exponent);
        if (lowest == int.MaxValue) return false;
        BigInteger exact = BigInteger.Zero;
        foreach ((long significand, int exponent) in products)
            if (significand != 0) exact += new BigInteger(significand) << (exponent - lowest);
        return !exact.IsZero;
    }

    static (long Significand, int Exponent) Product(float left, float right, int sign)
    {
        (long a, int ea) = Decompose(left);
        (long b, int eb) = Decompose(right);
        return (sign * a * b, ea + eb);
    }

    static (long Significand, int Exponent) Decompose(float value)
    {
        int bits = BitConverter.SingleToInt32Bits(value);
        int field = (bits >> 23) & 0xff;
        long significand = bits & 0x7fffff;
        int exponent = -149;
        if (field != 0)
        {
            significand |= 0x800000;
            exponent = field - 150;
        }
        return (bits < 0 ? -significand : significand, exponent);
    }
}
