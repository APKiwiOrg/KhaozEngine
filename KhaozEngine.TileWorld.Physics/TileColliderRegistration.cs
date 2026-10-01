using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The statics <see cref="TileWorldColliders.AddTo"/> added to one physics world, one per collider and in the same
/// order. <see cref="Remove"/> takes them all out again, once. Disposing removes them too, so a registration can be
/// scoped with <c>using</c>. A rebuilt world is swapped in by removing the old registration and adding the new
/// colliders.
/// </summary>
public sealed class TileColliderRegistration : IDisposable
{
    readonly IPhysicsWorld _world;
    bool _removed;

    internal TileColliderRegistration(IPhysicsWorld world, StaticHandle[] handles)
    {
        _world = world;
        Handles = Array.AsReadOnly(handles);
    }

    /// <summary>The static handles, index for index with <see cref="TileWorldColliders.Colliders"/>. They still list
    /// the removed statics after <see cref="Remove"/>, and the world no longer knows them.</summary>
    public IReadOnlyList<StaticHandle> Handles { get; }

    /// <summary>Removes every static from the world it was added to. A second call does nothing.</summary>
    public void Remove()
    {
        if (_removed) return;
        _removed = true;
        foreach (StaticHandle handle in Handles) _world.RemoveStatic(handle);
    }

    /// <summary>Calls <see cref="Remove"/>.</summary>
    public void Dispose() => Remove();
}
