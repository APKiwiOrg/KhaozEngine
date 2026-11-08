namespace KhaozEngine.Locomotion;

/// <summary>The live movement read rule that refused a query.</summary>
public enum MovementReadRefusal
{
    /// <summary>The ordinary environment pin no longer matches its captured producer state.</summary>
    EnvironmentPinStale,
    /// <summary>A movement lease was used from a thread other than its acquiring thread.</summary>
    LeaseThreadMismatch,
    /// <summary>The movement query binding no longer matches its captured lease identity and frame.</summary>
    QueryBindingStale,
}
