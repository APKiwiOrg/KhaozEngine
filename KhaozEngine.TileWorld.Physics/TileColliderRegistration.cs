using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using KhaozEngine.Physics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The statics added by <see cref="TileWorldColliders"/> to one physics world, in compact selected collider order.
/// <see cref="Remove"/> takes them all out again, each once. Disposing removes them too, so a registration can
/// be scoped with <c>using</c>. A rebuilt world is swapped in by removing the old registration and adding the new
/// colliders.
/// </summary>
public sealed partial class TileColliderRegistration : IDisposable
{
    readonly IPhysicsWorld _world;
    readonly StaticHandle[] _handles;
    readonly StaticHandle[] _groundHandles;
    // Which handles the world has taken back, so a retry after a failed removal skips them.
    readonly bool[] _removed;
    readonly TileColliderProvenance[]? _provenance;
    readonly string? _worldId;
    readonly OrdinaryMovementExtent _extent;

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles)
        : this(world, handles, Array.Empty<StaticHandle>())
    {
    }

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles, StaticHandle[] groundHandles)
        : this(world, handles, groundHandles, null, null, default)
    {
    }

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles, StaticHandle[] groundHandles,
        TileColliderProvenance[]? provenance, string? worldId, OrdinaryMovementExtent extent)
        : this(world, handles, groundHandles, provenance, worldId, extent, Enumerable.Range(0, handles.Length).ToArray())
    {
    }

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles, StaticHandle[] groundHandles,
        TileColliderProvenance[]? provenance, string? worldId, OrdinaryMovementExtent extent, int[] colliderIndices)
    {
        _provenance = provenance;
        _worldId = worldId;
        _extent = extent;
        _world = world;
        _handles = handles;
        _groundHandles = groundHandles;
        _removed = new bool[handles.Length];
        Handles = Array.AsReadOnly(handles);
        GroundHandles = Array.AsReadOnly(groundHandles);
        ColliderIndices = Array.AsReadOnly((int[])colliderIndices.Clone());
    }

    /// <summary>The static handles in compact installed order. <see cref="ColliderIndices"/> maps each handle to
    /// its original <see cref="TileWorldColliders.Colliders"/> entry. Without filtering, the indices match directly.
    /// This read-only snapshot still lists removed statics after <see cref="Remove"/>.</summary>
    public IReadOnlyList<StaticHandle> Handles { get; }

    /// <summary>The original <see cref="TileWorldColliders.Colliders"/> index for each entry in <see cref="Handles"/>,
    /// strictly increasing and retained as a read-only snapshot after removal.</summary>
    public IReadOnlyList<int> ColliderIndices { get; }

    /// <summary>The registered ground handles in canonical order, retained after removal.</summary>
    public IReadOnlyList<StaticHandle> GroundHandles { get; }

    /// <summary>Creates a non-owning movement query view excluding this registration's analytic ground.</summary>
    public IPhysicsWorldQueryView CreateMovementQueryView() =>
        _world.CreateQueryViewExcludingStatics(_groundHandles);

    /// <summary>Removes every static this registration still holds from the world it was added to. Every handle is
    /// tried even when one fails. A handle the world took back is never removed again, so after a failure a second
    /// call retries only the handles that failed, and once all are gone a call does nothing.</summary>
    /// <exception cref="Exception">One removal failed: its exception, rethrown as it was.</exception>
    /// <exception cref="AggregateException">Several removals failed: their exceptions, in handle order.</exception>
    public void Remove()
    {
        List<Exception>? failures = null;
        for (int i = 0; i < _handles.Length; i++)
        {
            if (_removed[i]) continue;
            try
            {
                _world.RemoveStatic(_handles[i]);
                _removed[i] = true;
            }
            catch (Exception failure)
            {
                (failures ??= new List<Exception>()).Add(failure);
            }
        }

        if (failures is null) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    /// <summary>Calls <see cref="Remove"/>.</summary>
    public void Dispose() => Remove();
}
