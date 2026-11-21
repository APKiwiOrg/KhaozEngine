namespace KhaozEngine.Navigation;

/// <summary>
/// Where a <see cref="PathPlanRequest"/> is in its life. It is kept apart from <see cref="NavPathStatus"/>, which
/// describes a finished route: a request reaches <see cref="Ready"/> with a result of any status, including
/// <see cref="NavPath.Unreachable"/>.
/// </summary>
internal enum PathPlanState
{
    /// <summary>The request may still spend work. It has no result yet.</summary>
    Pending,

    /// <summary>The request completed and holds an immutable result. It spends nothing more.</summary>
    Ready,

    /// <summary>The request was cancelled or disposed before it completed. It has no result and spends nothing
    /// more.</summary>
    Cancelled,
}
