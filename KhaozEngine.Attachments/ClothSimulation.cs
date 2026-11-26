using System;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Bounds for one cloth substep, including all narrow-phase witness evaluations.</summary>
public readonly record struct ClothWorkLimits(int Iterations, int MaxSupports, int MaxContactCandidates,
    int MaxContactChecks, float MaxNodeTravel = .025f, long MaxMaterialFactorProducts = 1_000_000)
{
    /// <summary>Maximum interior sweep samples for one narrow-phase attempt.</summary>
    public int MaxSweepRefinements { get; init; } = ClothContactSweep.RefinementLimit;

    internal const int NormalVelocityCheckLimit = 16_384;

    /// <summary>Maximum normal impulse and complementarity evaluations per substep.</summary>
    public int MaxNormalVelocityChecks { get; init; } = NormalVelocityCheckLimit;
}

/// <summary>Closing seam pins, matching support poses and a fixed world-space time step.</summary>
public readonly ref struct ClothSubstep(ReadOnlySpan<Vector3> pins, ReadOnlySpan<ClothSupport> openingSupports,
    ReadOnlySpan<ClothSupport> closingSupports, float seconds, Vector3 gravity)
{
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
}

/// <summary>GPU-free fixed-step cloth state over a shared material rig.</summary>
public sealed class ClothSimulation
{
    private readonly ClothRig _rig;
    private readonly ClothWorkLimits _limits;
    private readonly ClothMaterialConstraints _material;
    private readonly ClothContact _contact;
    private readonly ClothAirDrag? _airDrag;
    private Vector3[] _velocities;
    private Vector3[] _workingVelocities;
    private Vector3[] _accepted;
    private Vector3[] _working;
    private float _stepSeconds;
    private bool _initialized;

    public ClothSimulation(ClothRig rig, ClothWorkLimits limits)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (limits.Iterations <= 0 || limits.MaxSupports < 0 || limits.MaxContactCandidates < 0 ||
            limits.MaxContactChecks < 0 || limits.MaxNormalVelocityChecks is < 0 or > ClothWorkLimits.NormalVelocityCheckLimit ||
            limits.MaxSweepRefinements is < 0 or > ClothContactSweep.RefinementLimit || limits.MaxMaterialFactorProducts < 0 || !float.IsFinite(limits.MaxNodeTravel) || limits.MaxNodeTravel <= 0)
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

    /// <summary>Advances once. The caller chooses a displayed fallback if the step needs recovery.</summary>
    public ClothStepResult Advance(in ClothSubstep step)
    {
        if (!_initialized) throw new InvalidOperationException("Reset cloth before stepping.");
        Validate(step);
        _stepSeconds = step.Seconds;
        _material.Begin();
        _contact.Begin();

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
        ClothStepStatus status = CheckTravel();
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
            (_velocities, _workingVelocities) = (_workingVelocities, _velocities);
            (_accepted, _working) = (_working, _accepted);
        }
        return new ClothStepResult(status, iterations, _contact.Candidates, _contact.Checks,
            factorizations * _material.FactorProducts)
        {
            NormalVelocityContacts = _contact.VelocityContacts,
            NormalVelocityChecks = _contact.VelocityChecks,
            SweepRefinements = _contact.SweepRefinements,
        };
    }

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

    private ClothStepStatus CheckTravel()
    {
        float squared = _limits.MaxNodeTravel * _limits.MaxNodeTravel;
        for (int i = 0; i < _working.Length; i++)
        {
            if (!ChainMath.IsFinite(_working[i])) return ClothStepStatus.NonfiniteState;
            if (Vector3.DistanceSquared(_accepted[i], _working[i]) > squared) return ClothStepStatus.NodeTravel;
        }
        return ClothStepStatus.Accepted;
    }

    private void Validate(in ClothSubstep step)
    {
        if (!float.IsFinite(step.Seconds) || step.Seconds <= 0 || (_stepSeconds != 0 && _stepSeconds != step.Seconds))
            throw new ArgumentOutOfRangeException(nameof(step), "Cloth requires the same positive fixed duration after each reset.");
        if (!ChainMath.IsFinite(step.Gravity) || step.Pins.Length != _rig.Pins.Length || !AreFinite(step.Pins))
            throw new ArgumentException("The step requires finite gravity and one finite position for every pin.", nameof(step));
        if (step.OpeningSupports.Length != step.ClosingSupports.Length || step.ClosingSupports.Length > _limits.MaxSupports)
            throw new ArgumentException("Opening and closing support poses must match in order and fit the support limit.", nameof(step));
        for (int i = 0; i < step.ClosingSupports.Length; i++)
            if (!step.ClosingSupports[i].Matches(step.OpeningSupports[i]))
                throw new ArgumentException("Each support must keep the same local geometry through a step.", nameof(step));
    }

    private static bool AreFinite(ReadOnlySpan<Vector3> positions)
    {
        foreach (Vector3 position in positions)
            if (!ChainMath.IsFinite(position)) return false;
        return true;
    }
}
