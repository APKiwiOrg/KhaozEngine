using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>One authored command membership of a complete physical equipment part.</summary>
public readonly record struct CoverageEquipmentCommand(uint CommandId, CoverageSupportSurface Support);

public sealed partial class Scene3D
{
    /// <summary>Stage a complete rigid material snapshot without queuing any draw. Storage is reserved at cold equip.</summary>
    public bool TryStageCoverageEquipment(MotionKey actorKey, ulong equipmentInstanceId,
        ReadOnlySpan<CoverageEquipmentCommand> commands, ReadOnlySpan<RigidInstanceDraw> draws,
        Span<RigidInstanceDraw> frozenDraws, out CoverageFailure failure)
    {
        RequireCoverageThread();
        uint commandId = commands.IsEmpty ? 0 : commands[0].CommandId;
        SkinnedHostCoverage? owner = null;
        foreach (var host in _coverageHosts) if (host.Actor == actorKey) owner = host;
        if (_disposalComplete || _coverageArena.Generation == 0 || _framePrepared || actorKey.IsNone || equipmentInstanceId == 0 || owner is null)
            return Refuse(CoverageFailureCode.StaleFrame, out failure);
        if (commands.Length is < 1 or > CoverageCarryUniforms.MaxCommands)
            return Refuse(CoverageFailureCode.CapacityExceeded, out failure, commands.Length, CoverageCarryUniforms.MaxCommands);
        CoverageSupportSurface support = commands[0].Support;
        if (support is null) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        for (int c = 0; c < commands.Length; c++)
        {
            var member = commands[c];
            if (member.CommandId == 0 || member.Support is null || !member.Support.RenderEvidence.Supported
                || !ReferenceEquals(member.Support.Source, support.Source)) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
            for (int previous = 0; previous < c; previous++)
                if (commands[previous].CommandId == member.CommandId) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        }
        var evidence = support.RenderEvidence;
        if (!evidence.Supported || draws.Length != evidence.Slices.Count || frozenDraws.Length != draws.Length)
            return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        CoverageEquipmentStage? available = null;
        foreach (var stage in owner.Equipment)
        {
            if (stage.Generation != _coverageArena.Generation || stage.Revision != owner.Request.Revision)
            { if (available is null && stage.Draws.Length >= draws.Length) available = stage; continue; }
            if (stage.Instance != equipmentInstanceId) continue;
            foreach (var member in commands)
                if (stage.TryCommand(member.CommandId, out _)) return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        }
        if (available is null) return Refuse(CoverageFailureCode.CapacityExceeded, out failure, draws.Length, 0);
        for (int i = 0; i < draws.Length; i++)
        {
            var draw = draws[i];
            int slice = support.Source.Materials[i].Slice;
            if (draw.Coverage.Stage is not null || !_slots.IsValid(draw.Mesh.Index, draw.Mesh.Generation)
                || !_coverageUploads.TryGetValue((draw.Mesh.Index, draw.Mesh.Generation), out var upload)
                || !evidence.Slices.TryGetValue(slice, out var fingerprint) || upload.Fingerprint != fingerprint
                || !upload.FinitePositionSinkInputs || draw.ShadowOnly && !draw.CastsShadows || draw.World != draws[0].World
                || _meshes[draw.Mesh.Index] is not { SplatMaterial: < 0, TileGroundMaterial: < 0, AlphaCutoff: 0 })
                return Refuse(CoverageFailureCode.IdentityMismatch, out failure);
        }
        available.Generation = _coverageArena.Generation; available.Revision = owner.Request.Revision;
        available.Instance = equipmentInstanceId; available.Count = draws.Length; available.CommandCount = commands.Length;
        commands.CopyTo(available.Commands); draws.CopyTo(available.Draws);
        Array.Clear(available.Submitted); Array.Clear(available.Outlined);
        for (int i = 0; i < draws.Length; i++)
        {
            var draw = draws[i] with { Coverage = new(available, available.Generation, available.Revision, i) };
            available.Draws[i] = draw; frozenDraws[i] = draw;
        }
        failure = default;
        return true;

        bool Refuse(CoverageFailureCode code, out CoverageFailure refused, int count = 0, int limit = 0)
        { refused = new(code, null, equipmentInstanceId, commandId, count, limit); return false; }
    }

