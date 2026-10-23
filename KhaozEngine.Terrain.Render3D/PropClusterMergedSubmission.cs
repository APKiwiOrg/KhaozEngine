using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>One coverage descriptor for visible and shadow-only merged cluster draws.</summary>
internal static class PropClusterMergedSubmission
{
    public static void Draw(Scene3D scene, MeshHandle handle, PropLayer layer, float dissolve,
        bool complement, bool shadowOnly) => scene.Draw(new RigidInstanceDraw(handle, Matrix4x4.Identity)
        {
            Dissolve = dissolve,
            DissolveComplement = complement ? 1f : 0f,
            CastsShadows = layer.CastsShadows,
            ShadowOnly = shadowOnly,
        });
}
