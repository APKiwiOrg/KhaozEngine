using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class ContactDropProof
{
    ContactTerrainDropProof? _terrain;
    internal ContactTerrainDropRefusal TerrainRefusal => _terrain?.Refusal ?? ContactTerrainDropRefusal.None;
    internal void EnableTerrainDescents(PhysicsNavDescentEvidence evidence, ContactTerrainProfileEvidence sources)
    {
        if (_terrain is not null) throw new InvalidOperationException("Terrain evidence is already installed.");
        _terrain = new(_options, _binding, _frame, _acquire, _ground, _allowance, _reuse, evidence, sources);
    }
    internal bool TryEdge(NavSpace space, NavLink link) => _terrain is { } terrain && terrain.IsTerrain(space, link)
        ? terrain.TryEdge(space, link) : TrySharpEdge(space, link);
}
