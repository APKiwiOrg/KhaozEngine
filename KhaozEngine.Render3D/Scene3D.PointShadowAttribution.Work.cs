using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void ObservePointAttributionSelection()
    {
        if (_pointAttribution is not { } capture || !_pointAttributionFrameActive) return;
        for (int i = 0; i < _pointRebuilds.Count; i++)
        {
            int index = AttributionIndex(_pointRebuilds[i]);
            var row = capture.PendingRow(index);
            row.SelectedOrdinal = i;
            capture.UpdateRow(index, row);
        }
        for (int i = 0; i < _selectedPointTransientRequests.Count; i++)
        {
            int request = _selectedPointTransientRequests[i], index = AttributionIndex(request);
            var row = capture.PendingRow(index);
            row.TransientSlot = _pointTransientRowsByRequest[request];
            row.TransientSelectedOrdinal = _pointRebuilds.Count + i;
            capture.UpdateRow(index, row);
        }
    }

    void ObservePointAttributionWork(in PackedPointShadowSlot packed, int draws,
        in ShadowStageProbeCounts before)
    {
        if (_pointAttribution is not { } capture || !_pointAttributionFrameActive) return;
        int index = AttributionIndex(packed.RequestIndex);
        if (index < 0) return;
        var row = capture.PendingRow(index);
        int foliage = FoliagePointShadowDrawCalls - before.PointFoliageFaceDrawCalls;
        int dynamic = _pointDynamicSkinnedDrawCallsThisFrame - before.PointDynamicSkinnedFaceDrawCalls;
        int transient = _pointStaticTransientSkinnedDrawCallsThisFrame - before.PointTransientSkinnedFaceDrawCalls;
        row.RigidDraws += draws - foliage - dynamic - transient;
        row.FoliageDraws += foliage; row.DynamicSkinnedDraws += dynamic; row.TransientSkinnedDraws += transient;
        row.RigidInstances += _shadowProbeCounts.PointRigidIssuedInstances - before.PointRigidIssuedInstances;
        row.FoliageInstances += _shadowProbeCounts.PointFoliageIssuedInstances - before.PointFoliageIssuedInstances;
        if (ReferenceEquals(packed.Atlas, _pointShadowTransientAtlas)) row.TransientCompleted = true;
        else row.Completed = true;
        capture.UpdateRow(index, row);
    }

    ShadowStageProbeCounts PointAttributionWorkBoundary() => _shadowProbeCounts with
    {
        PointFoliageFaceDrawCalls = FoliagePointShadowDrawCalls,
        PointDynamicSkinnedFaceDrawCalls = _pointDynamicSkinnedDrawCallsThisFrame,
        PointTransientSkinnedFaceDrawCalls = _pointStaticTransientSkinnedDrawCallsThisFrame,
    };
}
