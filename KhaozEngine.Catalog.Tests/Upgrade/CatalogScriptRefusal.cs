namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Test-only refusals of a finite upgrade script or a missing readiness signal.</summary>
internal enum CatalogScriptRefusal
{
    /// <summary>The draft observation count exceeded the script's bound.</summary>
    ObservationBoundExceeded,
    /// <summary>A freeze was requested after the script's last freeze slot.</summary>
    FreezeCountExceeded,
    /// <summary>A freeze's base version differed from the expected version at its script index.</summary>
    FreezeBaseVersionMismatch,
    /// <summary>The producer completed before the requested readiness gate was signalled.</summary>
    RunnerCompletedBeforePhase,
}
