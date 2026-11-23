using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The chain motion state of one wearer.</summary>
public sealed class ChainSimulation
{
    private readonly ChainParticles _particles;
    private readonly ChainLinks _links;
    private readonly ChainPresentationBlend _blend;
    private readonly ChainStepPresentation _stepPresentation;
    private ChainStepClock _clock;
    private readonly ChainSettings[] _settings;
    private readonly Matrix4x4[] _jointsFromAnchor;
    private readonly Matrix4x4[] _anchorInverses;
    private readonly Matrix4x4[] _stepAnchors;
    private readonly Matrix4x4[] _stepAnchorInverses;
    private ChainPose[] _previousAnchors;
    private ChainPose[] _frameAnchors;
    private readonly ColliderCapsule[] _previousFrameColliders;
    private ColliderCapsule[] _stepColliders;
    private ColliderCapsule[] _previousStepColliders;
    private ChainPose _previousWearer;
    private Vector3 _stepWearerPosition;
    private Quaternion _stepWearerRotation;
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
        _blend = new ChainPresentationBlend(rig);
        _stepPresentation = new ChainStepPresentation(rig.JointCount);
        _clock = new ChainStepClock(options.MaxStepSeconds, options.MaxStepsPerFrame);
        _settings = new ChainSettings[rig.Chains.Count];
        for (int chain = 0; chain < _settings.Length; chain++)
            _settings[chain] = rig.Chains[chain].Settings;
        _jointsFromAnchor = rig.RestFromAnchor.ToArray();
        _anchorInverses = new Matrix4x4[rig.AnchorCount];
        _stepAnchors = new Matrix4x4[rig.AnchorCount];
        _stepAnchorInverses = new Matrix4x4[rig.AnchorCount];
        _previousAnchors = new ChainPose[rig.AnchorCount];
        _frameAnchors = new ChainPose[rig.AnchorCount];
        _previousFrameColliders = new ColliderCapsule[colliderCount];
        _stepColliders = new ColliderCapsule[colliderCount];
        _previousStepColliders = new ColliderCapsule[colliderCount];
    }

    public ChainRig Rig { get; }
    public int ColliderCount { get; }
    public ChainSimulationOptions Options { get; }

    /// <summary>The displayed joint frames relative to their current anchors.</summary>
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
            _clock.Reset();
            _stepPresentation.Reset(Rig.RestFromAnchor);
            _blend.Begin();
            _resuming = false;
            Rig.RestFromAnchor.CopyTo(_jointsFromAnchor);
        }
        else if (level == MotionLevel.Held)
        {
            _resuming = false;
            if (!_held)
                _blend.Begin();
            _blend.Advance(_jointsFromAnchor, anchorWorlds, _anchorInverses, deltaSeconds, Options.HoldBlendSeconds, toRest: true);
        }
        else
        {
            if (_held)
            {
                _particles.SeedFromShown(_previousAnchors, _jointsFromAnchor);
                _clock.Reset();
                _stepPresentation.Reset(_jointsFromAnchor);
                _blend.Begin();
                _resuming = true;
            }

            Advance(colliders, frameWearer, deltaSeconds);
            if (_resuming)
            {
                _stepPresentation.Show(_clock.Interpolation, _blend.LiveTarget);
                _resuming = _blend.Advance(_jointsFromAnchor, anchorWorlds, _anchorInverses,
                    deltaSeconds, Options.HoldBlendSeconds, toRest: false);
            }
            else
                _stepPresentation.Show(_clock.Interpolation, _jointsFromAnchor);
        }

        (_previousAnchors, _frameAnchors) = (_frameAnchors, _previousAnchors);
        colliders.CopyTo(_previousFrameColliders);
        if (reset || level == MotionLevel.Held)
        {
            colliders.CopyTo(_previousStepColliders);
            _stepWearerPosition = frameWearer.Matrix.Translation;
            _stepWearerRotation = frameWearer.Rotation;
        }
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
        int count = _clock.Advance(deltaSeconds);
        for (int step = 0; step < count; step++)
            Substep(colliders, wearer, _clock.StepFraction(step, count, deltaSeconds));
    }

    private void Substep(ReadOnlySpan<ColliderCapsule> colliders, in ChainPose wearer, float fraction)
    {
        float seconds = Options.MaxStepSeconds;
        for (int anchor = 0; anchor < _stepAnchors.Length; anchor++)
        {
            var current = _frameAnchors[anchor].Matrix;
            var pose = _previousAnchors[anchor].Interpolate(_frameAnchors[anchor], fraction);
            if (pose == current || !ChainMath.TryInvert(pose, out _stepAnchorInverses[anchor]))
            {
                // A change in handedness can make an intermediate pose singular.
                pose = current;
                _stepAnchorInverses[anchor] = _anchorInverses[anchor];
            }
            _stepAnchors[anchor] = pose;
        }
        for (int collider = 0; collider < ColliderCount; collider++)
            _stepColliders[collider] = Interpolate(_previousFrameColliders[collider], colliders[collider], fraction);

        var position = fraction == 1 ? wearer.Matrix.Translation : Vector3.Lerp(_previousWearer.Matrix.Translation, wearer.Matrix.Translation, fraction);
        var rotation = fraction == 1 ? wearer.Rotation : Quaternion.Slerp(_previousWearer.Rotation, wearer.Rotation, fraction);
        var turn = rotation == _stepWearerRotation ? Quaternion.Identity : rotation * Quaternion.Conjugate(_stepWearerRotation);
        _particles.SetRest(_stepAnchors, reset: false);

        for (int chain = 0; chain < _settings.Length; chain++)
        {
            int anchor = Rig.Chains[chain].Anchor;
            _particles.Predict(chain, _settings[chain], Options.Gravity, seconds, _stepWearerPosition, position, turn);
            ChainConstraints.FollowChain(_particles, chain, _stepAnchors[anchor],
                _stepColliders, _previousStepColliders, _settings[chain], seconds, pullTowardRest: true);
        }

        _links.FinishStep(_particles, _stepAnchors, _stepColliders, _previousStepColliders, _settings, seconds);

        _stepWearerPosition = position;
        _stepWearerRotation = rotation;
        _particles.RememberRestDirections();
        (_previousStepColliders, _stepColliders) = (_stepColliders, _previousStepColliders);
        _stepPresentation.Capture(Rig, _particles.JointWorlds, _stepAnchorInverses);
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
