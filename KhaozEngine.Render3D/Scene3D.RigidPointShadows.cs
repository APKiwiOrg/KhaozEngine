using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly List<ShadowCasterMobility> _instanceShadowMobilities = new();
    readonly PointRigidShadowLayers _pointRigidLayers = new();
    PointRigidBaseWork[] _pointRigidBaseWork = Array.Empty<PointRigidBaseWork>();
    int _pointStaticTransientRigidDrawCallsThisFrame;
    int _pointStaticFallbackSkinnedDrawCallsThisFrame;
    int _pointRigidFallbackRowsThisFrame;
    int _pointRigidTransitionRowsThisFrame;

    bool PointCasterInSet(int slot, PointShadowCasterSet set) =>
        ((slot < _instanceShadowMobilities.Count ? _instanceShadowMobilities[slot] : ShadowCasterMobility.Static)
            == ShadowCasterMobility.Dynamic ? (set & PointShadowCasterSet.MovingRigid) != 0
            : (set & PointShadowCasterSet.StaticRigid) != 0);

    // A moving actor can introduce a shared mesh first and reorder runs when its mesh changes. Sum independent
    // per-run hashes so its routing never changes the static signature through that first-seen ordering.
    void MixStaticRigidPointCasters(ref ulong hash)
    {
        Span<ModelRenderer.InstanceData> instances = CollectionsMarshal.AsSpan(_instanceData);
        ulong combined = 0, runHash = PointSignatureSeed;
        int previousRun = -1, casterCount = 0;
        foreach (int slot in _pointCasterHits)
        {
            if (!PointCasterInSet(slot, PointShadowCasterSet.StaticRigid)) continue;
            casterCount++;
            int run = _pointCasterIndex.RunOf(slot);
            if (run != previousRun)
            {
                if (previousRun >= 0) combined = unchecked(combined + runHash);
                runHash = PointSignatureSeed;
                previousRun = run;
            }
            MeshHandle mesh = _runs[run].Mesh;
            ref ModelRenderer.InstanceData data = ref instances[slot];
            MixPointSignature(ref runHash, SignatureWord((uint)mesh.Index, (uint)mesh.Generation));
            MixPointSignature(ref runHash, SignatureWord(PointDissolveWord(data), (uint)PointCasterKind(slot)));
            MixPointSignature(ref runHash, data.Model);
        }
        if (previousRun >= 0) combined = unchecked(combined + runHash);
        MixPointSignature(ref hash, combined);
        MixPointSignature(ref hash, (ulong)casterCount);
    }

    bool PointRequestTouchesMovingRigid(in PointShadowRequest request)
    {
        QueryPointCasters(request.Position, request.Radius, request.NearRadius,
            request.ExclusionMin, request.ExclusionMax);
        foreach (int slot in _pointCasterHits)
            if (PointCasterInSet(slot, PointShadowCasterSet.MovingRigid)) return true;
        return false;
    }

    void PrepareRigidPointBaseRows(PointShadowSlots cache)
    {
        if (_pointRigidBaseWork.Length < _pointRequests.Count)
            Array.Resize(ref _pointRigidBaseWork, _pointRequests.Count);
        Array.Clear(_pointRigidBaseWork);
        for (int i = 0; i < _pointRequests.Count; i++)
        {
            PointShadowRequest request = _pointRequests[i];
            if (request.Slot < 0 || request.Mode != LightShadowMode.Static
                || !cache.EverRendered(request.Slot) || !cache.IsDirty(request.Slot)) continue;
            // Eligibility must not advance previous demand before the final layer plan reads it.
            if (_pointRigidLayers.RequiresTransition(request.Slot, request.Key,
                _pointCasterSignatures[request.Slot], cache.IsDirty(request.Slot), PointRequestTouchesMovingRigid(request)))
                _pointRigidBaseWork[i] = PointRigidBaseWork.Transition;
        }
    }

    void ScheduleRigidPointBaseRows(PointShadowSlots cache)
    {
        _pointStaticTransientRigidDrawCallsThisFrame = 0;
        _pointStaticFallbackSkinnedDrawCallsThisFrame = 0;
        _pointRigidFallbackRowsThisFrame = 0;
        _pointRigidTransitionRowsThisFrame = 0;
        for (int i = 0; i < _pointRequests.Count; i++)
        {
            PointShadowRequest request = _pointRequests[i];
            if (request.Slot < 0 || request.Mode != LightShadowMode.Static) continue;
            bool selected = _pointRebuilds.Contains(i);
            if (!cache.EverRendered(request.Slot) && !selected) continue;
            PointRigidBaseWork work = _pointRigidLayers.Plan(request.Slot, request.Key,
                cache.EverRendered(request.Slot), _pointCasterSignatures[request.Slot],
                cache.IsDirty(request.Slot), PointRequestTouchesMovingRigid(request), PointRequestTouchesSkinned(request),
                _pointTransientRowsByRequest[i] >= 0);
            _pointRigidBaseWork[i] = work;
            if (work == PointRigidBaseWork.None) continue;
            if (!selected) _pointRebuilds.Add(i);
            if (work == PointRigidBaseWork.Fallback) _pointRigidFallbackRowsThisFrame++;
            else _pointRigidTransitionRowsThisFrame++;
        }
    }

    PointShadowCasterSet PointBaseCasterSet(int requestIndex)
    {
        if (_pointRequests[requestIndex].Mode == LightShadowMode.Static)
            return _pointRigidBaseWork[requestIndex] == PointRigidBaseWork.Fallback
                ? PointShadowCasterSet.Rigid | PointShadowCasterSet.Skinned
                : PointShadowCasterSet.StaticRigid;
        return PointShadowCasterSet.Rigid
            | (_pointDynamicSkinnedRequests[requestIndex] ? PointShadowCasterSet.Skinned : 0);
    }

    void RecordPointBaseContent(int requestIndex)
    {
        PointShadowRequest request = _pointRequests[requestIndex];
        if (request.Mode != LightShadowMode.Static) return;
        _pointRigidLayers.Rendered(request.Slot, _pointCasterSignatures[request.Slot],
            _pointRigidBaseWork[requestIndex] == PointRigidBaseWork.Fallback);
    }
}
