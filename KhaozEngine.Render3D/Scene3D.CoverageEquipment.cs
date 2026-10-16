using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Freezes a complete rigid source's material draws without queuing. Submit the returned descriptors
    /// through Draw and the descriptor outline overload. Duplicate actor, instance and command staging refuses.</summary>
    public bool TryStageCoverageEquipment(MotionKey actorKey, uint commandId, ulong equipmentInstanceId,
        CoverageSupportSurface support, ReadOnlySpan<RigidInstanceDraw> draws, Span<RigidInstanceDraw> frozenDraws,
        out CoverageFailure failure)
    {
        RequireCoverageThread();
        ObjectDisposedException.ThrowIf(_coverageDisposed, this);
        ArgumentNullException.ThrowIfNull(support);
        if (_coverageArena.Generation == 0 || _coverageArena.Closed || _framePrepared || actorKey.IsNone || commandId == 0 || equipmentInstanceId == 0)
            return Refuse(CoverageFailureCode.StaleFrame, out failure);
        CoverageRigidSourceEvidence evidence = support.RenderEvidence;
        if (!evidence.Supported || draws.Length != evidence.Slices.Count || frozenDraws.Length != draws.Length)
            return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        for (int i = 0; i < _coverageArena.EquipmentCount; i++)
        {
            CoverageEquipmentStage prior = _coverageArena.Equipment[i];
            if (prior.Actor == actorKey && prior.Command == commandId && prior.Instance == equipmentInstanceId)
                return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        }
        for (int i = 0; i < draws.Length; i++)
        {
            RigidInstanceDraw draw = draws[i];
            int slice = support.Source.Materials[i].Slice;
            if (draw.Coverage.Stage is not null || !_slots.IsValid(draw.Mesh.Index, draw.Mesh.Generation)
                || !_coverageUploads.TryGetValue((draw.Mesh.Index, draw.Mesh.Generation), out var upload)
                || !evidence.Slices.TryGetValue(slice, out var fingerprint) || upload.Fingerprint != fingerprint
                || !double.IsFinite(upload.AttributeSum) || draw.ShadowOnly && !draw.CastsShadows
                || draw.World != draws[0].World || _meshes[draw.Mesh.Index] is not { SplatMaterial: < 0, TileGroundMaterial: < 0, AlphaCutoff: 0 })
                return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        }
        int index = _coverageArena.EquipmentCount;
        if (index == _coverageArena.Equipment.Count) _coverageArena.Equipment.Add(new());
        CoverageEquipmentStage stage = _coverageArena.Equipment[index];
        if (stage.Draws.Length != draws.Length)
        { stage.Draws = new RigidInstanceDraw[draws.Length]; stage.Submitted = new bool[draws.Length]; stage.Outlined = new bool[draws.Length]; }
        stage.Scene = this; stage.Generation = _coverageArena.Generation; stage.Actor = actorKey;
        stage.Command = commandId; stage.Instance = equipmentInstanceId; stage.Support = support; stage.Count = draws.Length;
        Array.Clear(stage.Submitted); Array.Clear(stage.Outlined);
        for (int i = 0; i < draws.Length; i++)
        {
            RigidInstanceDraw draw = draws[i] with { Coverage = new(stage, stage.Generation, i) };
            stage.Draws[i] = draw; frozenDraws[i] = draw;
        }
        _coverageArena.EquipmentCount++;
        failure = default;
        return true;

        bool Refuse(CoverageFailureCode code, out CoverageFailure refused)
        { refused = new(code, null, equipmentInstanceId, commandId, 0, 0); return false; }
    }

    void ValidateCoverageEquipment(in RigidInstanceDraw draw, bool outline)
    {
        CoverageEquipmentDraw token = draw.Coverage;
        if (token.Stage is not { } stage) return;
        RequireCoverageThread();
        if (_coverageDisposed || _coverageArena.Closed || _framePrepared || !ReferenceEquals(stage.Scene, this)
            || token.Generation != _coverageArena.Generation || token.Generation != stage.Generation
            || (uint)token.Slice >= (uint)stage.Count || !_slots.IsValid(draw.Mesh.Index, draw.Mesh.Generation)
            || !SameRigid(draw, stage.Draws[token.Slice]) || (outline ? stage.Outlined[token.Slice] : stage.Submitted[token.Slice]))
            throw new InvalidOperationException("Equipment coverage draw does not match its owned frozen snapshot.");
    }

    void RecordCoverageEquipment(in RigidInstanceDraw draw, bool outline)
    {
        if (draw.Coverage.Stage is not { } stage) return;
        if (outline) stage.Outlined[draw.Coverage.Slice] = true;
        else stage.Submitted[draw.Coverage.Slice] = true;
    }

    static bool SameRigid(in RigidInstanceDraw a, in RigidInstanceDraw b) => a.Mesh.Index == b.Mesh.Index
        && a.Mesh.Generation == b.Mesh.Generation && a.World == b.World && (Vector4)a.Tint == (Vector4)b.Tint
        && (Vector4)a.Material.Emissive == (Vector4)b.Material.Emissive && a.Material.Specular == b.Material.Specular
        && a.Material.Shininess == b.Material.Shininess && a.Dissolve == b.Dissolve
        && a.DissolveEdgeWidth == b.DissolveEdgeWidth && (Vector4)a.DissolveEdgeColor == (Vector4)b.DissolveEdgeColor
        && a.CastsShadows == b.CastsShadows && a.ShadowOnly == b.ShadowOnly
        && a.InvertShadowDissolve == b.InvertShadowDissolve && a.DissolveComplement == b.DissolveComplement && a.Motion == b.Motion;

    /// <summary>Submits a frozen equipment descriptor to an outline group.</summary>
    public void DrawMeshOutline(MeshOutlineGroup group, in RigidInstanceDraw draw)
    {
        RequireMeshOutlineGroup(group);
        ValidateCoverageEquipment(draw, true);
        AddMeshOutlinePart(group, draw.Mesh, draw.World, draw.Dissolve, draw.DissolveComplement > .5f);
        RecordCoverageEquipment(draw, true);
    }
}
