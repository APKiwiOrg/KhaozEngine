using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>One profile's serial recipe and worker-owned dependency receipts. No worker shares mutable proof state.</summary>
internal sealed class ContactTerrainProfileBuild
{
    readonly PhysicsNavDescentEvidence _evidence;
    readonly ContactTerrainProfileEvidence _sources;
    readonly ContactTerrainEvidenceReads _reads;
    readonly Dictionary<ContactNavigationProof, ContactTerrainEvidenceReads> _workers = [];
    readonly MoveTuning _tuning;
    internal IReadOnlyList<NavLink> Candidates { get; }
    internal ContactTerrainProfileBuild(PhysicsNavDescentEvidence evidence, GroundNavigation navigation, MoveTuning tuning, float allowance)
    {
        _evidence = evidence; _tuning = tuning; _sources = new(evidence, navigation); _reads = new(evidence);
        using (_reads.Observe()) Candidates = ContactTerrainDropCandidates.Generate(navigation, evidence, _sources, tuning, allowance);
    }
    internal void Register(ContactNavigationProof proof)
    {
        proof.EnableTerrainDescents(_evidence, _sources);
        _workers.Add(proof, new(_evidence));
    }
    internal bool Prove(ContactNavigationProof proof, NavSpace space, NavLink link)
    {
        using var scope = _workers[proof].Observe();
        return proof.TryDrop(space, link);
    }
    internal void Attach(GroundNavigation navigation)
    {
        var workers = new ContactTerrainEvidenceReads(_evidence);
        foreach (var r in _workers.Values) workers.UnionWith(r);
        _reads.UnionWith(workers);
        navigation.AttachTerrain(ContactTerrainEvidenceSubset.Build(_evidence, _reads, _tuning, workers), _tuning);
    }
}
