using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point caster walk as it stood before <see cref="PointCasterIndex"/>, kept here as the oracle the index is
/// compared against. It visits every caster for every light, which is the cost the index removes, and it is the
/// definition of the right answer: the index must return exactly these slots in exactly this order.
/// </summary>
internal static class PointCasterFullWalk
{
    /// <summary>One caster as the index sees it: its instance slot and its world bounding sphere.</summary>
    internal readonly record struct Caster(int Slot, Vector3 Centre, float Radius);

    /// <summary>Every caster that touches the light's shadowing shell, in the order given, which is ascending slot
    /// order because the scene's run walk adds casters that way.</summary>
    internal static List<int> Slots(IReadOnlyList<Caster> casters, Vector3 light, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        var slots = new List<int>();
        foreach (Caster caster in casters)
            if (Scene3D.InstanceTouchesLight(caster.Centre, caster.Radius, light, radius, nearRadius,
                exclusionMin, exclusionMax))
                slots.Add(caster.Slot);
        return slots;
    }

    /// <summary>
    /// The pre-index <c>Scene3D.BuildPointCasterSpans</c>, copied line for line apart from reading the scene's state
    /// from arguments: the queue it grouped, each mesh's local bounds (null for a stale or unloaded handle, which
    /// stands for both of the scene's handle checks) and the terrain flag. It regroups the queue with the scene's
    /// own <c>GroupInstances</c>, so its slots are the scene's slots when frustum culling is off.
    /// </summary>
    internal static List<Scene3D.ShadowCasterSpan> Spans(IReadOnlyList<SceneInstances.Instance> items,
        Func<MeshHandle, MeshBounds?> boundsOf, bool terrainCastsShadows, Vector3 lightPosAbsolute, float radius,
        float nearRadius, Vector3 exclusionMin, Vector3 exclusionMax)
    {
        var instanceData = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var instanceCastKinds = new List<ShadowCastKind>();
        Scene3D.GroupInstances(items, instanceData, runs, castKinds: instanceCastKinds);

        var spans = new List<Scene3D.ShadowCasterSpan>();
        var pointCasterKinds = new List<ShadowCastKind>();
        if (instanceData.Count == 0) return spans;
        for (int i = 0; i < instanceData.Count; i++)
            pointCasterKinds.Add(i < instanceCastKinds.Count ? instanceCastKinds[i] : ShadowCastKind.Opaque);

        foreach (Scene3D.MeshRun run in runs)
        {
            if (boundsOf(run.Mesh) is not { } bounds) continue;
            // Every mesh the tests load is a plain model mesh, splat material -1.
            if (!Scene3D.MeshCastsShadows(-1, terrainCastsShadows)) continue;
            for (uint s = 0; s < run.Count; s++)
            {
                int slot = (int)(run.Start + s);
                if (slot >= pointCasterKinds.Count) break;
                if (pointCasterKinds[slot] == ShadowCastKind.None) continue;
                if (!Scene3D.InstanceTouchesLight(bounds, instanceData[slot].Model, lightPosAbsolute, radius,
                    nearRadius, exclusionMin, exclusionMax))
                    pointCasterKinds[slot] = ShadowCastKind.None;
            }
            Scene3D.AppendCasterSpans(run.Mesh.Index, run.Mesh.Generation, run.Start, run.Count,
                pointCasterKinds, spans);
        }
        return spans;
    }
}
