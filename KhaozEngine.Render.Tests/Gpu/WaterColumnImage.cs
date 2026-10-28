using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Linear pre-post RGB and separate final bytes, with render-relative pixel-centre ray geometry.</summary>
public sealed class WaterColumnImage(byte[] rgba, float[] linearRgb, Matrix4x4 viewProjection,
    WaterColumnDiagnostics diagnostics, WaterColumnCase settings, float[] copiedRgb, float[] resolvedRgb)
{
    public byte[] Rgba { get; } = rgba;
    public float[] LinearRgb { get; } = linearRgb;
    public Matrix4x4 ViewProjection { get; } = viewProjection;
    public WaterColumnDiagnostics Diagnostics { get; } = diagnostics;
    public WaterColumnCase Settings { get; } = settings;
    public float[] CopiedRgb { get; } = copiedRgb;
    public float[] ResolvedRgb { get; } = resolvedRgb;
    public int Size => Settings.Size;

    public Vector2 Project(Vector3 world)
    {
        Vector4 p = Vector4.Transform(new Vector4(world - Settings.Origin, 1f), ViewProjection);
        Assert.True(p.W > 0f, "The probe must be in front of the camera.");
        return new Vector2((p.X / p.W + 1f) * Size * 0.5f, (1f - p.Y / p.W) * Size * 0.5f);
    }

    public Vector3 Pixel(int x, int y)
    {
        Assert.InRange(x, 0, Size - 1);
        Assert.InRange(y, 0, Size - 1);
        int i = (y * Size + x) * 3;
        return new Vector3(LinearRgb[i], LinearRgb[i + 1], LinearRgb[i + 2]);
    }

    public Vector3 ResolvedPixel(int x, int y)
    {
        Assert.InRange(x, 0, Size - 1);
        Assert.InRange(y, 0, Size - 1);
        int i = (y * Size + x) * 3;
        return new Vector3(ResolvedRgb[i], ResolvedRgb[i + 1], ResolvedRgb[i + 2]);
    }

    public Vector3 Sample(Vector3 world) => SamplePixels(world, resolved: false);

    public Vector3 ResolvedSample(Vector3 world) => SamplePixels(world, resolved: true);

    Vector3 SamplePixels(Vector3 world, bool resolved)
    {
        Vector2 p = Project(world);
        Vector3 sum = Vector3.Zero;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                sum += resolved ? ResolvedPixel((int)p.X + dx, (int)p.Y + dy) : Pixel((int)p.X + dx, (int)p.Y + dy);
        return sum / 9f;
    }

    public Vector3 Incident(int x, int y)
    {
        Assert.True(Matrix4x4.Invert(ViewProjection, out Matrix4x4 inverse));
        Vector2 xy = new((x + 0.5f) / Size * 2f - 1f, 1f - (y + 0.5f) / Size * 2f);
        Vector4 a = Vector4.Transform(new Vector4(xy, 0f, 1f), inverse);
        Vector4 b = Vector4.Transform(new Vector4(xy, 0.5f, 1f), inverse);
        return Vector3.Normalize(new Vector3(b.X, b.Y, b.Z) / b.W - new Vector3(a.X, a.Y, a.Z) / a.W);
    }

    public Vector3 SurfacePoint(int x, int y)
    {
        Assert.True(Matrix4x4.Invert(ViewProjection, out Matrix4x4 inverse));
        Vector4 a = Vector4.Transform(new Vector4((x + 0.5f) / Size * 2f - 1f,
            1f - (y + 0.5f) / Size * 2f, 0f, 1f), inverse);
        Vector3 near = new Vector3(a.X, a.Y, a.Z) / a.W;
        Vector3 incident = Incident(x, y);
        return near - incident * (near.Y / incident.Y);
    }
}
