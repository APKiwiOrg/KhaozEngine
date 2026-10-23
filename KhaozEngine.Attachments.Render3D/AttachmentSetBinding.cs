using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
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
    readonly Matrix4x4[]? _hostInverseBind;
    readonly SkinningProfile _hostProfile;

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
        _hostProfile = hostMesh?.Profile ?? SkinningProfile.LinearBlend;
        if (hostMesh is null)
        {
            foreach (AttachmentPartDefinition part in definition.Parts)
                if (part is SkinnedAttachmentPartDefinition)
                    throw new InvalidDataException($"Attachment part {part.Id}: skinned parts require an explicit host mesh, not a map-only binding.");
        }
        else SkinnedAttachmentBinding.ValidateHost(map, hostMesh);
        _parts = new ResolvedPart[definition.Parts.Count];
        for (int i = 0; i < _parts.Length; i++)
        {
            AttachmentPartDefinition definitionPart = definition.Parts[i];
            _partIndices.Add(definitionPart.Id, i);
            if (definitionPart is SkinnedAttachmentPartDefinition skinned)
            {
                _hostInverseBind ??= (Matrix4x4[])hostMesh!.InverseBind.Clone();
                SkinnedAttachmentBinding skin = resources is null
                    ? SkinnedAttachmentBinding.FromMesh(skinned, map, _hostInverseBind, hostMesh!.Profile, skinnedMeshes!)
                    : SkinnedAttachmentBinding.FromResources(skinned, map, _hostInverseBind, hostMesh!.Profile, resources[i]);
                _parts[i] = new ResolvedPart(skinned.Id, string.Empty, -1, Matrix4x4.Identity, skin);
                continue;
            }
            var part = (RigidAttachmentPartDefinition)definition.Parts[i];
            try
            {
                int node = map.Node(part.Joint);
                int bone = map.Bone(part.Joint);
                Matrix4x4 alignment = map.BodyAlignment(node);
                _parts[i] = new ResolvedPart(part.Id, part.Joint, bone, part.Local * alignment);
            }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException($"Attachment part {part.Id}: joint '{part.Joint}' cannot bind to the contract skin.", error);
            }
        }
        _worlds = new Matrix4x4[_parts.Length];
        SkinningProfile profile = hostMesh?.Profile ?? SkinningProfile.LinearBlend;
        // Preserve the established LBS identity. A changed body surface invalidates fit and history even
        // for a rigid-only set, although its socket composition remains unchanged.
        FitIdentity = profile == SkinningProfile.LinearBlend ? definition.SemanticSha256
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{definition.SemanticSha256}\nskinning:{(int)profile.Method}:{profile.AlgorithmVersion}")));
    }

    public Skeleton Skeleton { get; }
    public int PartCount => _parts.Length;

    internal bool MatchesHostSkin(SkinnedHostConformer conformer) =>
        _hostProfile == SkinningProfile.LinearBlend
        && (_hostInverseBind is null || conformer.MatchesHostSkin(_hostInverseBind));
    /// <summary>Canonical definition and host deformation identity for fit evidence and motion history.</summary>
    public string FitIdentity { get; }

    /// <summary>The garment palette capacity for a named part, or zero for a rigid part.</summary>
    public int SkinnedJointCount(uint partId) => Part(partId).Skin?.JointCount ?? 0;

    /// <summary>Copy host joint model matrices into garment skin order without applying inverse binds.</summary>
    /// <remarks>Valid calls allocate no managed memory. The renderer applies the garment's inverse binds.</remarks>
    public void CopySkinnedPalette(Skeleton skeleton, ReadOnlySpan<Matrix4x4> hostPalette,
        uint partId, Span<Matrix4x4> destination)
    {
        ValidateFrame(skeleton, hostPalette);
        SkinnedAttachmentBinding skin = Part(partId).Skin
            ?? throw new ArgumentException($"Attachment part {partId} is rigid and has no garment palette.", nameof(partId));
        skin.CopyPalette(hostPalette, destination);
    }

    /// <summary>Write canonical part-ID order after validating the whole result. Output may overlap the palette.</summary>
    /// <remarks>Valid calls allocate no managed memory. Calls on the same binding must not overlap.</remarks>
    /// <exception cref="ArgumentException">The skeleton, palette, capacity or pose matrices are invalid.</exception>
    public void ComposeWorlds(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, Span<Matrix4x4> destination)
    {
        ValidateFrame(skeleton, palette);
        if (destination.Length < PartCount)
            throw new ArgumentException($"Output capacity {destination.Length} must hold {PartCount} attachment parts.", nameof(destination));
        if (!ValidMatrix(model))
            throw new ArgumentException("The model matrix must be finite, affine and invertible.", nameof(model));

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
                    throw new ArgumentException("The joint matrix must be finite, affine and invertible.");
                worlds[i] = BoneSocket.ComposeRigid(part.LocalAligned, joint, model);
                if (!ValidMatrix(worlds[i]))
                    throw new ArgumentException("The composed world matrix must be finite, affine and invertible.");
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"Attachment part {part.Id}: joint '{part.Joint}' has an invalid pose or world transform.",
                    nameof(palette), error);
            }
        }
        worlds.CopyTo(destination);
    }

    ResolvedPart Part(uint partId) => _partIndices.TryGetValue(partId, out int index)
        ? _parts[index]
        : throw new ArgumentException($"Attachment part {partId} is not in this binding.", nameof(partId));

    void ValidateFrame(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette)
    {
        if (!ReferenceEquals(skeleton, Skeleton))
            throw new ArgumentException("The attachment binding requires its exact skeleton instance.", nameof(skeleton));
        if (palette.Length != Skeleton.BoneCount)
            throw new ArgumentException($"Palette length {palette.Length} must equal skin bone count {Skeleton.BoneCount}.", nameof(palette));
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