    void ValidateCoverageEquipment(in RigidInstanceDraw draw, bool outline)
    {
        var token = draw.Coverage;
        if (token.Stage is not { } stage) return;
        RequireCoverageThread();
        if (_disposalComplete || _framePrepared || !ReferenceEquals(stage.Owner.Scene, this) || stage.Owner.Disposed
            || token.Generation != _coverageArena.Generation || token.Generation != stage.Generation
            || token.Revision != stage.Revision || token.Revision != stage.Owner.Request.Revision
            || (uint)token.Slice >= (uint)stage.Count || !_slots.IsValid(draw.Mesh.Index, draw.Mesh.Generation)
            || !SameCoverageRigid(draw, stage.Draws[token.Slice]) || (outline ? stage.Outlined[token.Slice] : stage.Submitted[token.Slice]))
            throw StaleCoverage(stage.Owner.Request);
        for (int material = 0; material < stage.Count; material++)
            if (!_slots.IsValid(stage.Draws[material].Mesh.Index, stage.Draws[material].Mesh.Generation))
                throw StaleCoverage(stage.Owner.Request);
    }
    static void RecordCoverageEquipment(in RigidInstanceDraw draw, bool outline)
    {
        if (draw.Coverage.Stage is not { } stage) return;
        if (outline) stage.Outlined[draw.Coverage.Slice] = true;
        else stage.Submitted[draw.Coverage.Slice] = true;
    }
    static bool SameCoverageRigid(in RigidInstanceDraw a, in RigidInstanceDraw b) => a.Mesh.Index == b.Mesh.Index
        && a.Mesh.Generation == b.Mesh.Generation && a.World == b.World && (Vector4)a.Tint == (Vector4)b.Tint
        && (Vector4)a.Material.Emissive == (Vector4)b.Material.Emissive && a.Material.Specular == b.Material.Specular
        && a.Material.Shininess == b.Material.Shininess && a.Dissolve == b.Dissolve
        && a.DissolveEdgeWidth == b.DissolveEdgeWidth && (Vector4)a.DissolveEdgeColor == (Vector4)b.DissolveEdgeColor
        && a.CastsShadows == b.CastsShadows && a.ShadowOnly == b.ShadowOnly && a.ShadowMobility == b.ShadowMobility
        && a.InvertShadowDissolve == b.InvertShadowDissolve && a.DissolveComplement == b.DissolveComplement
        && a.CutInsideInteriors == b.CutInsideInteriors && a.Motion == b.Motion;

    /// <summary>Queue a complete frozen rigid descriptor into an existing outline group.</summary>
    public void DrawMeshOutline(MeshOutlineGroup group, in RigidInstanceDraw draw)
    {
        RequireMeshOutlineGroup(group);
        ValidateCoverageEquipment(draw, true);
        AddMeshOutlinePart(group, draw.Mesh, draw.World, draw.Dissolve, draw.DissolveComplement > .5f, draw.CutInsideInteriors);
        RecordCoverageEquipment(draw, true);
    }
    /// <summary>Queue a frozen host descriptor and its exact raw palette into an existing outline group.</summary>
    public void DrawSkinnedOutline(MeshOutlineGroup group, in SkinnedInstanceDraw draw, ReadOnlySpan<Matrix4x4> palette)
    {
        RequireMeshOutlineGroup(group);
        var preparation = ValidateCoverageDraw(draw, palette, true);
        AddSkinnedOutlinePart(group, draw.Mesh, palette, draw.Model, draw.Dissolve, draw.DissolveComplement > .5f);
        RecordCoverageDraw(preparation, draw, true);
    }
}
