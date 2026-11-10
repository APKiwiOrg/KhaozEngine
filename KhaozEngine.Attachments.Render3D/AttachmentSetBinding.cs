using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Attachment parts resolved once against a contract map and an explicit host skin.</summary>
/// <remarks>Scratch storage is allocated at construction. Use each binding on its owning render thread.
/// Calls to ComposeWorlds on the same binding must not overlap.</remarks>
public sealed class AttachmentSetBinding
{
    readonly ResolvedPart[] _parts;
    readonly Dictionary<uint, int> _partIndices = [];
    readonly Matrix4x4[] _worlds;
    readonly IReadOnlyList<CoverageConfigurationPart> _configurationParts;
    readonly Matrix4x4[]? _hostInverseBind;

    public AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map)
        : this(definition, map, null, null, null) { }

    public AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map,
        SkinnedGltfMesh hostMesh, IReadOnlyDictionary<AttachmentMeshRef, SkinnedGltfMesh> skinnedMeshes)
        : this(definition, map, hostMesh ?? throw new ArgumentNullException(nameof(hostMesh)),
            skinnedMeshes ?? throw new ArgumentNullException(nameof(skinnedMeshes)), null)
    { }

    internal AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map,
        SkinnedGltfMesh hostMesh, IReadOnlyList<AttachmentPartResources> resources)
        : this(definition, map, hostMesh, null, resources) { }

    AttachmentSetBinding(AttachmentSetDefinition definition, ContractJointMap map, SkinnedGltfMesh? hostMesh,
        IReadOnlyDictionary<AttachmentMeshRef, SkinnedGltfMesh>? skinnedMeshes,
        IReadOnlyList<AttachmentPartResources>? resources)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(map);
        Skeleton = map.Skeleton;
        if (hostMesh is null)
        {
            foreach (AttachmentPartDefinition part in definition.Parts)
                if (part is SkinnedAttachmentPartDefinition)
                    throw new AttachmentBindingDataException(AttachmentBindingRefusal.HostMeshRequired,
                        $"Attachment part {part.Id}: skinned parts require an explicit host mesh, not a map-only binding.")
                    { PartId = part.Id };
        }
        else SkinnedAttachmentBinding.ValidateHost(map, hostMesh);
        _hostInverseBind = hostMesh?.InverseBind.AsSpan().ToArray();
        var configurationParts = new CoverageConfigurationPart[definition.Parts.Count];
        _parts = new ResolvedPart[definition.Parts.Count];
        for (int i = 0; i < _parts.Length; i++)
        {
            AttachmentPartDefinition definitionPart = definition.Parts[i];
            _partIndices.Add(definitionPart.Id, i);
            if (definitionPart is SkinnedAttachmentPartDefinition skinned)
            {
                SkinnedAttachmentBinding skin = resources is null
                    ? SkinnedAttachmentBinding.FromMesh(skinned, map, hostMesh!, skinnedMeshes!)
                    : SkinnedAttachmentBinding.FromResources(skinned, map, hostMesh!, resources[i]);
                _parts[i] = new ResolvedPart(skinned.Id, string.Empty, -1, Matrix4x4.Identity, skin);
                configurationParts[i] = new(skinned.Id, string.Empty, Matrix4x4.Identity, Matrix4x4.Identity);
                continue;
            }
            var part = (RigidAttachmentPartDefinition)definition.Parts[i];
            try
            {
                int node = map.Node(part.Joint);
                int bone = map.Bone(part.Joint);
                Matrix4x4 alignment = map.BodyAlignment(node);
                configurationParts[i] = new(part.Id, part.Joint, part.Local, alignment);
                _parts[i] = new ResolvedPart(part.Id, part.Joint, bone, part.Local * alignment);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.RigidJointUnresolved,
                    $"Attachment part {part.Id}: joint '{part.Joint}' cannot bind to the contract skin.", error)
                { PartId = part.Id, JointName = part.Joint };
            }
        }
        _worlds = new Matrix4x4[_parts.Length];
        _configurationParts = Array.AsReadOnly(configurationParts);
    }

    internal IReadOnlyList<CoverageConfigurationPart> ConfigurationParts => _configurationParts;
    internal bool MatchesHostSkin(SkinnedHostCoverage host) => _hostInverseBind is null || host.MatchesHostSkin(_hostInverseBind);

    public Skeleton Skeleton { get; }
    public int PartCount => _parts.Length;

    /// <summary>The garment palette capacity for a named part, or zero for a rigid part.</summary>
    public int SkinnedJointCount(uint partId) => Part(partId).Skin?.JointCount ?? 0;

    /// <summary>Copy host joint model matrices into garment skin order without applying inverse binds.</summary>
    /// <remarks>Valid calls allocate no managed memory. The renderer applies the garment's inverse binds.</remarks>
    public void CopySkinnedPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> hostPalette,
        uint partId, Span<Matrix4x4> destination)
    {
        ValidateFrame(skeleton, hostPalette);
        SkinnedAttachmentBinding skin = Part(partId).Skin
            ?? throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.PartHasNoGarmentPalette,
                $"Attachment part {partId} is rigid and has no garment palette.", nameof(partId))
            { PartId = partId };
        skin.CopyPalette(hostPalette, destination);
    }

    /// <summary>Write canonical part-ID order after validating the whole result. Output may overlap the palette.</summary>
    /// <remarks>Valid calls allocate no managed memory. Calls on the same binding must not overlap.</remarks>
    /// <exception cref="AttachmentBindingArgumentException">The skeleton, palette, capacity or pose matrices are invalid.
    /// <see cref="AttachmentBindingArgumentException.Reason"/> identifies the rule. Pose composition failures retain
    /// the original error as <see cref="Exception.InnerException"/>.</exception>
    public void ComposeWorlds(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, Span<Matrix4x4> destination)
    {
        ValidateFrame(skeleton, palette);
        if (destination.Length < PartCount)
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.WorldOutputTooSmall,
                $"Output capacity {destination.Length} must hold {PartCount} attachment parts.", nameof(destination))
            { ActualCapacity = destination.Length, RequiredCapacity = PartCount };
        if (!ValidMatrix(model))
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.ModelTransformInvalid,
                "The model matrix must be finite, affine and invertible.", nameof(model));

        Span<Matrix4x4> worlds = _worlds;
        for (int i = 0; i < _parts.Length; i++)
        {
            ResolvedPart part = _parts[i];
            if (part.Skin is { } skin)
            {
                skin.ValidatePalette(palette);
                worlds[i] = model;
                continue;
            }
            Matrix4x4 joint = palette[part.Bone];
            try
            {
                if (!ValidMatrix(joint))
                    throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.JointPoseInvalid,
                        "The joint matrix must be finite, affine and invertible.", null)
                    { PartId = part.Id, JointName = part.Joint };
                worlds[i] = BoneSocket.ComposeRigid(part.LocalAligned, joint, model);
                if (!ValidMatrix(worlds[i]))
                    throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.WorldTransformInvalid,
                        "The composed world matrix must be finite, affine and invertible.", null)
                    { PartId = part.Id, JointName = part.Joint };
            }
            catch (ArgumentException error)
            {
                throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.PoseCompositionFailed,
                    $"Attachment part {part.Id}: joint '{part.Joint}' has an invalid pose or world transform.",
                    nameof(palette), error)
                { PartId = part.Id, JointName = part.Joint };
            }
        }
        worlds.CopyTo(destination);
    }

    ResolvedPart Part(uint partId) => _partIndices.TryGetValue(partId, out int index)
        ? _parts[index]
        : throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.PartMissing,
            $"Attachment part {partId} is not in this binding.", nameof(partId))
        { PartId = partId };

    void ValidateFrame(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette)
    {
        if (!ReferenceEquals(skeleton, Skeleton))
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.SkeletonMismatch,
                "The attachment binding requires its exact skeleton instance.", nameof(skeleton));
        if (palette.Length != Skeleton.BoneCount)
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.HostPaletteLengthMismatch,
                $"Palette length {palette.Length} must equal skin bone count {Skeleton.BoneCount}.", nameof(palette))
            { ActualCount = palette.Length, RequiredCount = Skeleton.BoneCount };
    }

    internal static bool ValidMatrix(in Matrix4x4 matrix) => Finite(matrix)
        && matrix.M14 == 0 && matrix.M24 == 0 && matrix.M34 == 0 && matrix.M44 == 1
        && Matrix4x4.Invert(matrix, out Matrix4x4 inverse) && Finite(inverse);

    internal static bool Finite(in Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!float.IsFinite(matrix[row, column])) return false;
        return true;
    }

    readonly record struct ResolvedPart(uint Id, string Joint, int Bone, Matrix4x4 LocalAligned,
        SkinnedAttachmentBinding? Skin = null);
}
