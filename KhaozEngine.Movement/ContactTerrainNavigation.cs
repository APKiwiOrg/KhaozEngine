using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Owned immutable terrain authority for one frozen profile, shared by proof and live continuation.</summary>
internal sealed class ContactTerrainNavigation
{
    readonly Dictionary<NavLink, ContactTerrainDropCertificate> _certificates = [];
    internal PhysicsNavDescentEvidence Evidence { get; }
    internal ContactTerrainProfileEvidence Sources { get; }
    internal ContactTerrainNavigation(GroundNavigation navigation, PhysicsNavDescentEvidence evidence, MoveTuning tuning)
    {
        Evidence = evidence;
        Sources = new(evidence, navigation);
        foreach (var link in navigation.Graph.Links)
        {
            if (link.Kind != NavLinkKind.Drop) continue;
            var from = ContactDropProof.Feet(navigation.Space.Layers[link.FromLayer], link.FromX, link.FromZ);
            var to = ContactDropProof.Feet(navigation.Space.Layers[link.ToLayer], link.ToX, link.ToZ);
            if (!ContactTerrainDropCertificate.HasSteep(evidence, tuning, from, to)) continue;
            var certificate = ContactTerrainDropCertificate.Create(evidence, Sources, navigation.Footprint, tuning,
                navigation.Space, link, from, to, navigation.MaxDropMetres)
                ?? throw new InvalidOperationException("An accepted terrain link has no complete certificate.");
            _certificates.Add(link, certificate);
        }
    }
    internal bool TryGet(NavLink link, out ContactTerrainDropCertificate? certificate) => _certificates.TryGetValue(link, out certificate);
}

public sealed partial class GroundNavigation
{
    internal ContactTerrainNavigation? Terrain { get; private set; }
    internal void AttachTerrain(PhysicsNavDescentEvidence evidence, MoveTuning tuning)
    {
        if (!AllowTerrainDescents || Terrain is not null) throw new InvalidOperationException("Terrain ownership must be installed once on an enabled profile.");
        Terrain = new(this, evidence, tuning);
    }
    internal void RebindTerrainEvidence(PhysicsNavDescentEvidence evidence)
    {
        if (!AllowTerrainDescents) throw new InvalidOperationException("Only enabled terrain navigation owns terrain evidence.");
        Terrain = new(this, evidence, Identity.Tuning);
    }
}
