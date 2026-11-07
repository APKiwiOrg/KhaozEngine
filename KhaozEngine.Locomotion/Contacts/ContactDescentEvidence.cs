using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

internal enum ContactDescentEvidenceStatus : byte
{ Unavailable, Complete, AnalyticIdentityUnavailable, SupportUnavailable, Invalid, CapacityExceeded, StepRefused }
internal enum ContactDescentMode : byte { Ground, Slide, Air, Water }

/// <summary>Copied selected support. Canonical analytic identity is optional and its absence refuses evidence.</summary>
internal readonly record struct ContactSupportEvidence(Vector3 Feet, SupportStatus Status, float Height,
    float HeightError, Vector3 Normal, Vector3 Witness, MovementContactSupportBinding Binding, MovementSurfaceFeature? Feature);

/// <summary>A callback-only view. Local installed addresses never escape as portable identity, and the mutable
/// support arena is not exposed. Callbacks are pure and finish before the authenticated interval closes.</summary>
internal readonly ref struct ContactSelectedSupport(MovementQueryLease lease, ContactSupportEvidence facts,
    ContactDescentMode mode, StaticHandle? installed, int featureId, bool firstWalkable)
{
    internal ContactSupportEvidence Facts { get; } = facts;
    internal ContactDescentMode Mode { get; } = mode;
    internal StaticHandle? Installed { get; } = installed;
    internal int FeatureId { get; } = featureId;
    internal bool FirstWalkable { get; } = firstWalkable;
    internal void AssertCurrent() => lease.AssertCurrent();
    internal bool AnalyticSupportsAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second) =>
        lease.AnalyticSupportsAdjacent(first, second);
}
internal delegate bool ContactDescentValidator(in ContactSelectedSupport support);

/// <summary>Monotonic selected-pose water membership and selected water-mode history, not a swept water certificate.</summary>
internal readonly record struct ContactDescentSummary(ContactDescentEvidenceStatus Status,
    ContactSupportEvidence? FirstWalkableSupport, bool Steep, bool Water, bool Swimming, bool WaterExcursion, bool Accepted);

/// <summary>Fixed opt-in scratch reused across serial ticks. Overflow refuses evidence and never changes movement.
/// Only selected branches reach the callback. Results copy no lease, arena or local physics address.</summary>
internal sealed class ContactDescentEvidence(ContactDescentValidator? validator = null)
{
    internal const int Capacity = 256;
    readonly Entry[] _entries = new Entry[Capacity];
    int _count;
    bool _completed;
    ContactDescentEvidenceStatus _status;
    readonly record struct Entry(ContactSupportEvidence Facts, ContactDescentMode Mode, StaticHandle? Installed,
        int FeatureId, bool Water, bool Swimming, bool Excursion, bool SupportEvent);
    internal readonly record struct Mark(int Count, ContactDescentEvidenceStatus Status);
    internal ContactDescentSummary Summary { get; private set; }

    internal void Begin()
    {
        Clear();
        _completed = false;
        _status = ContactDescentEvidenceStatus.Complete;
        Summary = default;
    }

    internal Mark Checkpoint() => new(_count, _status);
    internal Mark BeginAlternative(Mark start)
    {
        Mark first = Checkpoint();
        _status = start.Status;
        return first;
    }

    // Slide's steer and carry-only proposals must not lend either identity or medium history to each other.
    internal void SelectAlternative(Mark start, Mark first, bool alternative)
    {
        int end = alternative ? start.Count + _count - first.Count : first.Count;
        if (alternative) Array.Copy(_entries, first.Count, _entries, start.Count, _count - first.Count);
        else _status = first.Status;
        Array.Clear(_entries, end, _count - end);
        _count = end;
    }

