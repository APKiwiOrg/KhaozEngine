namespace KhaozEngine.Movement;

/// <summary>The participant whose capability or identity refused a navigation read.</summary>
public enum NavigationSourceRole
{
    /// <summary>The complete physics source.</summary>
    PhysicsSource,
    /// <summary>The selected movement query view.</summary>
    MovementQueryView,
    /// <summary>The authenticated query read lease.</summary>
    ReadLease,
    /// <summary>The authored movement registration.</summary>
    MovementRegistration,
}
