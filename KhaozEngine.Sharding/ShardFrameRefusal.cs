namespace KhaozEngine.Sharding;

/// <summary>Rules refused while admitting a physics world into a cell's frame.</summary>
public enum ShardFrameRefusal
{
    /// <summary>Refuses a factory world whose origin differs from the cell's fixed frame anchor.</summary>
    PhysicsFactoryOriginMismatch,
}
