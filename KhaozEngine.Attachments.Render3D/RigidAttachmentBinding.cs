using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Rigid parts resolved once against a contract map and composed from the body's current pose.</summary>
public sealed class RigidAttachmentBinding
{
    readonly ResolvedPart[] _parts;

    public RigidAttachmentBinding(RigidAttachmentSetDefinition definition, ContractJointMap map)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(map);
        Skeleton = map.Skeleton;
        _parts = new ResolvedPart[definition.Parts.Count];
        for (int i = 0; i < _parts.Length; i++)
        {
            RigidAttachmentPartDefinition part = definition.Parts[i];
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
    }

    public Skeleton Skeleton { get; }
    public int PartCount => _parts.Length;

    /// <summary>Write canonical part-ID order after validating the whole result. Output may overlap the palette.</summary>
    /// <exception cref="ArgumentException">The skeleton, palette, capacity or pose matrices are invalid.</exception>
    public void ComposeWorlds(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, Span<Matrix4x4> destination)
    {
        if (!ReferenceEquals(skeleton, Skeleton))
            throw new ArgumentException("The attachment binding requires its exact skeleton instance.", nameof(skeleton));
        if (palette.Length != Skeleton.BoneCount)
            throw new ArgumentException($"Palette length {palette.Length} must equal skin bone count {Skeleton.BoneCount}.", nameof(palette));
        if (destination.Length < PartCount)
            throw new ArgumentException($"Output capacity {destination.Length} must hold {PartCount} attachment parts.", nameof(destination));
        if (!ValidMatrix(model))
            throw new ArgumentException("The model matrix must be finite, affine and invertible.", nameof(model));

        Matrix4x4[]? rented = null;
        Span<Matrix4x4> worlds = PartCount <= 128
            ? stackalloc Matrix4x4[PartCount]
            : (rented = ArrayPool<Matrix4x4>.Shared.Rent(PartCount)).AsSpan(0, PartCount);
        try
        {
            for (int i = 0; i < _parts.Length; i++)
            {
                ResolvedPart part = _parts[i];
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
        finally
        {
            if (rented is not null) ArrayPool<Matrix4x4>.Shared.Return(rented);
        }
    }

    static bool ValidMatrix(in Matrix4x4 matrix) => Finite(matrix)
        && matrix.M14 == 0 && matrix.M24 == 0 && matrix.M34 == 0 && matrix.M44 == 1
        && Matrix4x4.Invert(matrix, out Matrix4x4 inverse) && Finite(inverse);

    static bool Finite(in Matrix4x4 matrix)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!float.IsFinite(matrix[row, column])) return false;
        return true;
    }

    readonly record struct ResolvedPart(uint Id, string Joint, int Bone, Matrix4x4 LocalAligned);
}
