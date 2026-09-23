using System;
using KhaozEngine.Accounts;
using KhaozEngine.Identity.Exchange;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>The fixed gate on its own, as a game's own issuer calls it: ban first, whitelist second.</summary>
public class AuthAdmissionTests
{
    private static readonly DateTimeOffset Now = ExchangeFixture.Now;

    [Theory]
    [InlineData(true, true, AuthExchangeOutcome.Banned)]
    [InlineData(false, true, AuthExchangeOutcome.Banned)]
    [InlineData(true, false, AuthExchangeOutcome.Banned)]
    [InlineData(false, false, AuthExchangeOutcome.Banned)]
    public void AnActiveBan_RefusesFirst_WhateverTheWhitelist(bool whitelisted, bool requireWhitelist,
        AuthExchangeOutcome expected)
    {
        var account = new AccountRecord("discord:1", "Wren", whitelisted, new AccountBan("x", Until: null));

        Assert.Equal(expected, AuthAdmission.Decide(account, Now, requireWhitelist));
    }

    [Theory]
    [InlineData(false, true, AuthExchangeOutcome.NotWhitelisted)]
    [InlineData(false, false, AuthExchangeOutcome.Ok)]
    [InlineData(true, true, AuthExchangeOutcome.Ok)]
    public void TheWhitelist_RefusesSecond_OnlyWhenRequired(bool whitelisted, bool requireWhitelist,
        AuthExchangeOutcome expected)
    {
        var account = new AccountRecord("discord:1", "Wren", whitelisted, Ban: null);

        Assert.Equal(expected, AuthAdmission.Decide(account, Now, requireWhitelist));
    }

    [Fact]
    public void ABanLapsingAtNow_HasLapsed()
    {
        var account = new AccountRecord("discord:1", "Wren", true, new AccountBan("x", Now));

        Assert.Equal(AuthExchangeOutcome.Ok, AuthAdmission.Decide(account, Now, requireWhitelist: true));
        Assert.Equal(AuthExchangeOutcome.Banned, AuthAdmission.Decide(account, Now.AddTicks(-1), requireWhitelist: true));
    }

    [Fact]
    public void ANullAccount_IsRefused() =>
        Assert.Throws<ArgumentNullException>(() => AuthAdmission.Decide(null!, Now, requireWhitelist: true));
}
