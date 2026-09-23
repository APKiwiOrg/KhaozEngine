using System;
using System.Text.Json;
using KhaozEngine.Identity;
using Xunit;

namespace KhaozEngine.Tests.Identity;

/// <summary>
/// The <c>/auth/exchange</c> wire DTOs pinned against what both games' clients and auth services exchange today, so
/// either game switches to the engine records with no wire change and every outstanding client keeps working.
/// </summary>
/// <remarks>
/// The game records below restate Ruinborne's <c>Ruinborne.Core/Auth/AuthProtocol.cs</c> and Grimhollow's
/// <c>Grimhollow.Shared/AuthProtocol.cs</c> member for member, and both games serialize them with the web defaults.
/// The JSON literals are the bytes those records produce. Ruinborne's response is the union's superset, so the engine
/// writes it byte for byte. Grimhollow's differs only by members a reader skips.
/// </remarks>
public class AuthExchangeWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Expiry = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FractionalExpiry = Expiry.AddTicks(1234567);

    private sealed record GameRequest(string Provider, string AccessToken);

    private sealed record RuinborneResponse(
        string Status, string? SessionToken = null, DateTimeOffset? ExpiresAtUtc = null,
        string? Subject = null, string? DisplayName = null,
        string? BanReason = null, DateTimeOffset? BanExpiresAtUtc = null);

    private sealed record GrimhollowResponse(
        string Status, string? SessionToken = null, DateTimeOffset? ExpiresAtUtc = null,
        string? Subject = null, string? DisplayName = null, int? RetryAfterSeconds = null);

    private const string RequestJson = """{"provider":"discord","accessToken":"access-token"}""";

    private const string RuinborneOkJson =
        """{"status":"ok","sessionToken":"v3.acct:42.V3Jlbg.Y2hhcmFjdGVyOjE3.1790769600.mac","expiresAtUtc":"2026-09-30T12:00:00.1234567+00:00","subject":"acct:42","displayName":"Wren","banReason":null,"banExpiresAtUtc":null}""";

    private const string RuinborneBannedJson =
        """{"status":"banned","sessionToken":null,"expiresAtUtc":null,"subject":"acct:42","displayName":null,"banReason":"griefing","banExpiresAtUtc":"2026-09-30T12:00:00+00:00"}""";

    private const string GrimhollowOkJson =
        """{"status":"ok","sessionToken":"v2.discord:80351110224678912.V3Jlbg.1790769600.mac","expiresAtUtc":"2026-09-30T12:00:00+00:00","subject":"discord:80351110224678912","displayName":"Wren","retryAfterSeconds":null}""";

    [Fact]
    public void TheRequest_IsTheBytesBothClientsSendToday_WhateverTheCallersOptions()
    {
        var request = new AuthExchangeRequest("discord", "access-token");

        Assert.Equal(RequestJson, JsonSerializer.Serialize(new GameRequest("discord", "access-token"), Web));
        Assert.Equal(RequestJson, JsonSerializer.Serialize(request, Web));
        Assert.Equal(RequestJson, JsonSerializer.Serialize(request, JsonSerializerOptions.Default));
        Assert.Equal(request, JsonSerializer.Deserialize<AuthExchangeRequest>(RequestJson, Web));
        Assert.Equal(request, JsonSerializer.Deserialize<AuthExchangeRequest>(RequestJson, JsonSerializerOptions.Default));
    }

    [Fact]
    public void EveryResponse_IsByteForByteWhatRuinborneWritesToday()
    {
        (AuthExchangeResponse Engine, RuinborneResponse Game)[] pairs =
        {
            (new AuthExchangeResponse(AuthExchangeStatuses.Ok, "v3.acct:42.V3Jlbg.Y2hhcmFjdGVyOjE3.1790769600.mac",
                FractionalExpiry, "acct:42", "Wren"),
             new RuinborneResponse("ok", "v3.acct:42.V3Jlbg.Y2hhcmFjdGVyOjE3.1790769600.mac",
                FractionalExpiry, "acct:42", "Wren")),
            (new AuthExchangeResponse(AuthExchangeStatuses.Banned, Subject: "acct:42", BanReason: "griefing",
                BanExpiresAtUtc: Expiry),
             new RuinborneResponse("banned", Subject: "acct:42", BanReason: "griefing", BanExpiresAtUtc: Expiry)),
            (new AuthExchangeResponse(AuthExchangeStatuses.NotWhitelisted, Subject: "acct:42", DisplayName: "Wren"),
             new RuinborneResponse("not_whitelisted", Subject: "acct:42", DisplayName: "Wren")),
            (new AuthExchangeResponse(AuthExchangeStatuses.InvalidCredential), new RuinborneResponse("invalid_credential")),
            (new AuthExchangeResponse(AuthExchangeStatuses.Unavailable), new RuinborneResponse("unavailable")),
        };

        foreach ((AuthExchangeResponse engine, RuinborneResponse game) in pairs)
        {
            string expected = JsonSerializer.Serialize(game, Web);
            Assert.Equal(expected, JsonSerializer.Serialize(engine, Web));
            Assert.Equal(expected, JsonSerializer.Serialize(engine, JsonSerializerOptions.Default));
        }
        Assert.Equal(RuinborneOkJson, JsonSerializer.Serialize(pairs[0].Engine, Web));
        Assert.Equal(RuinborneBannedJson, JsonSerializer.Serialize(pairs[1].Engine, Web));
    }

    [Fact]
    public void WhatBothServicesWriteToday_ReadsIntoTheEngineRecord()
    {
        AuthExchangeResponse fromRuinborne = JsonSerializer.Deserialize<AuthExchangeResponse>(RuinborneBannedJson, Web)!;
        AuthExchangeResponse fromGrimhollow = JsonSerializer.Deserialize<AuthExchangeResponse>(GrimhollowOkJson, Web)!;

        Assert.Equal(new AuthExchangeResponse("banned", Subject: "acct:42", BanReason: "griefing", BanExpiresAtUtc: Expiry),
            fromRuinborne);
        // Grimhollow's client-only retryAfterSeconds is skipped, and the ban fields it never wrote read as null.
        Assert.Equal(new AuthExchangeResponse("ok", "v2.discord:80351110224678912.V3Jlbg.1790769600.mac", Expiry,
            "discord:80351110224678912", "Wren"), fromGrimhollow);
    }

    [Fact]
    public void WhatTheEngineWrites_ReadsIntoBothGamesClientRecords()
    {
        var engine = new AuthExchangeResponse(AuthExchangeStatuses.Banned, Subject: "discord:1", DisplayName: "Wren",
            BanReason: "griefing", BanExpiresAtUtc: Expiry);
        string json = JsonSerializer.Serialize(engine, Web);

        RuinborneResponse ruinborne = JsonSerializer.Deserialize<RuinborneResponse>(json, Web)!;
        GrimhollowResponse grimhollow = JsonSerializer.Deserialize<GrimhollowResponse>(json, Web)!;

        Assert.Equal(new RuinborneResponse("banned", Subject: "discord:1", DisplayName: "Wren", BanReason: "griefing",
            BanExpiresAtUtc: Expiry), ruinborne);
        Assert.Equal(new GrimhollowResponse("banned", Subject: "discord:1", DisplayName: "Wren"), grimhollow);
    }

    [Fact]
    public void AFractionalExpiry_RoundTripsExactly()
    {
        var engine = new AuthExchangeResponse(AuthExchangeStatuses.Ok, "token", FractionalExpiry, "discord:1", "Wren");

        AuthExchangeResponse back = JsonSerializer.Deserialize<AuthExchangeResponse>(JsonSerializer.Serialize(engine, Web), Web)!;

        Assert.Equal(FractionalExpiry, back.ExpiresAtUtc);
        Assert.Equal(TimeSpan.Zero, back.ExpiresAtUtc!.Value.Offset);
    }

    [Fact]
    public void TheStatusTokens_AreTheWireLiteralsBothGamesUse()
    {
        Assert.Equal("ok", AuthExchangeStatuses.Ok);
        Assert.Equal("not_whitelisted", AuthExchangeStatuses.NotWhitelisted);
        Assert.Equal("banned", AuthExchangeStatuses.Banned);
        Assert.Equal("invalid_credential", AuthExchangeStatuses.InvalidCredential);
        Assert.Equal("unavailable", AuthExchangeStatuses.Unavailable);
        Assert.Equal("retry_later", AuthExchangeStatuses.RetryLater);
    }

    [Fact]
    public void ToString_NeverPrintsTheCredential_TheToken_OrTheAccount()
    {
        string request = new AuthExchangeRequest("discord", "access-token").ToString();
        string response = new AuthExchangeResponse(AuthExchangeStatuses.Ok, "v2.secret-token", Expiry, "discord:1", "Wren",
            "reason").ToString();

        Assert.Equal("AuthExchangeRequest { Provider = discord }", request);
        Assert.Equal("AuthExchangeResponse { Status = ok }", response);
    }
}
