using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeResolverTests
{
    [Fact]
    public void NativeResolver_ReorderingAndCallerEditsCannotChangeSnapshot()
    {
        var f = NativeResolverFixtures.Create();
        var a = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, f.Options);
        f.Document.Placements.Reverse();
        f.Document.NativeAssets.Reverse();
        var b = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, f.Options);
        Assert.Equal(a.AuthoredHash, b.AuthoredHash);
        Assert.Equal(new[] { "a", "b" }, a.Placements.Select(p => p.PlacementId));
        Assert.Equal(a.Placements.Select(p => p.Transform), b.Placements.Select(p => p.Transform));
        f.Document.Placements.Single(p => p.Id == "a").Tags[0] = "edited";
        f.Document.Bounds.MinX = -200;
        f.Document.PlayableBounds!.MinX = -90;
        Assert.Equal("first", a.Placements[0].Tags[0]);
        Assert.Equal(-100, a.StorageBounds.MinX);
        Assert.Equal(-100, a.PlayableBounds.MinX);
        Assert.False(a.Placements is MapResolvedPlacement[]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)a.Placements[0].Tags)[0] = "mutable");
        Assert.NotEqual(a.AuthoredHash, MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options));
    }

    [Fact]
    public void NativeResolver_TwoHeadsKeepIdentityFieldsAndOnlySnapNullY()
    {
        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        var first = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 7, f.Options);
        var g = NativeResolverFixtures.Create();
        g.Document.Placements[0].Y = null;
        var second = MapResolver.Resolve(g.Document, g.Assets, (_, _) => 7, g.Options);
        Assert.Equal(first.AuthoredHash, second.AuthoredHash);
        Assert.Equal(f.Assets.Hash, g.Assets.Hash);
        Assert.Equal(new[] { ("a", "building_inn", "tree", (long?)11, 7f), ("b", "scenery", "tree", (long?)12, 2.5f) },
            first.Placements.Select(p => (p.PlacementId, p.Kind, p.AssetId, p.NumericId, p.Transform.Position.Y)));
        Assert.Equal(first.Placements.Select(p => p.Transform), second.Placements.Select(p => p.Transform));
        Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(f.Document, f.Assets, (_, _) => float.NaN, f.Options));
    }

    [Fact]
    public void NativeResolver_SupportCallbackCannotChangePublishedAuthoredInputs()
    {
        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        string hash = MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options);
        var result = MapResolver.Resolve(f.Document, f.Assets, (_, _) =>
        {
            f.Document.Placements[1].X = 90;
            f.Document.Placements[1].Tags.Clear();
            f.Document.Bounds.MaxX = 200;
            return 7;
        }, f.Options);
        Assert.Equal(hash, result.AuthoredHash);
        Assert.Equal(70, result.Placements[1].Transform.Position.X);
        Assert.Equal(new[] { "first", "second" }, result.Placements[1].Tags);
        Assert.Equal(100, result.StorageBounds.MaxX);
    }

    [Fact]
    public void NativeTransform_ComposesYawPositionAndScaleOnce()
    {
        var parent = new MapTransform(new Vector3(0.23f, 1.5f, 0.17f), 0.371f, 1.137f);
        var local = new MapTransform(new Vector3(2, 0.4f, 3), 0.2f, 0.8f);
        var composed = MapTransform.Compose(parent, local);
        Assert.Equal(parent.TransformPoint(local.Position), composed.Position);
        Assert.Equal(0.371f + 0.2f, composed.YawRadians);
        Assert.Equal(1.137f * 0.8f, composed.Scale);
        var quarterTurn = new MapTransform(new Vector3(10, 20, 30), MathF.PI / 2, 2);
        Assert.True(Vector3.Distance(new Vector3(10, 20, 28), quarterTurn.TransformPoint(Vector3.UnitX)) < 0.00001f);
        Assert.True(Vector3.Distance(parent.TransformPoint(local.TransformPoint(Vector3.One)), composed.TransformPoint(Vector3.One)) < 0.00001f);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("resolver")]
    [InlineData("missing")]
    [InlineData("asset")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("scale")]
    [InlineData("y")]
    [InlineData("scale-infinity")]
    [InlineData("bounds")]
    public void NativeResolver_RefusesInvalidNativeInputs(string fault)
    {
        var f = NativeResolverFixtures.Create();
        switch (fault)
        {
            case "payload": f.Document.ResolverIdentity = new(2, 1); break;
            case "resolver": f.Document.ResolverIdentity = new(1, 2); break;
            case "missing": f.Document.ResolverIdentity = null; break;
            case "asset": f.Document.Placements[0].AssetId = "absent"; break;
            case "nan": f.Document.Placements[0].Yaw = float.NaN; break;
            case "infinity": f.Document.Placements[0].X = float.PositiveInfinity; break;
            case "scale": f.Document.Placements[0].Scale = 0; break;
            case "y": f.Document.Placements[0].Y = float.NaN; break;
            case "scale-infinity": f.Document.Placements[0].Scale = float.PositiveInfinity; break;
            case "bounds": f.Document.Bounds.MinX = float.NegativeInfinity; break;
        }
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options));
        Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0, f.Options));
    }

    [Theory]
    [InlineData("subset")]
    [InlineData("superset")]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("digest")]
    [InlineData("version")]
    public void NativeIdentity_RequiresExactInputRoots(string fault)
    {
        var f = NativeResolverFixtures.Create();
        var root = f.Document.NativeAssets[0];
        switch (fault)
        {
            case "subset": f.Document.NativeAssets.RemoveAt(0); break;
            case "superset": f.Document.NativeAssets.Add(f.Assets.GetResource("mesh").Reference); break;
            case "duplicate": f.Document.NativeAssets.Add(root); break;
            case "path": f.Document.NativeAssets[0] = root with { Path = "other.json" }; break;
            case "digest": f.Document.NativeAssets[0] = root with { Sha256 = new string('a', 64) }; break;
            case "version": f.Document.NativeAssets[0] = root with { PayloadVersion = 2 }; break;
        }
        Assert.Throws<MapDocumentException>(() => MapAuthoredIdentity.Compute(f.Document, f.Assets, f.Options));
        Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0, f.Options));
    }

    [Fact]
    public void NativeClosure_PreservesRootsBeforeSourceReads()
    {
        var f = NativeAssetFixtures.Valid();
        var roots = f.Roots.ToList();
        var closure = MapAssetClosure.Load(roots, new MutatingSource(f.Source, roots));
        Assert.Empty(roots);
        Assert.Equal(f.Roots, closure.Roots);
        Assert.Throws<NotSupportedException>(() => ((IList<MapAssetRef>)closure.Roots).Clear());
    }

    [Fact]
    public void NativeResolver_PublishesTheSameVerifiedClosure()
    {
        var f = NativeResolverFixtures.Create();
        var resolved = MapResolver.Resolve(f.Document, f.Assets, (_, _) => 0f, f.Options);
        Assert.Same(f.Assets, resolved.AssetClosure);
        Assert.Same(f.Assets.Assets, resolved.Assets);
    }

    sealed class MutatingSource(IMapAssetSource source, List<MapAssetRef> roots) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            roots.Clear();
            return source.Read(reference);
        }
    }
}
