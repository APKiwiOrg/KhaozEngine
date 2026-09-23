using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>
/// The endpoint is public and unauthenticated, so what a response can tell a caller is the security property. These
/// pin the three the design names: no account oracle, ban before whitelist with ban details off by default, and one
/// failure envelope whichever dependency failed.
/// </summary>
/// <remarks>
/// <see cref="DesignStatus"/> restates the design's outcome to HTTP table, which the ASP.NET Core handler implements.
/// Two results "read alike" here when that status and the serialized body are both equal.
/// </remarks>
public class AuthExchangeDisclosureTests
{
    private static readonly DateTimeOffset Now = ExchangeFixture.Now;

    private static int DesignStatus(AuthExchangeOutcome outcome) => outcome switch
    {
        AuthExchangeOutcome.Ok => 200,
        AuthExchangeOutcome.NotWhitelisted => 403,
        AuthExchangeOutcome.Banned => 403,
        AuthExchangeOutcome.InvalidCredential => 401,
        AuthExchangeOutcome.Unavailable => 503,
        AuthExchangeOutcome.Malformed => 400,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static void AssertReadAlike(AuthExchangeResult expected, AuthExchangeResult actual, bool includeBanDetails = false)
    {
        Assert.Equal(DesignStatus(expected.Outcome), DesignStatus(actual.Outcome));
        Assert.Equal(ExchangeFixture.Json(expected, includeBanDetails), ExchangeFixture.Json(actual, includeBanDetails));
    }

    [Fact]
    public async Task AnUnknownSubject_ReadsExactlyLikeAKnownUnwhitelistedOne()
    {
        // Find-or-create means no answer ever says "no such account": a first sign-in simply creates it.
        var store = new CountingAccountStore(whitelistOnCreate: false);
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store);

        AuthExchangeResult unknown = await exchange.ExchangeAsync("discord", "tok");
        AuthExchangeResult known = await exchange.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.NotWhitelisted, unknown.Outcome);
        AssertReadAlike(unknown, known);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABannedCaller_NeverLearnsWhetherTheyWereWhitelisted(bool includeBanDetails)
    {
        var whitelistedStore = new CountingAccountStore(whitelistOnCreate: true);
        var unlistedStore = new CountingAccountStore(whitelistOnCreate: false);
        ScriptedValidator validator = ScriptedValidator.Users(("tok", "1", "Wren"));
        AuthExchange whitelisted = ExchangeFixture.Build(validator, whitelistedStore);
        AuthExchange unlisted = ExchangeFixture.Build(validator, unlistedStore);
        await whitelisted.ExchangeAsync("discord", "tok");
        await unlisted.ExchangeAsync("discord", "tok");
        await whitelistedStore.Inner.BanAsync("discord:1", "griefing", Now.AddDays(3));
        await unlistedStore.Inner.BanAsync("discord:1", "griefing", Now.AddDays(3));

        AuthExchangeResult a = await whitelisted.ExchangeAsync("discord", "tok");
        AuthExchangeResult b = await unlisted.ExchangeAsync("discord", "tok");

        Assert.Equal(AuthExchangeOutcome.Banned, a.Outcome);
        AssertReadAlike(a, b, includeBanDetails);
    }

