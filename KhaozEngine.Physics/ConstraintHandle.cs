namespace KhaozEngine.Physics;

/// <summary>An opaque handle to a joint constraint added via <see cref="IPhysicsWorld.AddConstraint"/>.
/// A constraint connects two dynamic bodies (or one dynamic body to a world-space static anchor) with one of
/// the joint kinds in <see cref="ConstraintKind"/>. Remove it with <see cref="IPhysicsWorld.RemoveConstraint"/>.
/// A constraint is also removed automatically when either body it connects is removed
/// (<see cref="IPhysicsWorld.RemoveDynamic"/>), so a handle can go stale without an explicit remove: querying or
/// removing a stale handle is a safe no-op for removal and throws for mutation, matching the dynamic-body
/// handle pattern.</summary>
public readonly record struct ConstraintHandle(int Value);
