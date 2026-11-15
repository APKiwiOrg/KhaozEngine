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
        // #1723. Refusal paths use the writer's platform-normalized full filesystem path.
        string target = Path.GetFullPath(address.Path, root);
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
    [InlineData("assets", true)]
    [InlineData("assets-checkpoint", true)]
    [InlineData("undo", true)]
    [InlineData("redo", true)]
    [InlineData("bind", false)]
    [InlineData("open", false)]
    [InlineData("open-markers", false)]
    [InlineData("create", false)]
    [InlineData("save", false)]
    [InlineData("window", false)]
    [InlineData("tiled", false)]
    [InlineData("single", false)]
    [InlineData("retile", false)]
    public void PublicationDuringMutation_PreservesSessionAndStorageAndAdmitsAfterCallback(string operation, bool native)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        var fixture = PrefabFixtures.Create();
        var session = PrefabEditorFixtures.Session(fixture);
        string root = Path.GetDirectoryName(session.DocumentPath)!;
        try
        {
            string tiled = Path.Combine(root, "tiled");
            Directory.CreateDirectory(tiled);
            foreach (var resource in fixture.Assets.CapturedResources)
            {
                string target = Path.Combine(tiled, resource.Reference.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, resource.Bytes.ToArray());
            }
            session.ConvertToTiled(tiled);
            session.Mutate((doc, _) => { doc.DisplayName = "Accepted name"; return 0; }, false);
            session.ExecuteNativeCommand(Move());
            session.UndoNative();
            Action publish = operation switch
            {
                "assets" => () => session.ApplyNativeAssets(Move(), fixture.Assets),
                "assets-checkpoint" => () => session.ApplyNativeAssets(Move(), fixture.Assets, () => File.WriteAllText(Path.Combine(root, "checkpoint"), "published")),
                "undo" => () => session.UndoNative(),
                "redo" => () => session.RedoNative(),
                "bind" => () => session.BindNativeAssets(fixture.Assets),
                "open" => () => session.Open(Path.Combine(root, "other.map.json")),
                "open-markers" => () => session.Open(Path.Combine(root, "other.map.json"), null, KhaozEngine.MapDoc.Markers.MapMarkerConsumerOptions.None),
                "create" => () => session.Create(Path.Combine(root, "created.map.json"), "created", "Created", -8, -8, 8, 8),
                "save" => () => session.Save(),
                "window" => () => session.SetWindow(-32, -32, 32, 32, true),
                "tiled" => () => session.ConvertToTiled(root),
                "single" => () => session.ConvertToSingle(Path.Combine(root, "converted.map.json")),
                _ => () => session.Retile(32),
            };
            var before = PrefabEditorFixtures.State(session);
            long version = session.SnapshotVersion;
            string[] files = Files(root);
            string expectedOperation = operation switch
            {
                "assets" or "assets-checkpoint" => "ApplyNativeAssets", "undo" => "UndoNative", "redo" => "RedoNative",
                "bind" => "BindNativeAssets", "open" or "open-markers" => "Open", "create" => "Create",
                "save" => "Save", "window" => "SetWindow", "tiled" => "ConvertToTiled", "single" => "ConvertToSingle", _ => "Retile",
            };
            Action mutate = () => session.Mutate((doc, _) => { doc.DisplayName = "Unpublished name"; publish(); return 0; }, false);
            if (native)
            {
                var error = Assert.Throws<MapDocumentException>(mutate);
                Assert.Equal(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, error.Reason);
                Assert.Equal(fixture.Document.Id, error.DocumentId);
                Assert.Equal(expectedOperation, error.Operation);
            }
            else
            {
                var error = Assert.Throws<MapSessionException>(mutate);
                Assert.Equal(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, error.Reason);
                Assert.Equal(fixture.Document.Id, error.DocumentId);
                Assert.Equal(expectedOperation, error.Operation);
            }
            Assert.Equal(before, PrefabEditorFixtures.State(session));
            Assert.Equal(version, session.SnapshotVersion);
            Assert.Equal(files, Files(root));
            publish();
            Assert.True(session.SnapshotVersion > version);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedEditHandler_CanSaveButCallbackPublicationRemainsRefused(bool staged)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        try
        {
            Action<MapNativeEditEffects> save = _ =>
            {
                var nested = Assert.Throws<MapDocumentException>(() => session.Mutate((_, _) => 0, false));
                Assert.Equal(MapDocumentRefusal.ToolNestedMutationUnsupported, nested.Reason);
                session.Save();
            };
            session.NativeEdited += save;
            var before = PrefabEditorFixtures.State(session);
            string[] files = Files(Path.GetDirectoryName(session.DocumentPath)!);
            var error = Assert.Throws<MapSessionException>(() => session.Mutate((doc, _) =>
            {
                doc.DisplayName = "Unpublished name";
                save(new(null, null, [], [], [], [], MapNativeInvalidation.None));
                return 0;
            }, false));
            Assert.Equal(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, error.Reason);
            Assert.Equal(session.WithDocument((doc, _) => doc.Id), error.DocumentId);
            Assert.Equal("Save", error.Operation);
            Assert.Equal(before, PrefabEditorFixtures.State(session));
            Assert.Equal(files, Files(Path.GetDirectoryName(session.DocumentPath)!));

            session.Mutate((doc, registry) =>
            {
                if (staged) session.ApplyNative(Move(), doc, registry);
                doc.DisplayName = "Accepted name";
                return 0;
            }, false);
            Assert.False(session.IsDirty);
            Assert.Equal("Accepted name", MapDocumentFile.Load(session.DocumentPath!).DisplayName);
            Assert.Equal(PrefabEditorFixtures.State(session).DocumentBytes,
                MapDocumentFile.SaveText(MapDocumentFile.Load(session.DocumentPath!)));
            Assert.Equal((1, 1, 1L), session.NativeHistoryState);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void StagedNativeCommand_RequiresTheCallbacksDetachedCandidate()
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        try
        {
            var before = PrefabEditorFixtures.State(session);
            var error = Assert.Throws<MapDocumentException>(() => session.Mutate((_, _) =>
                session.WithDocument((live, registry) => session.ApplyNative(Move(), live, registry)), true));
            Assert.Equal(MapDocumentRefusal.ToolMutationCandidateRequired, error.Reason);
            Assert.Equal(session.WithDocument((doc, _) => doc.Id), error.DocumentId);
            Assert.Equal("ApplyNative", error.Operation);
            Assert.Equal(typeof(PrefabEditCommand), error.CommandType);
            Assert.Equal(before, PrefabEditorFixtures.State(session));
            session.ExecuteNativeCommand(Move());
            Assert.NotEqual(before, PrefabEditorFixtures.State(session));
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrefabPublicationDuringMutation_RefusesBeforeWritingImmutableBytes(bool updateOpenMap)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        var session = PrefabEditorFixtures.Session(PrefabFixtures.Create());
        try
        {
            var service = new PrefabService(session);
            var asset = service.Open("hut");
            var marker = PrefabEditorFixtures.Marker(asset);
            service.Edit(marker with { Value = marker.Value with { Name = "Changed" } });
            string root = Path.GetDirectoryName(session.DocumentPath)!;
            string[] files = Files(root);
            var before = PrefabEditorFixtures.State(session);
            var publisher = new PrefabAssetPublisher(session);
            var error = Assert.Throws<MapDocumentException>(() => session.Mutate((_, _) =>
                publisher.Publish(service.Export(), "hut", "published", updateOpenMap), false));
            Assert.Equal(files, Files(root));
            Assert.Equal(before, PrefabEditorFixtures.State(session));
            Assert.Equal(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, error.Reason);
            Assert.Equal("Publish", error.Operation);
            Assert.Equal(session.WithDocument((doc, _) => doc.Id), error.DocumentId);
            Assert.Null(publisher.PublishedAssets);
            var published = publisher.Publish(service.Export(), "hut", "published", updateOpenMap);
            Assert.True(File.Exists(Path.Combine(root, published.Resource.Path)));
            Assert.Equal(updateOpenMap, published.Mutation is not null);
        }
        finally { FoliageEditorFixtures.Delete(session); }
    }

    [Fact]
    public void ColliderPublicationDuringMutation_RefusesBeforeWritingAndKeepsDryRunsAvailable()
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1481
        using var fixture = new NativeCollisionToolFixture();
        var session = fixture.Session;
        byte[] files = fixture.ReadAssetDirectoryDigest();
        string before = session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry));
        var history = session.NativeHistoryState;
        long version = session.SnapshotVersion;
        var error = Assert.Throws<MapDocumentException>(() => session.Mutate((_, _) =>
            fixture.Service.SetHeights("wall-variant", 0, 3, dryRun: false), true));
        Assert.Equal(files, fixture.ReadAssetDirectoryDigest());
        Assert.Equal(before, session.WithDocument((doc, registry) => MapDocumentFile.SaveText(doc, registry)));
        Assert.Equal(history, session.NativeHistoryState);
        Assert.Equal(version, session.SnapshotVersion);
        Assert.False(session.IsDirty);
        Assert.Equal(MapDocumentRefusal.ToolPublicationDuringMutationUnsupported, error.Reason);
        Assert.Equal("SetHeights", error.Operation);
        Assert.Equal(session.WithDocument((doc, _) => doc.Id), error.DocumentId);
        Assert.False(session.Mutate((_, _) => fixture.Service.SetHeights("wall-variant", 0, 3), false).Applied);
        Assert.Equal(files, fixture.ReadAssetDirectoryDigest());
        Assert.True(fixture.Service.SetHeights("wall-variant", 0, 3, dryRun: false).Applied);
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
