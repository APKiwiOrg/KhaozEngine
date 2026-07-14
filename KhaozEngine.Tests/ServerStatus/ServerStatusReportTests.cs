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
        };

        ServerStatusReport? parsed = ServerStatusReport.TryParse(original.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(original, parsed);
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
        // The endpoint added a field a shipped client never heard of; the client must still parse the rest.
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
