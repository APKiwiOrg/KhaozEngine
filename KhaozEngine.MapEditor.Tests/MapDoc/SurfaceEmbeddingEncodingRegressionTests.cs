using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using Xunit;
using static KhaozEngine.Tests.MapDoc.SurfaceEmbeddingRegressionFixtures;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceEmbeddingEncodingRegressionTests
{
    public static IEnumerable<object[]> LiteralBoundaries()
    {
        foreach (bool useNode in new[] { true, false })
        {
            yield return new object[] { useNode, "{}", "{}", 2 };
            yield return new object[] { useNode,
                """{"s":"\"\\\n\t\b\f\r\u0000"}""",
                """{"s":"\u0022\\\n\t\b\f\r\u0000"}""", 32 };
            yield return new object[] { useNode,
                """{"s":"é雪😀"}""", """{"s":"\u00E9\u96EA\uD83D\uDE00"}""", 32 };
            yield return new object[] { useNode,
                """{ "\"<é" : [ "A", true, null, -1.0e+02 ] }""",
                """{"\u0022\u003C\u00E9":["A",true,null,-1.0e+02]}""", 47 };
        }
    }

    [Theory]
    [MemberData(nameof(LiteralBoundaries))]
    public void PayloadConstructionUsesTheExactEscapedByteBoundary(bool useNode, string input,
        string expected, int exactBytes)
    {
        Assert.Equal(exactBytes, Encoding.UTF8.GetByteCount(expected));
        JsonObject node = JsonNode.Parse(input)!.AsObject();
        using JsonDocument json = JsonDocument.Parse(input);
        var accepted = new PayloadArrays(exactBytes);
        byte[] payload = useNode
            ? MapSurfaceEmbeddedPayload.Encode(node, exactBytes, accepted.Allocate)
            : MapSurfaceEmbeddedPayload.Encode(json.RootElement, exactBytes, accepted.Allocate);
        Assert.Single(accepted.Buffers);
        Assert.Same(accepted.Buffers[0], payload);
        accepted.AssertPayload(0, expected);

        var refused = new PayloadArrays(exactBytes - 1);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (useNode) MapSurfaceEmbeddedPayload.Encode(node, exactBytes - 1, refused.Allocate);
            else MapSurfaceEmbeddedPayload.Encode(json.RootElement, exactBytes - 1, refused.Allocate);
        });
        Assert.Contains("tiled", error.Message);
        Assert.Empty(refused.Buffers);
    }
}
