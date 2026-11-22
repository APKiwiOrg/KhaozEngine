using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The chain motion state of one wearer.</summary>
public sealed class ChainSimulation
{
    private readonly ChainParticles _particles;
    private readonly ChainLinks _links;
    private readonly ChainPresentationBlend _blend;
    private readonly ChainSettings[] _settings;
    private readonly Matrix4x4[] _jointsFromAnchor;
    private readonly Matrix4x4[] _anchorInverses;
    private readonly Matrix4x4[] _stepAnchors;
    private ChainPose[] _previousAnchors;
    private ChainPose[] _frameAnchors;
    private readonly ColliderCapsule[] _previousColliders;
    private readonly ColliderCapsule[] _stepColliders;
    private ChainPose _previousWearer;
    private float _previousStepSeconds;
    private bool _needsReset = true;
    private bool _held;
    private bool _resuming;

    /// <summary>Allocates the state and all step buffers for a rig and a fixed collider count.</summary>
    public ChainSimulation(ChainRig rig, int colliderCount, ChainSimulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentOutOfRangeException.ThrowIfNegative(colliderCount);
        options ??= new ChainSimulationOptions();
        options.Validate();

        Rig = rig;
        ColliderCount = colliderCount;
        Options = options;
        _particles = new ChainParticles(rig);
        _links = new ChainLinks(rig);
        _blend = new ChainPresentationBlend(rig.JointCount);
        _settings = new ChainSettings[rig.Chains.Count];
        for (int chain = 0; chain < _settings.Length; chain++)
            _settings[chain] = rig.Chains[chain].Settings;
        _jointsFromAnchor = rig.RestFromAnchor.ToArray();
        _anchorInverses = new Matrix4x4[rig.AnchorCount];
        _stepAnchors = new Matrix4x4[rig.AnchorCount];
        _previousAnchors = new ChainPose[rig.AnchorCount];
        _frameAnchors = new ChainPose[rig.AnchorCount];
        _previousColliders = new ColliderCapsule[colliderCount];
        _stepColliders = new ColliderCapsule[colliderCount];
    }

    public ChainRig Rig { get; }
    public int ColliderCount { get; }
    public ChainSimulationOptions Options { get; }

    /// <summary>The latest joint frames relative to their anchors, or the rest hang before the first step.</summary>
    public ReadOnlySpan<Matrix4x4> JointsFromAnchor => _jointsFromAnchor;

    /// <summary>Advances to the supplied pose, or blends toward rest while held.</summary>
    public void Step(ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<ColliderCapsule> colliders,
        in Matrix4x4 wearer, float deltaSeconds, MotionLevel level = MotionLevel.Simulated)
    {
        ValidateFrame(anchorWorlds, colliders, wearer, deltaSeconds, level);
        var frameWearer = new ChainPose(wearer);
        for (int anchor = 0; anchor < anchorWorlds.Length; anchor++)
            _frameAnchors[anchor] = new ChainPose(anchorWorlds[anchor]);

        bool reset = _needsReset || deltaSeconds > Options.LongestGapSeconds ||
            ChainMath.DistanceSquared(wearer.Translation, _previousWearer.Matrix.Translation) >
                (double)Options.TeleportDistance * Options.TeleportDistance;
        if (reset)
        {
            _particles.SetRest(anchorWorlds, reset: true);
            _previousStepSeconds = 0;
            _resuming = false;
            if (level == MotionLevel.Held)
                _blend.Begin(_particles, _frameAnchors);
            Rig.RestFromAnchor.CopyTo(_jointsFromAnchor);
        }
        else if (level == MotionLevel.Held)
        {
            _resuming = false;
            if (!_held)
                _blend.Begin(_particles, _previousAnchors);

            if (_blend.Advance(_particles, _frameAnchors, deltaSeconds, Options.HoldBlendSeconds, toRest: true))
                PublishJoints();
            else
            {
                _particles.SetRest(anchorWorlds, reset: true);
                Rig.RestFromAnchor.CopyTo(_jointsFromAnchor);
            }

            _previousStepSeconds = 0;
        }
        else
        {
            if (_held)
            {
                _blend.Begin(_particles, _previousAnchors);
                _resuming = true;
            }

            Advance(colliders, frameWearer, deltaSeconds);
            if (_resuming)
                _resuming = _blend.Advance(_particles, _frameAnchors, deltaSeconds, Options.HoldBlendSeconds, toRest: false);
            PublishJoints();
        }

        (_previousAnchors, _frameAnchors) = (_frameAnchors, _previousAnchors);
        colliders.CopyTo(_previousColliders);
        _previousWearer = frameWearer;
        _held = level == MotionLevel.Held;
        _needsReset = false;
    }

    /// <summary>Requests a rest hang with no velocity on the next valid step.</summary>
    public void Reset() => _needsReset = true;

