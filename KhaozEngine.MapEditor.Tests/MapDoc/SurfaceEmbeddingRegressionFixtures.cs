using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

internal static class SurfaceEmbeddingRegressionFixtures
{
    internal const int PatchBytes = 240;
    internal const int EscapedPatchBytes = 274;
    internal const string Patch0 = """{"key":{"surfaceId":"s","slotX":"0","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"AAAAAAAAAAAAAAAAAAAAAA==","cells":"AAAAAAAAAAA=","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";
    internal const string Patch1 = """{"key":{"surfaceId":"s","slotX":"1","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"AAAAAAAAAAAAAAAAAAAAAA==","cells":"AAAAAAAAAAA=","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";
    internal const string EscapedInput = """{ "key" : { "surfaceId" : "sé<\"\\\n😀", "slotX" : "0", "slotZ" : "0" }, "cellMinX" : 0, "cellMinZ" : 0, "width" : 1, "depth" : 1, "heights" : "AAAAAAAAAAAAAAAAAAAAAA==", "cells" : "AAAAAAAAAAA=", "presence" : "AQAAAAAAAAA=", "cornerDependencies" : [], "edgeSubdivisions" : [], "records" : [] }""";
    internal const string EscapedPayload = """{"key":{"surfaceId":"s\u00E9\u003C\u0022\\\n\uD83D\uDE00","slotX":"0","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"AAAAAAAAAAAAAAAAAAAAAA==","cells":"AAAAAAAAAAA=","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";
    internal const string PlusInput = """{"key":{"surfaceId":"s","slotX":"0","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"\u002BAAAAPgAAAD4AAAA\u002BAAAAA==","cells":"AAAAAAAAAAA=","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";
    internal const string PlusPayload = """{"key":{"surfaceId":"s","slotX":"0","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"+AAAAPgAAAD4AAAA+AAAAA==","cells":"AAAAAAAAAAA=","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";
    internal const string MalformedPayload = """{"key":{"surfaceId":"s","slotX":"0","slotZ":"0"},"cellMinX":0,"cellMinZ":0,"width":1,"depth":1,"heights":"AAAAAAAAAAAAAAAAAAAAAA==","cells":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","presence":"AQAAAAAAAAA=","cornerDependencies":[],"edgeSubdivisions":[],"records":[]}""";

    internal static string Array(int count) => count switch
    {
        0 => "[]",
        1 => "[" + Patch0 + "]",
        2 => "[" + Patch0 + "," + Patch1 + "]",
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    internal static IReadOnlyList<MapSurfacePatch> Read(bool useNode, string array, int patchCap,
        long aggregateCap, PayloadArrays output)
    {
        if (useNode)
        {
            JsonObject root = JsonNode.Parse("{\"surfacePatches\":" + array + "}")!.AsObject();
            var set = new MapSurfaceSet();
            MapSurfaceEmbedding.Read(root, set, patchCap, aggregateCap, output.Allocate);
            return set.Patches.Values.OrderBy(p => p.Key).ToArray();
        }
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.Converters.Insert(0, new MapSurfaceEmbeddingConverter(patchCap, aggregateCap, output.Allocate));
        return JsonSerializer.Deserialize<List<MapSurfacePatch>>(array, options)!;
    }

    internal static void Refuses(bool useNode, Action action)
    {
        Exception error = useNode ? Assert.Throws<MapDocumentException>(action) : Assert.Throws<JsonException>(action);
        Assert.Contains("tiled", error.Message);
    }

    internal sealed class PayloadArrays(int maximumRequest)
    {
        internal List<byte[]> Buffers { get; } = new();
        internal byte[] Allocate(int size)
        {
            Assert.InRange(size, 1, maximumRequest);
            var buffer = new byte[size];
            buffer.AsSpan().Fill(0xCC);
            Buffers.Add(buffer);
            return buffer;
        }
        internal void AssertPayload(int index, string expected)
        {
            Assert.Equal(Encoding.UTF8.GetBytes(expected), Buffers[index]);
        }
    }
}
