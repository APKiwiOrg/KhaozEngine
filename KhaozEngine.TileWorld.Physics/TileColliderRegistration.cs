using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The statics <see cref="TileWorldColliders.AddTo"/> added to one physics world, one per collider and in the same
/// order. <see cref="Remove"/> takes them all out again, each once. Disposing removes them too, so a registration can
/// be scoped with <c>using</c>. A rebuilt world is swapped in by removing the old registration and adding the new
/// colliders.
/// </summary>
public sealed class TileColliderRegistration : IDisposable
{
    readonly IPhysicsWorld _world;
    readonly StaticHandle[] _handles;
    readonly StaticHandle[] _groundHandles;
    // Which handles the world has taken back, so a retry after a failed removal skips them.
    readonly bool[] _removed;

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles)
        : this(world, handles, Array.Empty<StaticHandle>())
    {
    }

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles, StaticHandle[] groundHandles)
    {
        _world = world;
        _handles = handles;
        _groundHandles = groundHandles;
        _removed = new bool[handles.Length];
        Handles = Array.AsReadOnly(handles);
        GroundHandles = Array.AsReadOnly(groundHandles);
    }

    /// <summary>The static handles, index for index with <see cref="TileWorldColliders.Colliders"/>. They still list
    /// the removed statics after <see cref="Remove"/>, and the world no longer knows them.</summary>
    public IReadOnlyList<StaticHandle> Handles { get; }

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
