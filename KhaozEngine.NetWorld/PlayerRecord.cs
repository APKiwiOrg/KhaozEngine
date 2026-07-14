using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The serialized player record stored under <c>player:{accountId}</c>. Flattens <see cref="PlayerMoveState"/>
/// to a versioned JSON DTO (via the source-generated <see cref="NetWorldJsonContext"/>, which preserves the
/// indented-write / tolerant-read encoding so records stay byte-compatible and NativeAOT-safe). Forward-tolerant: the
/// tolerant reader ignores unknown JSON members, so adding fields later (facing, health, inventory) never
/// breaks an old save, and an old save missing a newer field just gets the default. Extend by adding properties.
/// The engine's own fields are position (<see cref="X"/>/<see cref="Y"/>/<see cref="Z"/>); the game's durable
/// per-player state rides in the opaque <see cref="Game"/> blob, which the engine never interprets.
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

    /// <summary>
    /// The game's opaque, durable per-player blob (XP, skills, inventory, quest log, …), or null when the game
    /// persists no per-player state. The engine treats it as raw bytes - it never deserializes it - so the game
    /// owns the format and its migration (see <see cref="PlayerGameStateCapture"/> / <see cref="PlayerGameStateApply"/>).
    /// Serialized as base64 in the record JSON; an old save lacking this member decodes to null (forward-tolerant),
    /// and it is account-keyed like the whole record, so it is unaffected by cell handoff.
    /// </summary>
    public byte[]? Game { get; set; }

    /// <summary>Builds a record from the live movement state, with no game blob.</summary>
    public static PlayerRecord From(in PlayerMoveState state) =>
        new() { X = state.Position.X, Y = state.Position.Y, Z = state.Position.Z };

    /// <summary>Builds a record from the live movement state plus the game's opaque durable blob (empty is stored as
    /// null so a "no game state" record encodes identically to <see cref="From(in PlayerMoveState)"/>).</summary>
    public static PlayerRecord From(in PlayerMoveState state, byte[]? game) => new()
    {
        X = state.Position.X,
        Y = state.Position.Y,
        Z = state.Position.Z,
        Game = game is { Length: > 0 } ? game : null,
    };

    /// <summary>Reconstructs the movement state from this record.</summary>
    public PlayerMoveState ToState() => new() { Position = new Vector3(X, Y, Z) };

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, NetWorldJsonContext.Default.PlayerRecord);

    /// <summary>Deserializes from world-store bytes; tolerant of unknown / missing fields.</summary>
    public static PlayerRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize(data, NetWorldJsonContext.Default.PlayerRecord) ?? new PlayerRecord();
}
