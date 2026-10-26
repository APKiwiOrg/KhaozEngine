using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class UnderwaterSunlightElevationTests
{
    public static IEnumerable<object[]> WidenedEntryElevations()
    {
        foreach (var sample in UnderwaterEntryKernelOracle.Elevations)
            yield return new object[] { sample.Radians, sample.Radius };
    }

    [Theory]
    [MemberData(nameof(WidenedEntryElevations))]
    public void WidenedEntryAgreesWithDoublePrecisionThroughHorizontalLight(double radians, float radius)
    {
        Matrix4x4 matrix = UnderwaterEntryKernelOracle.Matrix(radians, radius);
        foreach (var tap in UnderwaterEntryKernelOracle.Taps())
            foreach (float stored in new[] { 0.25f, 0.5f, 0.75f, 1f })
            {
                const float depth = 0.5f;
                var actual = EntryComparison(matrix, tap.Wide - tap.Dry, stored, depth);
                var expected = UnderwaterEntryKernelOracle.Compare(matrix, tap.Wide - tap.Dry, stored, depth);
                Assert.True(float.IsFinite(actual.Height), $"Entry height is {actual.Height} at {radians} radians, radius {radius}.");
                Assert.InRange(Math.Abs(actual.Height - expected.Height), 0d,
                    Math.Max(1e-44d, Math.Abs(expected.Height) * 2e-5d));
                Assert.Equal(expected.Lit, actual.Lit);
            }
    }

    // Float32 mirror of the shipped entry comparison, checked against a general double-precision inverse.
    static (float Height, bool Lit) EntryComparison(Matrix4x4 m, Vector2 deltaUv, float stored, float depth)
    {
        Vector3 rowX = new(m.M11, m.M21, m.M31), rowY = new(m.M12, m.M22, m.M32);
        Vector3 rowZ = new(m.M13, m.M23, m.M33);
        Vector3 worldYPerClip = new(rowX.Y / rowX.LengthSquared(), rowY.Y / rowY.LengthSquared(),
            rowZ.Y / rowZ.LengthSquared());
        float height = Vector3.Dot(worldYPerClip, new Vector3(deltaUv * new Vector2(2f, -2f), stored - depth));
        bool lit = height == 0f ? depth <= stored : rowZ.Y > 0f ? height > 0f : height < 0f;
        return (height, stored >= 1f || lit);
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(35f)]
    [InlineData(1f)]
    [InlineData(0f)]
    public void StoredPointHeightAndKeyOutputsStayFiniteAtEveryElevation(float elevation)
    {
        float radians = elevation * MathF.PI / 180f;
        Vector3 light = elevation == 0f ? Vector3.UnitZ : new Vector3(0f, -MathF.Sin(radians), MathF.Cos(radians));
        Matrix4x4 matrix = ShadowMapMath.BuildLightViewProj(light, Vector3.Zero, 8f, 2048);
        Vector3 rowX = new(matrix.M11, matrix.M21, matrix.M31);
        Vector3 rowY = new(matrix.M12, matrix.M22, matrix.M32);
        Vector3 rowZ = new(matrix.M13, matrix.M23, matrix.M33);
        Vector3 worldYPerClip = new(rowX.Y / rowX.LengthSquared(), rowY.Y / rowY.LengthSquared(),
            rowZ.Y / rowZ.LengthSquared());
        const float eta = 1f / 1.333f;
        Vector3 travel = new(0f, -MathF.Sqrt(1f - eta * eta * light.Z * light.Z), eta * light.Z);
        Vector3 receiver = new(0.2f, -1.3f, 0.1f);
        float path = -receiver.Y / -travel.Y;
        Vector3 entry = receiver - travel * path;
        Vector3 end = Vector3.Lerp(entry, receiver, 0.6f);
        Vector4 clip = Vector4.Transform(new Vector4(end, 1f), matrix);
        Vector2 uv = new(clip.X * 0.5f + 0.5f, 0.5f - clip.Y * 0.5f);
        Vector2 tap = new((MathF.Floor(uv.X * 2048f) + 0.5f) / 2048f,
            (MathF.Floor(uv.Y * 2048f) + 0.5f) / 2048f);
        Vector2 snap = (tap - uv) * new Vector2(2f, -2f);
        float stored = clip.Z - 0.002f;
        float tapEndY = end.Y + Vector2.Dot(new Vector2(worldYPerClip.X, worldYPerClip.Y), snap);
        float storedY = tapEndY + (stored - clip.Z) * worldYPerClip.Z;
        float bandTopY = tapEndY + 0.25f * -receiver.Y;
        Assert.True(Matrix4x4.Invert(matrix, out Matrix4x4 inverse));
        Vector4 independent = Vector4.Transform(new Vector4(tap.X * 2f - 1f, 1f - tap.Y * 2f, stored, 1f), inverse);
        Assert.InRange(MathF.Abs(storedY - independent.Y / independent.W), 0f, 0.0001f);
        foreach (float value in new[] { worldYPerClip.X, worldYPerClip.Y, worldYPerClip.Z,
            path, entry.X, entry.Y, entry.Z, tapEndY, storedY, bandTopY })
            Assert.True(float.IsFinite(value), $"Elevation {elevation} produced {value}.");
        bool occluded = stored < clip.Z && storedY < 0f && storedY >= tapEndY && storedY <= bandTopY;
        Assert.True(occluded, $"The stored caster must belong to the current band at {elevation} degrees.");
        float visibility = occluded ? 0f : 1f;
        float transmittance = MathF.Exp(-0.3f * path);
        float keyColor = 0.65f * visibility * transmittance;
        Assert.True(float.IsFinite(visibility) && float.IsFinite(transmittance) && float.IsFinite(keyColor));
    }
}

