using System.Text;
using System.Text.Json;
using KhaozEngine.NetWorld;
using KhaozEngine.Serialization;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldMetaRecordTests
{
    [Fact]
    public void EncodeDecode_RoundTripsNextNetId()
    {
        WorldMetaRecord back = WorldMetaRecord.Decode(new WorldMetaRecord { NextNetId = 9_000_000_001L }.Encode());
        Assert.Equal(9_000_000_001L, back.NextNetId);
    }

    [Fact]
    public void Encode_PinsPersistedShape()
    {
        // Pins the durable meta blob shape; the source-generated context must encode it exactly as before.
        string json = Encoding.UTF8.GetString(new WorldMetaRecord { NextNetId = 42L }.Encode());
        Assert.Equal("{\n  \"Version\": 1,\n  \"NextNetId\": 42\n}", json);
    }

    [Fact]
    public void Encode_MatchesReflectionEncoding_ByteForByte()
    {
        var rec = new WorldMetaRecord { NextNetId = 123_456_789L };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(rec, JsonDefaults.IndentedWrite), rec.Encode());
    }

    [Fact]
    public void Decode_ToleratesOldNarrowerNextNetId()
    {
        // A pre-10.0.0 record stored NextNetId as a 32-bit value; it must widen into the long unchanged.
        byte[] old = Encoding.UTF8.GetBytes("{\"Version\":1,\"NextNetId\":123}");
        Assert.Equal(123L, WorldMetaRecord.Decode(old).NextNetId);
    }
}
