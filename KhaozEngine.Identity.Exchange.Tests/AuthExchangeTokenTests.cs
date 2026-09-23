using System;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>
/// The token the exchange mints is the one the game server's door verifies: v2 with no persistence key, v3 with one,
/// under the key a host loads through <see cref="SigningSecret"/>, expiring at the clock plus the lifetime.
/// </summary>
public class AuthExchangeTokenTests
{
    private static readonly DateTimeOffset Now = ExchangeFixture.Now;

    private static async Task<AuthExchangeResult> SignInAsync(IAuthExchangePolicy? policy, byte[] key, string? name = "Wren")
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Users(("tok", "80351110224678912", name)), store,
            policy: policy, key: key);
        return await exchange.ExchangeAsync("discord", "tok");
    }

    [Fact]
    public async Task TheDefaultPolicy_MintsAV2Token_ThatTheDoorVerifies()
    {
        byte[] key = ExchangeFixture.Key();
        AuthExchangeResult result = await SignInAsync(policy: null, key);
        var door = new HmacTokenAuthenticator(key, () => Now);
        byte[] token = Encoding.UTF8.GetBytes(result.SessionToken!);

        Assert.StartsWith("v2.", result.SessionToken, StringComparison.Ordinal);
        Assert.True(door.TryAuthenticate(token, out string subject, out string reason), reason);
        Assert.Equal("discord:80351110224678912", subject);
        Assert.Equal("Wren", door.ReadDisplayName(token));
        Assert.Equal(string.Empty, door.ReadPersistenceKey(token));
    }

    [Fact]
    public async Task APersistenceKey_MintsAV3Token_CarryingIt()
    {
        byte[] key = ExchangeFixture.Key();
        AuthExchangeResult result = await SignInAsync(new PassThroughPolicy("character:17"), key);
        var door = new HmacTokenAuthenticator(key, () => Now);
        byte[] token = Encoding.UTF8.GetBytes(result.SessionToken!);

        Assert.StartsWith("v3.", result.SessionToken, StringComparison.Ordinal);
        Assert.True(door.TryAuthenticate(token, out string subject, out _));
        Assert.Equal("discord:80351110224678912", subject);
        Assert.Equal("Wren", door.ReadDisplayName(token));
        Assert.Equal("character:17", door.ReadPersistenceKey(token));
    }

    [Fact]
    public async Task AnEmptyPersistenceKey_IsNoKey_AndMintsV2()
    {
        AuthExchangeResult result = await SignInAsync(new PassThroughPolicy(string.Empty), ExchangeFixture.Key());

        Assert.StartsWith("v2.", result.SessionToken, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToken_ExpiresAtTheClockPlusTheLifetime()
    {
        byte[] key = ExchangeFixture.Key();
        AuthExchangeResult result = await SignInAsync(policy: null, key);
        DateTimeOffset expires = Now + ExchangeFixture.Lifetime;

        Assert.Equal(expires, result.ExpiresAtUtc);
        Assert.True(SignedToken.TryVerify(result.SessionToken!, key, expires, out _, out _));
        Assert.False(SignedToken.TryVerify(result.SessionToken!, key, expires.AddSeconds(1), out _, out string reason));
        Assert.Equal("expired", reason);
    }

    [Fact]
    public async Task TheToken_DoesNotVerifyUnderAnotherKey()
    {
        AuthExchangeResult result = await SignInAsync(policy: null, ExchangeFixture.Key());

        Assert.False(SignedToken.TryVerify(result.SessionToken!, SigningSecret.CreateEphemeral(), Now, out _, out string reason));
        Assert.Equal("bad signature", reason);
    }

    [Fact]
    public async Task AnEphemeralDevelopmentKey_MintsTokensThatVerifyUnderIt()
    {
        byte[] key = SigningSecret.CreateEphemeral();
        AuthExchangeResult result = await SignInAsync(policy: null, key);

        Assert.True(SignedToken.TryVerify(result.SessionToken!, key, Now, out string subject, out _));
        Assert.Equal("discord:80351110224678912", subject);
    }

    [Fact]
    public async Task TheResponseName_IsTheStoredName_WhileTheTokenCarriesThePolicyClaim()
    {
        // Ruinborne stores null for a nameless provider and signs the subject as the token's name claim.
        byte[] key = ExchangeFixture.Key();
        AuthExchangeResult result = await SignInAsync(new PassThroughPolicy(), key, name: null);

        Assert.Null(result.DisplayName);
        Assert.True(SignedToken.TryVerify(result.SessionToken!, key, Now, out _, out string tokenName, out _));
        Assert.Equal("discord:80351110224678912", tokenName);
    }
}
