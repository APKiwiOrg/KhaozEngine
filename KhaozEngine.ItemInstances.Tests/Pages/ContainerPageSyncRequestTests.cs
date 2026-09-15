using System;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Pages;

/// <summary>
/// Spec 7.6's one new client-to-server message. Two bytes, both of them ids, and a reader that refuses
/// anything else. The half of spec 7.6 that is an INVARIANT over the whole client-to-server vocabulary is
/// <c>PageSyncFrameBoundTests</c>, because the take request lives in the netcode package.
/// </summary>
public class ContainerPageSyncRequestTests
{
    [Fact]
    public void The_request_is_two_bytes_and_round_trips()
    {
        var request = new ContainerPageSyncRequest(7, 3);
        Span<byte> bytes = stackalloc byte[ContainerPageSyncRequest.Bytes];

        Assert.Equal(2, ContainerPageSyncRequest.Bytes);
        Assert.Equal(2, request.Write(bytes));
        Assert.Equal((byte)7, bytes[0]);
        Assert.Equal((byte)3, bytes[1]);

        Assert.True(ContainerPageSyncRequest.TryRead(bytes, out ContainerPageSyncRequest read));
        Assert.Equal(request, read);
        Assert.Equal(request.ToArray(), bytes.ToArray());
    }

    [Fact]
    public void Anything_that_is_not_exactly_two_bytes_is_refused()
    {
        // The bytes came from a client, so a malformed one is a dropped message rather than an exception out
        // of the receive loop. Trailing bytes are refused too: this encoder emits one wire form, so a longer
        // frame is a peer probing for a reader that guesses.
        Assert.False(ContainerPageSyncRequest.TryRead(ReadOnlySpan<byte>.Empty, out ContainerPageSyncRequest read));
        Assert.Equal(default, read);
        Assert.False(ContainerPageSyncRequest.TryRead(new byte[] { 1 }, out _));
        Assert.False(ContainerPageSyncRequest.TryRead(new byte[] { 1, 2, 3 }, out _));
        Assert.True(ContainerPageSyncRequest.TryRead(new byte[] { 1, 2 }, out _));
    }

    [Fact]
    public void A_destination_too_short_to_hold_the_request_is_a_caller_bug()
    {
        // The SENDING side throws where the reading side refuses, which is the door rule every codec in this
        // package follows: a short buffer here is this process's own mistake.
        byte[] tooShort = new byte[1];
        Assert.Throws<ArgumentException>(() => new ContainerPageSyncRequest(1, 1).Write(tooShort));
    }
}
