using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>The authenticated policy that accepted a Drop. Sharp is the unchanged format-3 proof.</summary>
internal enum ContactDropRoute : byte { Sharp, Terrain }

/// <summary>Frozen route facts and local terrain evidence. Certificates reconstruct only on first use.</summary>
internal sealed class ContactTerrainNavigation
{
    readonly GroundNavigation _navigation;
    readonly HashSet<NavLink> _terrainLinks;
    readonly Dictionary<NavLink, ContactTerrainDropCertificate> _certificates = [];
    internal PhysicsNavDescentEvidence Evidence { get; }
    internal ContactTerrainProfileEvidence Sources { get; }
    internal float SlopeCos { get; }
    internal int CertificateCountForTests { get { lock (_certificates) return _certificates.Count; } }
    internal ContactTerrainNavigation(GroundNavigation navigation, PhysicsNavDescentEvidence evidence,
        IEnumerable<NavLink> terrainLinks, float slopeCos)
    {
        _navigation = navigation; Evidence = evidence.RequireDependencies(); SlopeCos = slopeCos;
        _terrainLinks = new(terrainLinks);
        Sources = new(Evidence, navigation);
    }
    internal ContactDropRoute Route(NavLink link) => _terrainLinks.Contains(link) ? ContactDropRoute.Terrain : ContactDropRoute.Sharp;
    internal bool TryGet(NavLink link, out ContactTerrainDropCertificate? certificate)
    {
        certificate = null;
        if (Route(link) != ContactDropRoute.Terrain) return false;
        lock (_certificates)
        {
            if (_certificates.TryGetValue(link, out certificate)) return true;
            var nav = _navigation;
            var from = ContactDropProof.Feet(nav.Space.Layers[link.FromLayer], link.FromX, link.FromZ);
            var to = ContactDropProof.Feet(nav.Space.Layers[link.ToLayer], link.ToX, link.ToZ);
            certificate = ContactTerrainDropCertificate.Create(Evidence, Sources, nav.Footprint, nav.Identity.Tuning,
                nav.Space, link, from, to, nav.MaxDropMetres, SlopeCos)
                ?? throw new InvalidOperationException("An accepted terrain link has no complete certificate.");
            _certificates.Add(link, certificate);
            return true;
        }
    }
    internal ContactTerrainNavigation Rebind(PhysicsNavDescentEvidence evidence) => new(_navigation, evidence, _terrainLinks, SlopeCos);
}

public sealed partial class GroundNavigation
{
    /// <summary>Constructs every accepted terrain certificate without physics. Call during loading, outside a
    /// frame or simulation tick, to avoid first-use work. Concurrent callers share the synchronized cache.
    /// Profiles without terrain routes need no preparation. No trajectory or live movement verdict is cached.</summary>
    /// <exception cref="PhysicsNavDescentEvidenceException">A retained certificate dependency is missing.</exception>
    public void PrepareTerrainDescents()
    {
        if (Terrain is not { } terrain) return;
        foreach (var link in Graph.Links)
            if (terrain.Route(link) == ContactDropRoute.Terrain) terrain.TryGet(link, out _);
    }

    ContactTerrainNavigation? _terrain;
    internal ContactTerrainNavigation? Terrain => AreaExclusionSource is { } source ? source.Terrain : _terrain;
    internal void AttachTerrain(PhysicsNavDescentEvidence evidence, IEnumerable<NavLink> terrainLinks, float slopeCos)
    {
        if (AreaExclusionSource is { } source)
        {
            source.AttachTerrain(evidence, terrainLinks, slopeCos);
            return;
        }
        if (!AllowTerrainDescents || Terrain is not null) throw new InvalidOperationException("Terrain ownership must be installed once on an enabled profile.");
        _terrain = new(this, evidence, terrainLinks, slopeCos);
    }
    internal void RebindTerrainEvidence(PhysicsNavDescentEvidence evidence)
    {
        if (AreaExclusionSource is { } source)
        {
            source.RebindTerrainEvidence(evidence);
            return;
        }
        if (Terrain is null) throw new InvalidOperationException("Only enabled terrain navigation owns terrain evidence.");
        _terrain = Terrain.Rebind(evidence);
    }
}
