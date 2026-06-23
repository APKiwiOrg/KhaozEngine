namespace KhaozEngine.Persistence;

/// <summary>
/// Opt-in contract for types that carry an integer schema-version field. Implementing this lets a
/// type use the zero-config <see cref="MigrationChain.For{T}()"/> factory instead of supplying
/// get/set delegates. Any POCO can still be migrated via the delegate overload without this interface.
/// </summary>
public interface ISchemaVersioned
{
    /// <summary>The persisted schema version of this value.</summary>
    int SchemaVersion { get; set; }
}
