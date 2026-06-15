using System;
using System.Linq;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Netcode.Abstractions.DecouplingTests;

/// <summary>
/// Proves the channel-split contract is dependency-free: a batch DTO can implement
/// <see cref="IChannelSplittable{TSelf}"/> with ONLY a reference to KhaozEngine.Netcode.Abstractions,
/// no MonoGame and no UDP transport in scope. This project deliberately omits KhaozEngine.Netcode
/// (MonoGame), KhaozEngine.Netcode.LiteNetLib, MonoGame, and LiteNetLib, so the dummy DTO below
/// compiling is the structural guarantee that mirrors SpaceGame.Multiplayer.Contracts (MessagePack-only,
/// also referenced by the ASP.NET leaderboard server SpaceGame.Web).
/// </summary>
public class ChannelSplitContractAbstractionsDecouplingTests
{
    /// <summary>
    /// A transport-agnostic batch DTO. Carries a position flag (unreliable) and an events flag
    /// (reliable), mirroring the HasPositionContent / HasReliableContent split a real DTO maps onto.
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
    public void Dto_ImplementsContract_WithAbstractionsOnly()
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
    public void Reliability_EnumIsVisible_FromAbstractionsPackage(NetChannelReliability reliability)
    {
        Assert.True(reliability is NetChannelReliability.UnreliableSequenced
            or NetChannelReliability.ReliableOrdered);
    }

    [Fact]
    public void Contract_LivesInAbstractionsAssembly_WithNoMonoGameOrTransportReference()
    {
        // The contract's declaring assembly is KhaozEngine.Netcode.Abstractions (not .Netcode),
        // and its referenced-assembly closure names no MonoGame and no UDP transport. This is the
        // runtime mirror of the build-time guarantee (this project omits those references entirely).
        var contractAssembly = typeof(IChannelSplittable<>).Assembly;
        Assert.Equal("KhaozEngine.Netcode.Abstractions", contractAssembly.GetName().Name);

        var referenced = contractAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("MonoGame.Framework", referenced);
        Assert.DoesNotContain("LiteNetLib", referenced);
        Assert.DoesNotContain("KhaozEngine.Netcode", referenced);
        Assert.DoesNotContain("KhaozEngine.Netcode.LiteNetLib", referenced);
    }
}
