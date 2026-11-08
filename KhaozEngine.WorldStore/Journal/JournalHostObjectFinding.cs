namespace KhaozEngine.WorldStore.Journal;

/// <summary>A host key or trigger whose action would run outside the journal during reset.</summary>
/// <param name="Reason">HostForeignKeyActionUnsafe for a key, or HostTriggerUnsafe for a trigger.</param>
/// <param name="HostTable">The table declaring the foreign key, or null for a trigger.</param>
/// <param name="ReferencedTable">The journal table referenced by the key or targeted by the trigger.</param>
/// <param name="DeleteAction">The provider's declared delete action, or null for a trigger.</param>
/// <param name="TriggerName">The trigger's name, or null for a foreign key.</param>
public sealed record JournalHostObjectFinding(
    JournalStoreRefusal Reason, string? HostTable, string ReferencedTable, string? DeleteAction, string? TriggerName)
{
    /// <summary>The SQL Server schema declaring the host key, or null for providers without schemas.</summary>
    public string? HostSchema { get; init; }
    /// <summary>The declared SQL Server foreign key name, or null for a trigger or an unnamed provider key.</summary>
    public string? ForeignKeyName { get; init; }
}
