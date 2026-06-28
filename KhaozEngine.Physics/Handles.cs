namespace KhaozEngine.Physics;

/// <summary>An opaque handle to a static body added via <see cref="IPhysicsWorld.AddStatic"/>.</summary>
public readonly record struct StaticHandle(int Value);
