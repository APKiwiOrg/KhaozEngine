using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Actor-local history invalidation, independent of the motion seeding and deformation execution path.</summary>
public sealed partial class Scene3D
{
    TemporalHistoryRejection? _skinningHistoryRejection;

    void CollectSkinningHistoryRejection()
    {
        _skinningHistoryRejection ??= new TemporalHistoryRejection(_gd, _retired);
        _skinningHistoryRejection.Clear();
        var items = _skinnedInstances.Items;
        _skinningHistoryRejection.Reserve(items.Count);
        MotionHistory? history = ActiveMotionHistory;
        if (PreviousFrameView is not null && history is not null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var draw = items[i];
                if (draw.Motion.IsNone || draw.ShadowOnly || !_skinnedSlots.IsValid(draw.Mesh.Index, draw.Mesh.Generation)) continue;
                var entry = _skinnedMeshes[draw.Mesh.Index];
                if (entry is null) continue;
                SkinnedMotionIdentity identity = MotionIdentity(draw.Mesh, entry);
                if (history.TryGetPreviousSkinned(draw.Motion, identity, out _, out var lbs, out var dq)
                    && (entry.Profile.Method == SkinningMethod.DualQuaternionRigid ? dq.Length : lbs.Length) == entry.InverseBind.Length)
                    continue;
                MeshBounds? current = history.TryGetCurrentBounds(draw.Motion, out MeshBounds now) ? now : null;
                MeshBounds? previous = history.TryGetPreviousBounds(draw.Motion, out MeshBounds old) ? old : null;
                _skinningHistoryRejection.Add(current, previous);
            }
        }
        _skinningHistoryRejection.Select(_currentFrameView, PreviousFrameView);
    }

    void ReleaseSkinningHistoryRejection()
    {
        _retired.Retire(_skinningHistoryRejection);
        _skinningHistoryRejection = null;
    }
}
