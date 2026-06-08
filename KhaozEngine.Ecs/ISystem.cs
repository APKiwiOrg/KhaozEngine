namespace KhaozEngine.Ecs;

/// <summary>
/// A unit of per-frame logic. Systems are registered with <see cref="World.AddSystem(ISystem)"/>
/// and run in registration order each <see cref="World.Update(float)"/>.
/// </summary>
public interface ISystem
{
    /// <summary>Advances this system by one frame.</summary>
    /// <param name="world">The world to read and mutate.</param>
    /// <param name="dt">Elapsed time since the last update, in seconds.</param>
    void Update(World world, float dt);
}
