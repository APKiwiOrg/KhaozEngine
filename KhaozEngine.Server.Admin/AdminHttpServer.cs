using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
/// </summary>
public sealed class AdminHttpServer : IAsyncDisposable
{
    private readonly WebApplication app;

    public AdminHttpServer(ServerAdmin admin, AdminEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(options);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // System.Text.Json drops fields by default, so a Vector3 (X/Y/Z are fields) in a response DTO serializes as
        // an empty {}. Register a scoped converter so OnlinePlayer.Position carries its components; scoped to this
        // endpoint's JSON options (not a blanket IncludeFields, which would change every other type's serialization).
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new Vector3JsonConverter()));
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(options.BindAddress, options.Port, listen => listen.UseHttps(options.Certificate.Certificate)));
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
            await admin.BanAsync(r.AccountId, r.Reason ?? string.Empty, r.Until);
            return Results.Accepted();
        });

        g.MapPost("/unban", async Task<IResult> (UnbanRequest r) =>
        {
            if (!admin.BansSupported) return Results.StatusCode(StatusCodes.Status501NotImplemented);
            await admin.UnbanAsync(r.AccountId);
            return Results.Accepted();
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => app.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => app.StopAsync(cancellationToken);
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
