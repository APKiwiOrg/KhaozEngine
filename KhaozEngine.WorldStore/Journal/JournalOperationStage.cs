namespace KhaozEngine.WorldStore.Journal;

/// <summary>The operation stage at which cancellation, a provider failure or an uncertain commit was refused.</summary>
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
    /// <summary>A runtime operation without an admitted transaction.</summary>
    Operation,
    /// <summary>Runtime work in an admitted transaction.</summary>
    Transaction,
    /// <summary>The runtime transaction commit has begun.</summary>
    Commit,
    /// <summary>The runtime transaction committed, but its response failed.</summary>
    AfterCommit,
}
