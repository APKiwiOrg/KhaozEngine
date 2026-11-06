using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEdit.Tools;
using KhaozEngine.MapEditor;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.DependencyInjection;

namespace KhaozEngine.Tests.MapEditTool;

[Collection(McpWireCollection.Name)]
public sealed class SessionPublicationRefusalTests
{
    [Fact]
    public async Task ToolGuard_RetainsTheOriginalRefusalAndPassesThroughExistingMcpErrors()
    {
        var refusal = new MapDocumentException(MapDocumentRefusal.ToolNativeDocumentRequired, "caller payload");
        var wrapped = Assert.Throws<McpException>(() => ToolGuard.Guard<int>(() => throw refusal));
        Assert.Same(refusal, wrapped.InnerException);
        var existing = new McpException("caller payload");
        Assert.Same(existing, Assert.Throws<McpException>(() => ToolGuard.Guard<int>(() => throw existing)));
        await using var harness = await McpWire.StartAsync(server => server.WithTools([
            McpServerTool.Create((Func<int>)(() => ToolGuard.Guard<int>(() => throw refusal)), new() { Name = "caller_payload" })]));
        var result = await harness.Client.CallAsync("caller_payload", new Dictionary<string, object?>());
        Assert.True(result.IsError == true);
        Assert.Contains("caller payload", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutableResourcePublication_PreservesExistingBytesAndAdmitsRepair(bool blockedDirectory) => TiledDocFixture.InDirectory(root =>
    {
        byte[] bytes = [1, 2, 3];
        var writer = new MapAssetFileWriter(root);
        var address = MapAssetFileWriter.Address(bytes, "bin");
        string target = Path.Combine(root, address.Path);
        string obstruction = blockedDirectory ? Path.Combine(root, MapAssetFileWriter.ContentDirectory) : target;
        Directory.CreateDirectory(Path.GetDirectoryName(obstruction)!);
        File.WriteAllBytes(obstruction, [9, 8, 7]);
        string[] before = Files(root);
        var error = Assert.Throws<MapDocumentException>(() => writer.WriteResource(bytes, "bin"));
        Assert.Equal(blockedDirectory ? MapDocumentRefusal.ToolResourceWriteFailed : MapDocumentRefusal.ToolImmutableResourceConflict, error.Reason);
        Assert.Equal(target, error.Path);
        Assert.Equal(address.Id, error.ResourceId);
        if (blockedDirectory) Assert.NotNull(error.InnerException);
        else Assert.Equal(address.Sha256, error.ExpectedHash);
        Assert.Equal(before, Files(root));
        File.Delete(obstruction);
        var published = writer.WriteResource(bytes, "bin");
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(root, published.Path)));
    });

