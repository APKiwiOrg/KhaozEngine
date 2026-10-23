using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Layer shadow-only submission with exactly the visible path's transforms, LOD and fade coverage.</summary>
internal static class PropClusterShadowSubmission
{
    public static int Draw(Scene3D scene, IReadOnlyList<PropPlacement> placements, PropLayer layer,
        Vector3 focus, float dissolveFloor) => Emit(placements, layer, focus, dissolveFloor, scene,
            static (target, mesh, world, dissolve, complement) => target.Draw(new RigidInstanceDraw(mesh, world)
            {
                ShadowOnly = true,
                Dissolve = dissolve,
                DissolveComplement = complement,
            }));

    internal static int Emit<T>(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
        float dissolveFloor, T target, Action<T, MeshHandle, Matrix4x4, float, float> sink)
    {
        if (!layer.CastsShadows) return 0;
        return layer.PartMeshes is { } parts
            ? PropRenderer.EmitParts(placements, parts, layer.LodPartMeshes, layer.LodDistance, focus,
                layer.DrawRadius, layer.FadeBandWidth, dissolveFloor, target, sink,
                lodCrossfadeWidth: layer.LodCrossfadeWidth)
            : PropRenderer.Emit(placements, layer.Meshes, layer.LodMeshes, layer.LodDistance, focus,
                layer.DrawRadius, layer.FadeBandWidth, dissolveFloor, target, sink,
                lodCrossfadeWidth: layer.LodCrossfadeWidth);
    }
}
