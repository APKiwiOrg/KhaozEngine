using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoverageGpuCamera : IIsoCamera3D
{
    public Matrix4x4 View { get; }
    public Matrix4x4 Projection { get; }
    public Matrix4x4 ViewProjection { get; }
    public Vector3 Eye { get; }
    public Vector3 Forward { get; }

    internal CoverageGpuCamera(IsoCamera3D source, bool independentVp)
    {
        View = Matrix4x4.CreateLookAt(source.Eye, source.Target, Vector3.UnitY);
        Projection = source.Projection;
        var projection = Projection;
        if (independentVp) projection.M11 *= .9f;
        ViewProjection = View * projection;
        Eye = source.Eye;
        Forward = source.Forward;
    }

    public bool WorldToScreen(Vector3 world, int width, int height, out Vector2 pixel) =>
        CameraProjection.WorldToScreen(ViewProjection, world, width, height, out pixel);
}
