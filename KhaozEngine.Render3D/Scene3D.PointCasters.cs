using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// WHICH RIGID INSTANCES CAN CAST INTO A POINT LIGHT THIS FRAME, answered once per frame into a
/// <see cref="PointCasterIndex"/> and then asked per light (issue #1110). Both halves of the point shadow ask here:
/// the static signature in <c>Scene3D.PointShadows.cs</c> and the spans the pass in <c>Scene3D.PointShadowPass.cs</c>
/// draws, static and dynamic rows alike. One answer for both is what keeps a signature from missing a change the
/// pass would have drawn.
/// <para>
/// BUILT LAZILY, ONCE PER POINT-SHADOW FRAME. <c>PreparePointShadows</c> builds it before the first signature, and
/// every query goes through the same guard, so the diagnostic entry points that render a row outside a frame's
/// request (<see cref="DebugRenderPointShadowSlot"/>, or a test driving <see cref="RenderPointShadowSlots"/>
/// directly) read an index built from the instances they are drawing rather than an older one.
/// </para>
/// </summary>
public sealed partial class Scene3D
{
    readonly PointCasterIndex _pointCasterIndex = new();

    // One light's touching casters, ascending by slot. Shared by the signature and the pass, which never hold it
    // at the same time.
    readonly List<int> _pointCasterHits = new();

    // The point-shadow frame the index was last built for. Starts at -1 so a scene that has never rendered still
    // builds on its first ask.
    int _pointCasterIndexFrame = -1;

    /// <summary>How many exact touch tests the point caster queries ran on the last built frame. Internal for the
    /// count tests, which assert it follows the casters near each light rather than every instance.</summary>
    internal int PointCasterTouchTests => _pointCasterIndex.TouchTests;

    /// <summary>
    /// Build this point-shadow frame's caster index, once. Every rigid slot the point pass could draw goes in with
    /// its world bounding sphere, under exactly the rules the pass has always applied: a run whose handle is stale or
    /// whose mesh is gone is skipped, a splat mesh casts only under <see cref="TerrainCastsShadows"/>, and an instance
    /// classified <see cref="ShadowCastKind.None"/> never casts. The sphere is the same
    /// <see cref="MeshBounds.WorldSphere"/> call on the same inputs the touch test used to make once per light.
    /// </summary>
    void EnsurePointCasterIndex()
    {
        if (_pointCasterIndexFrame == _pointShadowFrame) return;
        _pointCasterIndexFrame = _pointShadowFrame;

        // Read by reference: an InstanceData is 128 bytes and only its matrix is wanted here.
        Span<ModelRenderer.InstanceData> instances = CollectionsMarshal.AsSpan(_instanceData);
        _pointCasterIndex.Reset(instances.Length);
        for (int r = 0; r < _runs.Count; r++)
        {
            MeshRun run = _runs[r];
            if (!_slots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
            var m = _meshes[run.Mesh.Index];
            if (m is not { } mesh) continue;
            if (!MeshCastsShadows(mesh.SplatMaterial, TerrainCastsShadows)) continue;
            for (uint s = 0; s < run.Count; s++)
            {
                int slot = (int)(run.Start + s);
                if (slot >= instances.Length) break;
                if (PointCasterKind(slot) == ShadowCastKind.None) continue;
                mesh.Bounds.WorldSphere(instances[slot].Model, out Vector3 centre, out float radius);
                _pointCasterIndex.Add(slot, r, centre, radius);
            }
        }
        _pointCasterIndex.Seal();
    }

    /// <summary>Fill <see cref="_pointCasterHits"/> with the casters that touch one light, ascending by slot,
    /// building the index first when this frame has not.</summary>
    void QueryPointCasters(Vector3 lightPosAbsolute, float radius, float nearRadius,
        Vector3 exclusionMin, Vector3 exclusionMax)
    {
        EnsurePointCasterIndex();
        _pointCasterIndex.Query(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax, _pointCasterHits);
    }

    /// <summary>A slot's cast kind, with the unclassified default the grouping has always implied.</summary>
    ShadowCastKind PointCasterKind(int slot) =>
        slot < _instanceCastKinds.Count ? _instanceCastKinds[slot] : ShadowCastKind.Opaque;

    /// <summary>
    /// Group <see cref="_pointCasterHits"/> into caster spans: maximal stretches of consecutive slots in one mesh run
    /// with one cast kind, in slot order. That is what <see cref="AppendCasterSpans"/> makes of a run whose every
    /// missed slot reads <see cref="ShadowCastKind.None"/>, which is how the pass built its spans before the index,
    /// without visiting a slot the light does not touch.
    /// </summary>
    void AppendPointCasterSpans(List<ShadowCasterSpan> spans)
    {
        int run = -1;
        uint start = 0, count = 0;
        ShadowCastKind kind = ShadowCastKind.None;
        foreach (int slot in _pointCasterHits)
        {
            int slotRun = _pointCasterIndex.RunOf(slot);
            ShadowCastKind slotKind = PointCasterKind(slot);
            if (count > 0 && slotRun == run && slotKind == kind && (uint)slot == start + count)
            {
                count++;
                continue;
            }
            if (count > 0) AddPointCasterSpan(spans, run, start, count, kind);
            run = slotRun;
            start = (uint)slot;
            count = 1;
            kind = slotKind;
        }
        if (count > 0) AddPointCasterSpan(spans, run, start, count, kind);
    }

    void AddPointCasterSpan(List<ShadowCasterSpan> spans, int run, uint start, uint count, ShadowCastKind kind)
    {
        MeshHandle mesh = _runs[run].Mesh;
        spans.Add(new ShadowCasterSpan(mesh.Index, mesh.Generation, start, count, kind));
    }

    /// <summary>Diagnostic: this light's caster spans as the pass would draw them now. For the equivalence tests,
    /// which compare them with the walk over every instance. The list is the pass's own scratch and the next build
    /// overwrites it.</summary>
    internal IReadOnlyList<ShadowCasterSpan> DebugPointCasterSpans(Vector3 lightPosAbsolute, float radius,
        float nearRadius = 0f, Vector3 exclusionMin = default, Vector3 exclusionMax = default)
    {
        BuildPointCasterSpans(lightPosAbsolute, radius, nearRadius, exclusionMin, exclusionMax);
        return _pointCasterSpans;
    }
}
