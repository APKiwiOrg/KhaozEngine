using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapMarkerSupportWorldTests
{
    [Theory]
    [InlineData("stacked")]
    [InlineData("sloped")]
    [InlineData("cave")]
    public void MarkerSupportHeads_AgreeOnPointsProvenanceAndMembership(string variant)
    {
        MarkerFixture fixture = variant switch
        {
            "stacked" => MarkerFixtures.Stacked(),
            "sloped" => MarkerFixtures.Sloped(),
            _ => MarkerFixtures.Cave(),
        };
        MapBuiltWorld client = Build(fixture), server = Build(fixture);

        Assert.NotEmpty(client.Markers);
        Assert.Equal(Rows(client), Rows(server));
        Assert.Equal(Spaces(client), Spaces(server));
        Assert.Equal(client.AnnotationDigest, server.AnnotationDigest);
        Assert.Equal(client.BuildHash, server.BuildHash);
        Assert.All(client.Markers, m => Assert.NotNull(m.HeightProvenance));
        if (variant == "cave") Assert.Equal((MapMembershipStatus.Resolved, "deep-room"), Spaces(client).Single(s => s.Id == "underground").Space);
        if (variant != "sloped") return;
        foreach (MapBuiltWorld head in new[] { client, server })
        {
            MapSupportResult direct = new MapSupportQuery(head.Surfaces).SampleSurfaceExact(MarkerFixtures.Exact(0.3f, 0, 0.7f), "floor-2");
            Assert.Equal(MapSupportStatus.Supported, direct.Status);
            Assert.NotEqual(MathF.Round(direct.WorldY * 64f) / 64f, direct.WorldY);
            Assert.Equal(MapExactValue.FromSingle(direct.WorldY), head.Markers.Single(m => m.Id == "slope").Position.Y);
        }
    }

    // Each head reloads its own copy of the document, as an independent client and server would.
    static MapBuiltWorld Build(MarkerFixture fixture) => MapWorldBuilder.Build(MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document)),
        fixture.Assets, new(MarkerFixtures.Options(), "public-marker-heads/1", new()));

    static (string Id, string Name, MapExactPoint Position, float Yaw, string Role, bool Enabled, string Tags, string? Instance,
        MapMarkerHeightProvenance? Provenance, string? Archetype)[] Rows(MapBuiltWorld world) => world.Markers.Select(m =>
        (m.Id, m.Name, m.Position, m.YawRadians, m.Role, m.Enabled, string.Join("|", m.Tags), m.InstanceId, m.HeightProvenance, m.ArchetypeId)).ToArray();

    static (string Id, (MapMembershipStatus Status, string? SpaceId) Space, string Parents)[] Spaces(MapBuiltWorld world) =>
        world.Markers.Select(m =>
        {
            var membership = MapMarkerSpaces.Query(world.Surfaces, m);
            return (m.Id, (membership.Status, membership.SpaceId), string.Join("|", membership.ParentSpaceIds));
        }).ToArray();
}
