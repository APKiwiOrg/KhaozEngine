using System;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void Doorway_ProposesInsideTheOpening()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        var t = f.Resolved.Placements.Single(p => p.PlacementId == "doorway").Transform;
        MapStanceValidator seatAtFloor = (Vector3 c, out Vector3 seated) => { seated = new Vector3(c.X, 0f, c.Z); return true; };
        var found = MapStanceCandidates.Find(world, "doorway", new Vector3(0, 0, -2), MoveTuning.Default,
            new MapStanceOptions(0.25f, 0.4f), seatAtFloor);
        // Asset-local x and z: the opening spans |x| < 0.6 and the jambs are 0.3 deep, so |z| < 0.15 is between them.
        float c = MathF.Cos(t.YawRadians), s = MathF.Sin(t.YawRadians);
        Assert.Contains(found, p =>
        {
            float dx = p.X - t.Position.X, dz = p.Z - t.Position.Z;
            float x = (c * dx - s * dz) / t.Scale, z = (s * dx + c * dz) / t.Scale;
            return MathF.Abs(x) < 0.6f && MathF.Abs(z) < 0.15f;
        });
    }

    [Fact]
    public void ProposalsOutsideThePlayableBounds_NeverReachTheValidator()
    {
        // The crate spans x 31.4 to 32, so its outline outset by the 0.4 m capsule radius crosses the x = 32 edge.
        var proposed = new List<Vector3>();
        MapStanceValidator record = (Vector3 c, out Vector3 seated) => { proposed.Add(c); seated = c; return true; };
        MapStanceCandidates.Find(NativeWorldFixtures.BuildCrateAt(31.7f, 0f, 0f), "crate", new Vector3(30, 0, 0),
            MoveTuning.Default, new MapStanceOptions(0.05f, 0.5f), record);
        Assert.NotEmpty(proposed);
        Assert.All(proposed, p => Assert.True(p.X <= 32f));
        Assert.Contains(proposed, p => p.X > 32f - 0.05f);
    }

    [Fact]
    public void SeatsOutsideThePlayableBounds_AreDropped()
    {
        var world = NativeWorldFixtures.BuildCrateAt(31.7f, 0f, 0f);
        var options = new MapStanceOptions(0.25f, 0.5f);
        MapStanceValidator pushOut = (Vector3 c, out Vector3 seated) => { seated = new Vector3(32.1f, 0f, 0f); return true; };
        MapStanceValidator keepIn = (Vector3 c, out Vector3 seated) => { seated = new Vector3(31.9f, 0f, 0f); return true; };
        Assert.Empty(MapStanceCandidates.Find(world, "crate", new Vector3(30, 0, 0), MoveTuning.Default, options, pushOut));
        Assert.Equal(new[] { new Vector3(31.9f, 0f, 0f) },
            MapStanceCandidates.Find(world, "crate", new Vector3(30, 0, 0), MoveTuning.Default, options, keepIn));
    }

    [Fact]
    public void SeatsMovedOutOfReach_AreDropped()
    {
        MapStanceValidator pushAway = (Vector3 c, out Vector3 seated) =>
        {
            var away = Vector3.Normalize(new Vector3(c.X, 0f, c.Z)) * 2f;
            seated = c + away;
            return true;
        };
        Assert.Empty(MapStanceCandidates.Find(NativeWorldFixtures.BuildCrate(), "crate", new Vector3(0, 0, -2),
            MoveTuning.Default, new MapStanceOptions(0.25f, 0.5f), pushAway));
    }

    [Fact]
    public void Results_AreOrderedByDistanceThenXZAndY()
    {
        // Every proposal seats at one of eight points around the crate, by quadrant and by which axis dominates.
        MapStanceValidator snap = (Vector3 c, out Vector3 seated) =>
        {
            seated = new Vector3(c.X >= 0f ? 0.5f : -0.5f, MathF.Abs(c.X) > MathF.Abs(c.Z) ? 0.1f : -0.1f,
                c.Z >= 0f ? 0.5f : -0.5f);
            return true;
        };
        var world = NativeWorldFixtures.BuildCrate();
        var options = new MapStanceOptions(0.25f, 0.5f);

        // From the origin all eight are equally far, so X, then Z, then Y decide.
        Assert.Equal(new[]
        {
            new Vector3(-0.5f, -0.1f, -0.5f), new Vector3(-0.5f, 0.1f, -0.5f),
            new Vector3(-0.5f, -0.1f, 0.5f), new Vector3(-0.5f, 0.1f, 0.5f),
            new Vector3(0.5f, -0.1f, -0.5f), new Vector3(0.5f, 0.1f, -0.5f),
            new Vector3(0.5f, -0.1f, 0.5f), new Vector3(0.5f, 0.1f, 0.5f),
        }, MapStanceCandidates.Find(world, "crate", Vector3.Zero, MoveTuning.Default, options, snap));

        // From x 0.2 the +x seats are nearer, so distance overrides X.
        Assert.Equal(new[]
        {
            new Vector3(0.5f, -0.1f, -0.5f), new Vector3(0.5f, 0.1f, -0.5f),
            new Vector3(0.5f, -0.1f, 0.5f), new Vector3(0.5f, 0.1f, 0.5f),
            new Vector3(-0.5f, -0.1f, -0.5f), new Vector3(-0.5f, 0.1f, -0.5f),
            new Vector3(-0.5f, -0.1f, 0.5f), new Vector3(-0.5f, 0.1f, 0.5f),
        }, MapStanceCandidates.Find(world, "crate", new Vector3(0.2f, 0f, 0f), MoveTuning.Default, options, snap));
    }

    [Theory]
    [InlineData("spacing", "options")]
    [InlineData("fine-spacing", "options")]
    [InlineData("range", "options")]
    [InlineData("tolerance", "options")]
    [InlineData("band", "options")]
    [InlineData("radius", "tuning")]
    public void InvalidInputs_NameFindsOwnParameter(string input, string parameter)
    {
        var options = new MapStanceOptions(0.25f, 0.5f);
        var tuning = MoveTuning.Default;
        switch (input)
        {
            case "spacing": options = options with { Spacing = 0f }; break;
            case "fine-spacing": options = options with { Spacing = 1e-7f }; break;
            case "range": options = options with { Range = -1f }; break;
            case "tolerance": options = options with { Tolerance = float.NaN }; break;
            case "band": options = options with { Band = new MapInteractionBand(1f, 0f) }; break;
            case "radius": tuning = tuning with { CapsuleRadius = 0f }; break;
        }
        MapStanceValidator accept = (Vector3 c, out Vector3 seated) => { seated = c; return true; };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MapStanceCandidates.Find(
            NativeWorldFixtures.BuildCrate(), "crate", Vector3.Zero, tuning, options, accept));
        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void ProposalCap_CoversTheWholeCallRatherThanEachMember()
    {
        // The doorway's members are two 0.3 m square jambs and a 1.8 by 0.3 m lintel. Each outline, outset by the
        // capsule radius, is the member's perimeter plus one full circle. At this spacing every member alone proposes
        // at most 2^20 points, but the three together propose more.
        const int cap = 1 << 20;
        double circle = 2d * Math.PI * MoveTuning.Default.CapsuleRadius, jamb = 1.2d + circle, lintel = 4.2d + circle;
        float spacing = (float)(lintel / (0.9d * cap));
        Assert.True(Math.Ceiling(lintel / spacing) <= cap);
        Assert.True(2d * Math.Ceiling(jamb / spacing) + Math.Ceiling(lintel / spacing) > cap);
        var f = NativeWorldFixtures.Doorway(0f, 1f);
        var world = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
        int calls = 0;
        MapStanceValidator count = (Vector3 c, out Vector3 seated) => { calls++; seated = c; return true; };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MapStanceCandidates.Find(world, "doorway",
            Vector3.Zero, MoveTuning.Default, new MapStanceOptions(spacing, 0.5f), count));
        Assert.Equal("options", error.ParamName);
        Assert.Equal(0, calls);
    }
}
