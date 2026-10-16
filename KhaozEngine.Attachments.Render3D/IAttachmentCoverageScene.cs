using System.Collections.Generic;
using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Opt-in coverage preparation and opaque descriptor forwarding. Ordinary attachment adapters are unchanged.</summary>
public interface IAttachmentCoverageScene : IAttachmentScene
{
    /// <summary>Non-null stable reference to the actual scene that owns uploaded resources.</summary>
    object CoverageResourceOwner { get; }
    SkinnedHostConformer CreateHostConformer(Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> meshes);
    bool TryStageCoverageEquipment(MotionKey actorKey, ulong equipmentInstanceId,
        ReadOnlySpan<CoverageEquipmentCommand> commands, ReadOnlySpan<RigidInstanceDraw> draws,
        Span<RigidInstanceDraw> frozenDraws, out CoverageFailure failure);
    bool TryStageCoverageEquipment(MotionKey actorKey, uint commandId, ulong equipmentInstanceId,
        CoverageSupportSurface support, ReadOnlySpan<RigidInstanceDraw> draws,
        Span<RigidInstanceDraw> frozenDraws, out CoverageFailure failure);
    void DrawMeshOutline(MeshOutlineGroup group, in RigidInstanceDraw draw);
    void DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette);
}
