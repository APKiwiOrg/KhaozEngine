using System.Text.Json;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The serialized tile player record, stored under <c>player:{accountId}</c> where the account id is the verified
/// token subject. Forward-tolerant like its float sibling: unknown members are ignored and a missing member takes
/// its default, so adding a field later never breaks an old save. The engine's own fields are the tile, the plane
/// and the facing. The game's durable state rides in the opaque <see cref="Game"/> blob, which the engine never
/// deserializes.
/// <para>Everything here is an integer, which is the point: a tile world's authoritative position is a lattice
/// address, so a record round-trips exactly and two saves of the same standing player are byte-identical. That is
/// what makes the dirty comparison in the persistence core a cheap byte compare rather than a tolerance test.</para>
/// </summary>
public sealed class TilePlayerRecord
{
    /// <summary>Record schema version. Bump when the shape changes meaningfully, so a later reader can tell an old
    /// record from a new one rather than inferring it from which members happen to be present.</summary>
    public int Version { get; set; } = 1;

    /// <summary>World tile x.</summary>
    public int TileX { get; set; }

    /// <summary>World tile z.</summary>
    public int TileZ { get; set; }

    /// <summary>Plane the player stood on.</summary>
    public int Plane { get; set; }

    /// <summary>Facing, as the <see cref="TileDirection"/> byte. Stored as the raw byte rather than the enum name so
    /// the record does not break if the enum is ever reordered, and so a value outside the enum survives the decode
    /// intact and can be rejected by validation instead of silently becoming a legal direction.</summary>
    public byte Facing { get; set; }

    /// <summary>The game's opaque durable blob (XP, skills, inventory, quest log), or null when the game persists no
    /// per-player state. Base64 in the record JSON. The engine stores it verbatim and never interprets it, so the
    /// game owns the format and its migration.</summary>
    public byte[]? Game { get; set; }

    /// <summary>Builds a record from a live state. A route in progress is deliberately NOT persisted: a rejoining
    /// player stands on the tile they reached, which is where the server placed them. An empty blob is stored as
    /// null, so a "no game state" record encodes identically whether the hook returned null or an empty array.</summary>
    public static TilePlayerRecord From(in TileMoveState state, byte[]? game = null) => new()
    {
        TileX = state.Tile.X,
        TileZ = state.Tile.Z,
        Plane = state.Tile.Plane,
        Facing = (byte)state.Facing,
        Game = game is { Length: > 0 } ? game : null,
    };

    /// <summary>Reconstructs a standing state from this record: on the tile, facing the stored way, with no route
    /// and no step under way.</summary>
    public TileMoveState ToState() =>
        TileMoveState.At(new TileCoord(TileX, TileZ, Plane), (TileDirection)Facing);

    /// <summary>Serializes to UTF-8 JSON bytes for the world store.</summary>
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, TileNetcodeJsonContext.Default.TilePlayerRecord);

    /// <summary>Deserializes from world-store bytes, tolerant of unknown and missing members. Throws on bytes that
    /// are not JSON at all, which the persistence core catches and routes to quarantine.</summary>
    public static TilePlayerRecord Decode(byte[] data) =>
        JsonSerializer.Deserialize(data, TileNetcodeJsonContext.Default.TilePlayerRecord) ?? new TilePlayerRecord();
}
