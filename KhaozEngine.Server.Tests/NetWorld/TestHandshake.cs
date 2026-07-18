using System.Text;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Test helper: builds the connect token a real <see cref="WorldClient"/> now presents - this build's engine wire
/// generation (<see cref="MoveProtocol.WireProtocolVersion"/>) folded into the Hello - wrapping an optional inner auth
/// token. A bare <c>NetClient</c> driven straight into a <see cref="WorldServer"/> /
/// <see cref="ShardedWorldServer"/> must present this, because those servers now install a
/// <see cref="WireGenerationAuthenticator"/> that rejects a token omitting the wire generation (10.2.0). The gate
/// strips the wire layer and delegates the inner token unchanged, so subject / account derivation is exactly as if the
/// inner token were presented raw (pre-10.2.0).
/// </summary>
internal static class TestHandshake
{
    /// <summary>The wire-wrapped connect token for an anonymous (no inner auth token) client.</summary>
    public static byte[] Wire() => Wire((byte[]?)null);

    /// <summary>Wraps <paramref name="innerToken"/> (the auth token the server's inner authenticator expects) with this
    /// build's engine wire layer.</summary>
    public static byte[] Wire(byte[]? innerToken) =>
        ProtocolHandshake.BuildClientToken(MoveProtocol.WireProtocolVersion, consumerVersion: null, innerToken);

    /// <summary>UTF-8 convenience: wraps a string inner token (e.g. an account id an AllowAllAuthenticator echoes as
    /// the connection subject).</summary>
    public static byte[] Wire(string innerToken) => Wire(Encoding.UTF8.GetBytes(innerToken));
}
