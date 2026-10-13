namespace KhaozEngine.Physics;

/// <summary>Optional installed-static revision, independent of simulation steps and coordinate rebases.</summary>
public interface IPhysicsStaticGeometrySource
{
    long StaticGeometryGeneration { get; }
}
