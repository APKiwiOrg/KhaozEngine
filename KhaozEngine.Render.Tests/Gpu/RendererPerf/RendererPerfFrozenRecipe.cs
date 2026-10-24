using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfDerivedDigests(string GeometrySha256, string SequenceSha256, string ConstructionSha256);

internal static class RendererPerfFrozenRecipe
{
    internal static string PlatformFamily =>
        (OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsFreeBSD() ? "freebsd" : "unknown")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    // Keep the complete live construction hashed as well as its portable author-controlled inputs.
    // D1 will make the renderer's camera and skinning math portable. D2 never substitutes that math.
    internal static (JsonObject Authored, RendererPerfDerivedDigests Derived) Capture(byte[] description)
    {
        JsonObject authored = JsonNode.Parse(description)!.AsObject();
        var derived = new RendererPerfDerivedDigests(authored["GeometrySha256"]!.GetValue<string>(),
            authored["SequenceSha256"]!.GetValue<string>(), RendererPerfRecipe.Hash(description));
        authored.Remove("GeometrySha256");
        authored.Remove("SequenceSha256");
        var camera = authored["Camera"]!.AsObject();
        foreach (string field in new[] { "Eye", "Forward", "View", "Projection", "ViewProjection", "AbsoluteViewProjection" })
            camera.Remove(field);
        foreach (JsonNode? frame in authored["Frames"]!.AsArray())
        {
            var inputs = frame!.AsObject();
            foreach (string field in new[] { "View", "Projection", "ViewProjection", "PoseSha256" }) inputs.Remove(field);
        }
        StripDerivedGeometry(authored["Geometry"]);
        return (authored, derived);
    }

    static void StripDerivedGeometry(JsonNode? node)
    {
        if (node is JsonObject geometry)
        {
            StripDerivedGeometry(geometry["Rigid"]);
            geometry.Remove("PropWorlds");
            if (geometry["Draws"] is JsonArray draws)
                foreach (JsonNode? draw in draws) draw!.AsObject().Remove("World");
            if (geometry["Actor"] is JsonObject actor)
            {
                actor.Remove("RestPose");
                actor.Remove("RestPoseSha256");
                actor.Remove("InverseBind");
                actor.Remove("InverseBindSha256");
            }
            geometry.Remove("PaletteAtZero");
            geometry.Remove("Palettes");
        }
    }

    internal static byte[] Freeze(byte[] description)
    {
        var (authored, derived) = Capture(description);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = "khaozengine.renderer-perf-frozen-recipe/2",
            Authored = authored,
            DerivedByPlatform = new Dictionary<string, RendererPerfDerivedDigests> { [PlatformFamily] = derived },
        }, RendererPerfRecipe.Json);
    }

    internal static void Verify(JsonObject frozen, JsonObject authored, RendererPerfDerivedDigests derived, string family)
    {
        string id = authored["Id"]!.GetValue<string>();
        if (frozen["Schema"]?.GetValue<string>() != "khaozengine.renderer-perf-frozen-recipe/2")
            throw new InvalidOperationException($"The {id} frozen recipe has an unsupported identity schema.");
        string actualAuthored = RendererPerfRecipe.Digest(authored);
        string actual = JsonSerializer.Serialize(derived, RendererPerfRecipe.Json);
        if (RendererPerfRecipe.Digest(frozen["Authored"]) != actualAuthored)
            throw new InvalidOperationException($"The {id} portable authored construction differs for {family} at "
                + $"{Difference(frozen["Authored"], authored, "$")}. Actual AuthoredSha256={actualAuthored}. ActualDerived={actual}");
        if (frozen["DerivedByPlatform"] is not JsonObject families || families[family] is not JsonObject expected)
            throw new InvalidOperationException($"The {id} recipe has no frozen derived digests for {family}. ActualDerived={actual}");
        if (expected.Deserialize<RendererPerfDerivedDigests>(RendererPerfRecipe.Json) != derived)
            throw new InvalidOperationException($"The {id} derived construction differs for {family}. ActualDerived={actual}");
    }

    static string Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is JsonObject left && actual is JsonObject right)
        {
            foreach (string key in left.Select(pair => pair.Key).Union(right.Select(pair => pair.Key)))
                if (RendererPerfRecipe.Digest(left[key]) != RendererPerfRecipe.Digest(right[key]))
                    return Difference(left[key], right[key], path + "." + key);
        }
        if (expected is JsonArray a && actual is JsonArray b)
        {
            if (a.Count != b.Count) return path + ".Length";
            for (int i = 0; i < a.Count; i++)
                if (RendererPerfRecipe.Digest(a[i]) != RendererPerfRecipe.Digest(b[i]))
                    return Difference(a[i], b[i], path + "[" + i + "]");
        }
        return path;
    }
}
