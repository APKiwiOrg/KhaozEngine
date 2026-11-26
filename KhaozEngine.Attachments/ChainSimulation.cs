using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The chain motion state of one wearer.</summary>
public sealed class ChainSimulation
{
    private readonly ChainParticles _particles;
    private readonly ChainSolveTiers _tiers;
    private readonly ChainPresentationBlend _blend;
    private readonly ChainStepPresentation _stepPresentation;
    private ChainStepClock _clock;
    private readonly ChainSettings[] _settings;
    private readonly ChainPresentationFrame _shown;
    private readonly ChainPresentationAnchor[] _framePresentationAnchors;
    private readonly Matrix4x4[] _stepAnchors;
    private readonly Matrix4x4[] _stepAnchorInverses;
    private ChainPose[] _previousAnchors;
    private ChainPose[] _frameAnchors;
    private readonly ColliderCapsule[] _previousFrameColliders;
    private readonly ChainRestPlacement? _restPlacement;
    private ColliderCapsule[] _stepColliders;
    private ColliderCapsule[] _previousStepColliders;
    private ChainPose _previousWearer;
    private Vector3 _stepWearerPosition;
    private Quaternion _stepWearerRotation;
    private bool _needsReset = true;
    private bool _held;
    private bool _resuming;
    private ChainExclusion _lastExclusion;

    /// <summary>Allocates the state and all step buffers for a rig and a fixed collider count.</summary>
    public ChainSimulation(ChainRig rig, int colliderCount, ChainSimulationOptions? options = null)
        : this(rig, colliderCount, options, fitRestContacts: false)
    {
    }

    internal ChainSimulation(ChainRig rig, int colliderCount, ChainSimulationOptions? options, bool fitRestContacts)
    {
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentOutOfRangeException.ThrowIfNegative(colliderCount);
        options ??= new ChainSimulationOptions();
        options.Validate();

        Rig = rig;
        ColliderCount = colliderCount;
        Options = options;
        _tiers = new ChainSolveTiers(rig, colliderCount);
        _particles = new ChainParticles(rig, _tiers.ObstacleCount, _tiers.PlacementRows) { SolidCapsuleCount = colliderCount };
        if (fitRestContacts && _tiers.ObstacleCount > 0 && rig.JointCount > 0)
            _restPlacement = new ChainRestPlacement(_particles, _tiers, colliderCount);
        var hierarchy = new ChainPresentationHierarchy(rig);
        _blend = new ChainPresentationBlend(rig, hierarchy);
        _stepPresentation = new ChainStepPresentation(rig, hierarchy);
        _clock = new ChainStepClock(options.MaxStepSeconds, options.MaxStepsPerFrame);
        _settings = new ChainSettings[rig.Chains.Count];
        for (int chain = 0; chain < _settings.Length; chain++)
            _settings[chain] = rig.Chains[chain].Settings;
        _shown = new ChainPresentationFrame(rig);
        rig.RestFromAnchor.CopyTo(_shown.Frames);
        _framePresentationAnchors = new ChainPresentationAnchor[rig.AnchorCount];
        _stepAnchors = new Matrix4x4[rig.AnchorCount];
        _stepAnchorInverses = new Matrix4x4[rig.AnchorCount];
        _previousAnchors = new ChainPose[rig.AnchorCount];
        _frameAnchors = new ChainPose[rig.AnchorCount];
        _previousFrameColliders = new ColliderCapsule[colliderCount];
        _stepColliders = new ColliderCapsule[_tiers.ObstacleCount];
        _previousStepColliders = new ColliderCapsule[_tiers.ObstacleCount];
    }

    public ChainRig Rig { get; }
    public int ColliderCount { get; }
    public ChainSimulationOptions Options { get; }

    /// <summary>The displayed joint frames relative to their current anchors.</summary>
    public ReadOnlySpan<Matrix4x4> JointsFromAnchor => _shown.Frames;