    [Fact]
    public async Task BanDetails_AreOffByDefault_AndSentOnlyWhenTheGameOptsIn()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Users(("tok", "1", "Wren")), store);
        await exchange.ExchangeAsync("discord", "tok");
        await store.Inner.BanAsync("discord:1", "operator note: alt of discord:9", Now.AddDays(3));
        AuthExchangeResult timed = await exchange.ExchangeAsync("discord", "tok");
        await store.Inner.BanAsync("discord:1", "a different note", until: null);
        AuthExchangeResult permanent = await exchange.ExchangeAsync("discord", "tok");

        AuthExchangeResponse hidden = timed.ToResponse();
        Assert.Equal(AuthExchangeStatuses.Banned, hidden.Status);
        Assert.Null(hidden.BanReason);
        Assert.Null(hidden.BanExpiresAtUtc);
        Assert.Null(hidden.SessionToken);
        // With details off, neither the reason nor the expiry leaks through any difference in the body.
        AssertReadAlike(timed, permanent);

        AuthExchangeResponse shown = timed.ToResponse(includeBanDetails: true);
        Assert.Equal("operator note: alt of discord:9", shown.BanReason);
        Assert.Equal(Now.AddDays(3), shown.BanExpiresAtUtc);
        Assert.Equal("discord:1", shown.Subject);
        Assert.Equal("Wren", shown.DisplayName);
    }

    [Fact]
    public async Task ARefusedCredential_ReadsAlike_WhateverAccountStandsBehindIt()
    {
        // The provider refuses before any lookup, so a banned, an unknown and an unwhitelisted account's stale
        // credentials cannot be told apart, and the store is never asked.
        var store = new CountingAccountStore(whitelistOnCreate: false);
        await store.Inner.FindOrCreateAsync(new AccountSignIn("discord", "banned", "B", new Dictionary<string, string>(), Now));
        await store.Inner.BanAsync("discord:banned", "x", until: null);
        await store.Inner.FindOrCreateAsync(new AccountSignIn("discord", "unlisted", "U", new Dictionary<string, string>(), Now));
        int callsBefore = store.Calls;
        AuthExchange exchange = ExchangeFixture.Build(ScriptedValidator.Always(IdentityValidation.Refused()), store);

        AuthExchangeResult banned = await exchange.ExchangeAsync("discord", "stale-banned");
        AuthExchangeResult unknown = await exchange.ExchangeAsync("discord", "stale-unknown");
        AuthExchangeResult unlisted = await exchange.ExchangeAsync("discord", "stale-unlisted");

        Assert.Equal(AuthExchangeOutcome.InvalidCredential, banned.Outcome);
        AssertReadAlike(banned, unknown);
        AssertReadAlike(banned, unlisted);
        Assert.Equal("{\"status\":\"invalid_credential\",\"sessionToken\":null,\"expiresAtUtc\":null,\"subject\":null," +
            "\"displayName\":null,\"banReason\":null,\"banExpiresAtUtc\":null}", ExchangeFixture.Json(banned));
        Assert.Equal(callsBefore, store.Calls);
    }

    [Fact]
    public async Task EveryDependencyFailure_ReadsAlike_WhateverFailedAndWhateverTheAccount()
    {
        ScriptedValidator good = ScriptedValidator.Users(("tok", "1", "Wren"));
        var bannedStore = new CountingAccountStore(whitelistOnCreate: true);
        await bannedStore.Inner.FindOrCreateAsync(new AccountSignIn("discord", "1", "Wren", new Dictionary<string, string>(), Now));
        await bannedStore.Inner.BanAsync("discord:1", "x", until: null);
        bannedStore.Fault = new TimeoutException("store");
        var options = new AuthExchangeOptions
        {
            TokenLifetime = ExchangeFixture.Lifetime,
            ProviderTimeout = TimeSpan.FromMilliseconds(50),
            Clock = new FixedClock(Now),
        };

        var failures = new List<AuthExchangeResult>
        {
            await ExchangeFixture.Build(ScriptedValidator.Always(IdentityValidation.ProviderUnavailable("429")),
                new CountingAccountStore(true)).ExchangeAsync("discord", "tok"),
            await ExchangeFixture.Build(ScriptedValidator.Throwing(new HttpRequestException("reset")),
                new CountingAccountStore(true)).ExchangeAsync("discord", "tok"),
            await new AuthExchange(new[] { ScriptedValidator.Stalled() }, new CountingAccountStore(true),
                ExchangeFixture.Key(), options).ExchangeAsync("discord", "tok"),
            await ExchangeFixture.Build(good, bannedStore).ExchangeAsync("discord", "tok"),
            await ExchangeFixture.Build(good, new CountingAccountStore(true) { Fault = new TimeoutException("store") })
                .ExchangeAsync("discord", "tok"),
            await ExchangeFixture.Build(good, new CountingAccountStore(true),
                policy: new FaultyPolicy(failIssue: true)).ExchangeAsync("discord", "tok"),
            await ExchangeFixture.Build(good, new CountingAccountStore(true) { Rewrite = a => a with { Subject = "guest:1" } })
                .ExchangeAsync("discord", "tok"),
        };

        // The cause tells the host's log which dependency it was. The response never does.
        Assert.Equal(new[]
        {
            AuthExchangeCause.ProviderUnavailable, AuthExchangeCause.ProviderUnavailable, AuthExchangeCause.ProviderTimeout,
            AuthExchangeCause.StoreFault, AuthExchangeCause.StoreFault, AuthExchangeCause.PolicyFault,
            AuthExchangeCause.InadmissibleSubject,
        }, failures.Select(f => f.Cause));
        Assert.All(failures, f => Assert.Equal(AuthExchangeOutcome.Unavailable, f.Outcome));
        Assert.All(failures, f => AssertReadAlike(failures[0], f, includeBanDetails: true));
        Assert.Equal("{\"status\":\"unavailable\",\"sessionToken\":null,\"expiresAtUtc\":null,\"subject\":null," +
            "\"displayName\":null,\"banReason\":null,\"banExpiresAtUtc\":null}", ExchangeFixture.Json(failures[0]));
    }

    [Fact]
    public void AResponse_CarriesOnlyWhatItsOutcomeIsEntitledTo()
    {
        var ban = new AccountBan("reason", Now);
        AuthExchangeResult Full(AuthExchangeOutcome outcome) =>
            new(outcome, "v2.token", Now, "discord:1", "Wren", ban, new InvalidOperationException("fault"));

        AuthExchangeResponse ok = Full(AuthExchangeOutcome.Ok).ToResponse(includeBanDetails: true);
        AuthExchangeResponse unlisted = Full(AuthExchangeOutcome.NotWhitelisted).ToResponse(includeBanDetails: true);
        AuthExchangeResponse banned = Full(AuthExchangeOutcome.Banned).ToResponse(includeBanDetails: false);

        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.Ok, "v2.token", Now, "discord:1", "Wren"), ok);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.NotWhitelisted, Subject: "discord:1", DisplayName: "Wren"), unlisted);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.Banned, Subject: "discord:1", DisplayName: "Wren"), banned);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.InvalidCredential),
            Full(AuthExchangeOutcome.InvalidCredential).ToResponse(includeBanDetails: true));
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.Unavailable),
            Full(AuthExchangeOutcome.Unavailable).ToResponse(includeBanDetails: true));
    }

    [Fact]
    public void AMalformedExchange_HasNoResponseBody()
    {
        var malformed = new AuthExchangeResult(AuthExchangeOutcome.Malformed, Cause: AuthExchangeCause.UnknownProvider);

        Assert.Throws<InvalidOperationException>(() => malformed.ToResponse());
        Assert.Throws<InvalidOperationException>(() => new AuthExchangeResult((AuthExchangeOutcome)42).ToResponse());
    }

    [Fact]
    public async Task AResultsLogLine_NeverPrintsTheToken_TheSubject_OrTheName()
    {
        var store = new CountingAccountStore(whitelistOnCreate: true);
        AuthExchangeResult ok = await ExchangeFixture.Build(ScriptedValidator.Users(("tok", "80351110224678912", "Wren")), store)
            .ExchangeAsync("discord", "tok");
        AuthExchangeResponse body = ok.ToResponse();

        foreach (string printed in new[] { ok.ToString(), body.ToString() })
        {
            Assert.DoesNotContain(ok.SessionToken!, printed, StringComparison.Ordinal);
            Assert.DoesNotContain("80351110224678912", printed, StringComparison.Ordinal);
            Assert.DoesNotContain("Wren", printed, StringComparison.Ordinal);
        }
        Assert.Equal("AuthExchangeResult { Outcome = Ok, Cause = None }", ok.ToString());
        Assert.Equal("AuthExchangeResponse { Status = ok }", body.ToString());
    }
}