    /// <summary>Sets a chain's parameters for the next substep without resetting it.</summary>
    public void Tune(int chain, in ChainSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chain);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chain, _settings.Length);
        if (settings.FirstInvalidMember() is { } member)
            throw new ArgumentException($"Chain setting {member} is out of range.", nameof(settings));
        _settings[chain] = settings;
    }

    /// <summary>Composes the latest joint frames on the supplied anchors in rig joint order.</summary>
    public void ComposeJointWorlds(ReadOnlySpan<Matrix4x4> anchorWorlds, Span<Matrix4x4> destination)
    {
        ValidateAnchorCount(anchorWorlds);
        if (destination.Length < Rig.JointCount)
            throw new ArgumentException("Destination must hold every rig joint.", nameof(destination));

        for (int chain = 0; chain < Rig.Chains.Count; chain++)
        {
            var definition = Rig.Chains[chain];
            int first = Rig.FirstJoint(chain);
            var anchor = anchorWorlds[definition.Anchor];
            for (int joint = first; joint < first + definition.Joints.Count; joint++)
                destination[joint] = _jointsFromAnchor[joint] * anchor;
        }
    }

    private void ValidateFrame(ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<ColliderCapsule> colliders,
        in Matrix4x4 wearer, float deltaSeconds, MotionLevel level)
    {
        ValidateAnchorCount(anchorWorlds);
        if (colliders.Length != ColliderCount)
            throw new ArgumentException("Supply one pose per collider.", nameof(colliders));
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (level is not (MotionLevel.Simulated or MotionLevel.Held))
            throw new ArgumentOutOfRangeException(nameof(level));
        if (!ChainMath.IsAffine(wearer))
            throw new ArgumentException("The wearer transform must be finite and affine.", nameof(wearer));

        foreach (ref readonly var capsule in colliders)
        {
            if (!ChainMath.IsFinite(capsule.Start) || !ChainMath.IsFinite(capsule.End) ||
                !float.IsFinite(capsule.Radius) || capsule.Radius < 0)
                throw new ArgumentException("Capsule endpoints must be finite and radii finite and nonnegative.", nameof(colliders));
        }

        // Inverse scratch is unpublished and revalidated on every call.
        for (int anchor = 0; anchor < anchorWorlds.Length; anchor++)
        {
            if (!ChainMath.TryInvert(anchorWorlds[anchor], out _anchorInverses[anchor]))
                throw new ArgumentException($"Anchor {anchor} must have a finite invertible affine transform.", nameof(anchorWorlds));
        }
    }

    private void ValidateAnchorCount(ReadOnlySpan<Matrix4x4> anchorWorlds)
    {
        if (anchorWorlds.Length != Rig.AnchorCount)
            throw new ArgumentException("Supply one pose per rig anchor.", nameof(anchorWorlds));
    }

    private void Advance(ReadOnlySpan<ColliderCapsule> colliders, in ChainPose wearer, float deltaSeconds)
    {
        double maximumTime = (double)Options.MaxStepSeconds * Options.MaxStepsPerFrame;
        int count = (int)Math.Clamp(Math.Ceiling((double)deltaSeconds / Options.MaxStepSeconds), 1, Options.MaxStepsPerFrame);
        float seconds = (float)(Math.Min(deltaSeconds, maximumTime) / count);
        var oldPosition = _previousWearer.Matrix.Translation;
        var oldRotation = _previousWearer.Rotation;
        for (int index = 0; index < count; index++)
        {
            // Interpolate the full pose interval even when excess integration time is dropped.
            float fraction = (index + 1f) / count;
            for (int anchor = 0; anchor < _stepAnchors.Length; anchor++)
                _stepAnchors[anchor] = _previousAnchors[anchor].Interpolate(_frameAnchors[anchor], fraction);
            for (int collider = 0; collider < ColliderCount; collider++)
                _stepColliders[collider] = Interpolate(_previousColliders[collider], colliders[collider], fraction);

            var position = fraction == 1 ? wearer.Matrix.Translation : Vector3.Lerp(_previousWearer.Matrix.Translation, wearer.Matrix.Translation, fraction);
            var rotation = fraction == 1 ? wearer.Rotation : Quaternion.Slerp(_previousWearer.Rotation, wearer.Rotation, fraction);
            var turn = rotation == oldRotation ? Quaternion.Identity : rotation * Quaternion.Conjugate(oldRotation);
            _particles.SetRest(_stepAnchors, reset: false);

            for (int chain = 0; chain < _settings.Length; chain++)
            {
                var definition = Rig.Chains[chain];
                int root = Rig.FirstJoint(chain) + chain;
                _particles.Predict(root, definition.Joints.Count, _settings[chain], Options.Gravity,
                    seconds, _previousStepSeconds, oldPosition, position, turn);
                ChainConstraints.FollowChain(_particles, chain, _stepAnchors[definition.Anchor],
                    _stepColliders, _settings[chain], seconds, pullTowardRest: true);
            }

            if (_links.Apply(_particles, seconds))
            {
                for (int chain = 0; chain < _settings.Length; chain++)
                {
                    int anchor = Rig.Chains[chain].Anchor;
                    ChainConstraints.FollowChain(_particles, chain, _stepAnchors[anchor],
                        _stepColliders, _settings[chain], seconds, pullTowardRest: false);
                }
            }

            oldPosition = position;
            oldRotation = rotation;
            if (seconds > 0)
                _previousStepSeconds = seconds;
        }
    }

    private void PublishJoints()
    {
        for (int chain = 0; chain < Rig.Chains.Count; chain++)
        {
            var definition = Rig.Chains[chain];
            var inverse = _anchorInverses[definition.Anchor];
            int start = Rig.FirstJoint(chain);
            for (int joint = start; joint < start + definition.Joints.Count; joint++)
                _jointsFromAnchor[joint] = _particles.JointWorlds[joint] * inverse;
        }
    }

    private static ColliderCapsule Interpolate(in ColliderCapsule previous, in ColliderCapsule current, float amount) =>
        amount == 1 ? current : new ColliderCapsule(
            InterpolateColliderPoint(previous.Start, current.Start, amount),
            InterpolateColliderPoint(previous.End, current.End, amount),
            float.Lerp(previous.Radius, current.Radius, amount));

    private static Vector3 InterpolateColliderPoint(Vector3 previous, Vector3 current, float amount) =>
        new((float)double.Lerp(previous.X, current.X, amount),
            (float)double.Lerp(previous.Y, current.Y, amount),
            (float)double.Lerp(previous.Z, current.Z, amount));
}
