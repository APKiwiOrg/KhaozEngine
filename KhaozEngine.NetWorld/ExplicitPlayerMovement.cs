using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>Caller-owned explicit environment and bounded scope planning for player movement.
/// Scope planning/preparation precedes the read lease. A batch scope must cover its replay steps,
/// or those steps refuse. The factory supplies a cold (null selection hint) witness.</summary>
public sealed class ExplicitPlayerMovement
{
    public MovementEnvironmentContext Environment { get; }
    public Func<PlayerMoveState, WorldFrame, MovementQueryScope> Scope { get; }
    public WaterTraversalPolicy Water { get; }
    public ExplicitPlayerMovement(MovementEnvironmentContext environment,
        Func<PlayerMoveState, WorldFrame, MovementQueryScope> scope, WaterTraversalPolicy water)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(scope);
        if (!water.IsValid || water.Mode == WaterTraversalMode.Legacy) throw new ArgumentException("An explicit water policy is required.", nameof(water));
        Environment = environment;
        Scope = scope;
        Water = water;
    }
}

/// <summary>Retain this scope through pure state publication and release before physical mutation.
/// A correction or restored basis must not be published when BasisValid is false.</summary>
public sealed class PlayerMoveReadScope : IDisposable
{
    readonly Action<PlayerMoveReadScope> release;
    readonly int thread = System.Environment.CurrentManagedThreadId;
    bool disposed;
    internal MovementQueryLease? Queries { get; }
    internal MovementBoundary? Boundary { get; }
    public bool BasisValid => Outcome == MovementStepOutcome.Advanced;
    public MovementStepOutcome Outcome { get; }

    internal PlayerMoveReadScope(MovementQueryLease? queries, MovementStepOutcome outcome,
        Action<PlayerMoveReadScope> release, MovementBoundary? boundary = null)
    {
        Queries = queries;
        Boundary = boundary;
        Outcome = outcome;
        this.release = release;
    }

    internal void AssertUsable()
    {
        if (System.Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("An explicit player read belongs to its acquiring thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        if (System.Environment.CurrentManagedThreadId != thread)
            throw new InvalidOperationException("An explicit player read belongs to its acquiring thread.");
        if (disposed) return;
        disposed = true;
        try { Queries?.Dispose(); }
        finally { release(this); }
    }
}


public sealed partial class WorldServerConfig
{
    /// <summary>Opt-in leased environment. Configured spawn/resume positions must be valid capsule centres.
    /// Null retains the legacy simulator. The caller owns the environment and its physics view.</summary>
    public ExplicitPlayerMovement? ExplicitMovement { get; init; }
}

public sealed partial class WorldClientConfig
{
    /// <summary>Opt-in leased prediction and correction queries. Unresolved bases are held for retry
    /// before publication. Null retains legacy prediction.</summary>
    public ExplicitPlayerMovement? ExplicitMovement { get; init; }
}

public sealed partial class ShardedWorldServerConfig
{
    /// <summary>Creates one explicit environment binding per live cell runtime. The binding must
    /// use that cell's physics source and frame. The caller owns its query view and environment.
    /// Null retains legacy movement. A configured factory cannot return null.</summary>
    public Func<CellSim, ExplicitPlayerMovement>? ExplicitMovementFactory { get; init; }
}
