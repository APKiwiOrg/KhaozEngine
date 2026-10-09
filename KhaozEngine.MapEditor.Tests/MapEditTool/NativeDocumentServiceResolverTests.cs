using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEdit;
using Xunit;

namespace KhaozEngine.Tests.MapEditTool;

public sealed class NativeDocumentServiceResolverTests
{
    static readonly MapResolveOptions AuthoredOptions =
        new("khaozengine.mapedit.authored-support", 1, "mapdoc-resolver-v2-authored-bindings-v1", ResolverVersion: 2);

    [Fact]
    public void ResolverV2_SessionOpensSavesAndValidatesAuthoredSupport()
    {
        using var fixture = new ResolverFixture(2);
        var session = new MapEditSession();
        session.Open(fixture.Path);
        MapResolvedDocument expected = MapResolverV2.Resolve(fixture.Document, fixture.Closure,
            MapDocumentSurfaceSource.Capture(fixture.Document), AuthoredOptions).Document;
        Assert.Equal(5f, Assert.Single(expected.Placements).Transform.Position.Y);
        Assert.Equal(expected.AuthoredHash, session.Summary().Native!.AuthoredHash);
        Assert.True(session.Validate().Valid);
        Assert.True(session.Save().Saved);
        Assert.Equal(MapDocumentFile.SaveText(fixture.Document), File.ReadAllText(fixture.Path));

        var reopened = new MapEditSession();
        reopened.Open(fixture.Path);
        Assert.Equal(expected.AuthoredHash, reopened.Summary().Native!.AuthoredHash);
        MapResolvedDocument resolved = NativeDocumentService.ValidateComplete(fixture.Document,
            fixture.Source, AuthoredOptions);
        Assert.Equal(expected.AuthoredHash, resolved.AuthoredHash);
        Assert.Equal(5f, Assert.Single(resolved.Placements).Transform.Position.Y);
    }

    [Fact]
    public void ResolverV1_SessionKeepsItsAnalyticOptionsAndResolution()
    {
        using var fixture = new ResolverFixture(1);
        Assert.Equal(new MapResolveOptions("khaozengine.mapedit.analytic-support", 1,
            "mapruntime-buildfield-default-registry-v1"), NativeDocumentService.SessionOptions);
        var field = MapRuntime.BuildField(fixture.Document, MapDocRegistry.CreateDefault());
        MapResolvedDocument expected = MapResolver.Resolve(fixture.Document, fixture.Closure,
            field.SampleHeight, NativeDocumentService.SessionOptions);
        MapResolvedDocument resolved = NativeDocumentService.ValidateComplete(fixture.Document,
            fixture.Source, NativeDocumentService.SessionOptions);
        Assert.Equal(expected.AuthoredHash, resolved.AuthoredHash);
        Assert.Equal(Assert.Single(expected.Placements).Transform, Assert.Single(resolved.Placements).Transform);
        var session = new MapEditSession();
        session.Open(fixture.Path);
        Assert.Equal(expected.AuthoredHash, session.Summary().Native!.AuthoredHash);
        Assert.True(session.Save().Saved);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void ResolverMismatch_RefusesBeforeReadingResources(int documentVersion, int optionsVersion)
    {
        using var fixture = new ResolverFixture(documentVersion);
        var source = new UnreadableSource();
        MapResolveOptions options = NativeDocumentService.SessionOptions with { ResolverVersion = optionsVersion };
        string message = Assert.Throws<MapDocumentException>(() =>
            NativeDocumentService.ValidateComplete(fixture.Document, source, options)).Message;
        Assert.Equal(0, source.Reads);
        Assert.Contains($"(1, {documentVersion})", message);
        Assert.Contains($"options resolver version {optionsVersion}", message);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 3, 3)]
    public void UnknownIdentity_RefusesNamingTheIdentityBeforeReadingResources(int payload, int resolver, int optionsVersion)
    {
        using var fixture = new ResolverFixture(1);
        fixture.Document.ResolverIdentity = new(payload, resolver);
        var source = new UnreadableSource();
        string message = Assert.Throws<MapDocumentException>(() => NativeDocumentService.ValidateComplete(
            fixture.Document, source, NativeDocumentService.SessionOptions with { ResolverVersion = optionsVersion })).Message;
        Assert.Equal(0, source.Reads);
        Assert.Contains($"({payload}, {resolver})", message);
    }

