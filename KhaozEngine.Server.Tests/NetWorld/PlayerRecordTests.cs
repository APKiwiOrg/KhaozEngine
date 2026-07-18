using System.Numerics;
using System.Text;
using System.Text.Json;
using KhaozEngine.NetWorld;
using KhaozEngine.Serialization;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerRecordTests
{
    [Fact]
    public void Encode_PinsPersistedShape()
    {
        // Pins the durable blob shape. The source-generated context (NativeAOT-safe) must encode byte-for-byte like the
        // historical reflection path, including a null Game as `null` (not "" - the source-gen fast path's quirk, which
        // metadata mode avoids). Records exist in the wild via IWorldStore, so this string must not drift.
        var rec = PlayerRecord.From(new PlayerMoveState { Position = new Vector3(1f, 2f, 3f) });
        string json = Encoding.UTF8.GetString(rec.Encode());
        Assert.Equal("{\n  \"Version\": 1,\n  \"X\": 1,\n  \"Y\": 2,\n  \"Z\": 3,\n  \"Game\": null\n}", json);
    }

    [Fact]
    public void Encode_MatchesReflectionEncoding_ByteForByte()
    {
        // Direct equivalence to the old reflection encoder (JsonDefaults.IndentedWrite) for both a null and a populated
        // Game blob, proving the source-gen switch changed nothing on the wire.
        var noGame = PlayerRecord.From(new PlayerMoveState { Position = new Vector3(1.5f, -2f, 3.25f) });
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(noGame, JsonDefaults.IndentedWrite), noGame.Encode());

        var withGame = PlayerRecord.From(new PlayerMoveState { Position = new Vector3(9f, 8f, 7f) }, new byte[] { 1, 2, 3, 250 });
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(withGame, JsonDefaults.IndentedWrite), withGame.Encode());
    }

    [Fact]
    public void EncodeDecode_RoundTripsGameBlob()
    {
        var game = new byte[] { 10, 20, 30, 40 };
        PlayerRecord back = PlayerRecord.Decode(PlayerRecord.From(default, game).Encode());
        Assert.Equal(game, back.Game);
    }

    [Fact]
    public void EncodeDecode_RoundTripsPosition()
    {
        var state = new PlayerMoveState { Position = new Vector3(12.5f, 3.25f, -7f) };
        byte[] bytes = PlayerRecord.From(state).Encode();
        PlayerMoveState back = PlayerRecord.Decode(bytes).ToState();
        Assert.Equal(state.Position, back.Position);
    }

    [Fact]
    public void Decode_IgnoresUnknownFields()
    {
        // A record written by a FUTURE version with extra fields must still load (forward tolerance).
        byte[] forward = Encoding.UTF8.GetBytes(
            "{\"Version\":2,\"X\":1.0,\"Y\":2.0,\"Z\":3.0,\"Facing\":90.0,\"Health\":100}");
        PlayerRecord rec = PlayerRecord.Decode(forward);
        Assert.Equal(new Vector3(1f, 2f, 3f), rec.ToState().Position);
    }

    [Fact]
    public void Decode_MissingFieldsDefaultToZero()
    {
        // An OLD record missing newer fields still loads; absent numerics default to 0.
        byte[] old = Encoding.UTF8.GetBytes("{\"X\":4.0,\"Z\":6.0}");
        PlayerRecord rec = PlayerRecord.Decode(old);
        Assert.Equal(new Vector3(4f, 0f, 6f), rec.ToState().Position);
    }
}
