using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using LiteNetLib;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ChannelSplitterTests
{
    private readonly record struct FakeBatch(bool Unreliable, bool Reliable) : IChannelSplittable<FakeBatch>
    {
        public bool HasUnreliableContent => Unreliable;
        public bool HasReliableContent => Reliable;
        public FakeBatch ExtractUnreliable() => new(Unreliable: true, Reliable: false);
        public FakeBatch ExtractReliable() => new(Unreliable: false, Reliable: true);
    }

    [Fact]
    public void Send_BothContents_SendsTwoPartsOnCorrectChannels()
    {
        var sent = new List<(FakeBatch Batch, DeliveryMethod Delivery)>();
        ChannelSplitter.Send(new FakeBatch(true, true), (b, d) => sent.Add((b, d)));
        Assert.Equal(2, sent.Count);
        Assert.Equal(DeliveryMethod.Sequenced, sent[0].Delivery);
        Assert.True(sent[0].Batch.HasUnreliableContent);
        Assert.Equal(DeliveryMethod.ReliableOrdered, sent[1].Delivery);
        Assert.True(sent[1].Batch.HasReliableContent);
    }

    [Fact]
    public void Send_OnlyUnreliable_SendsOnePartSequenced()
    {
        var sent = new List<(FakeBatch, DeliveryMethod)>();
        ChannelSplitter.Send(new FakeBatch(true, false), (b, d) => sent.Add((b, d)));
        Assert.Single(sent);
        Assert.Equal(DeliveryMethod.Sequenced, sent[0].Item2);
    }

    [Fact]
    public void Send_Empty_SendsNothing()
    {
        var sent = new List<(FakeBatch, DeliveryMethod)>();
        ChannelSplitter.Send(new FakeBatch(false, false), (b, d) => sent.Add((b, d)));
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData(NetChannelReliability.UnreliableSequenced, DeliveryMethod.Sequenced)]
    [InlineData(NetChannelReliability.ReliableOrdered, DeliveryMethod.ReliableOrdered)]
    public void ToDeliveryMethod_Maps(NetChannelReliability reliability, DeliveryMethod expected)
    {
        Assert.Equal(expected, ChannelSplitter.ToDeliveryMethod(reliability));
    }
}
