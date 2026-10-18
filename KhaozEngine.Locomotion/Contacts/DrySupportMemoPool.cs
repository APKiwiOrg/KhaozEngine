using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Thread-owned storage for one ordinary dry tick and three nested intervals (CF-4, FA-R10). Every slot
/// is provisioned together before the first tick executes. Each interval owns its memo and immutable evidence,
/// so closing or reusing a nested interval cannot overwrite an outer interval's rows or authenticate its handles.</summary>
internal sealed class DrySupportMemoPool
{
    readonly SupportRequestMemo[] _memos = [new(), new(), new(), new()];

    internal SupportRequestMemo Open(IPhysicsQueryLease lease)
    {
        foreach (SupportRequestMemo memo in _memos)
        {
            if (memo.Lease is not null) continue;
            memo.Open(lease);
            return memo;
        }
        throw new InvalidOperationException("At most four dry support intervals may be open on one thread.");
    }
}
