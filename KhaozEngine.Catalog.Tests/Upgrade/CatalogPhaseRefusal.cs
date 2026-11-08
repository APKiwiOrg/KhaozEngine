namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Test-only inactivity refusals, separate from cancellation of real catalog and pack I/O.</summary>
internal enum CatalogPhaseRefusal
{
    /// <summary>The producer clock reached the current phase's inactivity budget.</summary>
    PhaseStalled,
    /// <summary>The independent safety clock reached the current phase's inactivity budget.</summary>
    SafetyPhaseStalled,
}
