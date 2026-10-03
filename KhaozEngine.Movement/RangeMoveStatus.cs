namespace KhaozEngine.Movement;

/// <summary>The observed ground range or route state for this tick.</summary>
public enum RangeMoveStatus
{
    Following,
    InRange,
    WaitingForPath,
    Unreachable,
    UnsupportedTransition,
    Suspended,
    Blocked,
}