    sealed class UnreadableSource : IMapAssetSource
    {
        internal int Reads { get; private set; }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            Reads++;
            throw new InvalidOperationException("Resolver refusal must precede resource reads.");
        }
    }

    sealed class ResolverFixture : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "native-resolver-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "world.map.json");
        internal MapDocument Document { get; }
        internal MapDirectoryAssetSource Source { get; }
        internal MapAssetClosure Closure { get; }

        internal ResolverFixture(int resolver)
        {
            Directory.CreateDirectory(_directory);
            try
            {
                MapAssetRef mesh = Write("mesh", "mesh.bin", "mesh");
                MapAssetRef root = Write("kit", "kit.json", $$$"""
                    {"payloadVersion":1,"assets":[{"id":"prop","meshResourceId":"mesh",
                     "supportResourceIds":[],"materialResourceIds":[],"lodResourceIds":[],"lightResourceIds":[],
                     "sourceUnitsToMetres":1,"renderBounds":{"min":{"x":-1,"y":0,"z":-1},"max":{"x":1,"y":2,"z":1}},
                     "source":"authored fixture","license":"CC0","category":"prop","textured":false}],
                     "resources":[{"reference":{"id":"mesh","path":"mesh.bin","sha256":"{{{mesh.Sha256}}}","payloadVersion":1},
                     "kind":"Mesh","dependencies":[]}]}
                    """);
                Document = new MapDocument
                {
                    Id = "resolver-session",
                    Bounds = new() { MinX = -10, MinZ = -10, MaxX = 10, MaxZ = 10 },
                    PlayableBounds = new() { MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 },
                    ResolverIdentity = new(1, resolver),
                    SupportRecipe = resolver == 2 ? MapSupportRecipe.AuthoredBindingsV2 : MapSupportRecipe.LegacyXzCallbackV1,
                    NativeAssets = new() { root },
                };
                Document.Terrain.Biomes.Add(new MapBiomeBand());
                Document.Placements.Add(new MapPlacement
                {
                    Id = "prop",
                    Kind = "prop",
                    AssetId = "prop",
                    X = 0.5f,
                    Z = 0.5f,
                    SupportBinding = resolver == 2 ? new(MapSupportBindingKind.Surface, "floor", null, null, null, null) : null,
                });
                if (resolver == 2)
                {
                    var patch = new MapSurfacePatch
                    {
                        Key = new("floor", 0, 0),
                        Width = 1,
                        Depth = 1,
                        Heights = new[] { 500, 500, 500, 500 },
                        Cells = new MapSurfaceCell[1],
                        Presence = new[] { 1UL },
                    };
                    var surface = new MapSurfaceRef("floor", new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, 0),
                        MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, "");
                    Document.Surfaces.Patches.Add(patch.Key, patch);
                    Document.Surfaces.Refs.Add(surface with
                    {
                        SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                            Document.Surfaces.Patches.Select(p => new System.Collections.Generic.KeyValuePair<MapPatchKey, string>(
                                p.Key, MapSurfaceSemantics.PatchDigest(p.Value)))),
                    });
                }
                Source = new MapDirectoryAssetSource(_directory);
                Closure = MapAssetClosure.Load(Document.NativeAssets, Source);
                MapDocumentFile.Save(Document, Path);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        MapAssetRef Write(string id, string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(System.IO.Path.Combine(_directory, path), bytes);
            return new(id, path, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1);
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