    /// <summary>Advances to the supplied pose, or blends toward rest while held.</summary>
    public void Step(ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<ColliderCapsule> colliders,
        in Matrix4x4 wearer, float deltaSeconds, MotionLevel level = MotionLevel.Simulated)
    {
        PrepareStep(anchorWorlds, colliders, wearer, deltaSeconds, level);
        StepPrepared(anchorWorlds, colliders, wearer, deltaSeconds, level);
    }

    internal void StepPrepared(ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<ColliderCapsule> colliders,
        in Matrix4x4 wearer, float deltaSeconds, MotionLevel level, IChainExternalStep? exterior = null)
    {
        var frameWearer = new ChainPose(wearer);

        bool reset = NeedsReset(wearer, deltaSeconds);
        ChainExclusion excluded = exterior?.Excluded ?? default;
        if (excluded != _lastExclusion)
            _tiers.SeedOpeningRadii(Rig, _previousStepColliders, _settings, excluded);
        _lastExclusion = excluded;
        if (reset)
        {
            ShowRest(anchorWorlds, _frameAnchors, colliders);
            if (_restPlacement is null)
                _particles.SetRest(anchorWorlds, reset: true);
            else
                _particles.SeedFromShown(_frameAnchors, _shown.Frames);
            _tiers.SeedOpeningRadii(Rig, _previousStepColliders, _settings, excluded);
            _clock.Reset();
            _stepPresentation.Reset(_shown);
            _blend.Finish(Options.HoldBlendSeconds);
            _resuming = false;
        }
        else if (level == MotionLevel.Held)
        {
            // Retain the opening fitted shape for an exterior consumer of held tick geometry.
            if (exterior is not null) _stepPresentation.BeginHeld(_shown);
            Hold(anchorWorlds, colliders, deltaSeconds, excluded);
            if (exterior is not null)
            {
                int count = _clock.Advance(deltaSeconds, out bool droppedTime);
                if (droppedTime) exterior.OnDroppedTime();
                for (int step = 0; step < count; step++)
                {
                    float fraction = _clock.StepFraction(step, count, deltaSeconds);
                    if (fraction < 1) SampleHeldShape(fraction);
                    exterior.OnSubstep(this, null, fraction);
                }
            }
        }
        else
        {
            if (_held)
            {
                _restPlacement?.Invalidate();
                _particles.SeedFromShown(_previousAnchors, _shown.Frames);
                var openingSettings = _restPlacement is { } placement ? placement.OpeningSettings(_settings) : _settings;
                _tiers.SeedOpeningRadii(Rig, _previousStepColliders, openingSettings, excluded);
                if (exterior is null) _clock.Reset();
                _stepPresentation.Reset(_shown);
                _blend.Begin();
                _resuming = true;
            }

            Advance(colliders, frameWearer, deltaSeconds, excluded, exterior);
            if (_resuming)
            {
                _restPlacement?.BeginBlend(_shown);
                if (deltaSeconds > 0 || Options.HoldBlendSeconds == 0)
                {
                    _stepPresentation.Show(_clock.Interpolation, _frameAnchors, _framePresentationAnchors, _blend.LiveTarget);
                    _resuming = _blend.Advance(_shown, _frameAnchors, _framePresentationAnchors,
                        deltaSeconds, Options.HoldBlendSeconds, _blend.LiveTarget);
                }
                ProjectBlend(anchorWorlds, colliders, excluded);
            }
            else if (deltaSeconds > 0)
                _stepPresentation.Show(_clock.Interpolation, _frameAnchors, _framePresentationAnchors, _shown);
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
        _restPlacement?.RememberSettings(_settings);
        _held = level == MotionLevel.Held;
        _needsReset = false;
    }

    /// <summary>Requests a rest hang with no velocity on the next valid step.</summary>
    public void Reset() => _needsReset = true;

    internal bool NeedsReset(in Matrix4x4 wearer, float seconds) =>
        _needsReset || seconds > Options.LongestGapSeconds ||
        ChainMath.DistanceSquared(wearer.Translation, _previousWearer.Matrix.Translation) >
            (double)Options.TeleportDistance * Options.TeleportDistance;

    internal float Interpolation => _clock.Interpolation;
    internal float Radius(int chain) => _settings[chain].Radius;

    internal ColliderCapsule ShownSegment(in ChainSegment segment)
    {
        int anchor = Rig.Chains[segment.Chain].Anchor;
        return segment.Pose(_shown.Frames, _previousAnchors[anchor].Matrix, Radius(segment.Chain));
    }

    internal bool IsHeld => _held;

    internal HeldChainSegment ShownHeldSegment(in ChainSegment segment)
    {
        int anchor = Rig.Chains[segment.Chain].Anchor;
        return segment.PoseHeld(_shown.Frames, _previousAnchors[anchor], Radius(segment.Chain));
    }

    internal HeldChainSegment HeldSegment(in ChainSegment segment, float fraction)
    {
        int anchor = Rig.Chains[segment.Chain].Anchor;
        if (fraction == 1) return segment.PoseHeld(_shown.Frames, _frameAnchors[anchor], Radius(segment.Chain));
        return segment.PoseHeld(_stepPresentation.HeldFrames, new ChainPose(_stepAnchors[anchor]), Radius(segment.Chain));
    }

    private void SampleHeldShape(float fraction)
    {
        SampleAnchors(fraction);
        Span<ChainPose> poses = stackalloc ChainPose[Rig.AnchorCount];
        Span<ChainPresentationAnchor> frame = stackalloc ChainPresentationAnchor[Rig.AnchorCount];
        for (int i = 0; i < poses.Length; i++)
        {
            poses[i] = new ChainPose(_stepAnchors[i]);
            frame[i] = new ChainPresentationAnchor(_stepAnchors[i], _stepAnchorInverses[i], poses[i].Rotation);
        }
        _stepPresentation.SampleHeld(_shown, poses, frame, fraction);
    }

    internal void SeedRange(int first, int count) => _particles.SeedRange(_previousAnchors, _shown.Frames, first, count);

    internal void SeedFittedRange(ChainExclusion range)
    {
        if (_restPlacement is not { } placement)
        {
            SeedRange(range.First, range.Count);
            return;
        }

        Span<ChainPresentationAnchor> presentation = stackalloc ChainPresentationAnchor[_previousAnchors.Length];
        for (int i = 0; i < presentation.Length; i++)
        {
            var anchor = _previousAnchors[i];
            _stepAnchors[i] = anchor.Matrix;
            ChainMath.TryInvert(anchor.Matrix, out var inverse);
            presentation[i] = new(anchor.Matrix, inverse, anchor.Rotation);
        }
        placement.UpdateFit(_stepAnchors, _previousAnchors, presentation, _previousFrameColliders, _settings);
        int first = Rig.FirstJoint(range.First);
        int stop = range.First + range.Count;
        int end = stop == Rig.Chains.Count ? Rig.JointCount : Rig.FirstJoint(stop);
        var joints = new Range(first, end);
        _shown.CopyJoints(placement.Fit, joints);
        _stepPresentation.SeedJoints(placement.Fit, joints);
        _particles.SeedRange(_previousAnchors, placement.Fit.Frames, range.First, range.Count);
    }

    internal void Rebase(Vector3 change)
    {
        if (change == Vector3.Zero) return;
        _restPlacement?.Invalidate();
        _previousWearer = Translate(_previousWearer);
        _stepWearerPosition += change;
        for (int i = 0; i < _previousAnchors.Length; i++)
            _previousAnchors[i] = Translate(_previousAnchors[i]);
        Shift(_previousFrameColliders);
        Shift(_previousStepColliders);
        _particles.Rebase(change);

        ChainPose Translate(in ChainPose pose)
        {
            var matrix = pose.Matrix;
            matrix.Translation += change;
            return new(matrix);
        }

        void Shift(Span<ColliderCapsule> capsules)
        {
            for (int i = 0; i < capsules.Length; i++)
                capsules[i] = capsules[i] with { Start = capsules[i].Start + change, End = capsules[i].End + change };
        }
    }

    internal bool HasRestContacts => _restPlacement is not null;

    // WornMotion has already accepted a pose, starting with the bind pose when contact is enabled.
    internal void ShowResetPose()
    {
        for (int index = 0; index < _previousAnchors.Length; index++)
        {
            var anchor = _previousAnchors[index];
            _stepAnchors[index] = anchor.Matrix;
            ChainMath.TryInvert(anchor.Matrix, out var inverse);
            _framePresentationAnchors[index] = new ChainPresentationAnchor(anchor.Matrix, inverse, anchor.Rotation);
        }
        ShowRest(_stepAnchors, _previousAnchors, _previousFrameColliders);
    }

    private void ShowRest(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ChainPose> poses, ReadOnlySpan<ColliderCapsule> colliders)
    {
        if (_restPlacement is null)
        {
            _shown.SetRest(_framePresentationAnchors);
            return;
        }

        _restPlacement.UpdateFit(anchors, poses, _framePresentationAnchors, colliders, _settings);
        _shown.CopyFrom(_restPlacement.Fit);
    }

    private void Hold(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> colliders, float seconds, ChainExclusion excluded)
    {
        _resuming = false;
        if (!_held)
            _blend.Begin();

        float duration = Options.HoldBlendSeconds;
        var placement = _restPlacement;
        if (placement is not null)
        {
            bool changed = placement.UpdateFit(anchors, _frameAnchors, _framePresentationAnchors, colliders, _settings,
                _previousAnchors, _shown.Frames, _previousFrameColliders, excluded);
            if (_held && !changed && !_blend.Continues(0, duration))
                return;
            if (_blend.Continues(seconds, duration))
                placement.BeginBlend(_shown);
        }

        if (_blend.Advance(_shown, _frameAnchors, _framePresentationAnchors, seconds, duration, placement?.Fit))
            ProjectBlend(anchors, colliders, excluded);
    }

    private void ProjectBlend(ReadOnlySpan<Matrix4x4> anchors, ReadOnlySpan<ColliderCapsule> colliders, ChainExclusion excluded) =>
        _restPlacement?.ProjectBlend(anchors, _frameAnchors, _framePresentationAnchors, colliders,
            _previousFrameColliders, _settings, _shown, _previousAnchors, excluded);

    /// <summary>Sets a chain's parameters for the next substep without resetting it.</summary>
    public void Tune(int chain, in ChainSettings settings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chain);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chain, _settings.Length);
        if (settings.FirstInvalidMember() is { } member)
            throw new ArgumentException($"Chain setting {member} is out of range.", nameof(settings));
        _settings[chain] = settings;
        _restPlacement?.Invalidate();
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
                destination[joint] = _shown.Frames[joint] * anchor;
        }
    }

    internal void PrepareStep(ReadOnlySpan<Matrix4x4> anchorWorlds, ReadOnlySpan<ColliderCapsule> colliders,
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

        // Anchor scratch is unpublished and revalidated on every call.
        for (int anchor = 0; anchor < anchorWorlds.Length; anchor++)
        {
            ref readonly var world = ref anchorWorlds[anchor];
            if (!ChainMath.TryInvert(world, out var inverse))
                throw new ArgumentException($"Anchor {anchor} must have a finite invertible affine transform.", nameof(anchorWorlds));

            var pose = new ChainPose(world);
            _frameAnchors[anchor] = pose;
            _framePresentationAnchors[anchor] = new ChainPresentationAnchor(world, inverse, pose.Rotation);
        }
    }

    private void ValidateAnchorCount(ReadOnlySpan<Matrix4x4> anchorWorlds)
    {
        if (anchorWorlds.Length != Rig.AnchorCount)
            throw new ArgumentException("Supply one pose per rig anchor.", nameof(anchorWorlds));
    }

    private void Advance(ReadOnlySpan<ColliderCapsule> colliders, in ChainPose wearer, float deltaSeconds,
        ChainExclusion excluded, IChainExternalStep? exterior)
    {
        int count = _clock.Advance(deltaSeconds, out bool droppedTime);
        if (droppedTime)
        {
            SkipToRetainedStep(colliders, wearer, _clock.StepFraction(-1, count, deltaSeconds), excluded);
            exterior?.OnDroppedTime();
        }

        for (int step = 0; step < count; step++)
            Substep(colliders, wearer, _clock.StepFraction(step, count, deltaSeconds), excluded, exterior);
    }

    private void Substep(ReadOnlySpan<ColliderCapsule> colliders, in ChainPose wearer, float fraction,
        ChainExclusion excluded, IChainExternalStep? exterior)
    {
        float seconds = Options.MaxStepSeconds;
        _particles.SnapshotOpening();
        SampleAnchors(fraction);
        for (int collider = 0; collider < ColliderCount; collider++)
        {
            _stepColliders[collider] = Interpolate(_previousFrameColliders[collider], colliders[collider], fraction);
            _particles.SetCapsuleGeometry(collider, _stepColliders[collider], _previousStepColliders[collider]);
        }

        var (position, rotation) = SampleWearer(wearer, fraction);
        var turn = WearerTurn(rotation);
        _particles.SetRest(_stepAnchors, reset: false);

        _tiers.Solve(_particles, _stepAnchors, _stepColliders, _previousStepColliders,
            _settings, Options.Gravity, seconds, _stepWearerPosition, position, turn, excluded);
        exterior?.OnSubstep(this, _particles, fraction);

        _stepWearerPosition = position;
        _stepWearerRotation = rotation;
        _stepPresentation.Capture(_particles, _stepAnchors, _stepAnchorInverses, excluded);
        _particles.RememberAnchorRotations();
        (_previousStepColliders, _stepColliders) = (_stepColliders, _previousStepColliders);
    }

    private void SkipToRetainedStep(ReadOnlySpan<ColliderCapsule> colliders, in ChainPose wearer, float fraction,
        ChainExclusion excluded)
    {
        SampleAnchors(fraction);
        var (position, rotation) = SampleWearer(wearer, fraction);
        var turn = WearerTurn(rotation);
        _particles.SetRest(_stepAnchors, reset: false);
        for (int chain = 0; chain < _settings.Length; chain++)
            if (excluded.Includes(chain)) _particles.CarryAcrossDroppedTime(chain, _settings[chain].Trail, _stepWearerPosition, position, turn);

        // Seed the first retained step from its actual opening poses without taking a physics step.
        _stepWearerPosition = position;
        _stepWearerRotation = rotation;
        _particles.RememberAnchorRotations();
        for (int collider = 0; collider < ColliderCount; collider++)
            _previousStepColliders[collider] = Interpolate(_previousFrameColliders[collider], colliders[collider], fraction);
    }

    private void SampleAnchors(float fraction)
    {
        for (int anchor = 0; anchor < _stepAnchors.Length; anchor++)
        {
            var current = _frameAnchors[anchor].Matrix;
            var pose = _previousAnchors[anchor].Interpolate(_frameAnchors[anchor], fraction);
            if (pose == current || !ChainMath.TryInvert(pose, out _stepAnchorInverses[anchor]))
            {
                // A change in handedness can make an intermediate pose singular.
                pose = current;
                _stepAnchorInverses[anchor] = _framePresentationAnchors[anchor].Inverse;
            }
            _stepAnchors[anchor] = pose;
        }
    }

    private (Vector3 Position, Quaternion Rotation) SampleWearer(in ChainPose wearer, float fraction) =>
        fraction == 1 ? (wearer.Matrix.Translation, wearer.Rotation) :
            (Vector3.Lerp(_previousWearer.Matrix.Translation, wearer.Matrix.Translation, fraction),
            Quaternion.Slerp(_previousWearer.Rotation, wearer.Rotation, fraction));

    private Quaternion WearerTurn(Quaternion rotation) =>
        rotation == _stepWearerRotation ? Quaternion.Identity : rotation * Quaternion.Conjugate(_stepWearerRotation);

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
