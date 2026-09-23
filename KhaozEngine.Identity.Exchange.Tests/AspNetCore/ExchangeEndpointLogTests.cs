using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Identity.Exchange.AspNetCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// What reaches the log. Every exchange writes one line with its outcome, cause, provider id, status and elapsed time,
/// and nothing the host logs, at any level and in any category, carries a credential, a session token, a subject, a
/// display name or a ban reason.
/// </summary>
[Collection(ExchangeKestrelCollection.Name)]
public class ExchangeEndpointLogTests
{
    [Fact]
    public async Task NoLineCarriesACredentialTokenSubjectOrName_AtAnyLevel()
    {
        var logs = new CapturedLogs();
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 100, IncludeBanDetails = true },
            logs: logs);
        const string refusedCredential = "refused-credential-5e1d";
        const string strangeProvider = "steam-provider-9a7c";

        ExchangeReply ok = await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential);
        await host.ExchangeAsync("discord", ExchangeAccounts.NewCredential);
        await host.ExchangeAsync("discord", ExchangeAccounts.BannedCredential);
        await host.ExchangeAsync("discord", refusedCredential);
        await host.ExchangeAsync("discord", ExchangeAccounts.DownCredential);
        await host.ExchangeAsync(strangeProvider, ExchangeAccounts.OkCredential);
        await host.PostAsync("not json " + refusedCredential);
        string token = ok.Envelope().SessionToken!;

        string[] secrets =
        {
            ExchangeAccounts.OkCredential, ExchangeAccounts.NewCredential, ExchangeAccounts.BannedCredential,
            ExchangeAccounts.DownCredential, refusedCredential, token,
            ExchangeAccounts.OkSubject, ExchangeAccounts.NewSubject, ExchangeAccounts.BannedSubject,
            "81234567890123456", "81234567890123457", "81234567890123458",
            ExchangeAccounts.OkName, ExchangeAccounts.NewName, ExchangeAccounts.BannedName,
            ExchangeAccounts.BanReason,
            // An unregistered provider id is caller input, so it is not logged either.
            strangeProvider,
        };
        IReadOnlyList<CapturedLine> lines = logs.Lines;
        Assert.NotEmpty(lines);
        foreach (CapturedLine line in lines)
            foreach (string secret in secrets)
                Assert.False(line.All.Contains(secret, StringComparison.Ordinal),
                    $"a {line.Level} line in {line.Category} carries a value that must never be logged");
    }

    [Fact]
    public async Task EachExchange_LogsOneLine_WithOutcomeCauseProviderStatusAndElapsedTime()
    {
        var logs = new CapturedLogs();
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { PermitsPerClientPerMinute = 100 }, logs: logs);

        await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential);
        await host.ExchangeAsync("discord", ExchangeAccounts.BannedCredential);
        await host.ExchangeAsync("discord", "bad");
        await host.ExchangeAsync("discord", ExchangeAccounts.DownCredential);
        await host.ExchangeAsync("steam", "x");
        await host.PostAsync("{");

        CapturedLine[] exchanges = logs.Lines
            .Where(l => l.EventName is "AuthExchangeAnswered" or "AuthExchangeUnavailable")
            .ToArray();
        Assert.Equal(6, exchanges.Length);
        Assert.All(exchanges, l => Assert.Equal(typeof(AuthExchangeEndpoints).FullName, l.Category));
        Assert.All(exchanges, l => Assert.Contains("ElapsedMs=", l.State, StringComparison.Ordinal));

        Assert.Contains("Outcome=Ok | Cause=None | Provider=discord | Status=200", exchanges[0].State, StringComparison.Ordinal);
        Assert.Contains("Outcome=Banned | Cause=None | Provider=discord | Status=403", exchanges[1].State, StringComparison.Ordinal);
        Assert.Contains("Outcome=InvalidCredential | Cause=CredentialRefused | Provider=discord | Status=401",
            exchanges[2].State, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, exchanges[3].Level);
        Assert.Contains("Cause=ProviderUnavailable | Provider=discord | Status=503", exchanges[3].State, StringComparison.Ordinal);
        Assert.Contains("Outcome=Malformed | Cause=UnknownProvider | Provider=none | Status=400", exchanges[4].State,
            StringComparison.Ordinal);
        Assert.Contains("Outcome=Malformed | Cause=UnparseableBody | Provider=none | Status=400", exchanges[5].State,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoreFault_IsLoggedWithItsException_AndAnswersTheOneEnvelope()
    {
        var logs = new CapturedLogs();
        var store = new CountingAccountStore(whitelistOnCreate: true) { Fault = new TimeoutException("the database is resuming") };
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(
            ExchangeFixture.Build(ExchangeAccounts.Validator(), store), logs: logs);

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.Unavailable), reply.Envelope());
        CapturedLine line = Assert.Single(logs.Lines, l => l.EventName == "AuthExchangeUnavailable");
        Assert.Contains("Cause=StoreFault", line.State, StringComparison.Ordinal);
        Assert.Contains("the database is resuming", line.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutForwardedHeaders_MappingWarnsThatTheLimitKeysOnThePeerAddress()
    {
        var logs = new CapturedLogs();
        await using ExchangeHttpHost off = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(), logs: logs);
        var quiet = new CapturedLogs();
        await using ExchangeHttpHost on = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            hosting: new AuthExchangeHostingOptions { TrustedProxies = TrustedProxyNetworks.PrivateRanges }, logs: quiet);

        CapturedLine warning = Assert.Single(logs.Lines, l => l.EventName == "AuthExchangeForwardedHeadersOff");
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("RemoteIpAddress", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(quiet.Lines, l => l.EventName == "AuthExchangeForwardedHeadersOff");
    }
}
