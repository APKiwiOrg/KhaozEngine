using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

public sealed partial class EnvironmentAcquisitionFixture
{
    void PrepareWitness()
    {
        MovementQueryIdentity identity = Fault == "witness-identity" ? OtherIdentity : _identity;
        var scope = WitnessScope ?? (Fault is "scope" or "support-envelope" or "witness-identity"
            ? new MovementQueryScope(_scope.Min,
                Fault == "scope" ? _scope.Max - Vector3.UnitX : _scope.Max,
                Fault == "support-envelope" ? 0f : _scope.MaxRise, _scope.MaxDrop,
                _scope.WorldId, _scope.CurrentSpace, identity, _scope.Frame) : _scope);
        if (_preparedWitness is { } prepared)
        {
            using var comparison = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.FixtureWitnessComparison);
            if (SameScope(prepared.Scope, scope) && SameIdentity(prepared.Identity, identity) && SameResources(prepared)) return;
        }
        Assert.Equal(MovementAvailability.Known,
            MovementScopeWitness.TryCreate(scope, identity, Resources, true, out _preparedWitness));
    }

    // Compare nested fields directly so record comparers are never initialized in the measured phase.
    static bool SameScope(in MovementQueryScope left, in MovementQueryScope right) =>
        left.Min == right.Min && left.Max == right.Max &&
        left.MaxRise == right.MaxRise && left.MaxDrop == right.MaxDrop &&
        string.Equals(left.WorldId, right.WorldId, StringComparison.Ordinal) &&
        SameSpace(left.CurrentSpace, right.CurrentSpace) && SameIdentity(left.Identity, right.Identity) &&
        left.Frame.Frame.X == right.Frame.Frame.X && left.Frame.Frame.Z == right.Frame.Frame.Z &&
        left.Frame.PhysicsOrigin == right.Frame.PhysicsOrigin && left.Frame.Epoch == right.Frame.Epoch;

    static bool SameIdentity(in MovementQueryIdentity left, in MovementQueryIdentity right) =>
        string.Equals(left.ClosureId, right.ClosureId, StringComparison.Ordinal) &&
        left.PolicyVersion == right.PolicyVersion &&
        string.Equals(left.ScopeDigest, right.ScopeDigest, StringComparison.Ordinal);

    static bool SameSpace(MovementSpaceKey? left, MovementSpaceKey? right)
    {
        if (left is { } a)
            return right is { } b && string.Equals(a.WorldId, b.WorldId, StringComparison.Ordinal) &&
                string.Equals(a.LocalId, b.LocalId, StringComparison.Ordinal);
        return !right.HasValue;
    }

    bool SameResources(MovementScopeWitness witness)
    {
        if (Resources.Count != witness.ResourceIds.Count) return false;
        for (int i = 0; i < Resources.Count; i++)
        {
            bool found = false;
            for (int j = 0; j < witness.ResourceIds.Count; j++)
                if (string.Equals(Resources[i], witness.ResourceIds[j], StringComparison.Ordinal)) found = true;
            if (!found) return false;
            for (int j = 0; j < i; j++)
                if (string.Equals(Resources[i], Resources[j], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
