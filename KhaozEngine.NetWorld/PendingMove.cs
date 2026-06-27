using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The movement command a cell's <see cref="PlayerMovementSystem"/> applies to an owned player on the next
/// fixed tick. Server-local and transient (set each tick by <see cref="ShardedWorldServer"/> on the owning
/// cell's player entity, overwritten the next tick); deliberately NOT registered for replication, so it is
/// neither sent to clients nor carried across an authority handoff (the post-handoff cell re-routes the next
/// command itself). A ghost or migrating entity never carries one.
/// </summary>
public struct PendingMove : IComponent
{
    /// <summary>The camera-relative input to apply this tick.</summary>
    public MoveCommand Command;
}
