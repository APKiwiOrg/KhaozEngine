using System.Numerics;
using System.Text;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PlayerRecordTests
{
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
