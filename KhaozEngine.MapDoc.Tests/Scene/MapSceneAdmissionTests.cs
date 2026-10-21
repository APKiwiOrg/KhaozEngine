using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneAdmissionTests
{
    [Fact]
    public void AggregatePayloadLimit_RefusesBeforeCloneEntry()
    {
        var patch = PrefabFixtures.Patch("floor", 2, 2, 0);
        var entries = new List<string>();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0), entries.Add);
        using (admission.Enter()) Assert.Throws<MapSceneCapacityException>(() => patch.Clone());
        Assert.Empty(entries);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(patch.Heights, patch.Clone().Heights);
    }

    [Fact]
    public void AggregateFaceLimit_RefusesBeforeCompilationEntry()
    {
        var patch = PrefabFixtures.Patch("floor", 2, 2, 0);
        var surface = PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor);
        var entries = new List<string>();
        var admission = new MapSceneAdmission(new(MaxPreparedFaces: 0), entries.Add);
        using (admission.Enter()) Assert.Throws<MapSceneCapacityException>(() => MapSurfaceCompiler.Compile(surface, patch));
        Assert.Empty(entries);
        Assert.Equal(0, admission.PreparedFaces);
        Assert.Equal(8, MapSurfaceCompiler.Compile(surface, patch).Faces.Count);
        Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(surface with { Role = MapSurfaceRole.PaintOverride }, patch));
    }

    [Fact]
    public void ExternalSurfaceSource_RefusesBeforeReading()
    {
        var external = new ExternalSource();
        var admission = new MapSceneAdmission(new());
        using (admission.Enter())
        {
            var error = Assert.Throws<MapSceneSourceException>(() => MapScopedSurfaces.Acquire(external,
                MapSceneScopeTests.Window().Surfaces, Array.Empty<string>()));
            Assert.Equal(MapSceneStatus.Invalid, error.Status);
            Assert.Contains("admission", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, external.Reads);
    }

    [Fact]
    public void StoredPayloadLimit_RefusesBeforePayloadReadEntry()
    {
        var fixture = PrefabFixtures.Create();
        string directory = NativeSceneFixtures.TempDirectory();
        try
        {
            MapDocumentFile.SaveTiled(fixture.Document, directory);
            var source = MapStoredSurfaceSource.Open(directory);
            var entries = new List<string>();
            var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0), entries.Add);
            using (admission.Enter()) Assert.Throws<MapSceneCapacityException>(() => source.ReadPatch(fixture.Document.Surfaces.Patches.First().Key));
            Assert.DoesNotContain("payload", entries);
            Assert.Equal(0, admission.RetainedBytes);
            Assert.Equal(MapPatchStatus.Present, source.ReadPatch(fixture.Document.Surfaces.Patches.First().Key).Status);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void SceneWithExternalProducer_ReportsInvalidFindingBeforeAnyRead()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        var external = new ExternalSource();
        var result = new MapSceneSource(source.Data with { Surfaces = external }).Acquire(MapSceneScopeTests.Window());
        Assert.Equal(MapSceneStatus.Invalid, result.Status);
        Assert.Null(result.Scene);
        Assert.Contains(result.Findings, f => f.Contains("admission", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, external.Reads);
        Assert.Throws<InvalidOperationException>(() => MapScopedSurfaces.Acquire(external, MapSceneScopeTests.Window().Surfaces, Array.Empty<string>()));
        Assert.Equal(1, external.Reads);
    }

    [Fact]
    public void FromDocument_DoesNotCopyScenePayloadBeforeCapture()
    {
        var fixture = PrefabFixtures.Create();
        var entries = new List<string>();
        var admission = new MapSceneAdmission(new(), entries.Add);
        using (admission.Enter())
        using (MapDocumentSource.FromDocument(fixture.Document)) { }
        Assert.DoesNotContain("clone", entries);
    }

    [Fact]
    public void ResidentCapture_ChargesPinnedPayloadBeforeCloning()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 64, 64, 0);
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        var placement = new MapPlacement
        {
            Id = "resident",
            Kind = "cosmetic",
            AssetId = "branch",
            NumericId = 1,
            X = 1,
            Y = 1,
            Z = 1,
            Tags = new() { "before" }
        };
        fixture.Document.Placements.Add(placement);
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        patch.Heights[0] = 123;
        placement.Tags[0] = "at-pin";
        var result = MapSceneSource.Capture(document, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, result.Status);
        Assert.True(result.Work.RetainedBytes >= 4L * 65 * 65 + 8L * 64 * 64);
        Assert.Equal(123, result.Source!.Data.Surfaces.ReadPatch(patch.Key).Patch!.Heights[0]);
        placement.Tags[0] = "after-pin";
        Assert.Equal("at-pin", Assert.Single(result.Source.Data.Owners["Placement:resident"].Input.Placements).Tags[0]);
        patch.Heights[0] = 456;
        Assert.Equal(123, result.Source.Data.Surfaces.ReadPatch(patch.Key).Patch!.Heights[0]);
        var entries = new List<string>();
        using (new MapSceneAdmission(new(), entries.Add).Enter())
        {
            var refused = MapSceneSource.Capture(document, fixture.Assets, new(MaxMetadataBytes: 1024));
            Assert.Equal(MapSceneSourceStatus.CapacityExceeded, refused.Status);
            Assert.Null(refused.Source);
            Assert.Equal(0, refused.Work.RetainedBytes);
        }
        Assert.DoesNotContain("clone", entries);
    }

    [Fact]
    public void ResidentCapture_GenerationAttestsPlacementValuesAtThePin()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var placement = new MapPlacement
        {
            Id = "resident",
            Kind = "cosmetic",
            AssetId = "branch",
            NumericId = 1,
            X = 1,
            Y = 1,
            Z = 1,
            Tags = new() { "before" }
        };
        fixture.Document.Placements.Add(placement);
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var before = MapSceneSource.Capture(document, fixture.Assets).Source!;
        placement.Tags[0] = "at-pin";
        var after = MapSceneSource.Capture(document, fixture.Assets).Source!;
        Assert.NotEqual(before.SourceGeneration, after.SourceGeneration);
        Assert.Equal("before", Assert.Single(before.Data.Owners["Placement:resident"].Input.Placements).Tags[0]);
        Assert.Equal("at-pin", Assert.Single(after.Data.Owners["Placement:resident"].Input.Placements).Tags[0]);
    }

    sealed class ExternalSource : IMapSurfaceAcquisitionSource
    {
        internal int Reads { get; private set; }
        public string SnapshotId => "external";
        public string RootSha256 => new('a', 64);
        public IReadOnlyList<MapSurfaceRef> Surfaces => Array.Empty<MapSurfaceRef>();
        public MapPatchRead ReadPatch(MapPatchKey key) { Reads++; throw new InvalidOperationException(); }
        public MapPatchFindResult FindPatches(MapSurfaceScope scope) { Reads++; throw new InvalidOperationException(); }
        public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) { Reads++; throw new InvalidOperationException(); }
    }
}
