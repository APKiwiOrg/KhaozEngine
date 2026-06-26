using KhaozEngine.Ecs;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// A representative position component for the benchmark population. Plain per-entity data, mutated by
/// <see cref="IntegratePositionSystem"/> - this is the trivial "real work" a server tick does over every entity.
/// </summary>
public struct BenchPosition : IComponent
{
    public float X;
    public float Y;
}

/// <summary>
/// A representative velocity component. Read by <see cref="IntegratePositionSystem"/> to advance
/// <see cref="BenchPosition"/>. Per-row-pure: each integrate reads/writes only its own entity's components.
/// </summary>
public struct BenchVelocity : IComponent
{
    public float X;
    public float Y;
}
