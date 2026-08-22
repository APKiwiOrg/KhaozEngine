using System;
using System.Text;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class HandshakeTokenCompatibilityTests
{
    [Fact]
    public void The_promoted_codec_and_ProtocolHandshake_produce_identical_bytes()
    {
        byte[] inner = Encoding.UTF8.GetBytes("subject");
        Assert.Equal(ProtocolHandshake.WrapToken("v9", inner), HandshakeToken.Wrap("v9", inner));
        Assert.Equal(ProtocolHandshake.WrapToken("", null), HandshakeToken.Wrap("", null));
    }

    [Fact]
    public void Each_unwraps_what_the_other_wrapped()
    {
        byte[] wrapped = HandshakeToken.Wrap("v9", Encoding.UTF8.GetBytes("subject"));
        Assert.True(ProtocolHandshake.TryUnwrapToken(wrapped, out string v, out byte[] inner));
        Assert.Equal("v9", v);
        Assert.Equal("subject", Encoding.UTF8.GetString(inner));
        Assert.True(HandshakeToken.TryUnwrap(ProtocolHandshake.WrapToken("v9", inner), out string v2, out _));
        Assert.Equal("v9", v2);
    }

    [Fact]
    public void The_incompatible_version_reason_token_is_the_same_string()
    {
        Assert.Equal(ProtocolHandshake.IncompatibleReason("v9"), HandshakeToken.IncompatibleVersionReason("v9"));
    }
}
