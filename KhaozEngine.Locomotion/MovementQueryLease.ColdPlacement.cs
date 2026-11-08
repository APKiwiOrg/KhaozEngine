using System;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    internal long IssueColdPlacement() => Workspace.ColdPlacement.Issue();

    // A copied value cannot spend a certificate twice or renew a disposed interval. Full lease and
    // producer authentication stays in the controller, preserving its existing refusal behavior.
    internal bool TryConsumeColdPlacement(long version)
    {
        return !_disposed && Environment.CurrentManagedThreadId == _thread && Workspace.Owns(this) &&
            Workspace.ColdPlacement.TryConsume(version);
    }
}
