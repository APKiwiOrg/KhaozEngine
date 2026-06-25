namespace KhaozEngine.Netcode;

/// <summary>
/// Opaque handle to a transport-level connection. Value 0 is the none/sentinel id; valid ids are positive.
/// Value-equatable so it can be a dictionary key and compared directly.
/// </summary>
public readonly record struct NetConnectionId(int Value)
{
    /// <summary>The sentinel "no connection" id.</summary>
    public static NetConnectionId None => new(0);

    /// <summary>True when this is a real (positive) connection id.</summary>
    public bool IsValid => Value > 0;
}
