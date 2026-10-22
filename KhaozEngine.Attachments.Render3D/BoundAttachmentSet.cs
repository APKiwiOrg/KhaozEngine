using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A skeleton binding that borrows its mesh resources from the cache.</summary>
/// <remarks>Use on the cache's owning render thread. Submission calls on this view must not overlap.</remarks>
public sealed class BoundAttachmentSet
{
    readonly AttachmentSetCache _owner;
    readonly AttachmentSetBinding _binding;
    readonly AttachmentSetDefinition _definition;
    readonly IReadOnlyList<AttachmentPartResources> _resources;
    readonly Matrix4x4[] _worlds;
    readonly AttachmentDraw[] _draws;
    readonly Matrix4x4[][] _palettes;
    readonly uint _revisionLow, _revisionHigh;
    readonly int _drawCount;
    readonly bool _hasSkinnedParts;

    internal BoundAttachmentSet(AttachmentSetCache owner, AttachmentSetDefinition definition,
        IReadOnlyList<AttachmentPartResources> resources, AttachmentSetBinding binding)
    {
        _owner = owner;
        _binding = binding;
        _definition = definition;
        _resources = resources;
        foreach (AttachmentPartResources part in resources)
        {
            _hasSkinnedParts |= part.IsSkinned;
            _drawCount += part.IsSkinned ? part.SkinnedHandles.Count : part.RigidHandles.Count;
        }
        _worlds = new Matrix4x4[_binding.PartCount];
        _draws = new AttachmentDraw[_drawCount];
        _palettes = new Matrix4x4[_binding.PartCount][];
        for (int part = 0; part < _palettes.Length; part++)
        {
            int joints = _binding.SkinnedJointCount(_definition.Parts[part].Id);
            _palettes[part] = joints == 0 ? Array.Empty<Matrix4x4>() : new Matrix4x4[joints];
        }
        byte[] revision = Convert.FromHexString(binding.FitIdentity.AsSpan(0, 16));
        _revisionLow = BinaryPrimitives.ReadUInt32LittleEndian(revision);
        _revisionHigh = BinaryPrimitives.ReadUInt32LittleEndian(revision.AsSpan(4));
    }

    public Skeleton Skeleton
    {
        get { _owner.ThrowIfDisposed(); return _binding.Skeleton; }
    }

    /// <summary>Canonical definition and host deformation identity for authored fit evidence.</summary>
    public string FitIdentity
    {
        get { _owner.ThrowIfDisposed(); return _binding.FitIdentity; }
    }

    /// <summary>One draw per material group of every canonical part.</summary>
    public int DrawCount
    {
        get { _owner.ThrowIfDisposed(); return _drawCount; }
    }

    /// <summary>Write borrowed descriptors in canonical part-ID and material order after validating the whole pose.</summary>
    /// <remarks>Replaces Mesh, World and Motion and preserves every other template field. Entries after DrawCount
    /// remain untouched. Valid calls allocate no managed memory. Change instanceKey when replacing an incarnation.</remarks>
    public int WriteDraws(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template,
        Span<RigidInstanceDraw> destination)
    {
        _owner.ThrowIfDisposed();
        if (_hasSkinnedParts)
            throw new InvalidOperationException("Skinned attachment parts require mixed draw submission, not rigid descriptors.");
        ValidateCapacity(destination.Length);
        Prepare(skeleton, palette, model, instanceKey, template);
        for (int draw = 0; draw < _draws.Length; draw++) destination[draw] = _draws[draw].Rigid;
        return _drawCount;
    }

    /// <summary>Write all material draws in canonical part-ID order after preparing the whole pose and template.</summary>
    /// <remarks>Preserves every template field except Mesh, World or Model, and Motion. Garment palettes borrow
    /// this view's preallocated memory until its next preparation or submission, or cache disposal. Consume
    /// synchronously. The unused destination suffix stays untouched. Valid calls allocate no managed memory.</remarks>
    public int WriteDraws(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template,
        Span<AttachmentDraw> destination)
    {
        _owner.ThrowIfDisposed();
        ValidateCapacity(destination.Length);
        Prepare(skeleton, palette, model, instanceKey, template);
        _draws.AsSpan().CopyTo(destination);
        return _drawCount;
    }

    void ValidateCapacity(int capacity)
    {
        if (capacity < _drawCount)
            throw new ArgumentException($"Output capacity {capacity} must hold {_drawCount} attachment draws.", "destination");
    }

