using System;
using System.Text.Json.Nodes;
using KhaozEngine.MapEdit;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeRenderRequestTests
{
    internal const string Json = """
    {"schemaVersion":1,"framing":{"eye":{"frame":{"x":0,"z":0},"local":{"x":0,"y":8,"z":0}},"target":{"frame":{"x":0,"z":0},"local":{"x":0,"y":0,"z":0}},"orthographic":true,"verticalSizeOrFovDegrees":8,"width":640,"height":480,"nearPlane":0.1,"farPlane":32},"scope":{"frame":{"x":0,"z":0},"min":{"x":-96,"y":-1,"z":-96},"max":{"x":96,"y":8,"z":96},"spaceIds":null,"channels":"All","motionPaddingMetres":0.35},"observer":{"frame":{"x":0,"z":0},"local":{"x":2,"y":1,"z":1}},"focus":{"frame":{"x":0,"z":0},"local":{"x":5,"y":0,"z":0}},"observerSpaceId":"room-b","roofMode":"AlwaysHidden","timeSeconds":3.25,"windSeed":23,"aa":"Quality","styleId":"windy","requirement":"Preview","warmupFrames":4}
    """;

    [Fact]
    public void NativeRenderWire_TransportsExactRequest()
    {
        var request = NativeRenderRequest.Read(Json);
        Assert.Equal(23, request.WindSeed);
        Assert.Equal(3.25, request.TimeSeconds);
        Assert.Equal("windy", request.StyleId);
        Assert.Equal(5, request.Focus!.Local.X);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("framing.eye")]
    [InlineData("framing.target")]
    [InlineData("observer")]
    [InlineData("focus")]
    public void FrameRange_EndpointsAndInvalidTokens(string path)
    {
        foreach (string axis in new[] { "x", "z" })
        {
            foreach (int value in new[] { -32768, 32767 })
            {
                var doc = JsonNode.Parse(Json)!;
                At(doc, path)["frame"]![axis] = value;
                var parsed = NativeRenderRequest.Read(doc.ToJsonString());
                var frame = path switch
                {
                    "scope" => parsed.Scope.Frame,
                    "framing.eye" => parsed.Framing.Eye.Frame,
                    "framing.target" => parsed.Framing.Target.Frame,
                    "observer" => parsed.Observer.Frame,
                    _ => parsed.Focus!.Frame
                };
                Assert.Equal(checked((short)value), axis == "x" ? frame.X : frame.Z);
            }
            foreach (long value in new long[] { -32769, 32768, int.MinValue, int.MaxValue })
            {
                var doc = JsonNode.Parse(Json)!;
                At(doc, path)["frame"]![axis] = value;
                Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(doc.ToJsonString()));
            }
            var fractional = JsonNode.Parse(Json)!;
            At(fractional, path)["frame"]![axis] = 1.5;
            Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(fractional.ToJsonString()));
        }
    }
    static JsonNode At(JsonNode node, string path)
    { foreach (string segment in path.Split('.')) node = node[segment]!; return node; }

    [Theory]
    [InlineData("scope.min.x", "97")]
    [InlineData("scope.max.z", "-96")]
    [InlineData("scope.motionPaddingMetres", "-1")]
    [InlineData("scope.spaceIds", "[\"a\",\"a\"]")]
    [InlineData("scope.spaceIds", "[\"\"]")]
    [InlineData("observerSpaceId", "\"\"")]
    [InlineData("framing.width", "0")]
    [InlineData("framing.nearPlane", "32")]
    [InlineData("framing.verticalSizeOrFovDegrees", "0")]
    [InlineData("aa", "\"quality\"")]
    [InlineData("scope.channels", "\"64\"")]
    [InlineData("schemaVersion", "2")]
    public void NativeRequest_InvalidDomainRefusesBeforeHost(string path, string value)
    {
        var doc = JsonNode.Parse(Json)!;
        int dot = path.LastIndexOf('.');
        var parent = dot < 0 ? doc : At(doc, path[..dot]);
        parent[path[(dot + 1)..]] = JsonNode.Parse(value);
        Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(doc.ToJsonString()));
    }

    [Fact]
    public void ClosedObjects_RefuseUnknownDuplicateAndCaseVariantFields()
    {
        Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(Json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")));
        Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(Json.Replace("\"x\":2", "\"x\":2,\"X\":2")));
        Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(Json.Replace("\"width\":640", "\"width\":640,\"unknown\":1")));
    }

    internal static System.Collections.Generic.IEnumerable<string> InvalidRequests()
    {
        foreach (var (path, value) in new[] { ("scope.min.x", "1e100"), ("timeSeconds", "1e1000"), ("scope.min.y", "9"),
            ("scope.spaceIds", "[null]"), ("framing.width", "1.5"), ("framing.height", "2147483648"),
            ("framing.farPlane", "0.01"), ("framing.nearPlane", "0"), ("roofMode", "\"unknown\""),
            ("aa", "1"), ("styleId", "\" \""), ("warmupFrames", "-1"), ("scope.channels", "\"Terrain, 64\""),
            ("windSeed", "2147483648"), ("observer.frame.x", "-2147483648"), ("focus.frame.z", "32768") })
        {
            var doc = JsonNode.Parse(Json)!; int dot = path.LastIndexOf('.');
            var parent = dot < 0 ? doc : At(doc, path[..dot]); parent[path[(dot + 1)..]] = JsonNode.Parse(value); yield return doc.ToJsonString();
        }
        yield return Json.Replace("\"x\":5", "\"x\":5,\"X\":5");
        yield return Json.Replace("\"width\":640", "\"width\":640,\"width\":640");
    }
    [Fact]
    public void InvalidNumericAndNestedDomains_Refuse()
    { foreach (string json in InvalidRequests()) Assert.Throws<ArgumentException>(() => NativeRenderRequest.Read(json)); }
    [Fact]
    public void Focus_IsOptionalOrNull()
    {
        var doc = JsonNode.Parse(Json)!.AsObject();
        doc.Remove("focus"); Assert.Null(NativeRenderRequest.Read(doc.ToJsonString()).Focus);
        doc["focus"] = null; Assert.Null(NativeRenderRequest.Read(doc.ToJsonString()).Focus);
    }
}
