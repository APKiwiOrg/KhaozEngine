using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity.Discord;
using KhaozEngine.Identity.Exchange;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange;

/// <summary>
/// The validator both games pass, answering through a stub handler. A Discord 5xx, 429 or 408, or a request that never
/// completed, is an outage and answers Unavailable: the defect in Ruinborne's exchange, which read every one of them
/// as a refused credential and sent the player back through sign-in against a provider that was already down.
/// </summary>
public class DiscordExchangeTests
{
    private const string ClientId = "1234567890";

    private sealed class StubHandler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static (AuthExchange Exchange, CountingAccountStore Store, StubHandler Handler) Build(Func<HttpResponseMessage> answer)
    {
        var handler = new StubHandler(answer);
        var validator = new DiscordTokenValidator(ClientId, new HttpClient(handler));
        var store = new CountingAccountStore(whitelistOnCreate: true);
        return (ExchangeFixture.Build(validator, store), store, handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ADiscordOutageOrRateLimit_IsUnavailable_NeverAnInvalidCredential(HttpStatusCode code)
    {
        (AuthExchange exchange, CountingAccountStore store, StubHandler handler) = Build(() => Status(code));

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "access-token");

        Assert.Equal(AuthExchangeOutcome.Unavailable, result.Outcome);
        Assert.Equal(AuthExchangeCause.ProviderUnavailable, result.Cause);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ATransportFailure_IsUnavailable()
    {
        (AuthExchange exchange, CountingAccountStore store, _) = Build(() => throw new HttpRequestException("reset"));

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "access-token");

        Assert.Equal(AuthExchangeOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DiscordRefusingTheToken_IsAnInvalidCredential(HttpStatusCode code)
    {
        (AuthExchange exchange, CountingAccountStore store, _) = Build(() => Status(code));

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "access-token");

        Assert.Equal(AuthExchangeOutcome.InvalidCredential, result.Outcome);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ATokenForThisApplication_SignsIn_AsTheSnowflakeSubject()
    {
        string body = "{\"application\":{\"id\":\"" + ClientId + "\"}," +
            "\"user\":{\"id\":\"80351110224678912\",\"username\":\"wren\"}}";
        (AuthExchange exchange, _, _) = Build(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        AuthExchangeResult result = await exchange.ExchangeAsync("discord", "access-token");

        Assert.Equal(AuthExchangeOutcome.Ok, result.Outcome);
        Assert.Equal("discord:80351110224678912", result.Subject);
        Assert.Equal("wren", result.DisplayName);
    }
}
