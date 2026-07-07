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
    private sealed class Handler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Valid_access_token_yields_discord_user()
    {
        HttpClient http = new(new Handler(HttpStatusCode.OK, "{\"id\":\"123\",\"username\":\"nick\"}"));
        DiscordTokenValidator v = new(http);
        VerifiedIdentity? id = await v.ValidateAsync("access-token");
        Assert.NotNull(id);
        Assert.Equal("123", id!.Value.Subject);
        Assert.Equal("nick", id.Value.DisplayName);
        Assert.Equal("discord", id.Value.ProviderId);
    }

    [Fact]
    public async Task Unauthorized_yields_null()
    {
        HttpClient http = new(new Handler(HttpStatusCode.Unauthorized, "{}"));
        DiscordTokenValidator v = new(http);
        Assert.Null(await v.ValidateAsync("bad-token"));
    }
}
