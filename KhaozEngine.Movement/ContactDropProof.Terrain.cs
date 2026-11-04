using System;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class ContactDropProof
{
    ContactTerrainDropProof? _terrain;
    bool _terrainSelected;
    internal ContactTerrainDropRefusal TerrainRefusal => _terrainSelected ? _terrain!.Refusal : ContactTerrainDropRefusal.None;
    internal void EnableTerrainDescents(PhysicsNavDescentEvidence evidence, ContactTerrainProfileEvidence sources)
    {
        if (_terrain is not null) throw new InvalidOperationException("Terrain evidence is already installed.");
        _terrain = new(_options, _binding, _frame, _acquire, _ground, _allowance, _reuse, evidence, sources);
    }
    internal bool TryEdge(NavSpace space, NavLink link)
    {
        _terrainSelected = _terrain is { } terrain && terrain.IsTerrain(space, link);
        return _terrainSelected ? _terrain!.TryEdge(space, link) : TrySharpEdge(space, link);
    }
}
