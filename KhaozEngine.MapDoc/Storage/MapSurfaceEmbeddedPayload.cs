using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Measures compact embedded JSON before requesting the exact buffer filled for decoding.</summary>
internal static class MapSurfaceEmbeddedPayload
{
    // Match the standalone patch codec's maximum container depth.
    const int MaxDepth = 16;

    internal static byte[] Encode(JsonObject payload, int maxBytes, Func<int, byte[]> allocatePayload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(allocatePayload);
        var measure = new MapSurfaceEmbeddedTokenWriter(maxBytes);
        Visit(payload, ref measure, 0);
        byte[] bytes = Allocate(measure.Length, allocatePayload);
        var output = new MapSurfaceEmbeddedTokenWriter(bytes);
        Visit(payload, ref output, 0);
        Finish(output.Length, bytes.Length);
        return bytes;
    }

    internal static byte[] Encode(JsonElement payload, int maxBytes, Func<int, byte[]> allocatePayload)
    {
        ArgumentNullException.ThrowIfNull(allocatePayload);
        var measure = new MapSurfaceEmbeddedTokenWriter(maxBytes);
        Visit(payload, ref measure, 0);
        byte[] bytes = Allocate(measure.Length, allocatePayload);
        var output = new MapSurfaceEmbeddedTokenWriter(bytes);
        Visit(payload, ref output, 0);
        Finish(output.Length, bytes.Length);
        return bytes;
    }

    static byte[] Allocate(int length, Func<int, byte[]> allocatePayload)
    {
        byte[] bytes = allocatePayload(length);
        if (bytes is null || bytes.Length != length)
            throw new MapDocumentException("embedded payload allocator must return the exact measured size");
        return bytes;
    }

    static void Finish(int written, int measured)
    {
        if (written != measured) throw new MapDocumentException("embedded payload changed during encoding");
    }

    static void Visit(JsonNode? node, ref MapSurfaceEmbeddedTokenWriter output, int depth, bool packed = false)
    {
        switch (node)
        {
            case null:
                output.Bytes("null"u8);
                break;
            case JsonObject obj:
                CheckDepth(depth);
                output.Byte((byte)'{');
                bool firstMember = true;
                foreach (KeyValuePair<string, JsonNode?> member in obj)
                {
                    if (!firstMember) output.Byte((byte)',');
                    firstMember = false;
                    output.String(member.Key.AsSpan());
                    output.Byte((byte)':');
                    Visit(member.Value, ref output, depth + 1, depth == 0 && IsPacked(member.Key));
                }
                output.Byte((byte)'}');
                break;
            case JsonArray array:
                CheckDepth(depth);
                output.Byte((byte)'[');
                bool firstItem = true;
                foreach (JsonNode? item in array)
                {
                    if (!firstItem) output.Byte((byte)',');
                    firstItem = false;
                    Visit(item, ref output, depth + 1);
                }
                output.Byte((byte)']');
                break;
            case JsonValue value:
                // Parsed scalars retain their JsonElement, including the original numeric spelling.
                if (value.TryGetValue(out JsonElement element)) Visit(element, ref output, depth, packed);
                else if (value.TryGetValue(out object? scalar)) output.Scalar(scalar, packed);
                else throw new MapDocumentException("unsupported JSON scalar in embedded surface patch");
                break;
            default:
                throw new MapDocumentException("invalid JSON node in embedded surface patch");
        }
    }

    static void Visit(JsonElement element, ref MapSurfaceEmbeddedTokenWriter output, int depth, bool packed = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                CheckDepth(depth);
                output.Byte((byte)'{');
                bool firstMember = true;
                foreach (JsonProperty member in element.EnumerateObject())
                {
                    if (!firstMember) output.Byte((byte)',');
                    firstMember = false;
                    output.String(member.Name.AsSpan());
                    output.Byte((byte)':');
                    Visit(member.Value, ref output, depth + 1, depth == 0 && IsPacked(member.Name));
                }
                output.Byte((byte)'}');
                break;
            case JsonValueKind.Array:
                CheckDepth(depth);
                output.Byte((byte)'[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem) output.Byte((byte)',');
                    firstItem = false;
                    Visit(item, ref output, depth + 1);
                }
                output.Byte((byte)']');
                break;
            case JsonValueKind.String:
                output.String(element.GetString().AsSpan(), packed);
                break;
            case JsonValueKind.Number:
                // Only a scalar token is copied, never the raw text of an object or array.
                output.Ascii(element.GetRawText().AsSpan());
                break;
            case JsonValueKind.True:
                output.Bytes("true"u8);
                break;
            case JsonValueKind.False:
                output.Bytes("false"u8);
                break;
            case JsonValueKind.Null:
                output.Bytes("null"u8);
                break;
            default:
                throw new MapDocumentException("invalid JSON value in embedded surface patch");
        }
    }

    static bool IsPacked(string name) => name is "heights" or "cells" or "presence";

    static void CheckDepth(int depth)
    {
        if (depth >= MaxDepth) throw new MapDocumentException("embedded surface patch exceeds its depth limit, use tiled storage");
    }
}
