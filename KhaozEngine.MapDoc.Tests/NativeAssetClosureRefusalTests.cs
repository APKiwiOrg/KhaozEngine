using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeAssetClosureRefusalTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("missing")]
    public void SourceReadFailure_KeepsTheResourceReadBoundaryAndConcreteCause(string fault)
    {
        // M7 X6 / D24: malformed callback data must keep the resource-read catch contract.
        var (roots, source) = NativeAssetFixtures.Valid();
        Exception cause = fault switch
        {
            "malformed" => new CallbackDataException(),
            "io" => new IOException(),
            "access" => new UnauthorizedAccessException(),
            _ => new KeyNotFoundException(),
        };
        var refusal = Assert.Throws<MapDocumentException>(() =>
            MapAssetClosure.Load(roots, new FailingSource(source, cause)));
        Assert.Equal(MapDocumentRefusal.AssetPayloadUnreadable, refusal.Reason);
        Assert.Equal("root-a", refusal.ResourceId);
        Assert.Equal(roots[0].Path, refusal.Path);
        Assert.Equal(roots[0].Sha256, refusal.ExpectedHash);
        Assert.Same(cause, refusal.InnerException);
        Assert.Equal("tree", Assert.Single(MapAssetClosure.Load(roots, source).Assets).Id);
    }

    [Theory]
    [InlineData("length", null, "mesh")]
    [InlineData("bytes", null, "mesh")]
    [InlineData("prefab", MapResourceKind.Prefab, "hut")]
    [InlineData("support", MapResourceKind.Surface, "deck-support")]
    [InlineData("light", MapResourceKind.Light, "glow-light")]
    public void BorrowingUncapturedPayload_IdentifiesTheRequestedResource(string operation, MapResourceKind? kind, string present)
    {
        var closure = PrefabFixtures.Create("attached").Assets;
        object Read(string id) => operation switch
        {
            "length" => closure.ResourceByteLength(id),
            "bytes" => closure.BorrowVerifiedResourceBytes(id),
            "prefab" => closure.BorrowPrefab(id),
            "support" => closure.BorrowSupport(id),
            _ => closure.BorrowLight(id),
        };
        var refusal = Assert.Throws<MapDocumentException>(() => Read("absent"));
        Assert.Equal(MapDocumentRefusal.AssetPayloadNotCaptured, refusal.Reason);
        Assert.Equal("absent", refusal.ResourceId);
        Assert.Equal(kind, refusal.ExpectedResourceKind);
        _ = Read(present);
    }

    [Fact]
    public void MissingPrefabLookup_IdentifiesTheRequiredKind()
    {
        var closure = PrefabFixtures.Create().Assets;
        var refusal = Assert.Throws<MapDocumentException>(() => closure.GetPrefab("absent"));
        Assert.Equal(MapDocumentRefusal.ReferenceMissing, refusal.Reason);
        Assert.Equal("absent", refusal.ResourceId);
        Assert.Equal(MapResourceKind.Prefab, refusal.ExpectedResourceKind);
        Assert.Equal("hut", closure.GetPrefab("hut").Id);
    }

    sealed class FailingSource(IMapAssetSource source, Exception cause) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) =>
            reference.Id == "root-a" ? throw cause : source.Read(reference);
    }

    sealed class CallbackDataException() : MalformedDataException("malformed callback data");
}
