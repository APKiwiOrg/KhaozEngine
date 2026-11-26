using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Bounds for one cloth substep, including all narrow-phase witness evaluations.</summary>
public readonly record struct ClothWorkLimits(int Iterations, int MaxSupports, int MaxContactCandidates,
    int MaxContactChecks, float MaxNodeTravel = .025f, long MaxMaterialFactorProducts = 1_000_000)
{
    /// <summary>Maximum interior sweep samples for one narrow-phase attempt.</summary>
    public int MaxSweepRefinements { get; init; } = ClothContactSweep.RefinementLimit;

    public const int NodeLimit = 128;
    public const int FaceLimit = 256;
    public const int RigidSupportLimit = 64;
    public const int LowerSegmentLimit = 64;
    public const int HullLimit = 2;
    public const int IterationLimit = 4;
    public const int BroadPhaseVisitLimit = 4_096;
    public const int NarrowPhaseCandidateLimit = 512;
    public const int StepsPerFrameLimit = 8;
    public const float LayeredStepSeconds = 1f / 120;

    /// <summary>The near-cloth contact profile. A frame driver submits up to eight fixed substeps.</summary>
    public static ClothWorkLimits LayeredContact => new(IterationLimit, RigidSupportLimit + LowerSegmentLimit,
        NarrowPhaseCandidateLimit, BroadPhaseVisitLimit, MaxMaterialFactorProducts: 2_000_000)
    {
        EnableSelfContact = true,
    };

    /// <summary>Checks non-neighbouring folds of the same material sheet.</summary>
    public bool EnableSelfContact { get; init; }

    public int MaxRigidSupports { get; init; } = RigidSupportLimit;
    public int MaxLowerSegments { get; init; } = LowerSegmentLimit;
    public int MaxHulls { get; init; } = HullLimit;

    internal const int NormalVelocityCheckLimit = 16_384;

    /// <summary>Maximum normal impulse and complementarity evaluations per substep.</summary>
    public int MaxNormalVelocityChecks { get; init; } = NormalVelocityCheckLimit;
}

/// <summary>Closing seam pins, matching support poses and a fixed world-space time step.</summary>
public readonly ref struct ClothSubstep(ReadOnlySpan<Vector3> pins, ReadOnlySpan<ClothSupport> openingSupports,
    ReadOnlySpan<ClothSupport> closingSupports, float seconds, Vector3 gravity, ReadOnlySpan<ClothHullSupport> hulls)
{
    public ClothSubstep(ReadOnlySpan<Vector3> pins, ReadOnlySpan<ClothSupport> openingSupports,
        ReadOnlySpan<ClothSupport> closingSupports, float seconds, Vector3 gravity)
        : this(pins, openingSupports, closingSupports, seconds, gravity, []) { }

    public ReadOnlySpan<ClothHullSupport> Hulls { get; } = hulls;
    public ReadOnlySpan<Vector3> Pins { get; } = pins;
    public ReadOnlySpan<ClothSupport> OpeningSupports { get; } = openingSupports;
    public ReadOnlySpan<ClothSupport> ClosingSupports { get; } = closingSupports;
    public float Seconds { get; } = seconds;
    public Vector3 Gravity { get; } = gravity;
}

public enum ClothStepStatus
{
    Accepted,
    ContactCapacity,
    ContactWork,
    NodeTravel,
    UnresolvedContact,
    NonfiniteState,
    CollapsedMaterial,
    NormalVelocityWork,
    SweepWork,
    SupportCapacity,
    UnsupportedHullTransform,
}

/// <summary>Outcome and attempted work. Factor products bound the work across attempted factorizations.
/// A refusal leaves the previous accepted positions available.</summary>
public readonly record struct ClothStepResult(ClothStepStatus Status, int Iterations, int ContactCandidates,
    int ContactChecks, long MaterialFactorProducts)
{
    /// <summary>Active free witnesses collected by the closing contact check.</summary>
    public int NormalVelocityContacts { get; init; }

    /// <summary>Attempted normal impulse and complementarity evaluations.</summary>
    public int NormalVelocityChecks { get; init; }

    /// <summary>Interior sweep samples across all attempted contacts.</summary>
    public int SweepRefinements { get; init; }

    /// <summary>Broad-phase visits charged to self-contact and included in ContactChecks.</summary>
    public int SelfContactChecks { get; init; }

    /// <summary>The part that reached a contact refusal, when the witness has an owning part.</summary>
    public uint? PartId { get; init; }

    /// <summary>The outcome of the independent fitted recovery check, when requested and attempted.</summary>
    public ClothStepStatus? FallbackStatus { get; init; }

    /// <summary>The owning part that refused the fitted recovery, when there was one.</summary>
    public uint? FallbackPartId { get; init; }

    /// <summary>The work charged to the independent fitted recovery envelope.</summary>
    public ClothContactWork FallbackWork { get; init; }

    public bool Recovered => FallbackStatus == ClothStepStatus.Accepted;
}

