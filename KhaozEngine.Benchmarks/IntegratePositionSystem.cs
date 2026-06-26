using KhaozEngine.Ecs;

namespace KhaozEngine.Benchmarks;

/// <summary>
/// The benchmark's representative per-entity work: an integrate-position pass over every entity with a
/// <see cref="BenchPosition"/> and <see cref="BenchVelocity"/> (<c>pos += vel * dt</c>). Per-row-pure - each
/// invocation touches only its own entity's components, no cross-entity reads, no shared mutable state, no inline
/// structural changes - so it is exactly the order-independent shape the later parallel-ForEach layer targets.
/// A cell registers <c>S</c> instances of this system to model <c>S</c> systems' worth of <c>O(S·E)</c> work.
/// </summary>
public sealed class IntegratePositionSystem : ISystem
{
    public void Update(World world, float dt)
    {
        world.ForEach<BenchPosition, BenchVelocity>((Entity _, ref BenchPosition p, ref BenchVelocity v) =>
        {
            p.X += v.X * dt;
            p.Y += v.Y * dt;
        });
    }
}
