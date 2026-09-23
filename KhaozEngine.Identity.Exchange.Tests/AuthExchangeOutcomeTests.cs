using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>
/// Every branch of the fixed sequence, in order: the shape check, the provider, the account, the gate and the token.
/// Each refusal before the account step is proven to touch no account.
/// </summary>
public class AuthExchangeOutcomeTests
{
    private static readonly DateTimeOffset Now = ExchangeFixture.Now;

    [Theory]
    [InlineData(null)]
    [InlineData("google")]
    [InlineData("Discord")]
    [InlineData("")]
    public async Task AnUnknownProvider_IsMalformed_BeforeAnyProviderOrStoreCall(string? provider)
    {
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(validator, store).ExchangeAsync(provider, "tok");

        Assert.Equal(AuthExchangeOutcome.Malformed, result.Outcome);
        Assert.Equal(AuthExchangeCause.UnknownProvider, result.Cause);
        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankCredential_IsMalformed_BeforeAnyProviderCall(string? credential)
    {
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(validator, store).ExchangeAsync("discord", credential);

        Assert.Equal(AuthExchangeOutcome.Malformed, result.Outcome);
        Assert.Equal(AuthExchangeCause.CredentialShape, result.Cause);
        Assert.Equal(0, validator.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ACredentialOverTheCap_IsMalformed_AndOneAtTheCapReachesTheProvider()
    {
        string atCap = new('a', 4096);
        ScriptedValidator validator = ScriptedValidator.Users((atCap, "1", "Wren"));
        var store = new CountingAccountStore(whitelistOnCreate: true);
        AuthExchange exchange = ExchangeFixture.Build(validator, store);

        AuthExchangeResult over = await exchange.ExchangeAsync("discord", atCap + "a");
        Assert.Equal(AuthExchangeOutcome.Malformed, over.Outcome);
        Assert.Equal(0, validator.Calls);

        AuthExchangeResult at = await exchange.ExchangeAsync("discord", atCap);
        Assert.Equal(AuthExchangeOutcome.Ok, at.Outcome);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task ARefusedCredential_IsInvalidCredential_AndTouchesNoAccount()
    {
        ScriptedValidator validator = ScriptedValidator.Always(IdentityValidation.Refused("expired"));
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(validator, store).ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.InvalidCredential, result.Outcome);
        Assert.Equal(AuthExchangeCause.CredentialRefused, result.Cause);
        Assert.Null(result.Subject);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AProviderOutage_IsUnavailable_NeverAnInvalidCredential()
    {
        ScriptedValidator validator = ScriptedValidator.Always(IdentityValidation.ProviderUnavailable("returned 503"));
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(validator, store).ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Unavailable, result.Outcome);
        Assert.Equal(AuthExchangeCause.ProviderUnavailable, result.Cause);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AValidatorThatThrows_IsUnavailable_WithTheFault()
    {
        var fault = new HttpRequestException("connection reset");
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(ScriptedValidator.Throwing(fault), store)
            .ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Unavailable, result.Outcome);
        Assert.Equal(AuthExchangeCause.ProviderUnavailable, result.Cause);
        Assert.Same(fault, result.Fault);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ALegacyValidator_KeepsItsMeaning_ThroughTheDefaultDetailedCall()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult refused = await ExchangeFixture.Build(new LegacyValidator(() => null), store)
            .ExchangeAsync("discord", "tok");
        AuthExchangeResult thrown = await ExchangeFixture
            .Build(new LegacyValidator(() => throw new HttpRequestException("down")), store)
            .ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.InvalidCredential, refused.Outcome);
        Assert.Equal(AuthExchangeOutcome.Unavailable, thrown.Outcome);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task TheProviderDeadline_AnswersUnavailable_AndCancelsACooperativeValidator()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        var options = new AuthExchangeOptions
        {
            TokenLifetime = ExchangeFixture.Lifetime,
            ProviderTimeout = TimeSpan.FromMilliseconds(50),
            Clock = new FixedClock(Now),
        };
        var exchange = new AuthExchange(new[] { ScriptedValidator.Stalled() }, store, ExchangeFixture.Key(), options);

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Unavailable, result.Outcome);
        Assert.Equal(AuthExchangeCause.ProviderTimeout, result.Cause);
        Assert.IsType<TimeoutException>(result.Fault);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task TheProviderDeadline_HoldsEvenForAValidatorThatIgnoresCancellation()
    {
        var never = new TaskCompletionSource<IdentityValidation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deaf = new ScriptedValidator("discord", (_, _) => never.Task);
        var options = new AuthExchangeOptions
        {
            TokenLifetime = ExchangeFixture.Lifetime,
            ProviderTimeout = TimeSpan.FromMilliseconds(50),
            Clock = new FixedClock(Now),
        };
        var exchange = new AuthExchange(new[] { deaf }, new CountingAccountStore(true), ExchangeFixture.Key(), options);

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeCause.ProviderTimeout, result.Cause);
        // The abandoned call faulting later is observed rather than left to surface as an unobserved exception.
        never.SetException(new HttpRequestException("late"));
    }

    [Fact]
    public async Task CallerCancellation_Propagates_BeforeAndDuringTheProviderCall()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store)
                .ExchangeAsync("discord", "tok", cancelled.Token));

        using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExchangeFixture.Build(ScriptedValidator.Stalled(), store).ExchangeAsync("discord", "tok", during.Token));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task AFirstSignIn_CreatesTheAccount_AsTheStoreWhitelistsOnCreate()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(ScriptedValidator.Users(("tok", "80351110224678912", "Wren")), store)
            .ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Ok, result.Outcome);
        Assert.Equal(AuthExchangeCause.None, result.Cause);
        Assert.Equal("discord:80351110224678912", result.Subject);
        Assert.Equal("Wren", result.DisplayName);
        AccountRecord stored = (await store.Inner.FindAsync("discord:80351110224678912"))!;
        Assert.True(stored.Whitelisted);
        Assert.Equal(Now, store.LastSignIn!.Value.At);
    }

    [Fact]
    public async Task NotWhitelisted_IsRefused_WhenTheWhitelistIsRequired_AndAdmittedWhenItIsNot()
    {
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        var store = new CountingAccountStore(whitelistOnCreate: false);

        AuthExchangeResult gated = await ExchangeFixture.Build(validator, store).ExchangeAsync("discord", "tok");
        AuthExchangeResult open = await ExchangeFixture.Build(validator, store, requireWhitelist: false)
            .ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.NotWhitelisted, gated.Outcome);
        Assert.Equal("discord:1", gated.Subject);
        Assert.Equal("Wren", gated.DisplayName);
        Assert.Null(gated.SessionToken);
        Assert.Equal(AuthExchangeOutcome.Ok, open.Outcome);
        Assert.NotNull(open.SessionToken);
    }

    [Fact]
    public async Task AWhitelistedAccount_IsAdmitted()
    {
        var store = new CountingAccountStore(whitelistOnCreate: false);
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        AuthExchange exchange = ExchangeFixture.Build(validator, store);
        await exchange.ExchangeAsync("discord", "tok");
        await store.Inner.SetWhitelistedAsync("discord:1", true);

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Ok, result.Outcome);
        Assert.Equal(Now + ExchangeFixture.Lifetime, result.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABan_IsDecidedBeforeTheWhitelist(bool whitelisted)
    {
        var store = new CountingAccountStore(whitelistOnCreate: whitelisted);
        var policy = new PassThroughPolicy();
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store, policy: policy);
        await exchange.ExchangeAsync("discord", "tok");
        await store.Inner.BanAsync("discord:1", "griefing", until: null);
        int issuedBefore = policy.Issued;

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Banned, result.Outcome);
        Assert.Equal(new AccountBan("griefing", null), result.Ban);
        Assert.Null(result.SessionToken);
        // The policy's claims run only after admission, so a banned caller gets no per-account state made for them.
        Assert.Equal(issuedBefore, policy.Issued);
    }

    [Fact]
    public async Task ALapsedBan_Admits_AndABanStillRefusesAnOpenGame()
    {
        var clock = new FixedClock(Now);
        var store = new CountingAccountStore(whitelistOnCreate: true);
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store, clock,
            requireWhitelist: false);
        await exchange.ExchangeAsync("discord", "tok");
        await store.Inner.BanAsync("discord:1", "cooldown", Now.AddMinutes(5));

        Assert.Equal(AuthExchangeOutcome.Banned, (await exchange.ExchangeAsync("discord", "tok")).Outcome);
        clock.Now = Now.AddMinutes(5);
        Assert.Equal(AuthExchangeOutcome.Ok, (await exchange.ExchangeAsync("discord", "tok")).Outcome);
    }

    [Fact]
    public async Task ARepeatSignIn_KeepsTheStoredName_WhenThePolicyResolvesNone()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        ScriptedValidator validator = ScriptedValidator.Users(("named", "1", "Wren"), ("nameless", "1", null));
        AuthExchange exchange = ExchangeFixture.Build(validator, store, policy: new PassThroughPolicy());

        await exchange.ExchangeAsync("discord", "named");
        AuthExchangeResult repeat = await exchange.ExchangeAsync("discord", "nameless");

        Assert.Null(store.LastSignIn!.Value.DisplayName);
        Assert.Equal("Wren", repeat.DisplayName);
        Assert.Equal("Wren", (await store.Inner.FindAsync("discord:1"))!.DisplayName);
    }

    [Fact]
    public async Task ARepeatSignIn_RefreshesTheName_WhenTheProviderGivesANewOne()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        ScriptedValidator validator = ScriptedValidator.Users(("old", "1", "Wren"), ("new", "1", "Kestrel"));
        AuthExchange exchange = ExchangeFixture.Build(validator, store);

        await exchange.ExchangeAsync("discord", "old");
        AuthExchangeResult repeat = await exchange.ExchangeAsync("discord", "new");

        Assert.Equal("Kestrel", repeat.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task TheDefaultPolicy_StoresTheProviderSubject_ForANamelessIdentity(string? providerName)
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(ScriptedValidator.Users(("tok", "42", providerName)), store)
            .ExchangeAsync("discord", "tok");

        Assert.Equal("42", result.DisplayName);
    }

    [Fact]
    public async Task TheDisplayName_IsClampedToTheCap_WithoutSplittingASurrogatePair()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        string longName = new string('n', 70);
        string pairAtTheCut = new string('n', 63) + "\U0001F600" + "tail";
        ScriptedValidator validator = ScriptedValidator.Users(("long", "1", longName), ("pair", "2", pairAtTheCut));
        AuthExchange exchange = ExchangeFixture.Build(validator, store);

        Assert.Equal(new string('n', 64), (await exchange.ExchangeAsync("discord", "long")).DisplayName);
        Assert.Equal(new string('n', 63), (await exchange.ExchangeAsync("discord", "pair")).DisplayName);
    }

    [Fact]
    public async Task AStoreThatThrowsOrAnswersNothing_IsUnavailable_WithTheFault()
    {
        var fault = new TimeoutException("database resuming");
        var throwing = new CountingAccountStore(whitelistOnCreate: true) { Fault = fault };
        var silent = new CountingAccountStore(whitelistOnCreate: true) { AnswerNull = true };
        var timedOut = new CountingAccountStore(whitelistOnCreate: true) { Fault = new OperationCanceledException() };
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));

        AuthExchangeResult thrown = await ExchangeFixture.Build(validator, throwing).ExchangeAsync("discord", "tok");
        AuthExchangeResult empty = await ExchangeFixture.Build(validator, silent).ExchangeAsync("discord", "tok");
        AuthExchangeResult cancelledInside = await ExchangeFixture.Build(validator, timedOut).ExchangeAsync("discord", "tok");

        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.StoreFault), (thrown.Outcome, thrown.Cause));
        Assert.Same(fault, thrown.Fault);
        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.StoreFault), (empty.Outcome, empty.Cause));
        Assert.IsType<InvalidOperationException>(empty.Fault);
        // A store's own timeout surfacing as a cancellation the caller never asked for is a store fault, not a cancel.
        Assert.Equal(AuthExchangeCause.StoreFault, cancelledInside.Cause);
    }

    [Fact]
    public async Task APolicyThatFails_IsUnavailable_AndAFailedNameResolveTouchesNoAccount()
    {
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        var resolveStore = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult resolve = await ExchangeFixture.Build(validator, resolveStore,
            policy: new FaultyPolicy(failResolve: true)).ExchangeAsync("discord", "tok");
        AuthExchangeResult issue = await ExchangeFixture.Build(validator, new CountingAccountStore(true),
            policy: new FaultyPolicy(failIssue: true)).ExchangeAsync("discord", "tok");
        AuthExchangeResult nameless = await ExchangeFixture.Build(validator, new CountingAccountStore(true),
            policy: new FaultyPolicy(issueNoName: true)).ExchangeAsync("discord", "tok");

        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.PolicyFault), (resolve.Outcome, resolve.Cause));
        Assert.Equal(0, resolveStore.Calls);
        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.PolicyFault), (issue.Outcome, issue.Cause));
        Assert.Equal("issue fault", issue.Fault!.Message);
        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.PolicyFault), (nameless.Outcome, nameless.Cause));
        Assert.Null(nameless.SessionToken);
    }

    [Theory]
    [InlineData("acct:1.2")]
    [InlineData("guest:3")]
    [InlineData("")]
    public async Task AnInadmissibleSubjectFromAGameStore_IsAServerFault_AndNeverAToken(string minted)
    {
        var store = new CountingAccountStore(whitelistOnCreate: true) { Rewrite = a => a with { Subject = minted } };

        AuthExchangeResult result = await ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store)
            .ExchangeAsync("discord", "tok");

        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.InadmissibleSubject), (result.Outcome, result.Cause));
        Assert.Null(result.SessionToken);
        Assert.Null(result.Subject);
        if (minted.Length > 0) Assert.DoesNotContain(minted, result.Fault!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReservedSubject_IsNeverMinted_ByAnEngineStore()
    {
        // A validator registered as "guest" would mint guest:<subject>, a tokenless seat. The engine store refuses it.
        var guestValidator = new ScriptedValidator("guest", (_, _) => Task.FromResult(IdentityValidation.Verified(
            new VerifiedIdentity("7", "guest", "Wren", new Dictionary<string, string>()))));
        var store = new CountingAccountStore(whitelistOnCreate: true);

        AuthExchangeResult result = await ExchangeFixture.Build(guestValidator, store).ExchangeAsync("guest", "tok");

        Assert.Equal((AuthExchangeOutcome.Unavailable, AuthExchangeCause.StoreFault), (result.Outcome, result.Cause));
        Assert.IsType<ArgumentException>(result.Fault);
        Assert.Empty(await store.Inner.ListAsync());
    }
}