    internal void Record(ContactQueryContext queries, Vector3 feet, in SupportSample sample,
        ContactDescentMode mode, float radius, float gate)
    {
        try
        {
            MovementContactSupportBinding binding = default;
            MovementSurfaceFeature? feature = null;
            if (sample.Status is SupportStatus.Walkable or SupportStatus.Steep)
            {
                binding = queries.BindSupport(feet, sample, radius, gate) ?? default;
                if (!binding.IsValid) Fail(ContactDescentEvidenceStatus.SupportUnavailable);
                else if (sample.Static is { } installed)
                {
                    SupportContinuitySource source = sample.Source;
                    if (source.Result.QueryWorld is not IPhysicsSupportFootprint issuer ||
                        source.Result.Lease is not IPhysicsQueryLease physics)
                        Fail(ContactDescentEvidenceStatus.SupportUnavailable);
                    else
                    {
                        issuer.AssertFootprintCurrent(source.Result, physics);
                        if (sample.Joined.Selected != new SupportMemberKey(installed, sample.FeatureId))
                            Fail(ContactDescentEvidenceStatus.Invalid);
                    }
                }
                else
                {
                    var status = queries.DescribeSelectedAnalytic(feet, sample, out var described);
                    if (status == MovementAvailability.Known) feature = described;
                    else Fail(status == MovementAvailability.Invalid ? ContactDescentEvidenceStatus.Invalid :
                        ContactDescentEvidenceStatus.AnalyticIdentityUnavailable);
                }
            }
            else if (sample.Status == SupportStatus.Refused) Fail(ContactDescentEvidenceStatus.SupportUnavailable);
            var point = queries.SampleMedium(feet);
            Add(new(new(feet, sample.Status, sample.Height, sample.HeightError, sample.Normal, sample.Witness, binding, feature),
                mode, sample.Static, sample.FeatureId, point?.InWater == true, false, false, true));
        }
        catch (ContactQueryFailure) { Fail(ContactDescentEvidenceStatus.SupportUnavailable); }
        catch (InvalidOperationException) { Fail(ContactDescentEvidenceStatus.SupportUnavailable); }
        catch (ArgumentException) { Fail(ContactDescentEvidenceStatus.Invalid); }
    }

    internal void Medium(in MovementWaterPoint point, bool swimming, WaterExcursionState excursion) =>
        Add(new(default, ContactDescentMode.Water, null, -1, point.InWater, swimming,
            excursion != WaterExcursionState.None, false));

    void Fail(ContactDescentEvidenceStatus status)
    {
        if (_status == ContactDescentEvidenceStatus.Complete) _status = status;
    }
    void Add(in Entry entry)
    {
        if (_count == _entries.Length) { _status = ContactDescentEvidenceStatus.CapacityExceeded; return; }
        _entries[_count++] = entry;
    }

    internal void Complete(MovementQueryLease lease)
    {
        ContactSupportEvidence? first = null;
        bool steep = false, water = false, swimming = false, excursion = false, accepted = true;
        for (int i = 0; i < _count; i++)
        {
            Entry entry = _entries[i];
            water |= entry.Water;
            swimming |= entry.Swimming;
            excursion |= entry.Excursion;
            if (!entry.SupportEvent) continue;
            steep |= entry.Facts.Status == SupportStatus.Steep;
            bool firstWalkable = first is null && entry.Facts.Status == SupportStatus.Walkable &&
                entry.Mode is ContactDescentMode.Air or ContactDescentMode.Slide or ContactDescentMode.Water;
            if (firstWalkable) first = entry.Facts;
            if (validator is not null)
            {
                lease.AssertCurrent();
                var selected = new ContactSelectedSupport(lease, entry.Facts, entry.Mode, entry.Installed,
                    entry.FeatureId, firstWalkable);
                accepted &= validator(selected);
                lease.AssertCurrent();
            }
        }
        Summary = new(_status, first, steep, water, swimming, excursion,
            accepted && _status == ContactDescentEvidenceStatus.Complete);
        _completed = true;
    }

    internal void End()
    {
        if (!_completed) Summary = new(ContactDescentEvidenceStatus.StepRefused, null, false, false, false, false, false);
        Clear();
    }
    void Clear() { Array.Clear(_entries, 0, _count); _count = 0; }
}
