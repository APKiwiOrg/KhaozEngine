using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Walk-up stance candidates around a placement's envelope, seated or refused by the caller's
/// validator.</summary>
public class MapStanceCandidatesTests
{
    [Fact]
    public void RotatedCompound_CandidatesAreDeterministicReachableAndSeatedByTheValidator()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var q = new MapWorldQueries(world);
        var options = new MapStanceOptions(Spacing: 0.25f, Range: 0.4f);
        MapStanceValidator seatAtFloor = (Vector3 c, out Vector3 seated) => { seated = new Vector3(c.X, 0f, c.Z); return true; };
        var a = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, seatAtFloor);
        var b = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default, options, seatAtFloor);
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        var t = MoveTuning.Default;
        Assert.All(a, p => Assert.Equal(0f, p.Y));
        Assert.All(a, p => Assert.True(q.Within(new MovementBody(p + Vector3.UnitY * t.CapsuleHalfHeight, t.CapsuleRadius, t.CapsuleHalfHeight), "doorway", 0.4f)));
    }

    [Fact]
    public void ValidatorRejectingEverything_GivesNoCandidates()
    {
        MapStanceValidator refuse = (Vector3 c, out Vector3 seated) => { seated = c; return false; };
        Assert.Empty(MapStanceCandidates.Find(NativeWorldFixtures.BuildCrate(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), refuse));
    }

    [Fact]
    public void LegacyWorld_ProposesAtTheLegacySupportHeight()
    {
        var proposed = new List<Vector3>();
        MapStanceValidator record = (Vector3 c, out Vector3 seated) => { proposed.Add(c); seated = c; return true; };
        MapStanceCandidates.Find(NativeWorldFixtures.BuildLegacySlope(), "crate", new Vector3(0, 0, -2), MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), record);
        Assert.NotEmpty(proposed);
        Assert.All(proposed, p => Assert.Equal(NativeWorldFixtures.LegacySlopeHeight(p.X, p.Z), p.Y));
    }
}
