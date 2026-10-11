using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterCodecTests
{
    [Fact]
    public void NegativeZeroLevel_HasTheIdentityOfZero()
    {
        MapDocument positive = WaterFixtures.OceanOverCaves(), negative = WaterFixtures.OceanOverCaves();
        int ocean = negative.Water!.Bodies.FindIndex(b => b.Id == "ocean");
        Assert.Equal(0f, positive.Water!.Bodies[ocean].SurfaceY);
        negative.Water.Bodies[ocean] = negative.Water.Bodies[ocean] with { SurfaceY = -0f };
        Assert.True(float.IsNegative(negative.Water.Bodies[ocean].SurfaceY));
        Assert.Equal(positive.Water.Digest(), negative.Water.Digest());
        Assert.Equal(MapDocumentHash.OfWorld(positive), MapDocumentHash.OfWorld(negative));
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("body")]
    public void NullEntry_RefusesAsDocumentError(string entry)
    {
        MapWaterSet water = WaterFixtures.OceanOverCaves().Water!;
        if (entry == "medium") water.Media.Insert(0, null!);
        else water.Bodies.Insert(1, null!);
        Assert.Throws<MapDocumentException>(() => water.Digest());
    }

    [Fact]
    public void Clone_IsIndependentOfLaterSourceEdits()
    {
        MapWaterSet source = WaterFixtures.WithRingBody(MapLatticeAddress.Create(-1, -1, 2), MapLatticeAddress.Create(704, 64, 64),
            MapLatticeAddress.Create(11, 3, 1), MapLatticeAddress.Create(1, 10, 3)).Water!;
        MapWaterSet copy = source.Clone();
        string digest = copy.Digest();
        Assert.Equal(source.Digest(), digest);

        MapWaterBodyDoc ocean = source.Bodies.Single(b => b.Id == "ocean"), ring = source.Bodies.Single(b => b.Id == "ring");
        ((MapWaterRect[])ocean.Domain.Rects)[0] = new MapWaterRect(1, 1, 2, 2);
        ((MapRecordRef[])ocean.Covers)[0] = new MapRecordRef("dry-cave", new MapPatchKey("dry-floor", 0, 0));
        ((MapLatticeAddress[])ring.Domain.Rings[0].Vertices)[0] = MapLatticeAddress.Corner(5, 5);
        ((MapWaterRing[])ring.Domain.Rings)[0] = new MapWaterRing(new[] { MapLatticeAddress.Corner(0, 0) });
        source.Bodies[0] = source.Bodies[0] with { SurfaceY = 7f };
        source.Bodies.RemoveAt(1);
        source.Media.Clear();

        Assert.NotEqual(digest, source.Digest());
        Assert.Equal(digest, copy.Digest());
        Assert.Equal(new MapWaterRect(0, 0, 12, 4), copy.Bodies.Single(b => b.Id == "ocean").Domain.Rects[0]);
    }

    [Fact]
    public void BodyAndMediumDigests_FollowContentNotListOrder()
    {
        MapWaterSet water = WaterFixtures.OceanOverCaves().Water!;
        water.Media.Add(new MapWaterMediumDoc("brine", MapWaterMediumKind.Water, 0.5f));
        Dictionary<string, string> bodies = BodyDigests(water), media = MediumDigests(water);
        Assert.Equal(water.Bodies.Count, bodies.Values.Distinct().Count());
        Assert.Equal(water.Media.Count, media.Values.Distinct().Count());

        water.Bodies.Reverse();
        water.Media.Reverse();
        Assert.Equal(bodies, BodyDigests(water));
        Assert.Equal(media, MediumDigests(water));

        int ocean = water.Bodies.FindIndex(b => b.Id == "ocean");
        water.Bodies[ocean] = water.Bodies[ocean] with { SurfaceY = 0.5f };
        int brine = water.Media.FindIndex(m => m.Key == "brine");
        water.Media[brine] = water.Media[brine] with { WadeSpeedScale = 0.25f };
        Dictionary<string, string> editedBodies = BodyDigests(water), editedMedia = MediumDigests(water);
        Assert.NotEqual(bodies["ocean"], editedBodies["ocean"]);
        Assert.NotEqual(media["brine"], editedMedia["brine"]);
        Assert.All(bodies.Keys.Where(id => id != "ocean"), id => Assert.Equal(bodies[id], editedBodies[id]));
        Assert.Equal(media["sea-water"], editedMedia["sea-water"]);
    }

    static Dictionary<string, string> BodyDigests(MapWaterSet water) =>
        water.Bodies.ToDictionary(b => b.Id, MapWaterSet.BodyDigest, StringComparer.Ordinal);

    static Dictionary<string, string> MediumDigests(MapWaterSet water) =>
        water.Media.ToDictionary(m => m.Key, MapWaterSet.MediumDigest, StringComparer.Ordinal);
}
