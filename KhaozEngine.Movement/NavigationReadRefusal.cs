namespace KhaozEngine.Movement;

/// <summary>The provider capability rule that refused a navigation read.</summary>
public enum NavigationReadRefusal
{
    /// <summary>A parallel contact build requires concurrent reads on its physics source and query view.</summary>
    ConcurrentReadsRequired,
    /// <summary>A stable legacy read requires query leases on its physics source and any selected query view.</summary>
    LegacyQueryLeaseRequired,
}
