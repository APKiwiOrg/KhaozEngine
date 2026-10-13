namespace KhaozEngine.Locomotion;

/// <summary>The coordinate space of ordinary analytic ground delegates and returned heights.</summary>
public enum OrdinaryGroundSpace
{
    /// <summary>Rounded absolute axes and absolute heights, adapted through the captured physics origin.</summary>
    World = 0,
    /// <summary>Original physics-local axes and local heights, without absolute float translation.</summary>
    Frame = 1
}
