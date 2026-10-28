using System.Collections.Frozen;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>One immutable publication. Only its root changes, while holding the physical writer.</summary>
internal sealed class OrdinaryRuntimeStaticState
{
    internal FrozenDictionary<MovementSupportKey, OrdinaryMovementRegistration.Entry> Owners { get; }
    internal FrozenDictionary<StaticHandle, OrdinaryMovementRegistration.Entry> Handles { get; }
    internal long ExpectedStaticRevision { get; }
    internal long Revision { get; }
    internal bool Poisoned { get; }

    internal OrdinaryRuntimeStaticState(long expectedStaticRevision, long revision,
        Dictionary<MovementSupportKey, OrdinaryMovementRegistration.Entry>? owners = null)
    {
        ExpectedStaticRevision = expectedStaticRevision;
        Revision = revision;
        Owners = owners is null ? FrozenDictionary<MovementSupportKey, OrdinaryMovementRegistration.Entry>.Empty : owners.ToFrozenDictionary();
        Handles = Owners.Values.ToFrozenDictionary(row => row.Handle);
    }

    internal OrdinaryRuntimeStaticState(OrdinaryRuntimeStaticState old)
    {
        Owners = old.Owners;
        Handles = old.Handles;
        ExpectedStaticRevision = old.ExpectedStaticRevision;
        Revision = old.Revision;
        Poisoned = true;
    }
}
