using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class MapTiledConcurrentSaveTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Overlapping_save_is_refused_before_it_can_change_the_live_writer(int phase)
    {
        TiledDocFixture.InDirectory(directory =>
        {
            MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
            MapDocument first = Edited(directory, 2.75f);
            MapDocument second = Edited(directory, 3.5f);
            bool attempted = false;
            var options = new MapDocumentSaveOptions
            {
                OnStep = step =>
                {
                    if (attempted || step != (MapTiledSaveStep)phase) return;
                    attempted = true;
                    MapDocumentException error = Assert.Throws<MapDocumentException>(() =>
                        MapDocumentFile.SaveTiled(second, Path.Combine(directory, ".")));
                    Assert.Contains("exclusive save", error.Message);
                },
            };

            MapDocumentFile.SaveTiled(first, directory, null, options);

            Assert.True(attempted);
            Assert.Empty(MapDocumentFile.VerifyTiled(directory));
            Assert.Equal(2.75f, MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-a").Yaw);

            // The loser can explicitly retry after the owning save releases the directory.
            MapDocumentFile.SaveTiled(second, directory);
            Assert.Empty(MapDocumentFile.VerifyTiled(directory));
            Assert.Equal(3.5f, MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-a").Yaw);
        });
    }

    [Fact]
    public void Aborted_writer_releases_its_directory_for_the_next_save()
    {
        TiledDocFixture.InDirectory(directory =>
        {
            MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), directory);
            MapDocument edited = Edited(directory, 2.75f);
            var options = new MapDocumentSaveOptions
            {
                OnStep = step =>
                {
                    if (step == MapTiledSaveStep.BeforeManifestRename)
                        throw new InvalidOperationException("simulated interrupted save");
                },
            };
            Assert.Throws<InvalidOperationException>(() => MapDocumentFile.SaveTiled(edited, directory, null, options));

            MapDocumentFile.SaveTiled(edited, directory);

            Assert.Empty(MapDocumentFile.VerifyTiled(directory));
            Assert.Equal(2.75f, MapDocumentFile.LoadTiled(directory).Placements.Single(p => p.Id == "p-a").Yaw);
        });
    }

    [Fact]
    public void Independent_directories_can_save_while_another_writer_is_active()
    {
        TiledDocFixture.InDirectory(firstDirectory => TiledDocFixture.InDirectory(secondDirectory =>
        {
            bool savedSecond = false;
            var options = new MapDocumentSaveOptions
            {
                OnStep = step =>
                {
                    if (savedSecond || step != MapTiledSaveStep.BeforeManifestRename) return;
                    MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), secondDirectory);
                    savedSecond = true;
                },
            };
            MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), firstDirectory, null, options);

            Assert.True(savedSecond);
            Assert.Empty(MapDocumentFile.VerifyTiled(firstDirectory));
            Assert.Empty(MapDocumentFile.VerifyTiled(secondDirectory));
        }));
    }

    static MapDocument Edited(string directory, float yaw)
    {
        MapDocument document = MapDocumentFile.LoadTiled(directory);
        document.Placements.Single(p => p.Id == "p-a").Yaw = yaw;
        return document;
    }
}
