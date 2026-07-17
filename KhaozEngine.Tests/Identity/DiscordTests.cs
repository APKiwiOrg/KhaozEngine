using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Discord;
using Xunit;

namespace KhaozEngine.Tests.Identity;

public class DiscordTests
{
    private const string ExpectedClientId = "my-client-id";

    private sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? RequestUri;
        public string? BearerToken;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            RequestUri = req.RequestUri;
            BearerToken = req.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A well-formed <c>oauth2/@me</c> body: the issuing application and the nested user object.</summary>
    private static string OAuthMe(string appId, string userId, string username, string? email = null)
    {
        string user = email is null
            ? $"{{\"id\":\"{userId}\",\"username\":\"{username}\"}}"
            : $"{{\"id\":\"{userId}\",\"username\":\"{username}\",\"email\":\"{email}\"}}";
        return $"{{\"application\":{{\"id\":\"{appId}\"}},\"scopes\":[\"identify\",\"email\"],\"user\":{user}}}";
    }

    [Fact]
    public async Task Token_from_matching_app_yields_discord_user()
    {
        Handler handler = new(HttpStatusCode.OK, OAuthMe(ExpectedClientId, "123", "nick", "nick@example.com"));
        DiscordTokenValidator v = new(ExpectedClientId, new HttpClient(handler));

        VerifiedIdentity? id = await v.ValidateAsync("access-token");

        Assert.NotNull(id);
        Assert.Equal("123", id!.Value.Subject);
        Assert.Equal("nick", id.Value.DisplayName);
        Assert.Equal("discord", id.Value.ProviderId);
        Assert.Equal("nick@example.com", id.Value.Claims["email"]);
        // Proves the validator hits the token-introspection endpoint (not users/@me) with the bearer token.
        Assert.Equal("https://discord.com/api/oauth2/@me", handler.RequestUri!.ToString());
        Assert.Equal("access-token", handler.BearerToken);
    }

    [Fact]
    public async Task Token_from_different_app_is_rejected()
    {
        // A token a victim minted for the attacker's own Discord app: identifies the victim, wrong application id.
        Handler handler = new(HttpStatusCode.OK, OAuthMe("attacker-app-id", "123", "nick", "nick@example.com"));
        DiscordTokenValidator v = new(ExpectedClientId, new HttpClient(handler));

        Assert.Null(await v.ValidateAsync("victim-access-token"));
    }

    [Theory]
    [InlineData("not-json-at-all")]                                   // unparseable body
    [InlineData("{\"scopes\":[\"identify\"]}")]                       // no application, no user
    [InlineData("{\"application\":{\"id\":\"my-client-id\"}}")]       // right app, but no user to identify
    [InlineData("{\"application\":{\"id\":\"my-client-id\"},\"user\":{\"username\":\"nick\"}}")] // user without id
    public async Task Malformed_response_is_rejected(string body)
    {
        DiscordTokenValidator v = new(ExpectedClientId, new HttpClient(new Handler(HttpStatusCode.OK, body)));

        Assert.Null(await v.ValidateAsync("access-token"));
    }

    [Fact]
    public async Task Unauthorized_yields_null()
    {
        DiscordTokenValidator v = new(ExpectedClientId, new HttpClient(new Handler(HttpStatusCode.Unauthorized, "{}")));

        Assert.Null(await v.ValidateAsync("bad-token"));
    }

    [Fact]
    public void Constructing_without_a_client_id_throws()
    {
        // Fail-closed: there is no unchecked mode, so an empty expected client id is a construction error.
        Assert.Throws<ArgumentException>(() => new DiscordTokenValidator(""));
    }
}
