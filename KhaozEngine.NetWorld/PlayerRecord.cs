using System.Numerics;
using System.Text.Json;
using KhaozEngine.Serialization;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The serialized player record stored under <c>player:{accountId}</c>. Flattens <see cref="PlayerMoveState"/>
/// to a versioned JSON DTO (via <see cref="KhaozEngine.Serialization.JsonDefaults"/>). Forward-tolerant: the
/// tolerant reader ignores unknown JSON members, so adding fields later (facing, health, inventory) never
/// breaks an old save, and an old save missing a newer field just gets the default. Extend by adding properties.
/// </summary>
public sealed class PlayerRecord
{
    /// <summary>Record schema version; bump when the shape changes meaningfully.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Capsule-centre X.</summary>
    public float X { get; set; }
    /// <summary>Capsule-centre Y (ground-clamped).</summary>
    public float Y { get; set; }
    /// <summary>Capsule-centre Z.</summary>
    public float Z { get; set; }

    /// <summary>Builds a record from the live movement state.</summary>
    public static PlayerRecord From(in PlayerMoveState state) =>
        new() { X = state.Position.X, Y = state.Position.Y, Z = state.Position.Z };

    /// <summary>Reconstructs the movement state from this record.</summary>
    public PlayerMoveState ToState() => new() { Position = new Vector3(X, Y, Z) };

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.IndentedWrite);

    /// <summary>Deserializes from world-store bytes; tolerant of unknown / missing fields.</summary>
    public static PlayerRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize<PlayerRecord>(data, JsonDefaults.TolerantRead) ?? new PlayerRecord();
}
