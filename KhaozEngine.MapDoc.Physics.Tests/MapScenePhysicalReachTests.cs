using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Physics;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.MapDoc.Physics.NativeCellHandoffFixtures;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapScenePhysicalReachTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShapeKindsOutsideCosmeticBounds_AreDiscovered(int kind)
    {
        PhysicsShape shape = kind switch
        {
            0 => new CylinderShape(10, 2),
            1 => new ConvexHullShape([new(-10, 0, 0), new(10, 0, 0), new(0, 1, 1)]),
            2 => new TriangleMeshShape([new(-10, 0, 0), new(10, 0, 0), new(0, 1, 1)], [0, 1, 2]),
            _ => new CompoundShape([new(new BoxShape(Vector3.One), new Pose(new(-10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f)))]),
        };
        using var fixture = Create();
        var assets = WithShape(fixture.Assets, shape, false);
        fixture.Document.NativeAssets = assets.Roots.ToList();
        fixture.Document.Placements.Add(new() { Id = "reaching", Kind = "crate", AssetId = "crate", X = 70, Y = 5, Z = 10 });
        var acquired = NativeSceneFixtures.Open(fixture with { Assets = assets }, false).Acquire(Domain(0));
        Assert.True(acquired.Witness.CompleteForScope);
        Assert.Contains(acquired.Scene!.Placements, p => p.PlacementId == "reaching");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShapeBeyondCosmeticBounds_IsDiscoveredAtTheCellBoundary(bool tiled, bool selection)
    {
        using var fixture = Create();
        var assets = WithShape(fixture.Assets, new BoxShape(new(10, 1, 1)), selection);
        fixture.Document.NativeAssets = assets.Roots.ToList();
        fixture.Document.Placements.Add(new() { Id = "reaching", Kind = "crate", AssetId = "crate", X = 70, Y = 5, Z = 10 });
        var source = NativeSceneFixtures.Open(fixture with { Assets = assets }, tiled);
        var scene = source.Acquire(Domain(0));
        Assert.True(scene.Witness.CompleteForScope);
        Assert.Contains(scene.Scene!.Placements, p => p.PlacementId == "reaching");
        if (selection) return;
        using var lease = new MapSimulationCoverage(source, assets, Options).Acquire([Domain(0)]);
        Assert.Equal(MapSceneStatus.CompleteForScope, lease.Status);
        var descriptor = Assert.Single(Assert.Single(lease.Worlds).Statics, s => s.OwnerId == "reaching");
        var complete = MapWorldBuilder.Build(fixture.Document, assets, Options);
        Assert.Equal(Assert.Single(complete.Statics, s => s.OwnerId == "reaching").Digest, descriptor.Digest);
    }

    internal static MapAssetClosure WithShape(MapAssetClosure assets, PhysicsShape shape, bool selection)
    {
        var root = assets.Roots.Single();
        var bytes = assets.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        var options = MapJsonTextOptions.CreateSerializer(false);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        MapNativeJson.Configure(options);
        options.Converters.Add(new JsonStringEnumConverter());
        var manifest = JsonSerializer.Deserialize<MapAssetManifestDoc>(bytes[root.Id], options)!;
        byte[] payload = NativeWorldFixtures.ColliderBytes(shape);
        var reference = new MapAssetRef("reach-shape", "assets/reach-shape", Digest(payload), 1);
        bytes.Add(reference.Id, payload);
        manifest.Resources.Add(new() { Reference = reference, Kind = selection ? MapResourceKind.Selection : MapResourceKind.Collider });
        var crate = manifest.Assets.Single(a => a.Id == "crate");
        if (selection) crate.SelectionResourceId = reference.Id;
        else crate.CollisionResourceId = reference.Id;
        bytes[root.Id] = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        return MapAssetClosure.Load([root with { Sha256 = Digest(bytes[root.Id]) }], new AssetBytes(bytes));
    }

    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class AssetBytes(IReadOnlyDictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }
}
