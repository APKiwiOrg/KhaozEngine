using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceMetadataReferenceControls
{
    [Fact]
    public void PartialMetadataCanAddAValidResidentParent()
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocumentFile.SaveTiled(SurfaceStorageFixtures.RecordReferrer(), dir);
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
            Assert.True(window.Tiles!.IsPartial);
            Assert.True(window.Surfaces.Patches.ContainsKey(SurfaceStorageFixtures.Yard0));
            var parent = new MapRecordRef("yard", SurfaceStorageFixtures.Yard0);
            window.Surfaces.Refs[0] = window.Surfaces.Refs[0] with
            {
                IndoorSpan = new("new-span", parent, 0, 100, Array.Empty<string>()),
            };
            MapDocumentFile.SaveTiled(window, dir);
            MapIndoorSpan? span = MapDocumentFile.LoadTiled(dir).Surfaces.Refs[0].IndoorSpan;
            Assert.NotNull(span);
            Assert.Equal(("new-span", parent, 0, 100), (span!.Id, span.ParentSpace, span.LowerOffsetUnits, span.UpperOffsetUnits));
            Assert.Empty(MapDocumentFile.VerifyTiled(dir));
        });
    }

    [Fact]
    public void UnchangedUnloadedParentRemainsCarryableWithOtherSpanMetadata()
    {
        TiledDocFixture.InDirectory(dir =>
        {
            MapDocument doc = SurfaceStorageFixtures.RecordReferrer();
            var parent = new MapRecordRef("yard", SurfaceStorageFixtures.Yard0);
            doc.Surfaces.Refs[0] = doc.Surfaces.Refs[0] with
            {
                IndoorSpan = new("existing-span", parent, 0, 100, Array.Empty<string>()),
            };
            MapDocumentFile.SaveTiled(doc, dir);
            MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
            Assert.False(window.Surfaces.Patches.ContainsKey(parent.Anchor));
            Assert.Equal(MapPatchStatus.Unloaded, window.Tiles!.Surfaces!.StatusOf(parent.Anchor));
            MapIndoorSpan original = window.Surfaces.Refs[0].IndoorSpan!;
            window.Surfaces.Refs[0] = window.Surfaces.Refs[0] with
            {
                IndoorSpan = original with { DomainTags = new[] { "updated-tag" } },
            };
            MapDocumentFile.SaveTiled(window, dir);
            Assert.Equal(MapPatchStatus.Unloaded, window.Tiles!.Surfaces!.StatusOf(parent.Anchor));
            MapIndoorSpan? span = MapDocumentFile.LoadTiled(dir).Surfaces.Refs[0].IndoorSpan;
            Assert.NotNull(span);
            Assert.Equal(parent, span!.ParentSpace);
            Assert.Equal(new[] { "updated-tag" }, span.DomainTags);
            Assert.Empty(MapDocumentFile.VerifyTiled(dir));
        });
    }
}