internal static class UnderwaterEntryKernelOracle
{
    internal static readonly (double Radians, float Radius)[] Elevations =
    {
        (Math.PI / 2d, 8f), (35d * Math.PI / 180d, 8f), (1e-3d, 8f),
        (1e-6d, 8f), (1e-9d, 8f), (0d, 8f), (1e-37d, 0.001f)
    };

    internal static Matrix4x4 Matrix(double radians, float radius)
    {
        Vector3 light = radians == 1e-37d ? new Vector3(0f, -1e-37f, 1f)
            : new Vector3(0f, -(float)Math.Sin(radians), (float)Math.Cos(radians));
        return ShadowMapMath.BuildLightViewProj(light, Vector3.Zero, radius, 2048);
    }

    // Independent inverse of the matrix's general 3x3 linear part, not the shader's orthogonal-row shortcut.
    internal static (double Height, bool Lit) Compare(Matrix4x4 m, Vector2 deltaUv, float stored, float depth)
    {
        double a = m.M11, b = m.M12, c = m.M13, d = m.M21, e = m.M22, f = m.M23;
        double g = m.M31, h = m.M32, i = m.M33;
        double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        double inverse12 = (c * h - b * i) / determinant;
        double inverse22 = (a * i - c * g) / determinant;
        double inverse32 = (b * g - a * h) / determinant;
        double height = 2d * deltaUv.X * inverse12 - 2d * deltaUv.Y * inverse22
            + ((double)stored - depth) * inverse32;
        bool lit = height == 0d ? depth <= stored : f > 0d ? height > 0d : height < 0d;
        return (height, stored >= 1f || lit);
    }

    internal static IEnumerable<(Vector2 Dry, Vector2 Wide)> Taps()
    {
        const float step = 1f / 32f;
        static float Noise(Vector2 p)
        {
            float inner = Vector2.Dot(p, new Vector2(0.06711056f, 0.00583715f));
            float outer = 52.9829189f * (inner - MathF.Floor(inner));
            return outer - MathF.Floor(outer);
        }
        Vector2 jitter = (new Vector2(Noise(new Vector2(19f, 47f)), Noise(new Vector2(73f, 11f)))
            - new Vector2(0.5f)) * 0.5f;
        // Concentric mapping of the 4x4 cell centres. The production kernel rotates it by -pi at pixel zero.
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                float u = (x + 0.5f) * 0.5f - 1f, v = (y + 0.5f) * 0.5f - 1f;
                float r = MathF.Abs(u) > MathF.Abs(v) ? u : v;
                float angle = MathF.Abs(u) > MathF.Abs(v) ? MathF.PI / 4f * v / u
                    : MathF.PI / 2f - MathF.PI / 4f * u / v;
                Vector2 spread = -new Vector2(r * MathF.Cos(angle), r * MathF.Sin(angle)) * 8f + jitter;
                Vector2 wide = (new Vector2(MathF.Floor(16f + spread.X), MathF.Floor(16f + spread.Y))
                    + new Vector2(0.5f)) * step;
                Vector2 dry = (new Vector2(14.5f + x, 14.5f + y)) * step;
                yield return (dry, wide);
            }
        yield return (new Vector2(0.5f), new Vector2(0.5f));
    }
}
