using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Opt-in table coverage. Ordinary attachment adapters need no new capability.</summary>
public interface IAttachmentCoverageScene : IAttachmentScene
{
    /// <summary>Stable non-null reference to the actual scene owning the uploaded resources.</summary>
    object CoverageResourceOwner { get; }
    SkinnedHostCoverage CreateHostCoverage(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes, CoverageRecessionTable table);
    bool TryStageCoverageEquipment(MotionKey actorKey, ulong equipmentInstanceId,
        ReadOnlySpan<CoverageEquipmentCommand> commands, ReadOnlySpan<RigidInstanceDraw> draws,
        Span<RigidInstanceDraw> frozenDraws, out CoverageFailure failure);
    void DrawMeshOutline(MeshOutlineGroup group, in RigidInstanceDraw draw);
    void DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette);
}
