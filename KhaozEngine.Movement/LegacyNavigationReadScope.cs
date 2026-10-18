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
            throw new NotSupportedException("A stable legacy navigation read scope needs IPhysicsQueryLeaseSource " +
                "on the complete physics source and any selected movement query view.");
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
        if (!ReferenceEquals(_lease.SourceWorld, _context.Physics) ||
            _lease.GeometryGeneration != _basis.GeometryGeneration || _lease.Origin != _basis.Origin)
            throw new InvalidOperationException("The physics lease owner, geometry or origin differs from the capture.");
        AssertView(_context, _basis.Origin);
    }

    private static void AssertView(GroundMoveContext context, Vector3 origin)
    {
        if (context.Physics!.Origin != origin || context.MovementQueries is { } view &&
            (!ReferenceEquals(view.SourceWorld, context.Physics) || view.Origin != origin))
            throw new InvalidOperationException("The movement query source or origin differs from the capture.");
    }

    public void Dispose() => _lease.Dispose();
}
