using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>Per-frame cascade foliage policy. Animated content never enters the rigid atlas signature.</summary>
internal static class FoliageShadowSelection
{
    internal static bool NeedsDepthPass(bool anyCurrent, bool anyPrevious) => anyCurrent || anyPrevious;

    internal static bool IntersectsCascade(in MeshBounds bounds, in FrustumPlanes cascade, Vector3 renderOrigin = default)
    {
        // Directional depth vertices pancake the near plane, so only XY and the far plane can reject a caster.
        for (int i = 0; i < 6; i++)
        {
            if (i == 4) continue;
            Vector4 plane = cascade[i];
            Vector3 positive = new(
                plane.X >= 0f ? bounds.Max.X : bounds.Min.X,
                plane.Y >= 0f ? bounds.Max.Y : bounds.Min.Y,
                plane.Z >= 0f ? bounds.Max.Z : bounds.Min.Z);
            if (renderOrigin != Vector3.Zero) positive -= renderOrigin;
            if (Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), positive) + plane.W < 0f) return false;
        }
        return true;
    }
}
