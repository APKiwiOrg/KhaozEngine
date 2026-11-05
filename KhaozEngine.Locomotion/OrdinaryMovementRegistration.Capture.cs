using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementRegistration
{
    internal MovementAvailability BindCapturedFeature(IPhysicsQueryLease lease, StaticHandle handle, int feature,
        out MovementSupportKey owner, out bool analytic)
    {
        owner = default;
        analytic = _exclusions.Contains(handle);
        var status = Authenticate(lease);
        if (status != MovementAvailability.Known) return status;
        if (!_entries.TryGetValue(handle, out var entries)) return MovementAvailability.Unresolved;
        bool found = false;
        foreach (var row in entries)
        {
            if (feature < row.First || feature > row.Last) continue;
            if (found && row.Owner != owner) return MovementAvailability.Invalid;
            owner = row.Owner;
            found = true;
        }
        return found ? MovementAvailability.Known : MovementAvailability.Unresolved;
    }
    internal string CaptureProvenance => SemanticDigest;
}
