using System.Linq;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Netcode.DecouplingTests;

/// <summary>
/// Proves the channel-split contract is transport-free AND still bound through KhaozEngine.Netcode.
/// Since 4.9.0 the contract physically lives in KhaozEngine.Netcode.Abstractions and KhaozEngine.Netcode
/// type-forwards it; this project references ONLY KhaozEngine.Netcode (no LiteNetLib), so its compiling
/// and passing is the guarantee that existing consumers referencing KhaozEngine.Netcode keep binding
/// <see cref="IChannelSplittable{TSelf}"/> with no source change. The sibling
/// KhaozEngine.Netcode.Abstractions.DecouplingTests covers the Abstractions-only DTO path.
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

    [Fact]
    public void Contract_ResolvesViaTypeForward_ToAbstractionsAssembly()
    {
        // Referenced through KhaozEngine.Netcode, but the type physically lives in (and forwards to)
        // KhaozEngine.Netcode.Abstractions. This is what keeps existing consumers binding unchanged.
        Assert.Equal("KhaozEngine.Netcode.Abstractions", typeof(IChannelSplittable<>).Assembly.GetName().Name);
        Assert.Equal("KhaozEngine.Netcode.Abstractions", typeof(NetChannelReliability).Assembly.GetName().Name);

        // And the contract's declaring assembly drags in no MonoGame / UDP transport.
        var referenced = typeof(IChannelSplittable<>).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("MonoGame.Framework", referenced);
        Assert.DoesNotContain("LiteNetLib", referenced);
    }
}
