using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>
/// What the composition root cannot get wrong silently: a short key, a missing or duplicated validator, and an option
/// out of range, including a display name cap above what the engine stores hold.
/// </summary>
public class AuthExchangeConstructionTests
{
    private static readonly ScriptedValidator Validator = ScriptedValidator.Users(("tok", "1", "Wren"));
    private static readonly FixedClock Clock = new(ExchangeFixture.Now);

    [Fact]
    public void AKeyShorterThanTheMinimum_IsRefused_AndOneAtTheMinimumIsTaken()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        byte[] shortKey = new byte[SigningSecret.MinimumBytes - 1];

        ArgumentException refused = Assert.Throws<ArgumentException>(() =>
            new AuthExchange(new[] { Validator }, store, shortKey, ExchangeFixture.Options(Clock)));
        Assert.Equal("signingSecret", refused.ParamName);
        Assert.Contains($"{SigningSecret.MinimumBytes}-byte minimum", refused.Message, StringComparison.Ordinal);

        _ = new AuthExchange(new[] { Validator }, store, new byte[SigningSecret.MinimumBytes], ExchangeFixture.Options(Clock));
    }

    [Fact]
    public void ANullArgument_IsRefused()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        byte[] key = ExchangeFixture.Key();
        AuthExchangeOptions options = ExchangeFixture.Options(Clock);

        Assert.Throws<ArgumentNullException>(() => new AuthExchange(null!, store, key, options));
        Assert.Throws<ArgumentNullException>(() => new AuthExchange(new[] { Validator }, null!, key, options));
        Assert.Throws<ArgumentNullException>(() => new AuthExchange(new[] { Validator }, store, null!, options));
        Assert.Throws<ArgumentNullException>(() => new AuthExchange(new[] { Validator }, store, key, null!));
    }

    [Fact]
    public void TheValidatorSet_NeedsOneValidatorPerProvider()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        byte[] key = ExchangeFixture.Key();
        AuthExchangeOptions options = ExchangeFixture.Options(Clock);
        var blank = new ScriptedValidator(" ", (_, _) => Task.FromResult(IdentityValidation.Refused()));

        Assert.Throws<ArgumentException>(() => new AuthExchange(Array.Empty<IIdentityValidator>(), store, key, options));
        Assert.Throws<ArgumentException>(() => new AuthExchange(new IIdentityValidator[] { null! }, store, key, options));
        Assert.Throws<ArgumentException>(() => new AuthExchange(new[] { blank }, store, key, options));
        Assert.Throws<ArgumentException>(() => new AuthExchange(new[] { Validator, Validator }, store, key, options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AccountStoreRules.MaxDisplayNameChars + 1)]
    public void ADisplayNameCap_OutsideWhatTheStoresHold_IsRefused(int cap)
    {
        var options = new AuthExchangeOptions
        {
            TokenLifetime = ExchangeFixture.Lifetime,
            Clock = Clock,
            MaxDisplayNameChars = cap,
        };

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => new AuthExchange(
            new[] { Validator }, new InMemoryAccountStore(true), ExchangeFixture.Key(), options));
        Assert.Equal(nameof(AuthExchangeOptions.MaxDisplayNameChars), refused.ParamName);
    }

    [Fact]
    public async Task TheLargestDisplayNameCap_IsTheStoreLimit_AndAFullLengthNameIsStored()
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: true);
        string name = new('n', AccountStoreRules.MaxDisplayNameChars + 10);
        var options = new AuthExchangeOptions
        {
            TokenLifetime = ExchangeFixture.Lifetime,
            Clock = Clock,
            MaxDisplayNameChars = AccountStoreRules.MaxDisplayNameChars,
        };
        var exchange = new AuthExchange(new[] { ScriptedValidator.Users(("tok", "1", name)) }, store,
            ExchangeFixture.Key(), options);

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Ok, result.Outcome);
        Assert.Equal(AccountStoreRules.MaxDisplayNameChars, result.DisplayName!.Length);
    }

    [Fact]
    public void TheDefaults_AreTheDesignedOnes()
    {
        var options = new AuthExchangeOptions { TokenLifetime = ExchangeFixture.Lifetime };

        Assert.True(options.RequireWhitelist);
        Assert.Equal(4096, options.MaxCredentialChars);
        Assert.Equal(64, options.MaxDisplayNameChars);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ProviderTimeout);
        Assert.Same(TimeProvider.System, options.Clock);
    }

    [Theory]
    [InlineData("zero lifetime", nameof(AuthExchangeOptions.TokenLifetime))]
    [InlineData("negative lifetime", nameof(AuthExchangeOptions.TokenLifetime))]
    [InlineData("zero credential cap", nameof(AuthExchangeOptions.MaxCredentialChars))]
    [InlineData("zero deadline", nameof(AuthExchangeOptions.ProviderTimeout))]
    [InlineData("infinite deadline", nameof(AuthExchangeOptions.ProviderTimeout))]
    [InlineData("deadline past a timer", nameof(AuthExchangeOptions.ProviderTimeout))]
    [InlineData("no clock", nameof(AuthExchangeOptions.Clock))]
    public void AnOptionOutOfRange_IsRefused_NamingTheOption(string scenario, string option)
    {
        TimeSpan lifetime = ExchangeFixture.Lifetime;
        AuthExchangeOptions options = scenario switch
        {
            "zero lifetime" => new AuthExchangeOptions { TokenLifetime = TimeSpan.Zero },
            "negative lifetime" => new AuthExchangeOptions { TokenLifetime = TimeSpan.FromDays(-1) },
            "zero credential cap" => new AuthExchangeOptions { TokenLifetime = lifetime, MaxCredentialChars = 0 },
            "zero deadline" => new AuthExchangeOptions { TokenLifetime = lifetime, ProviderTimeout = TimeSpan.Zero },
            "infinite deadline" => new AuthExchangeOptions { TokenLifetime = lifetime, ProviderTimeout = Timeout.InfiniteTimeSpan },
            "deadline past a timer" => new AuthExchangeOptions { TokenLifetime = lifetime, ProviderTimeout = TimeSpan.FromDays(30) },
            "no clock" => new AuthExchangeOptions { TokenLifetime = lifetime, Clock = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new AuthExchange(
            new[] { Validator }, new InMemoryAccountStore(true), ExchangeFixture.Key(), options));
        Assert.Equal(option, refused.ParamName);
    }

    [Fact]
    public async Task TheExchange_KeepsItsOwnCopyOfTheKey()
    {
        byte[] key = ExchangeFixture.Key();
        byte[] original = (byte[])key.Clone();
        AuthExchange exchange = ExchangeFixture.Build(Validator, new InMemoryAccountStore(true), key: key);
        Array.Clear(key);

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.True(SignedToken.TryVerify(result.SessionToken!, original, ExchangeFixture.Now, out _, out _));
    }
}
