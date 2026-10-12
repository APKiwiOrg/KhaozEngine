using System;
using System.Numerics;
using KhaozEngine.Locomotion;
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
        if (_preparedWitness is { } prepared && prepared.Scope == scope && prepared.Identity == identity &&
            SameResources(prepared)) return;
        Assert.Equal(MovementAvailability.Known,
            MovementScopeWitness.TryCreate(scope, identity, Resources, true, out _preparedWitness));
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
