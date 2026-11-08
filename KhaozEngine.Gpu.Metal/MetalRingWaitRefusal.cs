namespace KhaozEngine.Gpu.Metal;

/// <summary>The rules of the MetalRingWait refusal family.</summary>
public enum MetalRingWaitRefusal
{
    /// <summary>Refuses a Metal segment claim while the caller holds the submit lock.</summary>
    SubmitLockHeld,
}
