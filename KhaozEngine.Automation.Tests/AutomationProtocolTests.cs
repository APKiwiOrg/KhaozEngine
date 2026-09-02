using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Automation;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// The wire protocol on its own: what parses, what does not, and what a reply looks like on the wire. No host, no
/// socket, no frames.
/// </summary>
public class AutomationProtocolTests
{
    [Theory]
    [InlineData("{\"id\":1,\"cmd\":\"ping\"}", "ping", 1L)]
    [InlineData("{\"id\":2,\"cmd\":\"step\",\"frames\":3}", "step", 2L)]
    [InlineData("{\"id\":3,\"cmd\":\"state\"}", "state", 3L)]
    [InlineData("{\"id\":4,\"cmd\":\"call\",\"name\":\"walk_to\",\"args\":{\"x\":1}}", "call", 4L)]
    [InlineData("{\"id\":5,\"cmd\":\"quit\"}", "quit", 5L)]
    [InlineData("{\"id\":6,\"cmd\":\"input\",\"x\":10,\"y\":20,\"button\":\"left\"}", "input", 6L)]
    public void ParsesEveryCommandShape(string line, string command, long id)
    {
        Assert.True(AutomationRequest.TryParse(line, out AutomationRequest? request, out string? error));
        Assert.Null(error);
        Assert.Equal(command, request!.Command);
        Assert.Equal(id, request.Id);
    }

    [Fact]
    public void ParsesTheTokenAndTheCommandSpecificArguments()
    {
        AutomationRequest request = AutomationTestKit.Parse(
            "{\"id\":7,\"token\":\"abc\",\"cmd\":\"input\",\"x\":10.5,\"y\":20,\"key\":\"W\",\"holdFrames\":3}");

        Assert.Equal("abc", request.Token);
        Assert.True(request.TryReadFloat("x", out float x, out _));
        Assert.Equal(10.5f, x);
        Assert.Equal("W", request.ReadString("key"));
        Assert.True(request.TryReadInt("holdFrames", out int hold, out _));
        Assert.Equal(3, hold);
    }

    [Fact]
    public void LowerCasesTheCommandSoTheTableIsOrdinal()
    {
        Assert.Equal("input", AutomationTestKit.Parse("{\"cmd\":\"INPUT\"}").Command);
    }

    [Fact]
    public void AnAbsentIdReadsAsZero()
    {
        Assert.Equal(0L, AutomationTestKit.Parse("{\"cmd\":\"ping\"}").Id);
    }

    [Theory]
    [InlineData("", "empty request line")]
    [InlineData("   ", "empty request line")]
    [InlineData("not json at all", "malformed JSON")]
    [InlineData("[1,2,3]", "request must be a JSON object")]
    [InlineData("{\"id\":1}", "request is missing a string 'cmd'")]
    [InlineData("{\"cmd\":42}", "request is missing a string 'cmd'")]
    [InlineData("{\"cmd\":\"\"}", "request 'cmd' is empty")]
    [InlineData("{\"cmd\":\"ping\",\"id\":\"seven\"}", "request 'id' is not an integer")]
    public void RejectsAMalformedLineWithAReason(string line, string expected)
    {
        Assert.False(AutomationRequest.TryParse(line, out AutomationRequest? request, out string? error));
        Assert.Null(request);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void ASuccessReplyCarriesIdFrameAndOk()
    {
        string line = AutomationReply.Success(7, 412, new JsonObject { ["open"] = true }).ToJsonLine();

        JsonElement reply = AutomationTestKit.Json(line);
        Assert.Equal(7, reply.GetProperty("id").GetInt64());
        Assert.Equal(412, reply.GetProperty("frame").GetInt64());
        Assert.True(reply.GetProperty("ok").GetProperty("open").GetBoolean());
        Assert.False(reply.TryGetProperty("error", out _));
    }

    [Fact]
    public void AFailureReplyCarriesErrorAndNoOk()
    {
        string line = AutomationReply.Failure(7, 412, "unknown verb 'walk_to'").ToJsonLine();

        JsonElement reply = AutomationTestKit.Json(line);
        Assert.Equal("unknown verb 'walk_to'", reply.GetProperty("error").GetString());
        Assert.False(reply.TryGetProperty("ok", out _));
    }

    [Fact]
    public void AReplyLineIsOneLine()
    {
        string line = AutomationReply.Success(1, 2, new JsonObject { ["a"] = 1, ["b"] = 2 }).ToJsonLine();

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
    }

    [Fact]
    public void TheSameOkNodeCanBeSerializedTwice()
    {
        // A game verb returning a cached document must not throw on its second reply: JsonNode belongs to at most
        // one parent, so AutomationReply deep-clones on the way out.
        var shared = new JsonObject { ["hp"] = 10 };

        Assert.Contains("\"hp\":10", AutomationReply.Success(1, 1, shared).ToJsonLine());
        Assert.Contains("\"hp\":10", AutomationReply.Success(2, 2, shared).ToJsonLine());
    }

    [Fact]
    public void AMintedTokenIsBase64UrlAndAtLeast128Bits()
    {
        string token = AutomationHandshake.NewToken();

        Assert.True(token.Length >= 22, "a 128-bit token is at least 22 base64url characters");
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, AutomationHandshake.NewToken());
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", null, false)]
    [InlineData(null, "abc", false)]
    public void TokenMatchesIsExact(string? expected, string? presented, bool matches)
    {
        Assert.Equal(matches, AutomationHandshake.TokenMatches(expected, presented));
    }
}
