#if WATER_PRISM_CENSUS
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.WaterPrismCensus;

// Double swept AABBs with outward representation and coverage-skin margins.
// Eligibility is geometry only. Full contact and precision output still needs its own proof.
internal static class WaterPrismBounds
{
    readonly record struct Bounds(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
    static Bounds Path(in MovementMediumSweepQuery query, Vector3 origin)
    {
        double x = (double)query.Body.Centre.X + origin.X, y = (double)query.Body.Centre.Y + origin.Y,
            z = (double)query.Body.Centre.Z + origin.Z;
        return new(Math.Min(x, x + query.Delta.X) - query.Body.Radius,
            Math.Min(y, y + query.Delta.Y) - query.Body.HalfHeight,
            Math.Min(z, z + query.Delta.Z) - query.Body.Radius,
            Math.Max(x, x + query.Delta.X) + query.Body.Radius,
            Math.Max(y, y + query.Delta.Y) + query.Body.HalfHeight,
            Math.Max(z, z + query.Delta.Z) + query.Body.Radius);
    }

    internal static bool Reject(TileWaterPrism piece, int face, in MovementMediumSweepQuery query, Vector3 origin)
    {
        var path = Path(query, origin);
        var triangle = piece.Triangle;
        double bedMin = Math.Min(triangle.A.Y, Math.Min(triangle.B.Y, triangle.C.Y)),
            bedMax = Math.Max(triangle.A.Y, Math.Max(triangle.B.Y, triangle.C.Y));
        double low = face == 4 ? piece.Column.Maximum.Y : bedMin;
        double high = face == 3 ? bedMax : piece.Column.Maximum.Y;
        double margin = piece.RepresentationError + MovementQueryLease.CoverageSkinMetres;
        return path.MaxY < low - margin || path.MinY > high + margin;
    }

    internal static (bool Dry, bool Submerged) Classify(TileWaterTraceTestSnapshot geometry,
        in MovementMediumSweepQuery query, Vector3 origin)
    {
        var path = Path(query, origin);
        double margin = MovementQueryLease.CoverageSkinMetres;
        bool any = false, sameSurface = true, dry = true;
        double surface = double.NaN, bed = double.NegativeInfinity;
        double minX = double.PositiveInfinity, minZ = minX, maxX = double.NegativeInfinity, maxZ = maxX;
        foreach (var piece in geometry.Prisms)
        {
            var c = piece.Column;
            minX = Math.Min(minX, c.Minimum.X); minZ = Math.Min(minZ, c.Minimum.Z);
            maxX = Math.Max(maxX, c.Maximum.X); maxZ = Math.Max(maxZ, c.Maximum.Z);
            if (path.MaxX < c.Minimum.X || path.MinX > c.Maximum.X || path.MaxZ < c.Minimum.Z || path.MinZ > c.Maximum.Z) continue;
            any = true;
            if (double.IsNaN(surface)) surface = c.Maximum.Y;
            sameSurface &= surface == c.Maximum.Y;
            bed = Math.Max(bed, Math.Max(piece.Triangle.A.Y, Math.Max(piece.Triangle.B.Y, piece.Triangle.C.Y)) + piece.RepresentationError);
            dry &= Reject(piece, -1, query, origin);
        }
        // Fixtures are one solid wet rectangle. Verify complete tile ownership instead of assuming a hull has no holes.
        bool filled = path.MinX > minX + margin && path.MaxX < maxX - margin &&
            path.MinZ > minZ + margin && path.MaxZ < maxZ - margin;
        for (int z = (int)Math.Floor(-path.MaxZ); filled && z <= (int)Math.Floor(-path.MinZ); z++)
            for (int x = (int)Math.Floor(path.MinX); filled && x <= (int)Math.Floor(path.MaxX); x++)
            {
                bool tile = false;
                foreach (var piece in geometry.Prisms) tile |= piece.TileX == x && piece.TileZ == z;
                filled &= tile;
            }
        return (!any || dry, any && filled && sameSurface && path.MinY > bed + margin && path.MaxY < surface - margin);
    }
}
#endif
