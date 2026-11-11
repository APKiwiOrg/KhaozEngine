using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Actor-owned table compatibility and transactional borrowed equipment snapshots. Use on the scene thread.</summary>
public sealed class HostCoverageBinding : IDisposable
{
    readonly IAttachmentCoverageScene? _scene;
    readonly object? _resourceOwner;
    readonly Skeleton _skeleton;
    readonly MotionKey _actor;
    readonly SkinnedHostCoverage? _host;
    readonly Matrix4x4[] _palette;
    EquipmentSnapshot[] _equipment = [];
    CoverageCommandPose[] _commands = [];
    AttachmentDraw[] _draws = [];
    int _preparedCount;
    bool _disposed;

    public HostCoverageBinding(IAttachmentScene scene, Skeleton skeleton, IReadOnlyList<SkinnedMeshHandle> hosts,
        MotionKey actorKey, CoverageRecessionTable table)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(hosts); ArgumentNullException.ThrowIfNull(table);
        if (actorKey.IsNone) throw new ArgumentException("An actor requires a nonzero motion key.", nameof(actorKey));
        _scene = scene as IAttachmentCoverageScene;
        _resourceOwner = _scene?.CoverageResourceOwner;
        _skeleton = skeleton; _actor = actorKey;
        _palette = new Matrix4x4[skeleton.BoneCount];
        if (_resourceOwner is not null)
        {
            var host = _scene!.CreateHostCoverage(skeleton, hosts, table);
            try { host.BindActor(actorKey); }
            catch { host.Dispose(); throw; }
            _host = host;
        }
    }

    /// <summary>Complete frozen descriptors, borrowed until the next prepare, successful replacement or disposal.
    /// Preparation never queues them. Forward each full rigid or skinned descriptor unchanged.</summary>
    public ReadOnlyMemory<AttachmentDraw> PreparedEquipmentDraws
    { get { RequireLive(); return _draws.AsMemory(0, _preparedCount); } }

    /// <summary>Validate the whole replacement before switching. Refusal preserves the active equipment and tokens.
    /// Empty equipment selects ordinary presentation without checking configuration identity.</summary>
    public bool TrySetEquipment(ReadOnlySpan<CoveredAttachmentInstance> equipment, out CoverageFailure failure)
    {
        RequireLive();
        if (!equipment.IsEmpty && (_resourceOwner is null || !ReferenceEquals(_scene!.CoverageResourceOwner, _resourceOwner)))
            return Refuse(CoverageFailureCode.UnsupportedAdapter, 0, 0, out failure);
        if (equipment.Length > EquipmentCoverageDefinition.MaxCommands)
            return Refuse(CoverageFailureCode.CapacityExceeded, 0, 0, out failure, equipment.Length, EquipmentCoverageDefinition.MaxCommands);
        int count = 0, drawCount = 0, maxMaterials = 0;
        int[] regions = new int[_host?.Topology.Map.Regions.Count ?? 0];
        Array.Fill(regions, -1);
        var configuration = new CoverageConfigurationItem[equipment.Length];
        Span<uint> ids = stackalloc uint[EquipmentCoverageDefinition.MaxCommands];
        for (int i = 0; i < equipment.Length; i++)
        {
            var instance = equipment[i];
            if (instance.InstanceId == 0 || instance.Set is null || instance.Coverage is null
                || !instance.Coverage.Matches(instance.Set, _resourceOwner!) || !ReferenceEquals(instance.Set.Skeleton, _skeleton)
                || !instance.Set.MatchesHostSkin(_host!))
                return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            for (int prior = 0; prior < i; prior++)
                if (instance.InstanceId == equipment[prior].InstanceId)
                    return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            if (!float.IsFinite(instance.TransitionWeight) || instance.TransitionWeight is < 0 or > 1)
                return Refuse(CoverageFailureCode.UnsupportedTransform, instance.InstanceId, 0, out failure);
            var definition = instance.Coverage.Definition;
            if (definition.HostMapSha256 != HostCoverageMapJson.SemanticSha256(_host!.Topology.Map))
                return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, 0, out failure);
            foreach (var command in definition.Commands)
            {
                if (count == ids.Length) return Refuse(CoverageFailureCode.CapacityExceeded, instance.InstanceId, command.Id,
                    out failure, count + 1, ids.Length);
                for (int prior = 0; prior < count; prior++)
                    if (ids[prior] == command.Id) return Refuse(CoverageFailureCode.IdentityMismatch, instance.InstanceId, command.Id, out failure);
                int region = Region(command.HostRegion);
                if (region < 0) return Refuse(CoverageFailureCode.UnknownRegion, instance.InstanceId, command.Id, out failure);
                if (regions[region] >= 0) return Refuse(CoverageFailureCode.OverlappingConformers, instance.InstanceId, command.Id, out failure);
                regions[region] = count; ids[count++] = command.Id;
            }
            configuration[i] = instance.Coverage.Configuration(instance.Set);
            drawCount = checked(drawCount + instance.Set.DrawCount);
            foreach (var resources in instance.Set.Resources) maxMaterials = Math.Max(maxMaterials,
                resources.IsSkinned ? resources.SkinnedHandles.Count : resources.RigidHandles.Count);
        }
        if (!equipment.IsEmpty && !_host!.MatchesConfiguration(configuration, regions))
            return Refuse(CoverageFailureCode.TableIdentityMismatch, 0, 0, out failure);
        var replacement = new EquipmentSnapshot[equipment.Length];
        int drawOffset = 0, commandOffset = 0;
        for (int i = 0; i < replacement.Length; i++)
        {
            var instance = equipment[i];
            var commandIds = new uint[instance.Coverage.Definition.Commands.Count];
            var resolved = new int[commandIds.Length];
            for (int c = 0; c < commandIds.Length; c++)
            { commandIds[c] = instance.Coverage.Definition.Commands[c].Id; resolved[c] = Region(instance.Coverage.Definition.Commands[c].HostRegion); }
            replacement[i] = new(instance, drawOffset, commandOffset, commandIds, resolved);
            drawOffset += instance.Set.DrawCount; commandOffset += commandIds.Length;
        }
        var draws = new AttachmentDraw[drawCount];
        var commands = new CoverageCommandPose[count];
        _host?.ReserveEquipment(maxMaterials);
        foreach (var instance in equipment) instance.Coverage.RegisterLifetime(_host!);
        foreach (var previous in _equipment) previous.Instance.Coverage.UnregisterLifetime(_host!);
        _host?.Invalidate();
        _equipment = replacement; _commands = commands; _draws = draws; _preparedCount = 0;
        failure = default;
        return true;
    }

    /// <summary>Freeze one pose and complete equipment descriptors without queuing. Missing and explicit-hole
    /// keys return TableHole, an empty token and no descriptors. Submit the uncovered current host with Motion
    /// equal to the binding actor and ordinary active equipment. Empty equipment succeeds with an empty token.</summary>
    public bool TryPrepare(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, CoverageStateKey stateKey,
        in RigidInstanceDraw equipmentTemplate, out SkinnedCoverageFrame frame, out CoverageFailure failure)
    {
        RequireLive();
        frame = default;
        if (_host is not null && _host.IsPreparedThisFrame)
            return Refuse(CoverageFailureCode.StaleFrame, 0, 0, out failure);
        _preparedCount = 0;
        _host?.Invalidate();
        bool accepted = false;
        try
        {
            if (_commands.Length > 0 && !ReferenceEquals(_scene!.CoverageResourceOwner, _resourceOwner))
                return Refuse(CoverageFailureCode.UnsupportedAdapter, 0, 0, out failure);
            if (!ReferenceEquals(skeleton, _skeleton) || palette.Length != _palette.Length)
                return Refuse(CoverageFailureCode.IdentityMismatch, 0, 0, out failure);
            Matrix4x4 snapshot = model;
            var template = equipmentTemplate with { Coverage = default };
            if (!AttachmentSetBinding.ValidMatrix(snapshot) || !(snapshot.GetDeterminant() > 0)
                || template.ShadowOnly && !template.CastsShadows)
                return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure);
            palette.CopyTo(_palette);
            foreach (var joint in _palette)
                if (!AttachmentSetBinding.ValidMatrix(joint)) return Refuse(CoverageFailureCode.UnsupportedTransform, 0, 0, out failure);
            try
            {
                foreach (var equipment in _equipment)
                {
                    equipment.Instance.Set.WriteDraws(_skeleton, _palette, snapshot,
                        MotionKey.Combine(MotionKey.Combine(_actor, (uint)equipment.Instance.InstanceId),
                            (uint)(equipment.Instance.InstanceId >> 32)), template, _draws.AsSpan(equipment.DrawOffset));
                    equipment.FreezePalettes(_draws);
                    for (int c = 0; c < equipment.CommandIds.Length; c++)
                    {
                        var command = equipment.Instance.Coverage.Definition.Commands[c];
                        var support = equipment.Instance.Coverage.Supports[c];
                        Matrix4x4 world = _draws[equipment.DrawOffset + equipment.FirstDraw[c]].Rigid.World;
                        _commands[equipment.CommandOffset + c] = new(command.Id, equipment.Instance.InstanceId,
                            _host!.Topology, equipment.Regions[c], support, world, support.CoverageLocal * world,
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
                    if (!_scene!.TryStageCoverageEquipment(_actor, equipment.Instance.InstanceId,
                        part.Commands, part.Draws, part.Frozen, out failure)) return false;
                    for (int m = 0; m < part.Frozen.Length; m++)
                        _draws[equipment.DrawOffset + part.FirstDraw + m] = new(part.PartId, m, part.Frozen[m]);
                }
            if (_commands.Length > 0 && !_host!.TryPrepare(_palette, snapshot, stateKey, _commands, _actor, out frame, out failure)) return false;
            _preparedCount = _draws.Length; accepted = true; failure = default;
            return true;
        }
        finally { if (!accepted) _host?.Invalidate(); }
    }

    int Region(string key)
    {
        var regions = _host!.Topology.Map.Regions;
        for (int i = 0; i < regions.Count; i++) if (regions[i].Key == key) return i;
        return -1;
    }
    bool Refuse(CoverageFailureCode code, ulong instance, uint command, out CoverageFailure failure, int count = 0, int limit = 0)
    { failure = new(code, _host?.Topology.HostIdentity, instance, command, count, limit); return false; }
    void RequireLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var equipment in _equipment) equipment.Instance.Set.Owner.ThrowIfDisposed();
    }
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var previous in _equipment) previous.Instance.Coverage.UnregisterLifetime(_host!);
        _disposed = true; _host?.Dispose();
        _equipment = []; _draws = []; _commands = []; _preparedCount = 0;
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
