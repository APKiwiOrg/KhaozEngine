using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

internal sealed partial class ContactDropProof
{
    ContactTerrainDropProof? _terrain;
    bool _terrainSelected;
    IReadOnlySet<NavLink>? _sharpCandidates;
    internal ContactDropRoute SelectedRoute => _terrainSelected ? ContactDropRoute.Terrain : ContactDropRoute.Sharp;
    internal ContactTerrainDropRefusal TerrainRefusal => _terrainSelected ? _terrain!.Refusal : ContactTerrainDropRefusal.None;
    internal void EnableTerrainDescents(PhysicsNavDescentEvidence evidence, ContactTerrainProfileEvidence sources,
        IReadOnlySet<NavLink>? sharpCandidates = null, float? slopeCos = null)
    {
        if (_terrain is not null) throw new InvalidOperationException("Terrain evidence is already installed.");
        _sharpCandidates = sharpCandidates ?? ContactDropCandidates.Enumerate(sources.Navigation.Space.Layers,
            sources.Navigation.Graph.Layers, _binding.Tuning, _allowance).ToHashSet();
        _terrain = new(_options, _binding, _frame, _acquire, _ground, _allowance, _reuse, evidence, sources,
            slopeCos ?? MathF.Cos(_binding.Tuning.MaxSlopeRadians));
    }
    internal bool TryEdge(NavSpace space, NavLink link)
    {
        _terrainSelected = false;
        if (_terrain is null) return TrySharpEdge(space, link);
        if (_sharpCandidates!.Contains(link) && TrySharpEdge(space, link)) return true;
        if (!_terrain.IsTerrain(space, link)) return false;
        _terrainSelected = true;
        return _terrain.TryEdge(space, link);
    }
}
