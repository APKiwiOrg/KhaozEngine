namespace KhaozEngine.WorldStore.Journal;

/// <summary>The schema operation stage at which cancellation or a provider failure was refused.</summary>
public enum JournalOperationStage
{
    /// <summary>Opening a writer schema connection, before transaction admission.</summary>
    SchemaOpen,
    /// <summary>Beginning the writer schema transaction.</summary>
    SchemaTransactionBegin,
    /// <summary>Initializing or validating the schema inside the writer transaction.</summary>
    SchemaInitialization,
    /// <summary>Opening the read-only schema connection and transaction.</summary>
    SchemaReadOnlyOpen,
    /// <summary>Validating the schema inside the read-only transaction.</summary>
    SchemaReadOnlyValidation,
}
