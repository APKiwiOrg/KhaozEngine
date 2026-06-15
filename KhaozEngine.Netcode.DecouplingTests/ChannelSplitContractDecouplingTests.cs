using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Netcode.DecouplingTests;

/// <summary>
/// Proves the channel-split contract is transport-free: a batch DTO can implement
/// <see cref="IChannelSplittable{TSelf}"/> with ONLY a reference to KhaozEngine.Netcode, no UDP
/// transport in scope. This project deliberately omits the KhaozEngine.Netcode.LiteNetLib and
/// LiteNetLib references, so the dummy DTO below compiling is the structural guarantee that mirrors
/// SpaceGame.Multiplayer.Contracts (MessagePack-only, also referenced by the leaderboard web server).
/// </summary>
public class ChannelSplitContractDecouplingTests
{
    /// <summary>
    /// A transport-agnostic batch DTO. Carries a position flag (unreliable) and an events flag
    /// (reliable), mirroring the HasPositionContent / HasEventContent split a real DTO maps onto.
    /// </summary>
    private readonly record struct DummyBatchDto(bool HasPositions, bool HasEvents)
        : IChannelSplittable<DummyBatchDto>
    {
        public bool HasUnreliableContent => HasPositions;
        public bool HasReliableContent => HasEvents;
        public DummyBatchDto ExtractUnreliable() => new(HasPositions: HasPositions, HasEvents: false);
        public DummyBatchDto ExtractReliable() => new(HasPositions: false, HasEvents: HasEvents);
    }

    [Fact]
    public void Dto_ImplementsContract_WithoutTransportReference()
    {
        IChannelSplittable<DummyBatchDto> batch = new DummyBatchDto(HasPositions: true, HasEvents: true);

        Assert.True(batch.HasUnreliableContent);
        Assert.True(batch.HasReliableContent);

        var unreliable = batch.ExtractUnreliable();
        Assert.True(unreliable.HasUnreliableContent);
        Assert.False(unreliable.HasReliableContent);

        var reliable = batch.ExtractReliable();
        Assert.False(reliable.HasUnreliableContent);
        Assert.True(reliable.HasReliableContent);
    }

    [Theory]
    [InlineData(NetChannelReliability.UnreliableSequenced)]
    [InlineData(NetChannelReliability.ReliableOrdered)]
    public void Reliability_EnumIsVisible_FromCorePackage(NetChannelReliability reliability)
    {
        // The enum lives in the transport-free core too, so a DTO project can name channels
        // without the LiteNetLib DeliveryMethod mapping (which stays in .LiteNetLib).
        Assert.True(reliability is NetChannelReliability.UnreliableSequenced
            or NetChannelReliability.ReliableOrdered);
    }
}
