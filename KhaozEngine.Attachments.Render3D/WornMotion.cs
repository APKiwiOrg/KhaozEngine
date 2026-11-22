using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Independent chain state for one wearer of a bound set or outfit.</summary>
/// <remarks>Animate the host and step once per frame. Keep one motion per wearer and use it with the binding that
/// created it. The motion owns its CPU state and holds no scene resources. Calls on one motion must not overlap.</remarks>
public sealed class WornMotion
{
    readonly object _bindingIdentity;
    readonly Skeleton _skeleton;
    readonly Dictionary<uint, WornMotionPart>?[] _parts;
    readonly ChainSimulation _simulation;
    readonly int[] _anchorBones;
    readonly WornCollider[] _colliders;
    readonly Matrix4x4[] _anchorWorlds;
    readonly ColliderCapsule[] _colliderWorlds;
    bool _rest = true;

    internal WornMotion(object bindingIdentity, Skeleton skeleton, Dictionary<uint, WornMotionPart>?[] parts,
        ChainRig rig, int[] anchorBones, WornCollider[] colliders, ChainSimulationOptions? options)
    {
        _bindingIdentity = bindingIdentity;
        _skeleton = skeleton;
        _parts = parts;
        _simulation = new ChainSimulation(rig, colliders.Length, options);
        _anchorBones = anchorBones;
        _colliders = colliders;
        _anchorWorlds = new Matrix4x4[anchorBones.Length];
        _colliderWorlds = new ColliderCapsule[colliders.Length];
    }

    internal void RequireBinding(object? bindingIdentity)
    {
        if (!ReferenceEquals(_bindingIdentity, bindingIdentity))
            throw new AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal.MotionMismatch,
                "Use the motion with the bound set or outfit that created it.", "motion");
    }

    /// <summary>Advance the chains from the host's exact skeleton and current model-space joint palette.</summary>
    /// <remarks>Call once after posing the host. Anchors and capsules are posed into world space, where gravity
    /// acts. Step validates the model and the palette matrices read for anchors and colliders. Each draw validates
    /// the matrices it reads and refuses invalid ones. Valid steps allocate no managed memory once warm.</remarks>
    public void Step(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        float deltaSeconds, MotionLevel level = MotionLevel.Simulated)
    {
        AttachmentSetBinding.ValidateFrame(_skeleton, skeleton, palette);
        AttachmentSetBinding.ValidateModel(model);
        for (int anchor = 0; anchor < _anchorBones.Length; anchor++)
            _anchorWorlds[anchor] = PoseJoint(_anchorBones[anchor], palette, model);
        for (int index = 0; index < _colliders.Length; index++)
        {
            ref readonly WornCollider collider = ref _colliders[index];
            _colliderWorlds[index] = collider.Pose(PoseJoint(collider.Bone, palette, model));
        }
        _simulation.Step(_anchorWorlds, _colliderWorlds, model, deltaSeconds, level);
        _rest = false;
    }

    /// <summary>Show the rest hang immediately and clear velocity on the next valid step.</summary>
    public void Reset()
    {
        _simulation.Reset();
        _rest = true;
    }

    /// <summary>Apply settings to a visible part's declared chain starting with the next simulation substep.</summary>
    /// <remarks>Worn indices are zero-based in outfit order. A bare set uses worn index zero.</remarks>
    public void Tune(int wornIndex, uint partId, int chain, in ChainSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wornIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wornIndex, _parts.Length);
        if (_parts[wornIndex] is not { } parts || !parts.TryGetValue(partId, out WornMotionPart part))
            throw new ArgumentOutOfRangeException(nameof(partId), partId, "The worn part has no visible chains.");
        ArgumentOutOfRangeException.ThrowIfNegative(chain);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chain, part.Binding.Chains.Length);
        _simulation.Tune(part.FirstChain + chain, settings);
    }

    // Replace only the chain slots of a garment palette. The supplied anchors may be in model or world space.
    internal void CopyChainPalette(int wornIndex, uint partId, ReadOnlySpan<Matrix4x4> palette,
        Span<Matrix4x4> destination)
    {
        if (_parts[wornIndex] is not { } parts || !parts.TryGetValue(partId, out WornMotionPart part)) return;
        BoundAttachmentChain[] chains = part.Binding.Chains;
        for (int index = 0; index < chains.Length; index++)
        {
            BoundAttachmentChain chain = chains[index];
            ReadOnlySpan<Matrix4x4> frames = _rest ? chain.RestFromAnchor :
                _simulation.JointsFromAnchor.Slice(_simulation.Rig.FirstJoint(part.FirstChain + index), chain.PaletteBones.Length);
            chain.CopyPalette(frames, palette[chain.AnchorBone], destination);
            foreach (int bone in chain.PaletteBones)
                if (!AttachmentSetBinding.ValidMatrix(destination[bone]))
                    throw SkinnedAttachmentBinding.InvalidPose(partId, chain.AnchorBone);
        }
    }

    static Matrix4x4 PoseJoint(int bone, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model)
    {
        Matrix4x4 pose = palette[bone];
        Matrix4x4 world = pose * model;
        if (!AttachmentSetBinding.ValidMatrix(pose) || !AttachmentSetBinding.ValidMatrix(world))
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.HostJointPoseInvalid,
                $"Motion host skin bone {bone} must produce a finite, affine and invertible joint pose.", nameof(palette))
            { BoneIndex = bone };
        return world;
    }
}

internal readonly record struct WornMotionPart(AttachmentChainBinding Binding, int FirstChain);
