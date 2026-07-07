namespace KhaozEngine.Objectives;

/// <summary>
/// The payload of <see cref="ObjectiveTracker.ObjectiveCompleted"/>: which objective completed, plus its
/// definition's opaque <see cref="Metadata"/> echoed back unchanged so the game can route rewards / points /
/// tree unlocks without the framework knowing any of it.
/// </summary>
public readonly struct ObjectiveCompletion
{
    /// <summary>The <see cref="ObjectiveDefinition.Id"/> of the objective that just completed.</summary>
    public string ObjectiveId { get; }

    /// <summary>The completed definition's <see cref="ObjectiveDefinition.Metadata"/>, echoed verbatim.</summary>
    public object? Metadata { get; }

    /// <summary>Creates a completion payload.</summary>
    public ObjectiveCompletion(string objectiveId, object? metadata)
    {
        ObjectiveId = objectiveId;
        Metadata = metadata;
    }
}
