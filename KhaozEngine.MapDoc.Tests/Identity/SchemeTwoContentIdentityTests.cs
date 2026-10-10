using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Identity;

/// <summary>The resolver-2 whole token covers placements, spawns, player spawns and sculpt, not only surfaces.</summary>
public sealed class SchemeTwoContentIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
    static string Token(MapDocument doc) => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2);

    static MapDocument Content(bool reversed = false)
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        MapPlacement explicitY = SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f);
        explicitY.DisplayName = "Old oak";
        var bound = new MapPlacement
        {
            Id = "bound-1",
            Kind = "scenery",
            AssetId = "tree",
            X = 61.5f,
            Z = 1.5f,
            SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, null, null, null),
        };
        MapPlacement[] placements = { explicitY, bound, SurfaceStorageFixtures.ExplicitYPlacement("tree-2", 100f, 12f) };
        doc.Placements.AddRange(reversed ? placements.Reverse() : placements);
        doc.Spawns.Add(new MapSpawn { Id = "spawn-1", ArchetypeId = "wolf", X = 20f, Z = 20f });
        doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start-1", X = 30f, Z = 30f, Yaw = 0.5f });
        doc.TerrainOverrides = new MapTerrainOverrides();
        doc.TerrainOverrides.SetDelta(4, 4, 0.5f);
        doc.TerrainOverrides.SetDelta(300, 4, -0.25f);
        return doc;
    }

    static void Apply(MapDocument doc, string change)
    {
        switch (change)
        {
            case "placementX": doc.Placements.Single(p => p.Id == "tree-1").X += 1f; break;
            case "explicitY": doc.Placements.Single(p => p.Id == "tree-1").Y = 11f; break;
            case "supportBinding":
                {
                    MapPlacement bound = doc.Placements.Single(p => p.Id == "bound-1");
                    bound.SupportBinding = bound.SupportBinding! with { SurfaceId = "ridge" };
                    break;
                }
            case "spawn": doc.Spawns.Single().X += 1f; break;
            case "playerSpawn": doc.PlayerSpawns.Single().Yaw = 1f; break;
            case "sculptCell": doc.TerrainOverrides!.SetDelta(5, 4, 0.125f); break;
            case "documentDisplayName": doc.DisplayName += " renamed"; break;
            case "placementDisplayName": doc.Placements.Single(p => p.Id == "tree-1").DisplayName = "Renamed oak"; break;
            case "placementOrder": break;
            default: throw new ArgumentException("unknown content change", nameof(change));
        }
    }

    [Theory]
    [InlineData("placementX", true)]
    [InlineData("explicitY", true)]
    [InlineData("supportBinding", true)]
    [InlineData("spawn", true)]
    [InlineData("playerSpawn", true)]
    [InlineData("sculptCell", true)]
    [InlineData("documentDisplayName", false)]
    [InlineData("placementDisplayName", false)]
    [InlineData("placementOrder", false)]
    public void SchemeTwo_TokenCoversAuthoredContent(string change, bool changes)
    {
        string before = Token(Content());
        MapDocument doc = Content(reversed: change == "placementOrder");
        Apply(doc, change);
        Assert.Equal(changes, before != Token(doc));
    }

    [Fact]
    public void ResolverV2_PlacementChangeChangesTheResolvedHash()
    {
        MapDocument original = SurfaceStorageFixtures.ThreeSurfaces();
        original.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
        MapDocument moved = SurfaceStorageFixtures.ThreeSurfaces();
        moved.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 11f, 10f));

        MapSupportedResolution a = MapResolverV2.Resolve(original, SurfaceStorageFixtures.Assets(),
            MapDocumentSurfaceSource.Capture(original), V2);
        MapSupportedResolution b = MapResolverV2.Resolve(moved, SurfaceStorageFixtures.Assets(),
            MapDocumentSurfaceSource.Capture(moved), V2);
        Assert.NotEqual(a.Document.Placements.Single().Transform.Position.X, b.Document.Placements.Single().Transform.Position.X);
        Assert.NotEqual(a.Document.AuthoredHash, b.Document.AuthoredHash);
    }
}
