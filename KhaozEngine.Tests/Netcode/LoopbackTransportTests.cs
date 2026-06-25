using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class LoopbackTransportTests
{
    [Fact]
    public void NetConnectionId_None_IsInvalid_AndPositiveIsValid()
    {
        Assert.False(NetConnectionId.None.IsValid);
        Assert.True(new NetConnectionId(1).IsValid);
        Assert.Equal(new NetConnectionId(1), new NetConnectionId(1)); // value equality
    }

    [Fact]
    public void NetEvent_FromData_CarriesPayloadAndReliability()
    {
        var ev = NetEvent.FromData(new NetConnectionId(1), new byte[] { 7, 8 }, NetChannelReliability.ReliableOrdered);
        Assert.Equal(NetEventType.Data, ev.Type);
        Assert.Equal(new NetConnectionId(1), ev.Connection);
        Assert.Equal(new byte[] { 7, 8 }, ev.Data);
        Assert.Equal(NetChannelReliability.ReliableOrdered, ev.Reliability);
    }
}