    [Fact]
    public void ManifestSerialization_IdentifiesTheRootAndWritesNothingBeforeRepair() => TiledDocFixture.InDirectory(root =>
    {
        var manifest = new MapAssetManifestDoc
        {
            Resources = [new() { Reference = new("mesh", "mesh.bin", Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])), 1), Kind = (MapResourceKind)int.MaxValue }],
        };
        var writer = new MapAssetFileWriter(root);
        string[] before = Files(root);
        var error = Assert.Throws<MapDocumentException>(() => writer.WriteManifest(manifest, "manifest-root"));
        Assert.Equal(MapDocumentRefusal.ToolManifestSerializationFailed, error.Reason);
        Assert.Equal("manifest-root", error.ResourceId);
        Assert.NotNull(error.InnerException);
        Assert.Equal(before, Files(root));
        manifest.Resources[0].Kind = MapResourceKind.Mesh;
        var published = writer.WriteManifest(manifest, "manifest-root");
        Assert.True(File.Exists(Path.Combine(root, published.Path)));
    });

    [Fact]
    public void ManifestSerialization_PreservesAKnownVectorRefusalAndAdmitsItsRepair() => TiledDocFixture.InDirectory(root =>
    {
        var manifest = new MapAssetManifestDoc
        {
            Resources = [new() { Reference = new("mesh", "mesh.bin", Convert.ToHexStringLower(SHA256.HashData([1, 2, 3])), 1), Kind = MapResourceKind.Mesh }],
            Assets = [new() { Id = "prop", MeshResourceId = "mesh", SourceUnitsToMetres = 1,
                RenderBounds = new(new(float.NaN, 0, 0), Vector3.One), Source = "authored", License = "CC0" }],
        };
        var writer = new MapAssetFileWriter(root);
        string[] before = Files(root);
        var error = Assert.Throws<MapDocumentException>(() => writer.WriteManifest(manifest, "manifest-root"));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("manifest-root", error.ResourceId);
        Assert.Equal("$.x", error.FieldPath);
        Assert.True(double.IsNaN(error.ActualNumber!.Value));
        Assert.NotNull(error.InnerException);
        Assert.Equal(before, Files(root));
        manifest.Assets[0].RenderBounds = new(Vector3.Zero, Vector3.One);
        Assert.NotNull(writer.WriteManifest(manifest, "manifest-root"));
    });

    [Theory]
    [InlineData("analytic", MapDocumentRefusal.ToolNativeDocumentRequired)]
    [InlineData("closure", MapDocumentRefusal.AssetClosureRequired)]
    [InlineData("path", MapDocumentRefusal.SchemaValueRequired)]
    public void NativeAssetBinding_IdentifiesTheFirstMissingRequirement(string fault, MapDocumentRefusal reason)
    {
        var fixture = PrefabFixtures.Create();
        var document = fault == "analytic" ? new MapDocument { Id = "analytic" } : fixture.Document;
        var assets = fault == "closure" ? null : fixture.Assets;
        string? path = fault == "path" ? null : "/native.map.json";
        string before = System.Text.Json.JsonSerializer.Serialize(document);
        var error = Assert.Throws<MapDocumentException>(() => MapSessionAdmission.RequireNativeAssetsForTests(document, assets, path));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(document.Id, error.DocumentId);
        Assert.Equal("WithNativeAssets", error.Operation);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(document));
        MapSessionAdmission.RequireNativeAssetsForTests(fixture.Document, fixture.Assets, "/native.map.json");
    }

    [Fact]
    public void NativeMutationWithoutClosure_RefusesBeforeAnySessionWork()
    {
        var assets = PrefabFixtures.Create().Assets;
        var error = Assert.Throws<MapDocumentException>(() => MapSessionAdmission.RequireNativeMutationForTests(null, false, "document"));
        Assert.Equal(MapDocumentRefusal.AssetClosureRequired, error.Reason);
        Assert.Equal("document", error.DocumentId);
        MapSessionAdmission.RequireNativeMutationForTests(assets, false, "document");
    }

    [Fact]
    public void NestedNativeMutation_PreservesSessionAndHistoryAndAdmitsAnUnnestedEdit()
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => session.Mutate((_, _) =>
            session.Mutate((document, _) => { document.DisplayName = "edited"; return 0; }, false), false));
        Assert.Equal(MapDocumentRefusal.ToolNestedMutationUnsupported, error.Reason);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.Mutate((document, _) => { document.DisplayName = "edited"; return 0; }, false);
        Assert.Equal("edited", session.WithDocument((document, _) => document.DisplayName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCommand_RequiresExactlyOneStagedMutationAndCanRetry(bool twice)
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        var command = Move();
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (twice) session.Mutate((document, registry) =>
            {
                session.ApplyNative(command, document, registry);
                return session.ApplyNative(command, document, registry);
            }, true);
            else session.WithDocument((document, registry) => session.ApplyNative(command, document, registry));
        });
        Assert.Equal(MapDocumentRefusal.ToolStagedMutationRequired, error.Reason);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.Mutate((document, registry) => session.ApplyNative(command, document, registry), true);
        Assert.NotEqual(before, PrefabEditorFixtures.State(session));
    }

    [Fact]
    public void PublicationClosureMismatch_PreservesSessionAndAdmitsItsCapturedClosure()
    {
        var fixture = PrefabFixtures.Create();
        var session = PrefabEditorFixtures.Session(fixture);
        var wrong = MapAssetClosure.Load([], new MapDirectoryAssetSource(Path.GetTempPath()));
        var command = Move();
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => session.ApplyNativeAssets(command, wrong));
        Assert.Equal(MapDocumentRefusal.ToolPublishedClosureMismatch, error.Reason);
        Assert.Equal(wrong.Hash, error.ExpectedHash);
        Assert.Equal(fixture.Assets.Hash, error.ActualHash);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        session.ApplyNativeAssets(command, fixture.Assets);
        Assert.NotEqual(before, PrefabEditorFixtures.State(session));
    }

    [Theory]
    [InlineData("rooted", MapDocumentRefusal.ToolPublicationPathInvalid)]
    [InlineData("escape", MapDocumentRefusal.ToolPublicationPathInvalid)]
    [InlineData("reserved", MapDocumentRefusal.AssetStoragePathReserved)]
    public void PublicationPath_IdentifiesTheFailedBoundaryAndAdmitsAResourcePath(string fault, MapDocumentRefusal reason)
    {
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        string path = fault switch { "rooted" => Path.GetFullPath("outside.json"), "escape" => "../outside.json", _ => Path.GetFileName(session.DocumentPath!) };
        var before = PrefabEditorFixtures.State(session);
        var error = Assert.Throws<MapDocumentException>(() => session.RequireNativeResourcePath(path));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(path, error.Path);
        Assert.Equal(session.DocumentPath, error.StoragePath);
        Assert.Equal(before, PrefabEditorFixtures.State(session));
        Assert.True(Path.IsPathFullyQualified(session.RequireNativeResourcePath("exports/accepted.json")));
    }

    [Fact]
    public void NativeCommandOnAnalyticDocument_WritesNothingThenAcceptsANativeDocument() => TiledDocFixture.InDirectory(root =>
    {
        using var fixture = new NativePlacementHistoryFixture();
        var session = new MapEditSession();
        session.Create(Path.Combine(root, "analytic.json"), "analytic", "Analytic", -8, -8, 8, 8);
        var command = new AllocateNativePlacementCommand(fixture.NewProp("fresh"), true);
        string before = session.WithDocument((document, registry) => MapDocumentFile.SaveText(document, registry));
        var error = Assert.Throws<MapDocumentException>(() => session.ExecuteNativeCommand(command));
        Assert.Equal(MapDocumentRefusal.ToolNativeDocumentRequired, error.Reason);
        Assert.Equal(before, session.WithDocument((document, registry) => MapDocumentFile.SaveText(document, registry)));
        fixture.Session.ExecuteNativeCommand(command);
        Assert.Contains(fixture.SessionDocument.Placements, p => p.Id == "fresh");
    });

    static PrefabEditCommand Move() => new(new MapMovePrefab("a", new(new Vector3(1, 0, 0), 0, 1)));
    static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray();
}
