using System;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>Water body ids in edit effects: described only when present, so every existing description keeps its
/// bytes.</summary>
public class MapWaterEffectsTests
{
    [Fact]
    public void Describe_AddsWaterOnlyWhenPresent()
    {
        var plain = new MapNativeEditEffects(null, null, Array.Empty<MapPatchKey>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<MapDigestChange>(), MapNativeInvalidation.Terrain);
        Assert.Equal("{\"oldBounds\":null,\"newBounds\":null,\"patches\":[],\"spaceIds\":[],\"dependencyIds\":[],\"digestChanges\":[],\"invalidates\":1}",
            plain.Describe());
        string water = (plain with { WaterBodyIds = new[] { "b", "a" }, Invalidates = MapNativeInvalidation.Medium }).Describe();
        Assert.Contains("\"dependencyIds\":[],\"waterBodyIds\":[\"a\",\"b\"],\"digestChanges\"", water, StringComparison.Ordinal);
        Assert.Contains("\"invalidates\":128", water, StringComparison.Ordinal);
    }
}
