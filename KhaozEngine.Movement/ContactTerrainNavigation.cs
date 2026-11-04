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
    internal ContactTerrainNavigation(GroundNavigation navigation, PhysicsNavDescentEvidence evidence,
        IEnumerable<NavLink> terrainLinks, float slopeCos)
    {
        _navigation = navigation; Evidence = evidence; SlopeCos = slopeCos;
        _terrainLinks = new(terrainLinks);
        Sources = new(evidence, navigation);
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
    internal ContactTerrainNavigation? Terrain { get; private set; }
    internal void AttachTerrain(PhysicsNavDescentEvidence evidence, IEnumerable<NavLink> terrainLinks, float slopeCos)
    {
        if (!AllowTerrainDescents || Terrain is not null) throw new InvalidOperationException("Terrain ownership must be installed once on an enabled profile.");
        Terrain = new(this, evidence, terrainLinks, slopeCos);
    }
    internal void RebindTerrainEvidence(PhysicsNavDescentEvidence evidence)
    {
        if (Terrain is null) throw new InvalidOperationException("Only enabled terrain navigation owns terrain evidence.");
        Terrain = Terrain.Rebind(evidence);
    }
}
