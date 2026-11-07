using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Netcode.DecouplingTests;

/// <summary>
/// Proves the channel-split contract is transport-free AND still bound through KhaozEngine.Netcode.
/// The contract physically lives in KhaozEngine.Netcode.Abstractions and KhaozEngine.Netcode
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
    public void Dto_ImplementsContract_ResolvedThroughCoreTypeForward()
    {
        // Compiling the DTO is the compile fence. Name-based lookup also verifies runtime binding.
        Type contract = Assembly.Load("KhaozEngine.Netcode")
            .GetType("KhaozEngine.Netcode.IChannelSplittable`1", throwOnError: true)!;

        Assert.Equal(typeof(IChannelSplittable<DummyBatchDto>), contract.MakeGenericType(typeof(DummyBatchDto)));
        Assert.True(contract.MakeGenericType(typeof(DummyBatchDto)).IsAssignableFrom(typeof(DummyBatchDto)));
    }

    [Theory]
    [InlineData("UnreliableSequenced")]
    [InlineData("ReliableOrdered")]
    public void Reliability_MembersResolveThroughCoreTypeForward(string member)
    {
        Type reliability = Assembly.Load("KhaozEngine.Netcode")
            .GetType("KhaozEngine.Netcode.NetChannelReliability", throwOnError: true)!;

        Assert.Equal(typeof(NetChannelReliability), reliability);
        Assert.True(reliability.IsEnum);
        Assert.True(Enum.IsDefined(reliability, member));
    }

    [Fact]
    public void Contract_ResolvesViaTypeForward_ToAbstractionsAssembly()
    {
        // Referenced through KhaozEngine.Netcode, but the type physically lives in (and forwards to)
        // KhaozEngine.Netcode.Abstractions. This is what keeps existing consumers binding unchanged.
        Assert.Equal("KhaozEngine.Netcode.Abstractions", typeof(IChannelSplittable<>).Assembly.GetName().Name);
        Assert.Equal("KhaozEngine.Netcode.Abstractions", typeof(NetChannelReliability).Assembly.GetName().Name);

        // The contract's declaring assembly has no UDP transport reference.
        var referenced = typeof(IChannelSplittable<>).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("LiteNetLib", referenced);
    }
}
