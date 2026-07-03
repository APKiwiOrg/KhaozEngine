using System;
using System.Text.Json;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordActivityPayloadTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Handshake_CarriesVersionAndClientId()
    {
        JsonElement root = Root(DiscordIpcPayloads.Handshake("12345"));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("12345", root.GetProperty("client_id").GetString());
    }

    [Fact]
    public void SetActivity_MapsDetailsStateTimestampParty()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var presence = new RichPresence
        {
            Details = "In the overworld",
            State = "Solo",
            StartTimestampUtc = start,
            Party = new PresenceParty("party-1", 2, 4),
            LargeImage = new PresenceImage("map_forest", "Forest"),
            JoinSecret = "join-abc",
        };

        JsonElement root = Root(DiscordIpcPayloads.SetActivity(4242, presence, "nonce-1"));
        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("nonce-1", root.GetProperty("nonce").GetString());

        JsonElement args = root.GetProperty("args");
        Assert.Equal(4242, args.GetProperty("pid").GetInt32());

        JsonElement activity = args.GetProperty("activity");
        Assert.Equal("In the overworld", activity.GetProperty("details").GetString());
        Assert.Equal("Solo", activity.GetProperty("state").GetString());

        long expectedUnix = ((DateTimeOffset)start).ToUnixTimeSeconds();
        Assert.Equal(expectedUnix, activity.GetProperty("timestamps").GetProperty("start").GetInt64());

        JsonElement party = activity.GetProperty("party");
        Assert.Equal("party-1", party.GetProperty("id").GetString());
        Assert.Equal(2, party.GetProperty("size")[0].GetInt32());
        Assert.Equal(4, party.GetProperty("size")[1].GetInt32());

        Assert.Equal("map_forest", activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("join-abc", activity.GetProperty("secrets").GetProperty("join").GetString());
    }

    [Fact]
    public void SetActivity_OmitsEmptyFields()
    {
        JsonElement activity = Root(DiscordIpcPayloads.SetActivity(1, new RichPresence { Details = "x" }, "n"))
            .GetProperty("args").GetProperty("activity");
        Assert.False(activity.TryGetProperty("timestamps", out _));
        Assert.False(activity.TryGetProperty("party", out _));
        Assert.False(activity.TryGetProperty("assets", out _));
        Assert.False(activity.TryGetProperty("secrets", out _));
    }

    [Fact]
    public void TryParseReadyUser_ExtractsUser()
    {
        string json = """
        {"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"77","username":"kiwi","global_name":"Kiwi"}}}
        """;
        Assert.True(DiscordIpcPayloads.TryParseReadyUser(json, out SocialUser user));
        Assert.Equal("77", user.Id);
        Assert.Equal("kiwi", user.Username);
        Assert.Equal("Kiwi", user.GlobalName);
    }

    [Fact]
    public void TryParseDispatch_SplitsEventAndData()
    {
        string json = """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"s-1"}}""";
        Assert.True(DiscordIpcPayloads.TryParseDispatch(json, out string evt, out string data));
        Assert.Equal("ACTIVITY_JOIN", evt);
        Assert.True(DiscordIpcPayloads.TryParseJoinSecret(data, out string secret));
        Assert.Equal("s-1", secret);
    }

    [Fact]
    public void TryParseJoinRequestUser_ExtractsUser()
    {
        string data = """{"user":{"id":"9","username":"ally","global_name":null}}""";
        Assert.True(DiscordIpcPayloads.TryParseJoinRequestUser(data, out SocialUser user));
        Assert.Equal("9", user.Id);
        Assert.Equal("ally", user.Username);
        Assert.Null(user.GlobalName);
    }

    [Fact]
    public void TryParseReadyUser_ReturnsFalseOnGarbage()
    {
        Assert.False(DiscordIpcPayloads.TryParseReadyUser("not json", out _));
        Assert.False(DiscordIpcPayloads.TryParseReadyUser("{}", out _));
    }

    [Fact]
    public void ClearActivity_SendsNullActivity_NotEmptyObject()
    {
        JsonElement args = Root(DiscordIpcPayloads.ClearActivity(99, "n")).GetProperty("args");
        Assert.Equal(99, args.GetProperty("pid").GetInt32());
        Assert.Equal(JsonValueKind.Null, args.GetProperty("activity").ValueKind);
    }

    [Theory]
    [InlineData("{\"evt\":\"READY\",\"data\":\"not-an-object\"}")]                       // data is a string
    [InlineData("{\"evt\":\"READY\",\"data\":{\"user\":{\"id\":12345,\"username\":\"k\"}}}")] // numeric id
    [InlineData("[1,2,3]")]                                                              // root is an array
    public void TryParseReadyUser_WrongTypedFields_ReturnFalseWithoutThrowing(string json)
    {
        Assert.False(DiscordIpcPayloads.TryParseReadyUser(json, out _));
    }

    [Fact]
    public void TryParseDispatch_NonObjectRoot_ReturnsFalseWithoutThrowing()
    {
        Assert.False(DiscordIpcPayloads.TryParseDispatch("[1,2,3]", out _, out _));
        Assert.False(DiscordIpcPayloads.TryParseDispatch("\"x\"", out _, out _));
    }

    [Fact]
    public void TryParseJoin_WrongTypedData_ReturnFalseWithoutThrowing()
    {
        Assert.False(DiscordIpcPayloads.TryParseJoinSecret("[1,2,3]", out _));
        Assert.False(DiscordIpcPayloads.TryParseJoinSecret("\"nope\"", out _));
        Assert.False(DiscordIpcPayloads.TryParseJoinRequestUser("\"nope\"", out _));
        Assert.False(DiscordIpcPayloads.TryParseJoinRequestUser("42", out _));
    }
}
