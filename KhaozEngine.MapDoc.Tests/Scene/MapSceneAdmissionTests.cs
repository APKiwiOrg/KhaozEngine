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
