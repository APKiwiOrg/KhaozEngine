using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Discord;
using Xunit;

namespace KhaozEngine.Tests.Identity;

/// <summary>
/// A provider outage and a bad credential used to arrive as the same null. A client that cannot tell them
/// apart throws away a good token and re-runs sign-in against a provider that is already down, which is a
/// retry loop pointed at an outage. These pin the third outcome and the default that keeps every existing
/// validator working unchanged.
/// </summary>
public class IdentityValidationTests
{
    private const string ExpectedClientId = "my-client-id";

    /// <summary>A validator written before the detailed member existed: it implements only the old method.</summary>
    private sealed class LegacyValidator(VerifiedIdentity? result) : IIdentityValidator
    {
        public string ProviderId => "legacy";

        public Task<VerifiedIdentity?> ValidateAsync(string credentialToken, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    private sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    private static string OAuthMe(string appId) =>
        $"{{\"application\":{{\"id\":\"{appId}\"}},\"user\":{{\"id\":\"123\",\"username\":\"nick\"}}}}";

    private static DiscordTokenValidator Discord(HttpMessageHandler handler) =>
        new(ExpectedClientId, new HttpClient(handler));

    [Fact]
    public async Task DefaultImplementationMapsAnIdentityToVerified()
    {
        VerifiedIdentity identity = new("42", "legacy", "nick", new Dictionary<string, string>());
        IIdentityValidator validator = new LegacyValidator(identity);

        IdentityValidation result = await validator.ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.Verified, result.Outcome);
        Assert.True(result.IsVerified);
        Assert.Equal("42", result.Identity!.Value.Subject);
    }

    [Fact]
    public async Task DefaultImplementationMapsNullToRefused()
    {
        // The meaning existing callers already have: null is a refusal, never an outage claim.
        IIdentityValidator validator = new LegacyValidator(null);

        IdentityValidation result = await validator.ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.Refused, result.Outcome);
        Assert.False(result.IsVerified);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task DiscordReportsTheProviderUnavailableOnATransientStatus(HttpStatusCode code)
    {
        IdentityValidation result = await Discord(new Handler(code, "{}")).ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.ProviderUnavailable, result.Outcome);
        Assert.Null(result.Identity);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task DiscordRefusesOnAClientStatus(HttpStatusCode code)
    {
        IdentityValidation result = await Discord(new Handler(code, "{}")).ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task DiscordVerifiesAGoodToken()
    {
        IdentityValidation result = await Discord(new Handler(HttpStatusCode.OK, OAuthMe(ExpectedClientId)))
            .ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.Verified, result.Outcome);
        Assert.Equal("123", result.Identity!.Value.Subject);
        Assert.Equal("discord", result.Identity.Value.ProviderId);
    }

    [Fact]
    public async Task DiscordRefusesATokenFromAnotherApplication()
    {
        // A completed round trip that answered about someone else's app is a refusal, not an outage.
        IdentityValidation result = await Discord(new Handler(HttpStatusCode.OK, OAuthMe("attacker-app-id")))
            .ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task DiscordReportsTheProviderUnavailableWhenTheRequestNeverCompletes()
    {
        ThrowingHandler handler = new(new HttpRequestException("dns is down"));

        IdentityValidation result = await Discord(handler).ValidateDetailedAsync("token");

        Assert.Equal(IdentityValidationOutcome.ProviderUnavailable, result.Outcome);
    }

    [Fact]
    public async Task TheOldMethodStillThrowsOnATransportFailure()
    {
        // Existing callers keep their exact meaning: only the new member absorbs a transport failure.
        DiscordTokenValidator validator = Discord(new ThrowingHandler(new HttpRequestException("dns is down")));

        await Assert.ThrowsAsync<HttpRequestException>(() => validator.ValidateAsync("token"));
    }

    [Fact]
    public async Task TheOldMethodStillReturnsNullOnAnOutageStatus()
    {
        DiscordTokenValidator validator = Discord(new Handler(HttpStatusCode.InternalServerError, "{}"));

        Assert.Null(await validator.ValidateAsync("token"));
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAnOutage()
    {
        // The caller asked to stop, so that has to surface as cancellation rather than a claim about Discord.
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        DiscordTokenValidator validator = Discord(new ThrowingHandler(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateDetailedAsync("token", cts.Token));
    }

    [Fact]
    public void RefusedAndUnavailableCarryNoIdentity()
    {
        Assert.Null(IdentityValidation.Refused().Identity);
        Assert.Null(IdentityValidation.ProviderUnavailable("discord 503").Identity);
        Assert.Equal("discord 503", IdentityValidation.ProviderUnavailable("discord 503").Detail);
        Assert.False(IdentityValidation.ProviderUnavailable().IsVerified);
    }
}
