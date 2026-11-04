using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>Accepted-link replay selects only its authenticated route. Refusals use the complete bake capture.</summary>
internal static class ContactTerrainRouteReplay
{
    internal static bool Prove(PhysicsNavBake capture, GroundMoveContext context, GroundNavigation navigation,
        NavLink link, PhysicsNavDescentEvidence? originalEvidence = null)
    {
        using var binding = context.CreateNavigationBinding(navigation.Identity.Tuning);
        if (navigation.Terrain!.Route(link) == ContactDropRoute.Sharp)
        {
            using var sharp = new ContactNavigationProof(capture, context, binding, navigation.Footprint,
                maxDropMetres: navigation.MaxDropMetres);
            return sharp.TryDrop(navigation.Space, link);
        }
        var evidence = originalEvidence ?? navigation.Terrain.Evidence;
        var sources = originalEvidence is null ? navigation.Terrain.Sources : new ContactTerrainProfileEvidence(evidence, navigation);
        var frame = context.NavigationFrame;
        MovementQueryLease Acquire(Vector3 _)
        {
            if (binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease) != MovementAvailability.Known || lease is null)
                throw new InvalidOperationException("Accepted terrain replay requires its original movement lease.");
            return lease;
        }
        using var terrain = new ContactTerrainDropProof(capture.Options, binding, frame, Acquire, navigation.Footprint,
            navigation.MaxDropMetres, true, evidence, sources, navigation.Terrain.SlopeCos);
        return terrain.TryEdge(navigation.Space, link);
    }
}
