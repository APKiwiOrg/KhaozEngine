using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class UnderwaterSunlightElevationTests
{
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
