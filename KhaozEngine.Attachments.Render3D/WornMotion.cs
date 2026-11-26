using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Determinism;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Independent chain state for one wearer of a bound set or outfit.</summary>
/// <remarks>Animate the host and step once per frame. Keep one motion per wearer and use it with the binding that
/// created it. The motion owns its CPU state and holds no scene resources. Calls on one motion must not overlap.</remarks>
public sealed partial class WornMotion
{
    readonly object _bindingIdentity;
    readonly Skeleton _skeleton;
    readonly Dictionary<uint, WornMotionPart>?[] _parts;
    readonly ChainSimulation _simulation;
    readonly int[] _anchorBones;
    readonly WornCollider[] _colliders;
    readonly Matrix4x4[] _anchorWorlds;
    readonly ColliderCapsule[] _colliderWorlds;
    bool _useAuthoredRest = true;

    internal IReadOnlyList<ChainLink> RigLinksForTests => _simulation.Rig.Links;
    internal ReadOnlySpan<Matrix4x4> JointsFromAnchor => _simulation.JointsFromAnchor;

    internal WornMotion(object bindingIdentity, Skeleton skeleton, Dictionary<uint, WornMotionPart>?[] parts,
        ChainRig rig, int[] anchorBones, WornCollider[] colliders, ChainSimulationOptions? options,
        Dictionary<uint, WornClothBinding?>?[]? cloth = null)
    {
        _bindingIdentity = bindingIdentity;
        _skeleton = skeleton;
        _parts = parts;
        _cloth = cloth ?? new Dictionary<uint, WornClothBinding?>?[parts.Length];
        bool hasCloth = cloth is not null && Array.Exists(cloth, parts => parts is not null);
        _lastHost = hasCloth ? new Matrix4x4[skeleton.BoneCount] : [];
        _simulation = new ChainSimulation(rig, colliders.Length, options, fitRestContacts: true);
        _anchorBones = anchorBones;
        _colliders = colliders;
        _anchorWorlds = new Matrix4x4[anchorBones.Length];
        _colliderWorlds = new ColliderCapsule[colliders.Length];
    }

    internal void InitializeRest(ReadOnlySpan<Matrix4x4> bindPose)
    {
        if (!_simulation.HasRestContacts)
            return;

        Step(_skeleton, bindPose, Matrix4x4.Identity, 0, MotionLevel.Held);
        _simulation.Reset();
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
    /// the matrices it reads and refuses invalid ones. Step uses canonical floating-point controls and restores
    /// the caller's controls on return. Valid steps allocate no managed memory once warm.</remarks>
    public void Step(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        float deltaSeconds, MotionLevel level = MotionLevel.Simulated)
        => Step(skeleton, palette, model, deltaSeconds, level, null);

    /// <summary>Advance the wearer, with an optional request for the local cloth slot.</summary>
    public void Step(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        float deltaSeconds, MotionLevel level, WornClothSelection? cloth)
    {
        using var scope = DeterministicFpScope.Enter();
        PrepareStep(skeleton, palette, model, deltaSeconds, level, cloth);
        StepPrepared(palette, model, deltaSeconds, level, cloth);
    }

    // Prepare only unpublished frame scratch so a batch can validate every wearer before advancing any.
    internal void PrepareStep(Skeleton skeleton, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model,
        float deltaSeconds, MotionLevel level, WornClothSelection? cloth)
    {
        AttachmentSetBinding.ValidateFrame(_skeleton, skeleton, palette);
        AttachmentSetBinding.ValidateModel(model);
        Matrix4x4 relative = PrepareClothFrame(skeleton, palette, model, cloth);
        for (int anchor = 0; anchor < _anchorBones.Length; anchor++)
            _anchorWorlds[anchor] = PoseJoint(_anchorBones[anchor], palette, relative);
        for (int index = 0; index < _colliders.Length; index++)
        {
            ref readonly WornCollider collider = ref _colliders[index];
            _colliderWorlds[index] = collider.Pose(PoseJoint(collider.Bone, palette, relative));
        }
        _simulation.PrepareStep(_anchorWorlds, _colliderWorlds, relative, deltaSeconds, level);
    }

    internal void StepPrepared(ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model, float deltaSeconds,
        MotionLevel level, WornClothSelection? cloth)
    {
        Matrix4x4 relative = Relative(model, _preparedOrigin);
        _simulation.Rebase(_origin - _preparedOrigin);
        _origin = _preparedOrigin;
        PoseRevision++;
        if (_simulation.NeedsReset(relative, deltaSeconds))
            HistoryRevision++;
        WornClothState? exterior = BeginCloth(palette, relative, model, deltaSeconds, level, cloth);
        _simulation.StepPrepared(_anchorWorlds, _colliderWorlds, relative, deltaSeconds, level, exterior);
        _useAuthoredRest = false;
        if (exterior is not null)
            EndCloth(exterior, palette, relative, model, deltaSeconds, level);
        if (_lastHost.Length > 0)
        {
            palette.CopyTo(_lastHost);
            _lastModel = model;
            _hasLastHost = true;
        }
    }

    /// <summary>Show the fitted rest hang from the last accepted pose. Clear velocity on the next valid step.</summary>
    public void Reset()
    {
        _useAuthoredRest = !_simulation.HasRestContacts;
        if (!_useAuthoredRest)
        {
            using var scope = DeterministicFpScope.Enter();
            _simulation.ShowResetPose();
        }
        ResetCloth();
        _simulation.Reset();
    }

    /// <summary>Apply settings to a visible part's declared chain starting with the next placement or simulation substep.</summary>
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
        Span<Matrix4x4> destination, bool useClothFallback = false)
    {
        if (!useClothFallback && _activeCloth?.State is { Live: true } state &&
            state.Binding.WornIndex == wornIndex && state.Binding.Surface.PartId == partId)
            return;
        if (_parts[wornIndex] is not { } parts || !parts.TryGetValue(partId, out WornMotionPart part)) return;
        BoundAttachmentChain[] chains = part.Binding.Chains;
        for (int index = 0; index < chains.Length; index++)
        {
            BoundAttachmentChain chain = chains[index];
            ReadOnlySpan<Matrix4x4> frames = _useAuthoredRest ? chain.RestFromAnchor :
                JointsFromAnchor.Slice(_simulation.Rig.FirstJoint(part.FirstChain + index), chain.PaletteBones.Length);
            chain.CopyPalette(frames, palette[chain.AnchorBone], destination);
            foreach (int bone in chain.PaletteBones)
                if (!AttachmentSetBinding.ValidMatrix(destination[bone]))
                    throw SkinnedAttachmentBinding.InvalidPose(partId, chain.AnchorBone);
        }
    }

    internal static Matrix4x4 PoseJoint(int bone, ReadOnlySpan<Matrix4x4> palette, in Matrix4x4 model)
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
