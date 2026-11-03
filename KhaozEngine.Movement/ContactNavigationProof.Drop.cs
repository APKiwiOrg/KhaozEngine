using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class ContactNavigationProof
{
    ContactDropProof? _drop;

    void PrepareDrops(EffectiveMovementBinding source, float allowance)
    {
        if (allowance > 0) _drop = new(_bake.Options, source, _frame, Acquire, _footprint, allowance, _reuse.Footprints);
    }

    internal bool TryDrop(NavSpace space, NavLink link) => (_drop ??
        throw new InvalidOperationException("This Contact proof has no Drop permission.")).TryEdge(space, link);
}
