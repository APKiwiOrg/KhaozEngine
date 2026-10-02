namespace KhaozEngine.Physics;

/// <summary>A non-owning, restricted query view over a physics world. Mutations are unsupported.</summary>
public interface IPhysicsWorldQueryView : IPhysicsWorld
{
    /// <summary>The exact logical world that created this view, retained as inspectable metadata after disposal.</summary>
    IPhysicsWorld SourceWorld { get; }
}
