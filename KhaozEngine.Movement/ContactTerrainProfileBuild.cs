using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>One profile's serial recipe and worker-owned dependency receipts. No worker shares mutable proof state.</summary>
internal sealed class ContactTerrainProfileBuild
{
    readonly PhysicsNavDescentEvidence _evidence;
    readonly ContactTerrainProfileEvidence _sources;
    readonly HashSet<NavLink> _sharp;
    readonly Dictionary<ContactNavigationProof, ContactTerrainEvidenceReads> _workers = [];
    readonly MoveTuning _tuning;
    readonly float _slopeCos;
    readonly HashSet<NavLink> _terrainLinks = [];
    internal IReadOnlyList<NavLink> Candidates { get; }
    internal ContactTerrainProfileBuild(PhysicsNavDescentEvidence evidence, GroundNavigation navigation, MoveTuning tuning, float allowance)
    {
        _evidence = evidence; _tuning = tuning; _sources = new(evidence, navigation);
        _slopeCos = MathF.Cos(tuning.MaxSlopeRadians);
        _sharp = ContactDropCandidates.Enumerate(navigation.Space.Layers, navigation.Graph.Layers, tuning, allowance).ToHashSet();
        Candidates = ContactTerrainDropCandidates.Generate(navigation, evidence, _sources, tuning, allowance);
    }
    internal void Register(ContactNavigationProof proof)
    {
        proof.EnableTerrainDescents(_evidence, _sources, _sharp, _slopeCos);
        _workers.Add(proof, new(_evidence));
    }
    internal bool Prove(ContactNavigationProof proof, NavSpace space, NavLink link)
    {
        var reads = new ContactTerrainEvidenceReads(_evidence);
        bool accepted;
        using (reads.Observe()) accepted = proof.TryDrop(space, link);
        if (accepted && proof.SelectedDropRoute == ContactDropRoute.Terrain)
        {
            _workers[proof].UnionWith(reads);
            lock (_terrainLinks) _terrainLinks.Add(link);
        }
        return accepted;
    }
    internal void Attach(GroundNavigation navigation)
    {
        var workers = new ContactTerrainEvidenceReads(_evidence);
        foreach (var r in _workers.Values) workers.UnionWith(r);
        navigation.AttachTerrain(ContactTerrainEvidenceSubset.Build(_evidence, workers, _tuning), _terrainLinks, _slopeCos);
    }
}
