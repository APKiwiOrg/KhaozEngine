using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>A world-anchored surface lattice following the visible water rather than a distant camera eye.</summary>
internal readonly record struct WaterSurfaceWindow(float MinX, float MinZ)
{
    internal const int Cells = 512;
    internal const float CellMetres = 0.125f, MapTexelMetres = 0.0625f, ExtentMetres = 64f,
        FadeMetres = 4f, NearReachMetres = 28f;

    internal static WaterSurfaceWindow Place(Vector3 eyeRender, Vector3 forward, Matrix4x4 viewProjection,
        bool orthographic, float surfaceY, Vector3 renderOrigin)
    {
        bool centreHit = TryHit(eyeRender, forward, surfaceY, out double centreX, out double centreZ);
        double nearX = eyeRender.X, nearZ = eyeRender.Z;
        if (!orthographic)
        {
            if (Matrix4x4.Invert(viewProjection, out var inverse))
            {
                var near = Vector4.Transform(new Vector4(0f, -1f, 0f, 1f), inverse);
                var far = Vector4.Transform(new Vector4(0f, -1f, 1f, 1f), inverse);
                if (near.W != 0f && far.W != 0f)
                {
                    var direction = new Vector3(far.X, far.Y, far.Z) / far.W
                        - new Vector3(near.X, near.Y, near.Z) / near.W;
                    if (TryHit(eyeRender, direction, surfaceY, out double x, out double z))
                    {
                        nearX = x;
                        nearZ = z;
                    }
                }
            }

            double headingLength = Math.Sqrt((double)forward.X * forward.X + (double)forward.Z * forward.Z);
            double distance = centreHit
                ? Math.Sqrt((centreX - nearX) * (centreX - nearX) + (centreZ - nearZ) * (centreZ - nearZ))
                : NearReachMetres;
            double reach = Math.Min(distance, NearReachMetres);
            centreX = nearX + (headingLength > 0d ? forward.X / headingLength * reach : 0d);
            centreZ = nearZ + (headingLength > 0d ? forward.Z / headingLength * reach : 0d);
        }
        else if (!centreHit)
        {
            centreX = eyeRender.X;
            centreZ = eyeRender.Z;
        }

        double minX = SnapDown(centreX + renderOrigin.X - ExtentMetres / 2d);
        double minZ = SnapDown(centreZ + renderOrigin.Z - ExtentMetres / 2d);
        if (!orthographic)
        {
            // Snapping must not move the near hit into the outer fade band.
            minX = KeepNearInside(minX, nearX + renderOrigin.X);
            minZ = KeepNearInside(minZ, nearZ + renderOrigin.Z);
        }
        return new((float)(minX - renderOrigin.X), (float)(minZ - renderOrigin.Z));
    }

    static double SnapDown(double value) => Math.Floor(value / CellMetres) * CellMetres;

    static double KeepNearInside(double minimum, double near) => Math.Clamp(minimum,
        Math.Ceiling((near - (ExtentMetres - FadeMetres)) / CellMetres) * CellMetres,
        SnapDown(near - FadeMetres));

    static bool TryHit(Vector3 eye, Vector3 direction, float surfaceY, out double x, out double z)
    {
        x = z = 0d;
        if (direction.Y == 0f) return false;
        double t = ((double)surfaceY - eye.Y) / direction.Y;
        if (!double.IsFinite(t) || t < 0d) return false;
        x = eye.X + direction.X * t;
        z = eye.Z + direction.Z * t;
        return double.IsFinite(x) && double.IsFinite(z);
    }
}
