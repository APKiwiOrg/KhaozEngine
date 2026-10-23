using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Caller policy covered by the provenance byte digest. Each present family is a closed, complete profile.</summary>
internal sealed record SceneLimitsProfile(MapSceneLimits Scene, MapRenderBudgets Residency)
{
    internal MapQueryLimits Queries { get; init; } = new();
    internal bool HasQueryProfile { get; init; }
    internal long? MaxProofBytes { get; init; }
    internal GltfDecodeLimits? Decode { get; init; }
    internal MapRenderAssetLimits? Assets { get; init; }
    internal static IReadOnlyDictionary<string, Type> Families { get; } = new Dictionary<string, Type>
    {
        ["scene"] = typeof(MapSceneLimits),
        ["residency"] = typeof(MapRenderBudgets),
        ["queries"] = typeof(MapQueryLimits),
        ["decode"] = typeof(GltfDecodeLimits),
        ["assets"] = typeof(MapRenderAssetLimits),
        ["foliage"] = typeof(MapFoliageLimits),
        ["foliageContext"] = typeof(MapFoliageContextLimits),
        ["water"] = typeof(MapWaterCompileLimits),
        ["refinement"] = typeof(MapRefinementLimits),
    };
    internal static IReadOnlyList<ParameterInfo> Fields(Type type) => Constructor(type).GetParameters()
        .Where(p => p.ParameterType == typeof(int) || p.ParameterType == typeof(long)).ToArray();
    static ConstructorInfo Constructor(Type type) => type.GetConstructors().Single();
    internal static string Field(ParameterInfo parameter) => JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!);

    internal static SceneLimitsProfile Read(JsonElement json)
    {
        Require(json.ValueKind == JsonValueKind.Object, "invalid scene limits profile");
        Require(json.TryGetProperty("scene", out _) && json.TryGetProperty("residency", out _), "incomplete scene limits profile");
        Shape(json, Families.Keys.Append("maxProofBytes").Where(name => json.TryGetProperty(name, out _)).ToArray());
        var scene = ReadGroup<MapSceneLimits>(json.GetProperty("scene"));
        var residency = ReadGroup<MapRenderBudgets>(json.GetProperty("residency"));
        var refinement = Optional<MapRefinementLimits>(json, "refinement");
        var queries = Optional<MapQueryLimits>(json, "queries") ?? new();
        if (refinement is not null) queries = queries with { Refinement = refinement };
        var foliage = Optional<MapFoliageLimits>(json, "foliage");
        var context = Optional<MapFoliageContextLimits>(json, "foliageContext");
        var water = Optional<MapWaterCompileLimits>(json, "water");
        if (refinement is not null) water = (water ?? new()) with { Reads = refinement };
        if (foliage is not null || context is not null || water is not null)
            scene = scene with { Preparation = new(foliage, context, water) };
        return new(scene, residency)
        {
            HasQueryProfile = json.TryGetProperty("queries", out _) || refinement is not null,
            MaxProofBytes = json.TryGetProperty("maxProofBytes", out _) ? Number(json, "maxProofBytes", long.MaxValue) : null,
            Queries = queries,
            Decode = Optional<GltfDecodeLimits>(json, "decode"),
            Assets = Optional<MapRenderAssetLimits>(json, "assets"),
        };
    }

    static T? Optional<T>(JsonElement json, string family) where T : class =>
        json.TryGetProperty(family, out var group) ? ReadGroup<T>(group) : null;
    static T ReadGroup<T>(JsonElement json)
    {
        var constructor = Constructor(typeof(T));
        Shape(json, Fields(typeof(T)).Select(Field).ToArray());
        return (T)constructor.Invoke(constructor.GetParameters().Select(p => p.ParameterType == typeof(int)
            ? (object)(int)Number(json, Field(p), int.MaxValue) : p.ParameterType == typeof(long)
                ? Number(json, Field(p), long.MaxValue) : p.DefaultValue).ToArray());
    }
    static long Number(JsonElement json, string name, long max)
    {
        var value = json.GetProperty(name);
        Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0 && number <= max,
            "invalid scene limits value " + name);
        return value.GetInt64();
    }
}
