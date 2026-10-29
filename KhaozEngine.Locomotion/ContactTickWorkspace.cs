using System;
using System.Numerics;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>Bounded private scratch for one composed interval and its serial contact ticks.</summary>
internal sealed class ContactTickWorkspace
{
    readonly record struct AnalyticEntry(MovementSpaceKey Space, Vector2 Axis, MovementAnalyticGround? Ground);
    readonly record struct OwnerEntry(StaticHandle Static, int Feature, MovementContactSupportBinding Binding);
    readonly AnalyticEntry[] _analytic = new AnalyticEntry[MovementQueryLease.MaxSupportCandidates];
    readonly OwnerEntry[] _owners = new OwnerEntry[MovementQueryLease.MaxSupportCandidates];
    readonly (MovementDomainKey Domain, float Level)[] _water = new (MovementDomainKey, float)[MovementQueryLease.MaxWaterDomainsPerLease];
    int _waterCount;
    internal int WaterCount => _waterCount;
    internal bool Owns(MovementQueryLease lease) => ReferenceEquals(_lease, lease);
    MovementQueryLease? _lease;
    int _analyticCount, _ownerCount;
    bool _tick;
    internal ContactCapsuleShapes Shapes { get; } = new();
    internal LeasedContactQueryContext Queries { get; } = new();
    internal ExplicitContactModes Modes { get; } = new();
    /// <summary>The current tick's exact repeat support reads (SC-15). It holds geometric certification only. The
    /// producer's analytic sample and binding run for every read, hit or miss.</summary>
    internal SupportRequestMemo SupportMemo { get; } = new();

    internal void Enter(MovementQueryLease lease)
    {
        if (_lease is not null) throw new InvalidOperationException("The contact workspace is already borrowed.");
        _lease = lease;
    }

    internal void BeginTick(MovementQueryLease lease, IPhysicsQueryLease physics)
    {
        if (!ReferenceEquals(_lease, lease) || _tick)
            throw new InvalidOperationException("The contact workspace requires its interval's serial tick.");
        ClearAnalytic();
        // Exact repeat reads are memoized for this tick only, so none crosses the interval's serial ticks.
        SupportMemo.Open(physics);
        _tick = true;
    }

    internal void EndTick()
    {
        Queries.Reset();
        Modes.Reset();
        ClearAnalytic();
        SupportMemo.Close();
        _tick = false;
    }

    internal void EndInterval()
    {
        EndTick();
        Array.Clear(_owners, 0, _ownerCount);
        _ownerCount = 0;
        Array.Clear(_water, 0, _waterCount);
        _waterCount = 0;
        _lease = null;
    }

    void ClearAnalytic()
    {
        Array.Clear(_analytic, 0, _analyticCount);
        _analyticCount = 0;
    }

    internal bool TryGetAnalytic(MovementSpaceKey space, Vector2 axis, out MovementAnalyticGround? ground)
    {
        for (int i = 0; i < _analyticCount; i++)
            if (_analytic[i].Space == space && _analytic[i].Axis == axis)
            {
                ground = _analytic[i].Ground;
                return true;
            }
        ground = null;
        return false;
    }

    internal MovementAvailability RememberAnalytic(MovementSpaceKey space, Vector2 axis, MovementAnalyticGround? ground)
    {
        if (TryGetAnalytic(space, axis, out var previous))
            return SameField(previous, ground) ? MovementAvailability.Known : MovementAvailability.Invalid;
        if (_analyticCount == _analytic.Length) return MovementAvailability.CapacityExceeded;
        _analytic[_analyticCount++] = new(space, axis, ground);
        return MovementAvailability.Known;
    }

    internal MovementAvailability RememberOwner(StaticHandle installed, int feature, in MovementContactSupportBinding binding)
    {
        for (int i = 0; i < _ownerCount; i++)
            if (_owners[i].Static == installed && _owners[i].Feature == feature)
            {
                var previous = _owners[i].Binding;
                return previous.Owner == binding.Owner && previous.Space == binding.Space
                    ? MovementAvailability.Known : MovementAvailability.Invalid;
            }
        if (_ownerCount == _owners.Length) return MovementAvailability.CapacityExceeded;
        _owners[_ownerCount++] = new(installed, feature, binding);
        return MovementAvailability.Known;
    }

    internal bool TryGetWater(MovementDomainKey domain, out float level)
    {
        for (int i = 0; i < _waterCount; i++)
            if (_water[i].Domain == domain)
            {
                level = _water[i].Level;
                return true;
            }
        level = default;
        return false;
    }

    internal MovementAvailability RememberWater(MovementDomainKey domain, float level)
    {
        if (TryGetWater(domain, out float previous))
            return previous == level ? MovementAvailability.Known : MovementAvailability.Invalid;
        if (_waterCount == _water.Length) return MovementAvailability.CapacityExceeded;
        _water[_waterCount++] = (domain, level);
        return MovementAvailability.Known;
    }

    static bool SameField(MovementAnalyticGround? first, MovementAnalyticGround? second) =>
        first is { } a && second is { } b
            ? a.Axis == b.Axis && a.Height == b.Height && a.Normal == b.Normal && a.Owner == b.Owner &&
                a.Space == b.Space && a.Identity == b.Identity && a.HeightTranslation == b.HeightTranslation
            : first.HasValue == second.HasValue;
}
