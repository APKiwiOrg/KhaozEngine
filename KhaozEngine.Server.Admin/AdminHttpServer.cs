using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KhaozEngine.Server.Admin;

/// <summary>
/// A minimal Kestrel HTTPS listener exposing the <see cref="ServerAdmin"/> surface as a small REST API, guarded by a
/// single bearer token. Off until constructed; binds the supplied certificate and address from
/// <see cref="AdminEndpointOptions"/>. Mutating routes return 202 (the command is enqueued / awaited on the store);
/// capabilities not wired in the facade return 501.
///
/// <para><see cref="AdminEndpointOptions.Port"/> 0 asks the OS for a free port and <see cref="BoundPort"/> reports
/// the one Kestrel actually took, which is the only race-free way to reach an ephemeral port: picking one from a
/// throwaway probe socket first leaves a window in which another listener on the host takes it between the probe
/// and the real bind, and the two then split the connections.</para>
/// </summary>
public sealed class AdminHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private int boundPort;

    public AdminHttpServer(ServerAdmin admin, AdminEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(options);
        // Eagerly, so a bad limit throws HERE and not later inside Kestrel's lazily-invoked configure callback.
        options.Validate();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // System.Text.Json drops fields by default, so a Vector3 (X/Y/Z are fields) in a response DTO serializes as
        // an empty {}. Register a scoped converter so OnlinePlayer.Position carries its components; scoped to this
        // endpoint's JSON options (not a blanket IncludeFields, which would change every other type's serialization).
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new Vector3JsonConverter()));
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Before the Listen, because these bound what an UNAUTHENTICATED peer can hold: the TLS handshake
            // completes before the bearer check ever runs, so connection count and header/idle timeouts are the only
            // pre-auth cost this endpoint can be made to pay.
            options.ApplyLimits(k.Limits);
            k.Listen(options.BindAddress, options.Port, listen => listen.UseHttps(options.Certificate.Certificate));
        });
        app = builder.Build();

        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + options.BearerToken);
        app.Use(async (ctx, next) =>
        {
            byte[] got = Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString());
            if (got.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(got, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
        });

        RouteGroupBuilder g = app.MapGroup(options.PathBase);

        g.MapGet("/online", IResult () => Results.Json(admin.ListOnline()));

        g.MapPost("/teleport", IResult (TeleportRequest r) =>
        {
            admin.Teleport(r.ToRef(), new Vector3(r.X, r.Y, r.Z));
            return Results.Accepted();
        });

        g.MapPost("/kick", IResult (KickRequest r) =>
        {
            admin.Kick(r.ToRef(), r.Reason ?? string.Empty);
            return Results.Accepted();
        });

        g.MapPost("/broadcast", IResult (BroadcastRequest r) =>
        {
            admin.Broadcast(r.Text ?? string.Empty);
            return Results.Accepted();
        });

        // A read: thread the request's cancellation token (RequestAborted) so a long account enumeration stops if the
        // client disconnects, instead of running to completion against a dropped connection.
        g.MapGet("/accounts", async Task<IResult> (HttpContext ctx, string? prefix) =>
            admin.AccountsSupported
                ? Results.Json(await admin.ListAccountsAsync(prefix, ctx.RequestAborted))
                : Results.StatusCode(StatusCodes.Status501NotImplemented));

        g.MapGet("/bans", IResult () =>
            admin.BansSupported
                ? Results.Json(admin.ListBans())
                : Results.StatusCode(StatusCodes.Status501NotImplemented));

        // Mutations deliberately do NOT thread RequestAborted: a 202-accepted ban/unban should complete atomically
        // rather than abort half-applied if the client disconnects mid-write (a DB-backed ban store is not transactional).
        g.MapPost("/ban", async Task<IResult> (BanRequest r) =>
        {
            if (!admin.BansSupported) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            try
            {
                await admin.BanAsync(r.AccountId, r.Reason ?? string.Empty, r.Until);
            }
            catch (ArgumentException ex)
            {
                // ServerAdmin refuses an id that names a seat rather than a player (a tokenless connection's
                // guest:{slot}). That is the operator naming the wrong thing, so it is a 400 carrying the reason
                // and not a 500 carrying a stack trace.
                return Results.BadRequest(new { error = ex.Message });
            }
            return Results.Accepted();
        });

        g.MapPost("/unban", async Task<IResult> (UnbanRequest r) =>
        {
            if (!admin.BansSupported) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            await admin.UnbanAsync(r.AccountId);
            return Results.Accepted();
        });

        // Game-registered custom actions, dispatched through the same auth/TLS/JSON pipeline. Each handler runs on this
        // HTTP request thread by contract (see ServerAdmin.RegisterAction): it enqueues mutations to the host thread
        // and reads published snapshots, never touching the simulation directly.
        g.MapGet("/actions", IResult () => Results.Json(admin.ActionNames.OrderBy(n => n, StringComparer.Ordinal)));

        g.MapGet("/actions/{name}", Task<IResult> (HttpContext ctx, string name) =>
            DispatchActionAsync(admin, name, null, ctx.RequestAborted));

        g.MapPost("/actions/{name}", async Task<IResult> (HttpContext ctx, string name) =>
        {
            JsonElement? payload;
            try
            {
                payload = await ReadOptionalJsonBodyAsync(ctx);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "malformed json body" });
            }
            return await DispatchActionAsync(admin, name, payload, ctx.RequestAborted);
        });
    }

    private static async Task<IResult> DispatchActionAsync(
        ServerAdmin admin, string name, JsonElement? payload, CancellationToken requestAborted)
    {
        if (!admin.TryGetAction(name, out var handler)) return Results.NotFound();

        // Hand the handler the request's cancellation token. Like the /accounts read (and unlike the atomic ban/unban
        // writes) whether to honor it is the handler's call: a query can abort on client disconnect, a mutation it
        // enqueues should not.
        AdminActionResult result = await handler(payload, requestAborted);
        return result.Status switch
        {
            AdminActionStatus.Ok => result.Payload is null ? Results.Ok() : Results.Json(result.Payload),
            AdminActionStatus.Accepted => Results.Accepted(),
            AdminActionStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<JsonElement?> ReadOptionalJsonBodyAsync(HttpContext ctx)
    {
        // Absent, empty, whitespace-only, and JSON-null bodies all mean no payload, so the canonical handler idiom
        // payload?.GetProperty(...) is safe (a ValueKind.Null element would satisfy HasValue and throw there). A
        // present but malformed body throws JsonException, which the caller maps to 400. Clone so the element
        // outlives the parsed document.
        using var reader = new StreamReader(ctx.Request.Body);
        string body = await reader.ReadToEndAsync(ctx.RequestAborted);
        if (string.IsNullOrWhiteSpace(body)) return null;
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The TCP port this endpoint is listening on. Reads back the port Kestrel resolved, so it is the OS-assigned
    /// one when <see cref="AdminEndpointOptions.Port"/> was 0 and the configured one otherwise. Only meaningful once
    /// <see cref="StartAsync"/> has returned, which is also the moment the socket is bound and accepting, so a
    /// caller that builds its URL from this property cannot connect before the listener is up.
    /// </summary>
    /// <exception cref="InvalidOperationException">The endpoint has not been started.</exception>
    public int BoundPort => boundPort != 0
        ? boundPort
        : throw new InvalidOperationException("BoundPort is only available once StartAsync has returned.");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        boundPort = ResolveBoundPort(app);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => app.StopAsync(cancellationToken);

    // Kestrel writes the endpoints it actually bound (ephemeral port resolved) into the server addresses feature
    // during start, so this runs after StartAsync and never before.
    private static int ResolveBoundPort(WebApplication app)
    {
        IServerAddressesFeature? addresses =
            app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (addresses is not null)
        {
            foreach (string address in addresses.Addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Port > 0) return uri.Port;
            }
        }
        throw new InvalidOperationException("Kestrel reported no bound address after start.");
    }

    public ValueTask DisposeAsync() => app.DisposeAsync();
}

// Request DTOs (internal; minimal-API JSON binding reads their public properties).
internal sealed record TeleportRequest(int? Slot, string? Account, float X, float Y, float Z)
{
    public PlayerRef ToRef() => Slot is { } s ? PlayerRef.Slot(s) : PlayerRef.Account(Account ?? string.Empty);
}
internal sealed record KickRequest(int? Slot, string? Account, string? Reason)
{
    public PlayerRef ToRef() => Slot is { } s ? PlayerRef.Slot(s) : PlayerRef.Account(Account ?? string.Empty);
}
internal sealed record BroadcastRequest(string? Text);
internal sealed record BanRequest(string AccountId, string? Reason, DateTimeOffset? Until);
internal sealed record UnbanRequest(string AccountId);
