using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>Test-only identity checkpoint. The caller supplies the finite authored faces, not sampled heights.
/// Walkable landing membership is a shared-edge connected component of the named target's own source.</summary>
public sealed class ContactTerrainEvidenceValidator
{
    public enum Refusal { None, AnalyticIdentityUnavailable, SupportOwnerChanged, FirstSupportOutsideLandingPatch,
        SteepOutsideChain, AirAfterLanding, Water, UnavailableEvidence, MovementRefused }
    readonly IMovementAnalyticSupportSource _source;
    readonly HashSet<MovementSurfaceFeatureKey> _steep, _landing;
    readonly Vector3 _target;
    readonly MovementSurfaceFeature _targetFeature;
    readonly bool _landingConnected;
    bool _atAnchor, _hold;
    internal Refusal Reason { get; private set; }
    internal bool SawSteep { get; private set; }
    internal ContactSupportEvidence? FirstSupport { get; private set; }
    internal bool Accepted => Reason == Refusal.None && SawSteep && FirstSupport.HasValue && _hold;

    internal ContactTerrainEvidenceValidator(IMovementAnalyticSupportSource source,
        IEnumerable<MovementSurfaceFeature> faces, Vector3 from, Vector3 target, float maxSlope, MovementSurfaceFeatureKey? excludedSteep = null)
    {
        _source = source;
        _target = target;
        if (source.Describe(new(target.X, target.Z), out _targetFeature) != MovementAvailability.Known)
            throw new ArgumentException("The named target requires an authored support feature.", nameof(target));
        var all = faces.ToArray();
        if (all.Length == 0) throw new ArgumentException("The finite authored face set is empty.", nameof(faces));
        float cos = MathF.Cos(maxSlope);
        var steep = all.Where(f => f.Normal.Y < cos && f.Key != excludedSteep).Select(f => f.Key).ToHashSet();
        var walkable = all.Where(f => f.Normal.Y >= cos).Select(f => f.Key).ToHashSet();
        if (source.Describe(new(from.X, from.Z), out var upper) != MovementAvailability.Known)
            throw new ArgumentException("The named source requires an authored support feature.", nameof(from));
        var upperPatch = Component([upper.Key], walkable);
        _landing = Component([_targetFeature.Key], walkable);
        _steep = Component(steep.Where(key => upperPatch.Any(start => source.AreAdjacent(start, key))), steep);
        _landingConnected = _landing.Any(key => _steep.Any(face => source.AreAdjacent(face, key)));
    }

    HashSet<MovementSurfaceFeatureKey> Component(IEnumerable<MovementSurfaceFeatureKey> seeds,
        HashSet<MovementSurfaceFeatureKey> allowed)
    {
        var result = seeds.Where(allowed.Contains).ToHashSet();
        var queue = new Queue<MovementSurfaceFeatureKey>(result);
        while (queue.TryDequeue(out var key))
            foreach (var next in allowed)
                if (!result.Contains(next) && _source.AreAdjacent(key, next))
                { result.Add(next); queue.Enqueue(next); }
        return result;
    }

    internal bool Observe(in ContactSelectedSupport selected)
    {
        selected.AssertCurrent();
        ContactSupportEvidence fact = selected.Facts;
        SawSteep |= fact.Status == SupportStatus.Steep;
        if (SawSteep && fact.Status == SupportStatus.Walkable) FirstSupport ??= fact;
        if (Reason != Refusal.None) return false;
        if (fact.Status == SupportStatus.None)
            return FirstSupport.HasValue ? Refuse(Refusal.AirAfterLanding) : true;
        if (fact.Feature is not { } feature)
            return Refuse(selected.Installed.HasValue ? Refusal.SupportOwnerChanged : Refusal.AnalyticIdentityUnavailable);
        if (feature.Owner != _targetFeature.Owner || feature.Key.Source != _targetFeature.Key.Source ||
            _source.GetFeature(feature.Key, out var canonical) != MovementAvailability.Known || canonical != feature)
            return Refuse(Refusal.SupportOwnerChanged);
        if (fact.Status == SupportStatus.Steep)
            return _steep.Contains(feature.Key) ? true : Refuse(Refusal.SteepOutsideChain);
        if (SawSteep && fact.Status == SupportStatus.Walkable && (!_landing.Contains(feature.Key) || !_landingConnected))
            return Refuse(Refusal.FirstSupportOutsideLandingPatch);
        return true;
    }

    internal void AcceptTick(in MovementStepResult result, in ContactDescentSummary summary, bool hold, float halfHeight)
    {
        if (result.Outcome != MovementStepOutcome.Advanced) Refuse(Refusal.MovementRefused);
        if (summary.Status != ContactDescentEvidenceStatus.Complete) Refuse(Refusal.UnavailableEvidence);
        if (summary.Water || summary.Swimming || summary.WaterExcursion) Refuse(Refusal.Water);
        Vector3 feet = result.State.State.Position - Vector3.UnitY * halfHeight;
        bool at = result.ContactWalkableSupport && GroundTraversalProbe.Near(feet, _target);
        if (at && _atAnchor && hold) _hold = true;
        _atAnchor = at;
    }
    bool Refuse(Refusal reason) { if (Reason == Refusal.None) Reason = reason; return false; }
}
