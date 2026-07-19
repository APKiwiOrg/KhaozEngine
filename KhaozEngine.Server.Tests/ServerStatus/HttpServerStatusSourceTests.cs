using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

/// <summary>
/// Proves the two hardening properties <see cref="HttpServerStatusSource"/> documents but never had a test for:
/// HTTPS-only (a non-TLS status URL never dials out) and the response size cap (a body over the cap yields null).
/// Mirrors <c>KhaozEngine.Tests.Updates.HttpUpdateSourceTests</c>, the sibling that already tests the same shape
/// for <c>HttpUpdateSource</c>, driving the source through a stub <see cref="HttpMessageHandler"/>.
/// </summary>
public sealed class HttpServerStatusSourceTests
{
    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
    }

    private static HttpServerStatusSource Source(HttpServerStatusSourceOptions options, byte[] body)
        => new(options, new HttpClient(new StubHandler(body)));

    [Fact]
    public async Task Fetch_NonHttpsUrl_ReturnsNull()
    {
        // http:// (non-TLS) is refused at construction, so the endpoint is null and the fetch never dials out.
        using var src = new HttpServerStatusSource(
            new HttpServerStatusSourceOptions { StatusUrl = "http://status.example.com/status" });

        Assert.Null(await src.FetchAsync());
    }

    [Fact]
    public async Task Fetch_OverSizeCap_ReturnsNull()
    {
        using HttpServerStatusSource src = Source(
            new HttpServerStatusSourceOptions { StatusUrl = "https://status.example.com/status", MaxResponseBytes = 10 },
            new byte[100]);

        Assert.Null(await src.FetchAsync());
    }

    [Fact]
    public async Task Fetch_WithinCap_ParsesReport()
    {
        // Positive control so the cap/TLS rejections above cannot pass vacuously: a valid report under the cap
        // must round-trip through the real fetch + parse path.
        byte[] body = Encoding.UTF8.GetBytes(
            new ServerStatusReport { Health = ServerHealth.Healthy, ServerVersion = "1.2.3" }.ToJson());
        using HttpServerStatusSource src = Source(
            new HttpServerStatusSourceOptions { StatusUrl = "https://status.example.com/status" },
            body);

        ServerStatusReport? report = await src.FetchAsync();

        Assert.NotNull(report);
        Assert.Equal(ServerHealth.Healthy, report!.Health);
        Assert.Equal("1.2.3", report.ServerVersion);
    }
}
