using System;
using KhaozEngine.ServerStatus;
using Xunit;

namespace KhaozEngine.Tests.ServerStatus;

public class ServerStatusReportTests
{
    [Fact]
    public void RoundTrips_AllFields_ThroughJson()
    {
        var original = new ServerStatusReport
        {
            SchemaVersion = 1,
            Health = ServerHealth.Restarting,
            ServerVersion = "1.4.2",
            MinClientVersion = "1.4.0",
            LatestClientVersion = "1.4.3",
            LastHeartbeatUtc = new DateTimeOffset(2026, 7, 14, 9, 41, 12, TimeSpan.Zero),
            LastDeployUtc = new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero),
            ExpectedBackUtc = new DateTimeOffset(2026, 7, 14, 9, 45, 0, TimeSpan.Zero),
            Motd = "Double XP weekend.",
            ServerAddress = "4.254.6.139",
        };

        ServerStatusReport? parsed = ServerStatusReport.TryParse(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void RoundTrips_WithoutServerAddress()
    {
        var original = new ServerStatusReport
        {
            Health = ServerHealth.Healthy,
            ServerVersion = "1.4.2",
            // ServerAddress left unset: the publisher does not know the address, or nothing is running.
        };

        ServerStatusReport? parsed = ServerStatusReport.TryParse(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
        Assert.Null(parsed!.ServerAddress);
    }

    [Fact]
    public void ServerAddress_SerializesUnderItsWireName()
    {
        string json = new ServerStatusReport { ServerAddress = "4.254.6.139" }.ToJson();
        Assert.Contains("\"serverAddress\":\"4.254.6.139\"", json);
    }

    [Fact]
    public void NullOptionals_AreWrittenAsNull_NotOmitted()
    {
        // The contract options set no DefaultIgnoreCondition, so an unset optional is emitted as a null
        // literal. serverAddress matches motd and expectedBackUtc exactly, which keeps the endpoint's byte
        // level output the shape a consumer already snapshots.
        string json = new ServerStatusReport().ToJson();

        Assert.Contains("\"motd\":null", json);
        Assert.Contains("\"expectedBackUtc\":null", json);
        Assert.Contains("\"serverAddress\":null", json);
    }

    [Fact]
    public void ServerAddress_IsPlacedAfterMotd_InTheSerializedOrder()
    {
        string json = new ServerStatusReport { Motd = "hi", ServerAddress = "4.254.6.139" }.ToJson();
        Assert.True(json.IndexOf("\"motd\"", StringComparison.Ordinal) < json.IndexOf("\"serverAddress\"", StringComparison.Ordinal));
    }

    [Fact]
    public void OldPayload_WithoutServerAddress_ParsesWithEveryOtherMemberIntact()
    {
        // A publisher that has not adopted the field yet. Nothing about the rest of the report changes.
        const string json = """
            {
              "schemaVersion": 1,
              "health": "restarting",
              "serverVersion": "1.4.2",
              "minClientVersion": "1.4.0",
              "latestClientVersion": "1.4.3",
              "lastHeartbeatUtc": "2026-07-14T09:41:12Z",
              "lastDeployUtc": "2026-07-14T09:30:00Z",
              "expectedBackUtc": "2026-07-14T09:45:00Z",
              "motd": "Double XP weekend."
            }
            """;

        ServerStatusReport? parsed = ServerStatusReport.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SchemaVersion);
        Assert.Equal(ServerHealth.Restarting, parsed.Health);
        Assert.Equal("1.4.2", parsed.ServerVersion);
        Assert.Equal("1.4.0", parsed.MinClientVersion);
        Assert.Equal("1.4.3", parsed.LatestClientVersion);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 41, 12, TimeSpan.Zero), parsed.LastHeartbeatUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero), parsed.LastDeployUtc);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 45, 0, TimeSpan.Zero), parsed.ExpectedBackUtc);
        Assert.Equal("Double XP weekend.", parsed.Motd);
        Assert.Null(parsed.ServerAddress);
    }

    [Fact]
    public void NewPayload_WithAnUnknownMember_StillParses()
    {
        // The forward compatibility the whole additive design rests on: schemaVersion stays 1 because a
        // client that has never heard of a member ignores it instead of failing the parse.
        const string json = """
            { "health": "healthy", "serverAddress": "4.254.6.139", "serverRegion": "australiaeast" }
            """;

        ServerStatusReport? parsed = ServerStatusReport.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SchemaVersion);
        Assert.Equal(ServerHealth.Healthy, parsed.Health);
        Assert.Equal("4.254.6.139", parsed.ServerAddress);
    }

    [Fact]
    public void Health_SerializesAsLowercaseWireToken()
    {
        string json = new ServerStatusReport { Health = ServerHealth.Down }.ToJson();
        Assert.Contains("\"health\":\"down\"", json);
    }

    [Theory]
    [InlineData("healthy", ServerHealth.Healthy)]
    [InlineData("RESTARTING", ServerHealth.Restarting)] // case-insensitive read
    [InlineData("Down", ServerHealth.Down)]
    [InlineData("unknown", ServerHealth.Unknown)]
    [InlineData("teleporting", ServerHealth.Unknown)]   // future/unknown token degrades, does not throw
    public void Health_TolerantlyReadsToken(string wire, ServerHealth expected)
    {
        ServerStatusReport? parsed = ServerStatusReport.TryParse($"{{\"health\":\"{wire}\"}}");
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Health);
    }

    [Fact]
    public void TolerantRead_IgnoresUnknownFields()
    {
        // The endpoint added a field a shipped client never heard of. The client must still parse the rest.
        const string json = """
            { "health": "healthy", "serverVersion": "2.0.0", "regionCode": "ap-southeast", "shardCount": 12 }
            """;

        ServerStatusReport? parsed = ServerStatusReport.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(ServerHealth.Healthy, parsed!.Health);
        Assert.Equal("2.0.0", parsed.ServerVersion);
    }

    [Fact]
    public void TolerantRead_MissingOptionalFields_FallBackToDefaults()
    {
        // A minimal body: only health present. Everything else defaults, nullables stay null.
        ServerStatusReport? parsed = ServerStatusReport.TryParse("{ \"health\": \"healthy\" }");

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.SchemaVersion);      // init default
        Assert.Equal("", parsed.ServerVersion);      // init default, never null
        Assert.Equal("", parsed.MinClientVersion);
        Assert.Equal("", parsed.LatestClientVersion);
        Assert.Null(parsed.ExpectedBackUtc);
        Assert.Null(parsed.Motd);
        Assert.Null(parsed.ServerAddress);
    }

    [Fact]
    public void TolerantRead_MissingHealth_DefaultsToUnknown()
    {
        ServerStatusReport? parsed = ServerStatusReport.TryParse("{ \"serverVersion\": \"1.0.0\" }");
        Assert.NotNull(parsed);
        Assert.Equal(ServerHealth.Unknown, parsed!.Health);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"health\": ")]          // truncated
    [InlineData("[1, 2, 3]")]               // wrong root shape
    public void TryParse_ReturnsNull_OnGarbage(string garbage)
    {
        Assert.Null(ServerStatusReport.TryParse(garbage));
    }

    [Fact]
    public void TryParse_ReturnsNull_OnJsonNullLiteral()
    {
        Assert.Null(ServerStatusReport.TryParse("null"));
    }

    [Fact]
    public void TryParse_AllowsCommentsAndTrailingCommas()
    {
        const string jsonc = """
            {
              "health": "healthy", // current
              "serverVersion": "3.1.4",
            }
            """;

        ServerStatusReport? parsed = ServerStatusReport.TryParse(jsonc);
        Assert.NotNull(parsed);
        Assert.Equal("3.1.4", parsed!.ServerVersion);
    }
}
