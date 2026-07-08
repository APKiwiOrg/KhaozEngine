namespace KhaozEngine.Physics;

/// <summary>An opaque handle to a static body added via <see cref="IPhysicsWorld.AddStatic"/>.</summary>
public readonly record struct StaticHandle(int Value);

/// <summary>An opaque handle to a dynamic body added via <see cref="IPhysicsWorld.AddDynamic"/>.
/// Dynamic bodies fall under gravity, collide with statics and each other, and are stepped by
/// <see cref="IPhysicsWorld.Step"/>. Query the current pose/velocity with the handle; remove it with
/// <see cref="IPhysicsWorld.RemoveDynamic"/>.</summary>
public readonly record struct DynamicBodyHandle(int Value);
