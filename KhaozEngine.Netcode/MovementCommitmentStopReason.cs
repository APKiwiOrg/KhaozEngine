namespace KhaozEngine.NetWorld;

/// <summary>Authoritative ground-control stop reasons, independent of the locomotion implementation.</summary>
public enum MovementCommitmentStopReason : byte
{
    None,
    Arrived,
    TargetLost,
    NoPath,
    TooLong,
    UnsupportedState,
    Cancelled,
}
