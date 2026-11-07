using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Netcode.Abstractions.DecouplingTests;

/// <summary>
/// Proves the channel-split contract is dependency-free: a batch DTO can implement
/// <see cref="IChannelSplittable{TSelf}"/> with ONLY a reference to KhaozEngine.Netcode.Abstractions,
/// no UDP transport in scope. This project deliberately omits KhaozEngine.Netcode,
/// KhaozEngine.Netcode.LiteNetLib, and LiteNetLib, so the dummy DTO below
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
    public void Dto_ImplementsContract_ResolvedFromAbstractions()
    {
        // Compiling the DTO is the compile fence. Name-based lookup also verifies runtime binding.
        Type contract = Assembly.Load("KhaozEngine.Netcode.Abstractions")
            .GetType("KhaozEngine.Netcode.IChannelSplittable`1", throwOnError: true)!;

        Assert.Equal(typeof(IChannelSplittable<DummyBatchDto>), contract.MakeGenericType(typeof(DummyBatchDto)));
        Assert.True(contract.MakeGenericType(typeof(DummyBatchDto)).IsAssignableFrom(typeof(DummyBatchDto)));
    }

    [Theory]
    [InlineData("UnreliableSequenced")]
    [InlineData("ReliableOrdered")]
    public void Reliability_MembersResolveFromAbstractions(string member)
    {
        Type reliability = Assembly.Load("KhaozEngine.Netcode.Abstractions")
            .GetType("KhaozEngine.Netcode.NetChannelReliability", throwOnError: true)!;

        Assert.Equal(typeof(NetChannelReliability), reliability);
        Assert.True(reliability.IsEnum);
        Assert.True(Enum.IsDefined(reliability, member));
    }

    [Fact]
    public void Contract_LivesInAbstractionsAssembly_WithoutNetcodeOrTransportReference()
    {
        // The contract's declaring assembly is KhaozEngine.Netcode.Abstractions (not .Netcode),
        // and its direct assembly references name neither KhaozEngine.Netcode nor a UDP transport. This is the
        // runtime mirror of the build-time guarantee (this project omits those references entirely).
        var contractAssembly = typeof(IChannelSplittable<>).Assembly;
        Assert.Equal("KhaozEngine.Netcode.Abstractions", contractAssembly.GetName().Name);

        var referenced = contractAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("LiteNetLib", referenced);
        Assert.DoesNotContain("KhaozEngine.Netcode", referenced);
        Assert.DoesNotContain("KhaozEngine.Netcode.LiteNetLib", referenced);
    }
}