    void Prepare(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template)
    {
        if (template.ShadowOnly && !template.CastsShadows)
            throw new ArgumentException("A shadow-only attachment must cast shadows.", nameof(template));
        PreparePose(skeleton, palette, model);
        MotionKey revision = MotionKey.Combine(MotionKey.Combine(instanceKey, _revisionLow), _revisionHigh);
        int draw = 0;
        for (int part = 0; part < _resources.Count; part++)
        {
            uint partId = _definition.Parts[part].Id;
            MotionKey partKey = MotionKey.Combine(revision, partId);
            AttachmentPartResources resources = _resources[part];
            if (resources.IsSkinned)
            {
                for (int material = 0; material < resources.SkinnedHandles.Count; material++)
                {
                    var skinned = new SkinnedInstanceDraw(resources.SkinnedHandles[material], model)
                    {
                        Tint = template.Tint,
                        Material = template.Material,
                        Dissolve = template.Dissolve,
                        DissolveEdgeWidth = template.DissolveEdgeWidth,
                        DissolveEdgeColor = template.DissolveEdgeColor,
                        CastsShadows = template.CastsShadows,
                        ShadowOnly = template.ShadowOnly,
                        InvertShadowDissolve = template.InvertShadowDissolve,
                        DissolveComplement = template.DissolveComplement,
                        Motion = MotionKey.Combine(partKey, (uint)material),
                    };
                    _draws[draw++] = new AttachmentDraw(partId, material, skinned, _palettes[part], resources.Profile);
                }
            }
            else
            {
                for (int material = 0; material < resources.RigidHandles.Count; material++)
                {
                    RigidInstanceDraw rigid = template with
                    {
                        Mesh = resources.RigidHandles[material],
                        World = _worlds[part],
                        Motion = MotionKey.Combine(partKey, (uint)material),
                    };
                    _draws[draw++] = new AttachmentDraw(partId, material, rigid);
                }
            }
        }
    }

    void PreparePose(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model)
    {
        _binding.ComposeWorlds(skeleton, palette, model, _worlds);
        for (int part = 0; part < _palettes.Length; part++)
            if (_resources[part].IsSkinned)
                _binding.CopySkinnedPalette(skeleton, palette, _definition.Parts[part].Id, _palettes[part]);
    }

    /// <summary>Queue the whole posed set with opaque white tint, no dissolve and shadow casting enabled.</summary>
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey)
    {
        var template = new RigidInstanceDraw(default, Matrix4x4.Identity);
        Queue(skeleton, palette, model, instanceKey, template);
    }

    /// <summary>Validate and compose the complete pose before submitting any material group.</summary>
    public void Queue(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MotionKey instanceKey, in RigidInstanceDraw template)
    {
        _owner.ThrowIfDisposed();
        Prepare(skeleton, palette, model, instanceKey, template);
        for (int i = 0; i < _draws.Length; i++)
        {
            AttachmentDraw draw = _draws[i];
            if (draw.IsSkinned) _owner.Scene.DrawSkinned(draw.Skinned, draw.BoneMatrices.Span);
            else _owner.Scene.DrawMesh(draw.Rigid);
        }
    }

    /// <summary>Add the entire posed set to the caller's scene-owned outline group.</summary>
    /// <remarks>Validates the complete pose before any scene call. The scene alone applies garment inverse binds.
    /// Occlusion and target settings belong to the caller's group.</remarks>
    public void QueueOutline(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette,
        in Matrix4x4 model, MeshOutlineGroup group, float dissolve = 0f, bool dissolveComplement = false)
    {
        _owner.ThrowIfDisposed();
        PreparePose(skeleton, palette, model);
        for (int part = 0; part < _resources.Count; part++)
        {
            AttachmentPartResources resources = _resources[part];
            if (resources.IsSkinned)
            {
                for (int material = 0; material < resources.SkinnedHandles.Count; material++)
                    _owner.Scene.DrawSkinnedOutlineDissolved(group, resources.SkinnedHandles[material],
                        _palettes[part], _worlds[part], dissolve, dissolveComplement);
            }
            else
            {
                for (int material = 0; material < resources.RigidHandles.Count; material++)
                    _owner.Scene.DrawMeshOutlineDissolved(group, resources.RigidHandles[material],
                        _worlds[part], dissolve, dissolveComplement);
            }
        }
    }
}
