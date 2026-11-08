using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>One authenticated read interval for an explicitly stable, synchronous legacy profile.</summary>
internal sealed class LegacyNavigationReadScope : IDisposable
{
    private readonly GroundMoveContext _context;
    private readonly PhysicsNavCaptureBasis _basis;
    private readonly IPhysicsQueryLease _lease;

    private LegacyNavigationReadScope(GroundMoveContext context, PhysicsNavCaptureBasis basis, IPhysicsQueryLease lease)
    {
        _context = context;
        _basis = basis;
        _lease = lease;
    }

    internal static LegacyNavigationReadScope Acquire(GroundMoveContext context, PhysicsNavCaptureBasis? captured,
        Vector3 origin)
    {
        IPhysicsWorld world = context.Physics!;
        IPhysicsWorldQueryView? view = context.MovementQueries;
        if (world is not IPhysicsQueryLeaseSource owner || captured is not { } basis ||
            (view is not null && view is not IPhysicsQueryLeaseSource))
            throw new NavigationReadCapabilityException(NavigationReadRefusal.LegacyQueryLeaseRequired,
                "A stable legacy navigation read scope needs IPhysicsQueryLeaseSource " +
                "on the complete physics source and any selected movement query view.")
                { RequiredCapability = typeof(IPhysicsQueryLeaseSource), SourceRole = world is not IPhysicsQueryLeaseSource || captured is null
                    ? NavigationSourceRole.PhysicsSource : NavigationSourceRole.MovementQueryView };
        // Internal invariant: the sole caller passes the retained context and Origin from the same capture.
        // Physics is immutable on that context, and capture stores basis.Origin as its own Origin.
        if (!ReferenceEquals(basis.Source, owner) || basis.Origin != origin)
            throw new InvalidOperationException("The captured physics source or origin changed.");
        AssertView(context, origin);
        IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource?)view ?? owner).AcquireQueryReadLease();
        // A token that fails thread/lifetime authentication is not ours to dispose on this thread.
        lease.AssertCurrent();
        try
        {
            var scope = new LegacyNavigationReadScope(context, basis, lease);
            scope.AssertCurrent();
            return scope;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal void AssertCurrent()
    {
        _lease.AssertCurrent();
        IPhysicsWorld source = _lease.SourceWorld;
        long? generation = null;
        Vector3? origin = null;
        if (!ReferenceEquals(source, _context.Physics) ||
            (generation = _lease.GeometryGeneration) != _basis.GeometryGeneration || (origin = _lease.Origin) != _basis.Origin)
        {
            const string message = "The physics lease owner, geometry or origin differs from the capture.";
            if (!ReferenceEquals(source, _context.Physics))
                throw new NavigationBuildException(NavigationBuildRefusal.LegacyLeaseSourceChanged, message)
                    { SourceRole = NavigationSourceRole.ReadLease };
            if (generation != _basis.GeometryGeneration)
                throw new NavigationBuildException(NavigationBuildRefusal.LegacyLeaseGenerationChanged, message)
                    { ExpectedGeneration = _basis.GeometryGeneration, ActualGeneration = generation };
            throw new NavigationBuildException(NavigationBuildRefusal.LegacyLeaseOriginChanged, message)
                { ExpectedOrigin = _basis.Origin, ActualOrigin = origin };
        }
        AssertView(_context, _basis.Origin);
    }

    private static void AssertView(GroundMoveContext context, Vector3 origin)
    {
        Vector3 worldOrigin = context.Physics!.Origin;
        IPhysicsWorld? viewSource = null;
        Vector3? viewOrigin = null;
        if (worldOrigin != origin || context.MovementQueries is { } view &&
            (!ReferenceEquals(viewSource = view.SourceWorld, context.Physics) || (viewOrigin = view.Origin) != origin))
        {
            const string message = "The movement query source or origin differs from the capture.";
            if (worldOrigin != origin)
                throw new NavigationBuildException(NavigationBuildRefusal.LegacyWorldOriginChanged, message)
                    { ExpectedOrigin = origin, ActualOrigin = worldOrigin };
            if (!ReferenceEquals(viewSource, context.Physics))
                throw new NavigationBuildException(NavigationBuildRefusal.LegacyViewSourceChanged, message)
                    { SourceRole = NavigationSourceRole.MovementQueryView };
            throw new NavigationBuildException(NavigationBuildRefusal.LegacyViewOriginChanged, message)
                { ExpectedOrigin = origin, ActualOrigin = viewOrigin };
        }
    }

    public void Dispose() => _lease.Dispose();
}
