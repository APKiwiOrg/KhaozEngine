using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Extrema through the joint zero clamp and linear-fractional weighting, without unrelated RGB maxima.</summary>
internal static class CoverageJointHistory
{
    internal static (CoverageRgbInterval Rgb, Vector2 Y) Bounds(Vector3 lo, Vector3 hi)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        float minY = float.MaxValue, maxY = float.MinValue, maxWeightedLuma = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 at = new((corner & 1) == 0 ? lo.X : hi.X, (corner & 2) == 0 ? lo.Y : hi.Y,
                (corner & 4) == 0 ? lo.Z : hi.Z);
            Visit(at);
            maxWeightedLuma = Math.Max(maxWeightedLuma, Vector3.Dot(Weighted(at), Rec709));
        }
        // Each RGB sign region is affine before fromWeighted. Within it the extrema are at vertices, including
        // where a channel becomes zero. The denominator floor adds one plane per sign region only if reached.
        Span<Plane> planes = stackalloc Plane[9];
        planes[0] = new(Vector3.UnitX, lo.X); planes[1] = new(Vector3.UnitX, hi.X);
        planes[2] = new(Vector3.UnitY, lo.Y); planes[3] = new(Vector3.UnitY, hi.Y);
        planes[4] = new(Vector3.UnitZ, lo.Z); planes[5] = new(Vector3.UnitZ, hi.Z);
        planes[6] = new(new(1, 1, -1), 0); planes[7] = new(new(1, 0, 1), 0); planes[8] = new(new(1, -1, -1), 0);
        bool signBoundary = lo.X + lo.Y - hi.Z < 0 || lo.X + lo.Z < 0 || lo.X - hi.Y - hi.Z < 0;
        if (signBoundary)
            for (int a = 0; a < 7; a++)
                for (int b = a + 1; b < 8; b++)
                    for (int c = Math.Max(b + 1, 6); c < 9; c++) Candidate(planes[a], planes[b], planes[c], -1);
        if (maxWeightedLuma >= 1 - 1e-4f)
            for (int signs = 0; signs < 8; signs++)
            {
                Vector3 n = Vector3.Zero;
                if ((signs & 1) != 0) n += Rec709.X * planes[6].Normal;
                if ((signs & 2) != 0) n += Rec709.Y * planes[7].Normal;
                if ((signs & 4) != 0) n += Rec709.Z * planes[8].Normal;
                var floor = new Plane(n, 1 - 1e-4f);
                for (int a = 0; a < 8; a++)
                    for (int b = a + 1; b < 9; b++) Candidate(planes[a], planes[b], floor, signs);
            }
        return (new(min, max), new(minY, maxY));

        void Candidate(Plane a, Plane b, Plane c, int signs)
        {
            var bc = Vector3.Cross(b.Normal, c.Normal);
            double d = Vector3.Dot(a.Normal, bc);
            if (d == 0) return;
            var ca = Vector3.Cross(c.Normal, a.Normal); var ab = Vector3.Cross(a.Normal, b.Normal);
            double x = (bc.X * a.Value + ca.X * b.Value + ab.X * c.Value) / d;
            double y = (bc.Y * a.Value + ca.Y * b.Value + ab.Y * c.Value) / d;
            double z = (bc.Z * a.Value + ca.Z * b.Value + ab.Z * c.Value) / d;
            if (x < lo.X || x > hi.X || y < lo.Y || y > hi.Y || z < lo.Z || z > hi.Z) return;
            Vector3 at = new((float)x, (float)y, (float)z);
            if (signs >= 0)
            {
                Vector3 rgb = Rgb(at);
                if ((rgb.X >= 0) != ((signs & 1) != 0) || (rgb.Y >= 0) != ((signs & 2) != 0)
                    || (rgb.Z >= 0) != ((signs & 4) != 0)) return;
            }
            Visit(at);
        }

        void Visit(Vector3 at)
        {
            Vector3 rgb = TemporalResolveMath.FromWeighted(Weighted(at));
            Vector3 weighted = TemporalResolveMath.ToWeighted(rgb);
            float y = .25f * weighted.X + .5f * weighted.Y + .25f * weighted.Z;
            min = Vector3.Min(min, rgb); max = Vector3.Max(max, rgb);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
    }

    internal static readonly Vector3 Rec709 = new(.2126f, .7152f, .0722f);
    internal static Vector3 Rgb(Vector3 c) => new(c.X + c.Y - c.Z, c.X + c.Z, c.X - c.Y - c.Z);
    static Vector3 Weighted(Vector3 c) => Vector3.Max(Rgb(c), Vector3.Zero);
    readonly record struct Plane(Vector3 Normal, double Value);
}
