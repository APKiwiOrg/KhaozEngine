using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Accounts;
using KhaozEngine.Identity;
using KhaozEngine.Identity.Exchange;
using KhaozEngine.Identity.Exchange.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KhaozEngine.Tests.Identity.Exchange.AspNetCore;

/// <summary>
/// The Kestrel facts share no state, but the global bound and the cancellation facts wait on real sockets and real
/// timers, so they run alone rather than race the rest of the assembly for the thread pool.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExchangeKestrelCollection
{
    public const string Name = "ExchangeKestrel";
}

/// <summary>
/// A real Kestrel bound to <c>127.0.0.1:0</c>, composed the way a game's auth service composes it: the hosting, a
/// <c>/healthz</c> the exchange limits never touch, and the mapped exchange. The client budget keeps a wrong listener
/// on the port from costing HttpClient's 100 second default.
/// </summary>
internal sealed class ExchangeHttpHost : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly WebApplication app;

    private ExchangeHttpHost(WebApplication app, Uri baseAddress)
    {
        this.app = app;
        Client = new HttpClient { BaseAddress = baseAddress, Timeout = RequestTimeout };
    }

    public HttpClient Client { get; }

    /// <summary>
    /// Starts the host. <paramref name="middleware"/> runs after the hosting's middleware and before the endpoints, for
    /// a fact that watches a request on its way in.
    /// </summary>
    public static async Task<ExchangeHttpHost> StartAsync(AuthExchange exchange,
        AuthExchangeEndpointOptions? endpoint = null, AuthExchangeHostingOptions? hosting = null,
        CapturedLogs? logs = null, Func<HttpContext, RequestDelegate, Task>? middleware = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
        }
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        builder.AddAuthExchangeHosting(hosting);

        WebApplication app = builder.Build();
        app.UseAuthExchangeHosting();
        if (middleware is not null) app.Use(middleware);
        app.MapGet("/healthz", () => Results.Ok());
        app.MapAuthExchange(exchange, endpoint);
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return new ExchangeHttpHost(app, new Uri(address));
    }

    public Task<ExchangeReply> PostAsync(string json, string? forwardedFor = null, CancellationToken ct = default) =>
        SendAsync(new StringContent(json, Encoding.UTF8, "application/json"), forwardedFor, ct);

    public Task<ExchangeReply> ExchangeAsync(string provider, string credential, string? forwardedFor = null,
        CancellationToken ct = default) =>
        PostAsync(JsonSerializer.Serialize(new AuthExchangeRequest(provider, credential), ExchangeFixture.Web),
            forwardedFor, ct);

    public async Task<ExchangeReply> SendAsync(HttpContent content, string? forwardedFor = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/exchange") { Content = content };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        using HttpResponseMessage response = await Client.SendAsync(request, ct);
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        return new ExchangeReply(response.StatusCode, body,
            response.Headers.CacheControl?.NoStore ?? false,
            response.Headers.RetryAfter?.Delta,
            response.Headers.Concat(response.Content.Headers).Select(h => h.Key).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>One answer as the client saw it.</summary>
internal sealed record ExchangeReply(HttpStatusCode Status, byte[] Body, bool NoStore, TimeSpan? RetryAfter,
    string[] HeaderNames)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public JsonElement Json()
    {
        using JsonDocument document = JsonDocument.Parse(Body);
        return document.RootElement.Clone();
    }

    public AuthExchangeResponse Envelope() =>
        JsonSerializer.Deserialize<AuthExchangeResponse>(Body, ExchangeFixture.Web)!;
}

/// <summary>
/// The accounts every status fact needs, one per outcome, behind a validator that verifies their credentials, refuses
/// every other, and reports an outage for one.
/// </summary>
internal static class ExchangeAccounts
{
    public const string OkCredential = "ok-credential-7f3a";
    public const string BannedCredential = "banned-credential-91c2";
    public const string NewCredential = "new-credential-44d0";
    public const string DownCredential = "down-credential-0b8e";

    public const string OkName = "Wren Hollowmere";
    public const string BannedName = "Rook Ashvale";
    public const string NewName = "Finch Emberly";

    public const string OkSubject = "discord:81234567890123456";
    public const string BannedSubject = "discord:81234567890123457";
    public const string NewSubject = "discord:81234567890123458";

    public const string BanReason = "griefing the east docks";
    public static readonly DateTimeOffset BanUntil = ExchangeFixture.Now.AddDays(3);

    public static ScriptedValidator Validator() => new("discord", (credential, _) => Task.FromResult(credential switch
    {
        OkCredential => Verified("81234567890123456", OkName),
        BannedCredential => Verified("81234567890123457", BannedName),
        NewCredential => Verified("81234567890123458", NewName),
        DownCredential => IdentityValidation.ProviderUnavailable("discord answered 503"),
        _ => IdentityValidation.Refused("unknown token"),
    }));

    /// <summary>An exchange over a store holding a whitelisted account and a banned one. A new sign-in is not whitelisted.</summary>
    public static async Task<AuthExchange> BuildAsync(IIdentityValidator? validator = null)
    {
        var store = new InMemoryAccountStore(whitelistOnCreate: false);
        IReadOnlyDictionary<string, string> none = new Dictionary<string, string>();
        await store.FindOrCreateAsync(new AccountSignIn("discord", "81234567890123456", OkName, none, ExchangeFixture.Now));
        await store.SetWhitelistedAsync(OkSubject, true);
        await store.FindOrCreateAsync(new AccountSignIn("discord", "81234567890123457", BannedName, none, ExchangeFixture.Now));
        await store.SetWhitelistedAsync(BannedSubject, true);
        await store.BanAsync(BannedSubject, BanReason, BanUntil);
        return ExchangeFixture.Build(validator ?? Validator(), store);
    }

    private static IdentityValidation Verified(string subject, string name) =>
        IdentityValidation.Verified(new VerifiedIdentity(subject, "discord", name, new Dictionary<string, string>()));
}

/// <summary>Every log line the host writes, every category and level, formatted, with its state and exception.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLine> lines = new();

    public IReadOnlyList<CapturedLine> Lines => lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<CapturedLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            lines.Enqueue(new CapturedLine(category, logLevel, eventId.Name, formatter(state, exception), values,
                exception?.ToString()));
        }
    }
}

/// <summary>One captured log line. <see cref="All"/> is everything in it a leak could hide in.</summary>
internal sealed record CapturedLine(string Category, LogLevel Level, string? EventName, string Message, string State,
    string? Exception)
{
    public string All => $"{Category} {EventName} {Message} {State} {Exception}";
}
