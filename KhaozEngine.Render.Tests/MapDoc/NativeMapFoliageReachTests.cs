using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed partial class NativeMapFoliageTests
{
    [Fact]
    public void AdjacentChunkReachingWindow_IsPreparedAndRecorded()
    {
        using var fixture = new Fixture(closure: BroadModels());
        var source = fixture.Scene.Source;
        var scope = fixture.Scene.Scope with { DrawBounds = new(31, -2, 1, 31.5, 4, 2), MotionPaddingMetres = 0 };
        var options = Options();
        options.WindStrength = 0;
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), Extents(fixture.Closure, 0));
        var scene = source.Acquire(scope).Scene!;
        var captured = MapFoliageGenerationContext.CaptureWindow(source, scope);
        Assert.Equal(MapFoliageStatus.Complete, captured.Status);
        var routing = MapPlacementRouting.Create(scene, fixture.PlacementPolicy, new HashSet<string>()).Routing!;
        using var batch = NativeMapFoliage.Prepare(scene, captured.Context!, fixture.Assets, policy, routing);
        var expected = MapFoliageDistribution.Generate(fixture.Context, "cover", new(1, 0));
        Assert.NotEmpty(expected.Samples);
        Assert.All(expected.Samples, sample => Assert.Contains(sample.Key, batch.GeneratedIdentities));
        var recorder = new MapDrawRecorder();
        var stamp = fixture.Stamp with { ScopeDigest = scope.Digest, PolicyDigest = routing.PolicyDigest };
        batch.Register(stamp, recorder);
        batch.Record(Frame with { Focus = new(WorldFrame.Origin, new(31.5f, 1, 1.5f)) }, recorder);
        Assert.Contains(recorder.Snapshot().Draws, draw => draw.Resource.Key.OwnerId == "cover/1/0");
    }

    static MapAssetClosure BroadModels()
    {
        var original = NativeRenderAssetFixtures.Create("foliage");
        var root = original.Roots.Single();
        var bytes = original.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(bytes[root.Id])!;
        // The real one-unit meshes now span 64 metres, with matching descriptor units and extent proofs.
        foreach (var asset in manifest["assets"]!.AsArray()) asset!["sourceUnitsToMetres"] = 64;
        bytes[root.Id] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        root = root with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes[root.Id])) };
        return MapAssetClosure.Load([root], new BroadAssetBytes(bytes));
    }

    sealed class BroadAssetBytes(IReadOnlyDictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }
}
