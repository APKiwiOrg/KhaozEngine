using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Actor-owned coverage and transactional borrowed equipment state.</summary>
public sealed class HostCoverageBinding : IDisposable
{
    readonly IAttachmentCoverageScene? _coverageScene;
    readonly object? _resourceOwner;
    readonly Skeleton _skeleton;
    readonly MotionKey _actor;
    readonly CoverageFallback _fallback;
    readonly SkinnedHostConformer? _conformer;
    readonly Matrix4x4[] _palette;
    Dictionary<(ulong, LoadedEquipmentCoverage, uint), uint> _commandIds = [];
    EquipmentSnapshot[] _equipment = [];
    CoverageCommandPose[] _commands = [];
    AttachmentDraw[] _draws = [];
    uint _lastCommandId;
    int _preparedCount;
    bool _disposed;

    public HostCoverageBinding(IAttachmentScene scene, Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> hosts,
        MotionKey actorKey, CoverageFallback fallback)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(hosts);
        if (actorKey.IsNone) throw new ArgumentException("An actor incarnation requires a positive motion key.", nameof(actorKey));
        if (fallback != CoverageFallback.UncoveredCurrentPose) throw new ArgumentOutOfRangeException(nameof(fallback));
        _coverageScene = scene as IAttachmentCoverageScene;
        _resourceOwner = _coverageScene?.CoverageResourceOwner;
        _skeleton = skeleton; _actor = actorKey; _fallback = fallback;
        _palette = new Matrix4x4[skeleton.BoneCount];
        if (_resourceOwner is not null) _conformer = _coverageScene!.CreateHostConformer(skeleton, hosts);
    }

    /// <summary>Complete frozen equipment descriptors, borrowed until the next preparation, state change or disposal.</summary>
    /// <remarks>Preparation never submits these descriptors. Forward covered rigid descriptors unchanged.</remarks>
    public ReadOnlyMemory<AttachmentDraw> PreparedEquipmentDraws
    { get { RequireLive(); return _draws.AsMemory(0, _preparedCount); } }

    /// <summary>Validate and acquire the complete replacement before switching equipment. Refusal preserves active state.</summary>
    public bool TrySetEquipment(ReadOnlySpan<CoveredAttachmentInstance> equipment, out CoverageFailure failure)
    {
        RequireLive();
        if (!equipment.IsEmpty && (_resourceOwner is null || !ReferenceEquals(_coverageScene!.CoverageResourceOwner, _resourceOwner)))
            return Refuse(CoverageFailureCode.UnsupportedAdapter, 0, 0, out failure);
        if (equipment.Length > EquipmentCoverageDefinition.MaxCommands)
            return Refuse(CoverageFailureCode.CapacityExceeded, 0, 0, out failure, equipment.Length, EquipmentCoverageDefinition.MaxCommands);
        int count = 0, draws = 0;
        Span<bool> regions = stackalloc bool[HostCoverageMap.MaxRegions];
        regions.Clear();
        for (int i = 0; i < equipment.Length; i++)
        {
            ref readonly var instance = ref equipment[i];
            if (instance.InstanceId == 0 || instance.Set is null || instance.Coverage is null
                || !instance.Coverage.Matches(instance.Set, _resourceOwner!) || !ReferenceEquals(instance.Set.Skeleton, _skeleton))
                return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            for (int j = 0; j < i; j++)
                if (instance.InstanceId == equipment[j].InstanceId)
                    return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            if (!float.IsFinite(instance.TransitionWeight) || instance.TransitionWeight is < 0 or > 1)
                return Refuse(CoverageFailureCode.UnsupportedTransform, instance.InstanceId, 0, out failure);
            var definition = instance.Coverage.Definition;
            if (definition.HostMapSha256 != HostCoverageMapJson.SemanticSha256(_conformer!.Topology.Map))
                return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            foreach (var command in definition.Commands)
            {
                int region = Region(command.HostRegion);
                if (region < 0) return Refuse(CoverageFailureCode.UnknownRegion, instance.InstanceId, command.Id, out failure);
                if (regions[region]) return Refuse(CoverageFailureCode.OverlappingConformers, instance.InstanceId, command.Id, out failure);
                regions[region] = true;
                count++;
            }
            draws = checked(draws + instance.Set.DrawCount);
        }
        if (count > EquipmentCoverageDefinition.MaxCommands)
            return Refuse(CoverageFailureCode.CapacityExceeded, 0, 0, out failure, count, EquipmentCoverageDefinition.MaxCommands);

        var replacement = new EquipmentSnapshot[equipment.Length];
        var ids = new Dictionary<(ulong, LoadedEquipmentCoverage, uint), uint>();
        uint last = _lastCommandId;
        int offset = 0, commandOffset = 0;
        for (int i = 0; i < replacement.Length; i++)
        {
            var instance = equipment[i];
            uint[] commands = new uint[instance.Coverage.Definition.Commands.Count];
            for (int c = 0; c < commands.Length; c++)
            {
                var key = (instance.InstanceId, instance.Coverage, instance.Coverage.Definition.Commands[c].Id);
                if (!_commandIds.TryGetValue(key, out uint id))
                {
                    if (last == uint.MaxValue) return Refuse(CoverageFailureCode.CapacityExceeded, instance.InstanceId, 0, out failure);
                    id = ++last;
                }
                ids.Add(key, id);
                commands[c] = id;
            }
            var resolved = new int[commands.Length];
            for (int c = 0; c < resolved.Length; c++) resolved[c] = Region(instance.Coverage.Definition.Commands[c].HostRegion);
            replacement[i] = new EquipmentSnapshot(instance, offset, commandOffset, commands, resolved);
            offset += instance.Set.DrawCount;
            commandOffset += commands.Length;
        }
        var prepared = new AttachmentDraw[draws];
        var poses = new CoverageCommandPose[count];
        _commandIds = ids; _equipment = replacement; _draws = prepared; _commands = poses; _lastCommandId = last; _preparedCount = 0;
        failure = default;
        return true;
    }

    /// <summary>Prepare one immutable pose and complete material snapshot without queuing any draw.</summary>
    /// <remarks>On refusal no token or equipment descriptors are published. Present the uncovered current pose.
    /// An empty equipment list succeeds with an empty token, selecting ordinary current-pose presentation.</remarks>
    public bool TryPrepare(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        in RigidInstanceDraw equipmentTemplate, out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        RequireLive();
        _preparedCount = 0;
        frame = default;
        if (_commands.Length > 0 && !ReferenceEquals(_coverageScene!.CoverageResourceOwner, _resourceOwner))
            return Refuse(CoverageFailureCode.UnsupportedAdapter, 0, 0, out failure);
        if (!ReferenceEquals(skeleton, _skeleton) || palette.Length != _palette.Length)
            return Refuse(CoverageFailureCode.IdentityMismatch, 0, 0, out failure);
        Matrix4x4 modelSnapshot = model;
        var template = equipmentTemplate with { Coverage = default };
        if (!AttachmentSetBinding.ValidMatrix(modelSnapshot) || !(modelSnapshot.GetDeterminant() > 0)
            || template.ShadowOnly && !template.CastsShadows)
            return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure);
        palette.CopyTo(_palette);
        foreach (var joint in _palette)
            if (!AttachmentSetBinding.ValidMatrix(joint)) return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure);
        try
        {
            foreach (var equipment in _equipment)
            {
                equipment.Instance.Set.WriteDraws(_skeleton, _palette, modelSnapshot,
                    MotionKey.Combine(MotionKey.Combine(_actor, (uint)equipment.Instance.InstanceId),
                        (uint)(equipment.Instance.InstanceId >> 32)), template, _draws.AsSpan(equipment.DrawOffset));
                equipment.FreezePalettes(_draws);
                for (int c = 0; c < equipment.CommandIds.Length; c++)
                {
                    var command = equipment.Instance.Coverage.Definition.Commands[c];
                    var support = equipment.Instance.Coverage.Supports[c];
                    Matrix4x4 world = _draws[equipment.DrawOffset + equipment.FirstDraw[c]].Rigid.World;
                    _commands[equipment.CommandOffset + c] = new(equipment.CommandIds[c], equipment.Instance.InstanceId,
                        _conformer!.Topology, equipment.Regions[c], support, world, support.CoverageLocal * world,
                        command.MinZ, command.MaxZ, command.CollarWidth, command.Clearance, command.MaxWorldRecession,
                        equipment.Instance.TransitionWeight);
                }
            }
        }
        catch (ArgumentException) { return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure); }
        foreach (var equipment in _equipment)
            foreach (var part in equipment.Parts)
            {
                for (int m = 0; m < part.Draws.Length; m++) part.Draws[m] = _draws[equipment.DrawOffset + part.FirstDraw + m].Rigid;
                if (!_coverageScene!.TryStageCoverageEquipment(_actor, equipment.Instance.InstanceId,
                    part.Commands, part.Draws, part.Frozen, out failure)) return false;
                for (int m = 0; m < part.Frozen.Length; m++)
                    _draws[equipment.DrawOffset + part.FirstDraw + m] = new(part.PartId, m, part.Frozen[m]);
            }
        if (_commands.Length > 0 && !_conformer!.TryPrepare(_palette, modelSnapshot, _commands, _actor, _fallback, out frame, out failure)) return false;
        _preparedCount = _draws.Length;
        failure = default;
        return true;
    }

    int Region(string key)
    {
        var regions = _conformer!.Topology.Map.Regions;
        for (int i = 0; i < regions.Count; i++) if (regions[i].Key == key) return i;
        return -1;
    }
    bool Refuse(CoverageFailureCode code, ulong instance, uint command, out CoverageFailure failure, int count = 0, int limit = 0)
    { failure = new(code, _conformer?.Topology.HostIdentity, instance, command, count, limit); return false; }
    void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var equipment in _equipment) equipment.Instance.Set.Owner.ThrowIfDisposed();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conformer?.Dispose();
        _equipment = []; _draws = []; _commands = []; _preparedCount = 0;
        _commandIds.Clear();
    }

    sealed class EquipmentSnapshot
    {
        internal readonly CoveredAttachmentInstance Instance;
        internal readonly int DrawOffset, CommandOffset;
        internal readonly uint[] CommandIds;
        internal readonly int[] FirstDraw;
        internal readonly int[] Regions;
        internal readonly PartSnapshot[] Parts;
        readonly Matrix4x4[][] _palettes;

        internal EquipmentSnapshot(CoveredAttachmentInstance instance, int drawOffset, int commandOffset, uint[] ids, int[] regions)
        {
            Instance = instance; DrawOffset = drawOffset; CommandOffset = commandOffset; CommandIds = ids;
            Regions = regions;
            _palettes = new Matrix4x4[instance.Set.DrawCount][];
            FirstDraw = new int[ids.Length];
            var parts = new List<PartSnapshot>();
            int first = 0;
            for (int p = 0; p < instance.Set.Resources.Count; p++)
            {
                var resources = instance.Set.Resources[p];
                int count = resources.IsSkinned ? resources.SkinnedHandles.Count : resources.RigidHandles.Count;
                var palette = new Matrix4x4[resources.JointNames.Count];
                for (int m = 0; m < count; m++) _palettes[first + m] = palette;
                var commands = new List<CoverageEquipmentCommand>();
                for (int c = 0; c < ids.Length; c++)
                    if (instance.Coverage.Parts[c] == p)
                    { FirstDraw[c] = first; commands.Add(new(ids[c], instance.Coverage.Supports[c])); }
                if (commands.Count > 0) parts.Add(new(instance.Set.Definition.Parts[p].Id, first, count, commands.ToArray()));
                first += count;
            }
            Parts = parts.ToArray();
        }
        internal void FreezePalettes(AttachmentDraw[] draws)
        {
            for (int d = 0; d < _palettes.Length; d++)
            {
                var draw = draws[DrawOffset + d];
                if (!draw.IsSkinned) continue;
                draw.BoneMatrices.Span.CopyTo(_palettes[d]);
                draws[DrawOffset + d] = new(draw.PartId, draw.MaterialIndex, draw.Skinned, _palettes[d]);
            }
        }
    }
    sealed class PartSnapshot(uint partId, int first, int count, CoverageEquipmentCommand[] commands)
    {
        internal readonly uint PartId = partId;
        internal readonly int FirstDraw = first;
        internal readonly CoverageEquipmentCommand[] Commands = commands;
        internal readonly RigidInstanceDraw[] Draws = new RigidInstanceDraw[count], Frozen = new RigidInstanceDraw[count];
    }
}
