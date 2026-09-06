namespace KhaozEngine.NetWorld;

/// <summary>
/// Source-compatible NetWorld facade for <see cref="KhaozEngine.Simulation.Hosting.DrainController"/>. New shared host code
/// can reference the Simulation type directly.
/// </summary>
public sealed class DrainController
{
    private readonly KhaozEngine.Simulation.Hosting.DrainController inner = new();

    /// <inheritdoc cref="KhaozEngine.Simulation.Hosting.DrainController.HasBegun"/>
    public bool HasBegun => inner.HasBegun;

    /// <inheritdoc cref="KhaozEngine.Simulation.Hosting.DrainController.IsDraining"/>
    public bool IsDraining => inner.IsDraining;

    /// <inheritdoc cref="KhaozEngine.Simulation.Hosting.DrainController.IsComplete"/>
    public bool IsComplete => inner.IsComplete;

    /// <inheritdoc cref="KhaozEngine.Simulation.Hosting.DrainController.Begin"/>
    public void Begin(float graceSeconds) => inner.Begin(graceSeconds);

    /// <inheritdoc cref="KhaozEngine.Simulation.Hosting.DrainController.Advance"/>
    public void Advance(float dt) => inner.Advance(dt);
}
