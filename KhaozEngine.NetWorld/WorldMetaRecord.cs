using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The small world-scope meta record persisted under <see cref="CellPersistenceConfig.MetaKey"/>. Carries the
/// <see cref="KhaozEngine.Replication.NetId"/> high-water mark so the allocator resumes above every persisted entity id on restart,
/// keeping restored cell entities from colliding with freshly spawned players. Versioned + tolerant like
/// <see cref="PlayerRecord"/>: extend by adding properties.
/// </summary>
public sealed class WorldMetaRecord
{
    /// <summary>Record schema version; bump when the shape changes meaningfully.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The next NetId the allocator will hand out (one past the highest ever allocated).</summary>
    public int NextNetId { get; set; }

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.IndentedWrite);

    /// <summary>Deserializes from world-store bytes; tolerant of unknown / missing fields.</summary>
    public static WorldMetaRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize<WorldMetaRecord>(data, JsonDefaults.TolerantRead) ?? new WorldMetaRecord();
}
