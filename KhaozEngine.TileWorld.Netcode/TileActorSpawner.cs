using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Which of the three things a <see cref="TileActorSpawner"/> is doing.</summary>
public enum TileActorSpawnerState : byte
{
    /// <summary>Nothing built yet. The state a fresh spawner starts in, and the one it spawns out of on the first
    /// tick it is driven.</summary>
    Empty = 0,

    /// <summary>Its actor exists. <see cref="TileActorSpawner.ActorNetId"/> names it.</summary>
    Alive = 1,

    /// <summary>Its actor is gone and the respawn is counting down.</summary>
    Waiting = 2,
}

/// <summary>
/// One authored spawn point: a definition, a home tile, and which of three things it is currently doing. Its whole
/// behaviour is three lines and they live in <see cref="TileActorHost"/>, because the host is what holds the order
/// and the server.
/// <para>The spawner asks the WORLD whether its actor is still there rather than subscribing to a death, which is
/// why nothing has to be wired for a respawn to work: a kill, a manual despawn and a cell eviction all remove the
/// entity, and all three therefore start the same countdown.</para>
/// </summary>
public sealed class TileActorSpawner
{
    /// <summary>Binds a definition to a home tile.</summary>
    /// <param name="definition">What to build.</param>
    /// <param name="home">Where to build it, and the tile the leash and the wander radius are measured from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="definition"/> asks for a max health of zero, a
    /// negative radius, or a negative respawn delay. Taken at the door for the same reason
    /// <see cref="TileWorldServer.SpawnActor"/> takes its own there: a refusal that waits for the first respawn
    /// surfaces inside a server tick, hours after the content shipped.</exception>
    public TileActorSpawner(TileActorDefinition definition, TileCoord home)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.MaxHealth == 0)
            throw new ArgumentOutOfRangeException(nameof(definition), definition.MaxHealth,
                "An actor definition's MaxHealth must be above zero.");
        if (definition.WanderRadius < 0 || definition.LeashRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(definition),
                "An actor definition's WanderRadius and LeashRadius must not be negative.");
        if (definition.RespawnDelayTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(definition), definition.RespawnDelayTicks,
                "An actor definition's RespawnDelayTicks must not be negative.");
        Definition = definition;
        Home = home;
    }

    /// <summary>What this point builds.</summary>
    public TileActorDefinition Definition { get; }

    /// <summary>The authored tile, and the origin the leash and the wander radius are measured from.</summary>
    public TileCoord Home { get; }

    /// <summary>Which of the three things this spawner is doing.</summary>
    public TileActorSpawnerState State { get; private set; } = TileActorSpawnerState.Empty;

    /// <summary>The live actor's net id while <see cref="State"/> is <see cref="TileActorSpawnerState.Alive"/>,
    /// otherwise 0. Never recycled, so a respawned actor is a NEW entity under a NEW id and nobody can be left
    /// holding a target that silently re-aims at the corpse's replacement.</summary>
    public long ActorNetId { get; private set; }

    /// <summary>Ticks still to wait while <see cref="State"/> is <see cref="TileActorSpawnerState.Waiting"/>.</summary>
    public int TicksUntilRespawn { get; private set; }

    // Driven by TileActorHost alone. Internal rather than public because the state machine is only coherent when one
    // caller owns it: a head that armed a Waiting by hand would have the host overwrite it on the next tick.
    internal void Alive(long netId)
    {
        State = TileActorSpawnerState.Alive;
        ActorNetId = netId;
        TicksUntilRespawn = 0;
    }

    internal void Wait(int ticks)
    {
        State = TileActorSpawnerState.Waiting;
        ActorNetId = 0;
        TicksUntilRespawn = Math.Max(0, ticks);
    }

    // True on the tick the countdown reaches zero AND on every tick after it, which is what makes a spawn refused by
    // a full cell retry rather than strand the spawner at zero forever.
    internal bool TickDown()
    {
        if (State != TileActorSpawnerState.Waiting) return false;
        if (TicksUntilRespawn > 0) TicksUntilRespawn--;
        return TicksUntilRespawn == 0;
    }
}
