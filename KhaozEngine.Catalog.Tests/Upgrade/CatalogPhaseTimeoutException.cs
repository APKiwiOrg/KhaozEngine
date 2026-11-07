using System;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>A test-only phase refusal that remains catchable as TimeoutException.</summary>
internal sealed class CatalogPhaseTimeoutException(CatalogPhaseRefusal reason, string message)
    : TimeoutException(message)
{
    /// <summary>The clock whose inactivity budget expired.</summary>
    public CatalogPhaseRefusal Reason { get; } = reason;
    /// <summary>The producer operation whose progress stopped.</summary>
    public CatalogPhase Phase { get; init; }
    /// <summary>The inactivity duration used by the expired timer.</summary>
    public TimeSpan Budget { get; init; }
    /// <summary>The publish interruption point, only for the Publish phase.</summary>
    public ContentPublishStep? PublishStep { get; init; }
    /// <summary>Whether the operation had returned and the runner had not entered its next phase.</summary>
    public bool IsRunnerProgress { get; init; }
}