/// <summary>Attempted geometric work in one contact envelope.</summary>
public readonly record struct ClothContactWork(int BroadPhaseVisits, int NarrowPhaseCandidates, int SweepRefinements);

/// <summary>GPU-free fixed-step cloth state over a shared material rig.</summary>
public sealed class ClothSimulation
{
    private readonly ClothRig _rig;
    private readonly ClothWorkLimits _limits;
    private readonly ClothMaterialConstraints _material;
    private readonly ClothContact _contact;
    private readonly ClothAirDrag? _airDrag;
    private readonly Vector3[] _displayOpening;
    private readonly Vector3[] _displayWorking;
    private Vector3[] _velocities;
    private Vector3[] _workingVelocities;
    private Vector3[] _accepted;
    private Vector3[] _working;
    private float _stepSeconds;
    private bool _initialized;

    public ClothSimulation(ClothRig rig, ClothWorkLimits limits)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (rig.RestPositions.Length > ClothWorkLimits.NodeLimit || rig.Triangles.Length > ClothWorkLimits.FaceLimit)
            throw new ArgumentOutOfRangeException(nameof(rig));
        if (limits.Iterations is <= 0 or > ClothWorkLimits.IterationLimit ||
            limits.MaxSupports is < 0 or > ClothWorkLimits.RigidSupportLimit + ClothWorkLimits.LowerSegmentLimit ||
            limits.MaxContactCandidates is < 0 or > ClothWorkLimits.NarrowPhaseCandidateLimit ||
            limits.MaxContactChecks is < 0 or > ClothWorkLimits.BroadPhaseVisitLimit ||
            limits.MaxRigidSupports is < 0 or > ClothWorkLimits.RigidSupportLimit ||
            limits.MaxLowerSegments is < 0 or > ClothWorkLimits.LowerSegmentLimit ||
            limits.MaxHulls is < 0 or > ClothWorkLimits.HullLimit ||
            limits.MaxNormalVelocityChecks is < 0 or > ClothWorkLimits.NormalVelocityCheckLimit ||
            limits.MaxSweepRefinements is < 0 or > ClothContactSweep.RefinementLimit ||
            limits.MaxMaterialFactorProducts < 0 || !float.IsFinite(limits.MaxNodeTravel) || limits.MaxNodeTravel <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));

        _rig = rig;
        _limits = limits;
        _material = new ClothMaterialConstraints(rig, limits);
        _contact = new ClothContact(rig, limits);
        if (rig.Material.NormalAirDrag > 0)
            _airDrag = new ClothAirDrag(rig);
        int nodes = rig.RestPositions.Length;
        _velocities = new Vector3[nodes];
        _workingVelocities = new Vector3[nodes];
        _accepted = new Vector3[nodes];
        _working = new Vector3[nodes];
        _displayOpening = new Vector3[nodes];
        _displayWorking = new Vector3[nodes];
    }

    /// <summary>The most recently accepted node positions, or an empty span before reset.</summary>
    public ReadOnlySpan<Vector3> Positions => _initialized ? _accepted : [];

    /// <summary>Copies the fitted positions and discards prior physical velocity and step duration.</summary>
    public void Reset(ReadOnlySpan<Vector3> fittedPositions)
    {
        if (fittedPositions.Length != _accepted.Length || !AreFinite(fittedPositions))
            throw new ArgumentException("Reset needs a finite fitted position for each node.", nameof(fittedPositions));

        if (!_material.HasMaterialFrames(fittedPositions))
            throw new ArgumentException("Reset requires nondegenerate fitted material triangles.", nameof(fittedPositions));

        fittedPositions.CopyTo(_accepted);
        Array.Clear(_velocities);
        _stepSeconds = 0;
        _initialized = true;
    }

    /// <summary>Advances once and retains the latest accepted state on refusal.</summary>
    public ClothStepResult Advance(in ClothSubstep step) => Advance(step, []);

    /// <summary>Advances once with optional fitted recovery. Recovery checks the exact fit at the closing pose.</summary>
    public ClothStepResult Advance(in ClothSubstep step, ReadOnlySpan<Vector3> fittedFallback)
    {
        ClothStepResult attempt = AdvanceCore(step);
        if (attempt.Status == ClothStepStatus.Accepted || fittedFallback.IsEmpty) return attempt;
        ClothStepStatus fallback = CheckFallback(step, fittedFallback);
        return attempt with { FallbackStatus = fallback, FallbackPartId = _contact.PartId, FallbackWork = ContactWork() };
    }

    private ClothStepResult AdvanceCore(in ClothSubstep step)
    {
        if (!_initialized) throw new InvalidOperationException("Reset cloth before stepping.");
        Validate(step);
        _contact.Begin();
        ClothStepStatus status = _contact.Prepare(step);
        if (status != ClothStepStatus.Accepted) return StepResult(status, 0, 0);
        _material.Begin();

        _airDrag?.Begin(_accepted, step.Seconds);
        Vector3 gravityStep = step.Gravity * step.Seconds;
        for (int i = 0; i < _working.Length; i++)
        {
            Vector3 velocity = _velocities[i] + gravityStep;
            if (_airDrag is not null)
                velocity = _airDrag.Damp(i, velocity);
            _working[i] = _accepted[i] + velocity * step.Seconds;
        }
        for (int i = 0; i < _rig.Pins.Length; i++)
            _working[_rig.Pins[i]] = step.Pins[i];

        int iterations = 0, factorizations = 0;
        status = CheckTravel();
        while (status == ClothStepStatus.Accepted && iterations < _limits.Iterations)
        {
            iterations++;
            status = _material.Project(_working, _accepted, step.Seconds, out bool factorAttempted);
            if (factorAttempted) factorizations++;
            if (status != ClothStepStatus.Accepted) break;
            status = CheckTravel();
            if (status == ClothStepStatus.Accepted)
                status = _contact.Project(_accepted, _working, step);
        }
        if (status == ClothStepStatus.Accepted)
            status = CheckTravel();
        if (status == ClothStepStatus.Accepted && !_material.HasMaterialFrames(_working))
            status = ClothStepStatus.CollapsedMaterial;
        if (status == ClothStepStatus.Accepted)
            status = ReconstructVelocities(step.Seconds);
        if (status == ClothStepStatus.Accepted)
            status = _contact.Complete(_accepted, _working, _workingVelocities, step);
        if (status == ClothStepStatus.Accepted)
        {
            _stepSeconds = step.Seconds;
            (_velocities, _workingVelocities) = (_workingVelocities, _velocities);
            (_accepted, _working) = (_working, _accepted);
        }
        return StepResult(status, iterations, factorizations);
    }

    /// <summary>Projects a display sample against the closing supports in a separate, single-pass envelope.</summary>
    public ClothStepResult ProjectDisplay(ReadOnlySpan<Vector3> source, Span<Vector3> displayed, in ClothSubstep pose)
    {
        Validate(pose);
        if (source.Length != _working.Length || displayed.Length != source.Length || !AreFinite(source))
            throw new ArgumentException("Display projection needs one finite position and one output slot per node.", nameof(source));
        source.CopyTo(_displayOpening);
        for (int i = 0; i < _rig.Pins.Length; i++)
            _displayOpening[_rig.Pins[i]] = pose.Pins[i];
        _displayOpening.CopyTo(_displayWorking, 0);
        ClothSubstep closing = ClosingOnly(pose);
        _contact.Begin();
        ClothStepStatus status = _contact.Prepare(closing, closingOnly: true);
        if (status == ClothStepStatus.Accepted)
            status = _contact.Project(_displayOpening, _displayWorking, closing);
        if (status == ClothStepStatus.Accepted)
            status = CheckTravel(_displayOpening, _displayWorking);
        if (status == ClothStepStatus.Accepted && !_material.HasMaterialFrames(_displayWorking))
            status = ClothStepStatus.CollapsedMaterial;
        if (status == ClothStepStatus.Accepted)
            status = _contact.VerifyDisplay(_displayOpening, _displayWorking, closing);
        if (status == ClothStepStatus.Accepted)
            _displayWorking.AsSpan().CopyTo(displayed);
        return StepResult(status, 0, 0);
    }

    private ClothStepStatus CheckFallback(in ClothSubstep step, ReadOnlySpan<Vector3> fitted)
    {
        _contact.Begin();
        if (fitted.Length != _working.Length || !AreFinite(fitted)) return ClothStepStatus.NonfiniteState;
        if (!_material.HasMaterialFrames(fitted)) return ClothStepStatus.CollapsedMaterial;
        for (int i = 0; i < _rig.Pins.Length; i++)
            if (fitted[_rig.Pins[i]] != step.Pins[i]) return ClothStepStatus.UnresolvedContact;
        ClothSubstep closing = ClosingOnly(step);
        ClothStepStatus status = _contact.Prepare(closing, closingOnly: true, allowCoarse: true);
        if (status != ClothStepStatus.Accepted) return status;
        fitted.CopyTo(_displayWorking);
        status = _contact.VerifyFit(_displayWorking, closing);
        if (status == ClothStepStatus.Accepted)
        {
            Reset(fitted);
            _stepSeconds = step.Seconds;
        }
        return status;
    }

    private static ClothSubstep ClosingOnly(in ClothSubstep step) => new(step.Pins, step.ClosingSupports,
        step.ClosingSupports, step.Seconds, step.Gravity, step.Hulls);

    private ClothContactWork ContactWork() => new(_contact.Checks, _contact.Candidates, _contact.SweepRefinements);

    private ClothStepResult StepResult(ClothStepStatus status, int iterations, int factorizations) =>
        new(status, iterations, _contact.Candidates, _contact.Checks, factorizations * _material.FactorProducts)
        {
            NormalVelocityContacts = _contact.VelocityContacts,
            NormalVelocityChecks = _contact.VelocityChecks,
            SweepRefinements = _contact.SweepRefinements,
            SelfContactChecks = _contact.SelfContactChecks,
            PartId = _contact.PartId,
        };

    private ClothStepStatus ReconstructVelocities(float seconds)
    {
        ReadOnlySpan<Vector3> positioningBias = _contact.PositionBias;
        for (int i = 0; i < _working.Length; i++)
        {
            _workingVelocities[i] = (_working[i] - _accepted[i] - positioningBias[i]) / seconds;
            if (!ChainMath.IsFinite(_workingVelocities[i])) return ClothStepStatus.NonfiniteState;
        }
        return ClothStepStatus.Accepted;
    }

    private ClothStepStatus CheckTravel() => CheckTravel(_accepted, _working);

    private ClothStepStatus CheckTravel(Vector3[] opening, Vector3[] working)
    {
        float squared = _limits.MaxNodeTravel * _limits.MaxNodeTravel;
        for (int i = 0; i < working.Length; i++)
        {
            if (!ChainMath.IsFinite(working[i])) return ClothStepStatus.NonfiniteState;
            if (Vector3.DistanceSquared(opening[i], working[i]) > squared) return ClothStepStatus.NodeTravel;
        }
        return ClothStepStatus.Accepted;
    }

    private void Validate(in ClothSubstep step)
    {
        if (!float.IsFinite(step.Seconds) || step.Seconds <= 0 || (_stepSeconds != 0 && _stepSeconds != step.Seconds))
            throw new ArgumentOutOfRangeException(nameof(step), "Cloth requires the same positive fixed duration after each reset.");
        if (!ChainMath.IsFinite(step.Gravity) || step.Pins.Length != _rig.Pins.Length || !AreFinite(step.Pins))
            throw new ArgumentException("The step requires finite gravity and one finite position for every pin.", nameof(step));
        if (step.OpeningSupports.Length != step.ClosingSupports.Length)
            throw new ArgumentException("Opening and closing support poses must match in order.", nameof(step));
        for (int i = 0; i < Math.Min(step.ClosingSupports.Length, _limits.MaxSupports); i++)
            if (!step.ClosingSupports[i].Matches(step.OpeningSupports[i]))
                throw new ArgumentException("Each support must keep the same identity and kind through a step.", nameof(step));
        for (int i = 0; i < Math.Min(step.Hulls.Length, _limits.MaxHulls); i++)
        {
            ClothHullSupport hull = step.Hulls[i];
            if (hull.Hull is null || hull.Identity.FeatureGroup <= 0)
                throw new ArgumentException("Each hull requires geometry and a positive feature group.", nameof(step));
            for (int j = 0; j < i; j++)
                if (hull.Identity.SameGroup(step.Hulls[j].Identity))
                    throw new ArgumentException("A feature group admits one detailed hull.", nameof(step));
        }
    }

    private static bool AreFinite(ReadOnlySpan<Vector3> positions)
    {
        foreach (Vector3 position in positions)
            if (!ChainMath.IsFinite(position)) return false;
        return true;
    }
}
