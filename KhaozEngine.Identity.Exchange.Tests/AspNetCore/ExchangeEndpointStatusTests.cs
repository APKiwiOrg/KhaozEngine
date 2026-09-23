using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Identity.Exchange.AspNetCore;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// The status mapping over a real socket: each outcome's code and body, the bare 400 for a malformed request or an
/// unparseable body, the 413 over the endpoint cap, <c>Cache-Control: no-store</c> on every answer, and ban details
/// absent unless the game opts in.
/// </summary>
[Collection(ExchangeKestrelCollection.Name)]
public class ExchangeEndpointStatusTests
{
    // Every member both games' client records read, in the web-default casing, written even when null.
    private static readonly string[] WireMembers =
    {
        "status", "sessionToken", "expiresAtUtc", "subject", "displayName", "banReason", "banExpiresAtUtc",
    };

    private static async Task<ExchangeHttpHost> StartAsync(bool includeBanDetails = false) =>
        await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(), new AuthExchangeEndpointOptions
        {
            PermitsPerClientPerMinute = 100,
            IncludeBanDetails = includeBanDetails,
        });

    private static void AssertWireShape(ExchangeReply reply)
    {
        Assert.Equal(WireMembers, reply.Json().EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(reply.NoStore, "every exchange answer must carry Cache-Control: no-store");
    }

    private static void AssertBare(ExchangeReply reply, HttpStatusCode status)
    {
        Assert.Equal(status, reply.Status);
        Assert.Empty(reply.Body);
        Assert.True(reply.NoStore, "every exchange answer must carry Cache-Control: no-store");
    }

    [Fact]
    public async Task Ok_Answers200_WithAVerifyingToken_AndNoStore()
    {
        await using ExchangeHttpHost host = await StartAsync();

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.OkCredential);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        AssertWireShape(reply);
        AuthExchangeResponse body = reply.Envelope();
        Assert.Equal(AuthExchangeStatuses.Ok, body.Status);
        Assert.Equal(ExchangeAccounts.OkSubject, body.Subject);
        Assert.Equal(ExchangeAccounts.OkName, body.DisplayName);
        Assert.Equal(ExchangeFixture.Now + ExchangeFixture.Lifetime, body.ExpiresAtUtc);
        Assert.True(SignedToken.TryVerify(body.SessionToken!, ExchangeFixture.Key(), ExchangeFixture.Now,
            out string subject, out _));
        Assert.Equal(ExchangeAccounts.OkSubject, subject);
        Assert.Null(body.BanReason);
    }

    [Fact]
    public async Task NotWhitelisted_Answers403_WithTheCallersOwnAccount_AndNoToken()
    {
        await using ExchangeHttpHost host = await StartAsync();

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.NewCredential);

        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        AssertWireShape(reply);
        AuthExchangeResponse body = reply.Envelope();
        Assert.Equal(AuthExchangeStatuses.NotWhitelisted, body.Status);
        Assert.Equal(ExchangeAccounts.NewSubject, body.Subject);
        Assert.Equal(ExchangeAccounts.NewName, body.DisplayName);
        Assert.Null(body.SessionToken);
        Assert.Null(body.ExpiresAtUtc);
    }

    [Fact]
    public async Task Banned_Answers403_WithoutBanDetails_ByDefault()
    {
        await using ExchangeHttpHost host = await StartAsync();

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.BannedCredential);

        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        AssertWireShape(reply);
        AuthExchangeResponse body = reply.Envelope();
        Assert.Equal(AuthExchangeStatuses.Banned, body.Status);
        Assert.Equal(ExchangeAccounts.BannedSubject, body.Subject);
        Assert.Null(body.SessionToken);
        Assert.Null(body.BanReason);
        Assert.Null(body.BanExpiresAtUtc);
        Assert.DoesNotContain(ExchangeAccounts.BanReason, reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Banned_CarriesTheReasonAndExpiry_WhenTheGameOptsIn()
    {
        await using ExchangeHttpHost host = await StartAsync(includeBanDetails: true);

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.BannedCredential);

        Assert.Equal(HttpStatusCode.Forbidden, reply.Status);
        AssertWireShape(reply);
        AuthExchangeResponse body = reply.Envelope();
        Assert.Equal(AuthExchangeStatuses.Banned, body.Status);
        Assert.Equal(ExchangeAccounts.BanReason, body.BanReason);
        Assert.Equal(ExchangeAccounts.BanUntil, body.BanExpiresAtUtc);
        Assert.Null(body.SessionToken);
    }

    [Fact]
    public async Task InvalidCredential_Answers401_WithTheStatusAlone()
    {
        await using ExchangeHttpHost host = await StartAsync();

        ExchangeReply reply = await host.ExchangeAsync("discord", "not-a-real-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        AssertWireShape(reply);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.InvalidCredential), reply.Envelope());
    }

    [Fact]
    public async Task Unavailable_Answers503_WithTheStatusAlone()
    {
        await using ExchangeHttpHost host = await StartAsync();

        ExchangeReply reply = await host.ExchangeAsync("discord", ExchangeAccounts.DownCredential);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        AssertWireShape(reply);
        Assert.Equal(new AuthExchangeResponse(AuthExchangeStatuses.Unavailable), reply.Envelope());
    }

    [Theory]
    [InlineData("""{"provider":"steam","accessToken":"ok-credential-7f3a"}""")]
    [InlineData("""{"provider":"Discord","accessToken":"ok-credential-7f3a"}""")]
    [InlineData("""{"provider":"discord","accessToken":"   "}""")]
    [InlineData("""{"provider":"discord"}""")]
    [InlineData("""{"accessToken":"ok-credential-7f3a"}""")]
    [InlineData("""{}""")]
    public async Task AMalformedRequest_IsABare400(string json)
    {
        await using ExchangeHttpHost host = await StartAsync();

        AssertBare(await host.PostAsync(json), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ACredentialOverTheCoreCap_IsABare400_UnderTheBodyCap()
    {
        await using ExchangeHttpHost host = await StartAsync();

        AssertBare(await host.ExchangeAsync("discord", new string('x', 4097)), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"discord\"")]
    [InlineData("""{"provider":5,"accessToken":"x"}""")]
    [InlineData("""{"provider":"discord","accessToken":"x" """)]
    public async Task AnUnparseableBody_IsABare400(string body)
    {
        await using ExchangeHttpHost host = await StartAsync();

        AssertBare(await host.PostAsync(body), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ABodyThatIsNotUtf8_IsABare400()
    {
        await using ExchangeHttpHost host = await StartAsync();
        byte[] prefix = Encoding.UTF8.GetBytes("""{"provider":"discord","accessToken":" """);
        byte[] body = prefix.Concat(new byte[] { 0xC3, 0x28, 0xFF }).Concat(Encoding.UTF8.GetBytes("\"}")).ToArray();
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        AssertBare(await host.SendAsync(content), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ABodyOverTheEndpointCap_IsABare413_DeclaredOrChunked()
    {
        await using ExchangeHttpHost host = await StartAsync();
        string json = JsonSerializer.Serialize(new AuthExchangeRequest("discord", new string('x', 9 * 1024)));

        // Declared: refused on the Content-Length before a byte is read.
        AssertBare(await host.PostAsync(json), HttpStatusCode.RequestEntityTooLarge);

        // Chunked, so no length is declared and the bounded read is what refuses it.
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/exchange") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        using HttpResponseMessage response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task ABodyAtTheCap_IsRead()
    {
        await using ExchangeHttpHost host = await ExchangeHttpHost.StartAsync(await ExchangeAccounts.BuildAsync(),
            new AuthExchangeEndpointOptions { MaxRequestBodyBytes = 128 });
        string json = JsonSerializer.Serialize(new AuthExchangeRequest("discord", ExchangeAccounts.OkCredential));
        string padded = json + new string(' ', 128 - Encoding.UTF8.GetByteCount(json));

        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(padded)).Status);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.PostAsync(padded + " ")).Status);
    }

    [Fact]
    public async Task NoAnswerCarriesACorsHeaderOrACookie()
    {
        await using ExchangeHttpHost host = await StartAsync();

        foreach (string credential in new[] { ExchangeAccounts.OkCredential, ExchangeAccounts.BannedCredential, "bad" })
        {
            ExchangeReply reply = await host.ExchangeAsync("discord", credential);
            Assert.DoesNotContain(reply.HeaderNames, h => h.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(reply.HeaderNames, h => h.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void TheMapping_IsTheDesignTable_AndMalformedNeverReachesTheSerializer()
    {
        Assert.Equal(200, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.Ok));
        Assert.Equal(403, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.NotWhitelisted));
        Assert.Equal(403, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.Banned));
        Assert.Equal(401, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.InvalidCredential));
        Assert.Equal(503, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.Unavailable));
        Assert.Equal(400, AuthExchangeEndpoints.StatusCodeFor(AuthExchangeOutcome.Malformed));
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthExchangeEndpoints.StatusCodeFor((AuthExchangeOutcome)99));

        // ToResponse throws for Malformed, so building the result proves the helper never called it.
        Assert.NotNull(AuthExchangeEndpoints.ToHttpResult(new AuthExchangeResult(AuthExchangeOutcome.Malformed), false));
    }
}
