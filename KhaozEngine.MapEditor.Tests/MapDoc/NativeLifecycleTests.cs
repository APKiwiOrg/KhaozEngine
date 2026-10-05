using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapEdit;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeLifecycleTests
{
    [Fact]
    public void NativeLifecycle_StaleClosureRefusesSaveWithoutChangingBytes()
    {
        using var f = new NativeLifecycleFixture();
        byte[] before = f.ReadSavedBytes();
        Assert.True(f.Session.Validate().Valid);
        f.CorruptResource();
        Assert.Throws<MapDocumentException>(() => f.Session.Save());
        Assert.Equal(before, f.ReadSavedBytes());
        Assert.False(f.Session.Validate(verifyWholeWorld: true).Valid);
    }

    [Fact]
    public void NativeLifecycle_ValidOpenBindsTheFreshlyVerifiedClosure()
    {
        using var f = new NativeLifecycleFixture();
        var service = new MutationService(f.Session);
        service.PlacementLabel("gate", "North gate");
        Assert.Equal("North gate", f.SessionDocument.Placements.Single(p => p.Id == "gate").DisplayName);

        MapResolvedDocument expected = NativeDocumentService.ValidateComplete(f.SessionDocument,
            new MapDirectoryAssetSource(f.ResourceRoot), NativeDocumentService.SessionOptions);
        NativeDocumentSummary native = f.Session.Summary().Native!;
        Assert.Equal(expected.AuthoredHash, native.AuthoredHash);
        Assert.Equal(f.Closure.Hash, native.ClosureHash);
        Assert.Equal(f.Closure.Hash, expected.AssetClosure.Hash);
        Assert.Equal(2, native.PlacementCount);
        Assert.Equal(1, native.NumericIdCount);
        Assert.Equal(NativeLifecycleFixture.HighWater, native.NumericIdHighWaterMark);
    }

    [Fact]
    public void NativeLifecycle_DirtyEditSurvivesRefusedSaveUntilResourcesAreRestored()
    {
        using var f = new NativeLifecycleFixture();
        new MutationService(f.Session).PlacementLabel("gate", "pending");
        byte[] before = f.ReadSavedBytes();
        string mesh = Path.Combine(f.ResourceRoot, NativeLifecycleFixture.MeshRelativePath);
        byte[] meshBytes = File.ReadAllBytes(mesh);
        f.CorruptResource();
        Assert.Throws<MapDocumentException>(() => f.Session.Save());
        Assert.Equal(before, f.ReadSavedBytes());
        Assert.True(f.Session.IsDirty);
        Assert.Equal("pending", f.SessionDocument.Placements.Single(p => p.Id == "gate").DisplayName);
        Assert.Empty(Directory.GetFiles(f.ResourceRoot, "*.tmp"));

        File.WriteAllBytes(mesh, meshBytes);
        Assert.True(f.Session.Save().Saved);
        Assert.False(f.Session.IsDirty);
        Assert.NotEqual(before, f.ReadSavedBytes());
        Assert.Empty(Directory.GetFiles(f.ResourceRoot, "*.tmp"));
        var reloaded = MapDocumentFile.Load(f.ValidPath);
        Assert.Equal("pending", reloaded.Placements.Single(p => p.Id == "gate").DisplayName);
        Assert.Equal(NativeLifecycleFixture.HighWater, reloaded.NumericIdHighWaterMark);
    }

    [Fact]
    public void NativeLifecycle_ValidateReportsClosureFindingAndSummaryRefuses()
    {
        using var f = new NativeLifecycleFixture();
        ValidateResult valid = f.Session.Validate();
        Assert.True(valid.ClosureChecked);
        Assert.True(valid.ClosureValid);
        Assert.Empty(valid.ClosureErrors);

        f.CorruptLod();
        ValidateResult stale = f.Session.Validate();
        Assert.True(stale.StructuralValid);
        Assert.True(stale.SchemaValid);
        Assert.True(stale.ClosureChecked);
        Assert.False(stale.ClosureValid);
        Assert.False(stale.Valid);
        Assert.Contains(stale.ClosureErrors, e => e.Contains("prop.lod0", StringComparison.Ordinal));
        Assert.Throws<MapDocumentException>(() => f.Session.Summary());
    }

    [Fact]
    public void NativeLifecycle_AnalyticDocumentsReportNoClosure()
    {
        using var f = new NativeLifecycleFixture();
        string path = Path.Combine(f.ResourceRoot, "analytic.map.json");
        var session = new MapEditSession();
        session.Create(path, "analytic", "Analytic", -10, -10, 10, 10);
        ValidateResult result = session.Validate();
        Assert.True(result.Valid);
        Assert.False(result.ClosureChecked);
        Assert.Null(session.Summary().Native);
        Assert.Equal(path, session.DocumentPath);
    }

    [Fact]
    public void NativeLifecycle_ConversionsAndRetileRefuseMissingOrStaleResourcesBeforeWriting()
    {
        using var f = new NativeLifecycleFixture();
        string hash = f.Session.Summary().Native!.AuthoredHash;
        byte[] before = f.ReadSavedBytes();

        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        Assert.Throws<MapDocumentException>(() => f.Session.ConvertToTiled(tiled));
        Assert.False(Directory.Exists(tiled));

        string elsewhere = Path.Combine(f.ResourceRoot, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        string single = Path.Combine(elsewhere, "copy.map.json");
        Assert.Throws<MapDocumentException>(() => f.Session.ConvertToSingle(single));
        Assert.False(File.Exists(single));
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        Assert.Equal(hash, f.Session.Summary().Native!.AuthoredHash);

        f.CorruptResource();
        float tileSize = f.SessionDocument.TileSize;
        Assert.Throws<MapDocumentException>(() => f.Session.Retile(tileSize / 2));
        Assert.Equal(tileSize, f.SessionDocument.TileSize);
        Assert.Equal(before, f.ReadSavedBytes());
        string sibling = Path.Combine(f.ResourceRoot, "sibling.map.json");
        Assert.Throws<MapDocumentException>(() => f.Session.ConvertToSingle(sibling));
        Assert.False(File.Exists(sibling));
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        Assert.False(f.Session.IsDirty);
    }

    [Fact]
    public void NativeLifecycle_ConvertToSingleBindsDestinationClosureAndKeepsIdentity()
    {
        using var f = new NativeLifecycleFixture();
        string hash = f.Session.Summary().Native!.AuthoredHash;
        string copy = Path.Combine(f.ResourceRoot, "copy.map.json");
        f.Session.ConvertToSingle(copy);
        Assert.Equal(copy, f.Session.DocumentPath);
        Assert.Equal(hash, f.Session.Summary().Native!.AuthoredHash);
        new MutationService(f.Session).PlacementLabel("post", "converted");
        f.Session.Save();
        Assert.Equal("converted", MapDocumentFile.Load(copy).Placements.Single(p => p.Id == "post").DisplayName);
    }

    [Fact]
    public void NativeLifecycle_TiledIdentityAndDecimalTransportSurviveConversionAndReopen()
    {
        using var f = new NativeLifecycleFixture(absoluteReferences: true);
        NativeDocumentSummary monolithic = f.Session.Summary().Native!;
        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        f.Session.ConvertToTiled(tiled);
        Assert.Equal(monolithic, f.Session.Summary().Native);

        var reopened = new MapEditSession();
        reopened.Open(tiled);
        Assert.Equal(monolithic, reopened.Summary().Native);
        Assert.True(reopened.Validate(verifyWholeWorld: true).Valid);
        new MutationService(reopened).PlacementLabel("gate", "tiled");
        reopened.Save();
        Assert.Equal(NativeLifecycleFixture.HighWater, MapDocumentFile.Load(tiled).NumericIdHighWaterMark);

        JsonNode json = JsonNode.Parse(JsonSerializer.Serialize(reopened.Summary(), McpJsonUtilities.DefaultOptions))!;
        JsonNode native = json["native"]!;
        Assert.Equal(JsonValueKind.String, native["numericIdHighWaterMark"]!.GetValueKind());
        Assert.Equal("9007199254740993", native["numericIdHighWaterMark"]!.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, native["placementCount"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, native["numericIdCount"]!.GetValueKind());
        var back = JsonSerializer.Deserialize<NativeDocumentSummary>(native.ToJsonString(), McpJsonUtilities.DefaultOptions)!;
        Assert.Equal(NativeLifecycleFixture.HighWater, back.NumericIdHighWaterMark);

        f.CorruptResource();
        ValidateResult stale = reopened.Validate(verifyWholeWorld: true);
        Assert.True(stale.WholeWorldValid);
        Assert.False(stale.ClosureValid);
        Assert.False(stale.Valid);
    }

    [Fact]
    public void NativeLifecycle_RelativeConversionIntoAPreprovisionedDirectoryIsRefusedByTheOverwriteGuard()
    {
        using var f = new NativeLifecycleFixture();
        NativeDocumentSummary monolithic = f.Session.Summary().Native!;
        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        f.CopyResourcesTo(tiled);
        string mesh = Path.Combine(tiled, NativeLifecycleFixture.MeshRelativePath);
        byte[] bytes = File.ReadAllBytes(mesh);

        var ex = Assert.Throws<MapDocumentException>(() => f.Session.ConvertToTiled(tiled));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(tiled, "map.json")));
        Assert.Equal(bytes, File.ReadAllBytes(mesh));
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        Assert.Equal(monolithic, f.Session.Summary().Native);

        // The same preprovisioned resources do verify for the tiled target form.
        MapResolvedDocument resolved = NativeDocumentService.Verify(f.SessionDocument, tiled, MapDocumentForm.Tiled, tiled);
        Assert.Equal(monolithic.AuthoredHash, resolved.AuthoredHash);
    }

    [Fact]
    public void NativeLifecycle_FailedOpenPreservesDirtyStateAndManifestPaths()
    {
        using var f = new NativeLifecycleFixture();
        string[] manifests = { "kit/props.manifest.json" };
        f.Session.Open(f.ValidPath, manifests);
        new MutationService(f.Session).PlacementLabel("gate", "unsaved");
        var document = f.SessionDocument;

        Assert.Throws<MapDocumentException>(() => f.Session.Open(f.BadPath));
        Assert.True(f.Session.IsDirty);
        Assert.Equal(manifests, f.Session.ManifestPaths);
        Assert.Same(document, f.SessionDocument);
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        new MutationService(f.Session).PlacementLabel("post", "still bound");
    }

    [Fact]
    public void NativeLifecycle_RetileValidatesAndRebindsTheRetiledDocument()
    {
        using var f = new NativeLifecycleFixture();
        RetileResult result = f.Session.Retile(256);
        Assert.Equal(256, MapDocumentFile.Load(f.ValidPath).TileSize);
        Assert.NotEqual(result.OldWorldHash, result.NewWorldHash);
        new MutationService(f.Session).PlacementLabel("gate", "retiled");
        Assert.True(f.Session.Validate().Valid);
    }

    [Fact]
    public void NativeLifecycle_PartialNativeOpenRefusesWithoutReplacingSession()
    {
        using var f = new NativeLifecycleFixture(absoluteReferences: true);
        string tiled = Path.Combine(f.ResourceRoot, "tiled-world");
        MapDocumentFile.SaveTiled(f.Document, tiled);
        var whole = new MapEditSession();
        whole.Open(tiled);
        Assert.False(whole.Summary().Native is null);

        f.Session.WholeWorldTileLimit = 1;
        f.Session.EditorWindowRadius = 0;
        string id = f.Session.Summary().Id;
        var document = f.SessionDocument;
        var ex = Assert.Throws<MapDocumentException>(() => f.Session.Open(tiled));
        Assert.Contains("tile", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(document, f.SessionDocument);
        Assert.Equal(id, f.Session.Summary().Id);
        Assert.Equal(f.ValidPath, f.Session.DocumentPath);
        new MutationService(f.Session).PlacementLabel("gate", "still bound");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
    public const string Name = "ProcessWorkingDirectory";
}

/// <summary>Changes the process working directory, so it never runs alongside another test.</summary>
[Collection(ProcessWorkingDirectoryCollection.Name)]
public sealed class NativeLifecycleTestsAnchoredRoot
{
    [Fact]
    public void NativeLifecycle_ResourceRootAndPathStayAnchoredAfterWorkingDirectoryChanges()
    {
        using var f = new NativeLifecycleFixture();
        string original = Environment.CurrentDirectory;
        string elsewhere = Path.Combine(Path.GetTempPath(), "native-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            Environment.CurrentDirectory = Path.GetDirectoryName(f.ResourceRoot)!;
            var session = new MapEditSession();
            session.Open(Path.Combine(Path.GetFileName(f.ResourceRoot), "world.map.json"));
            Environment.CurrentDirectory = elsewhere;
            Assert.True(Path.IsPathFullyQualified(session.DocumentPath!));
            new MutationService(session).PlacementLabel("gate", "anchored");
            Assert.True(session.Validate().Valid);
            Assert.NotNull(session.Summary().Native);
            session.Save();
            Assert.Equal("anchored", MapDocumentFile.Load(f.ValidPath).Placements.Single(p => p.Id == "gate").DisplayName);
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            Environment.CurrentDirectory = original;
            Directory.Delete(elsewhere, true);
        }
    }
}
