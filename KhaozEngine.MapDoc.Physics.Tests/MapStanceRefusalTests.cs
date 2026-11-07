using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapStanceRefusalTests
{
    [Theory]
    [InlineData("spacing", MapDocumentRefusal.ValueOutsideRange, "Spacing", 0d)]
    [InlineData("spacing-nonfinite", MapDocumentRefusal.ValueNonfinite, "Spacing", double.NaN)]
    [InlineData("radius", MapDocumentRefusal.ValueOutsideRange, "CapsuleRadius", 0d)]
    [InlineData("radius-nonfinite", MapDocumentRefusal.ValueNonfinite, "CapsuleRadius", double.NaN)]
    [InlineData("half-height", MapDocumentRefusal.ValueOutsideRange, "CapsuleHalfHeight", 0d)]
    [InlineData("half-height-nonfinite", MapDocumentRefusal.ValueNonfinite, "CapsuleHalfHeight", double.PositiveInfinity)]
    public void Options_IdentifyTheFailedNestedFieldAndAcceptItsBoundary(string fault, MapDocumentRefusal reason,
        string field, double actual)
    {
        var world = NativeWorldFixtures.BuildCrate();
        var options = new MapStanceOptions(0.25f, 0.5f);
        var tuning = MoveTuning.Default;
        if (fault == "spacing") options = options with { Spacing = 0 };
        if (fault == "spacing-nonfinite") options = options with { Spacing = float.NaN };
        if (fault == "radius") tuning = tuning with { CapsuleRadius = 0 };
        if (fault == "radius-nonfinite") tuning = tuning with { CapsuleRadius = float.NaN };
        if (fault == "half-height") tuning = tuning with { CapsuleHalfHeight = 0 };
        if (fault == "half-height-nonfinite") tuning = tuning with { CapsuleHalfHeight = float.PositiveInfinity };
        var error = Assert.Throws<MapStanceRangeException>(() =>
            MapStanceCandidates.Find(world, "crate", Vector3.Zero, tuning, options, Accept));
        Assert.IsAssignableFrom<ArgumentOutOfRangeException>(error);
        Assert.Equal(field == "Spacing" ? "options" : "tuning", error.ParamName);
        Assert.Null(error.ActualValue);
        Assert.Equal(reason, error.Reason);
        Assert.Equal(field, error.FieldPath);
        Assert.Equal(actual, error.ActualNumber);
        if (field == "CapsuleHalfHeight") Assert.Equal(tuning.CapsuleRadius, error.MinimumNumber);
        else Assert.Equal(0, error.MinimumNumber);
        var repairedTuning = field == "CapsuleHalfHeight"
            ? MoveTuning.Default with { CapsuleHalfHeight = MoveTuning.Default.CapsuleRadius } : MoveTuning.Default;
        Assert.NotEmpty(MapStanceCandidates.Find(world, "crate", Vector3.Zero, repairedTuning,
            new(0.25f, 0.5f), Accept));
    }

    [Fact]
    public void LegacyHeight_ReportsItsMissingCallbackAndNonfiniteResult()
    {
        var world = NativeWorldFixtures.BuildLegacySlope();
        var missing = Assert.Throws<MapStanceException>(() =>
            MapStanceCandidates.MissingLegacyHeightForTests(false, null));
        Assert.IsAssignableFrom<InvalidOperationException>(missing);
        Assert.Equal(MapDocumentRefusal.SupportLegacyHeightRequired, missing.Reason);
        Assert.Equal(1, missing.ResolverVersion);
        Assert.Equal("LegacySupportHeight", missing.FieldPath);
        MapStanceCandidates.MissingLegacyHeightForTests(false, (_, _) => 0);
        MapStanceCandidates.MissingLegacyHeightForTests(true, null);

        // The callback remains valid during world resolution and becomes faulty only during stance sampling.
        bool fault = false;
        var fixture = NativeWorldFixtures.LegacyCrate();
        var sampled = MapWorldBuilder.Build(fixture.Document, fixture.Assets, NativeWorldFixtures.Options() with
        { Resolve = NativeWorldFixtures.LegacyResolveOptions, LegacySupportHeight = (_, _) => fault ? float.NaN : 0 });
        fault = true;
        var error = Assert.Throws<MapStanceException>(() => MapStanceCandidates.Find(sampled, "crate", Vector3.Zero,
            MoveTuning.Default, new(0.25f, 0.5f), Accept));
        Assert.Equal(MapDocumentRefusal.ValueNonfinite, error.Reason);
        Assert.Equal("LegacySupportHeight", error.FieldPath);
        Assert.Equal(double.NaN, error.ActualNumber);
        Assert.Equal("crate", error.PlacementId);
        fault = false;
        Assert.NotEmpty(MapStanceCandidates.Find(sampled, "crate", Vector3.Zero, MoveTuning.Default,
            new(0.25f, 0.5f), Accept));
        Assert.NotEmpty(MapStanceCandidates.Find(world, "crate", Vector3.Zero, MoveTuning.Default,
            new(0.25f, 0.5f), Accept));
    }

    [Fact]
    public void OversizedOutlineCount_PreservesTheAlreadyCompletedProposal()
    {
        // A segment's two edges total 2^54. The normal radius's arcs are far below one binary64 ulp.
        // One previous outline contributed one proposal, making the exact aggregate count odd.
        Assert.Equal((1L << 54) + 1,
            MapStanceCandidates.OversizedOutlineCountForTests(1L << 53, Math.ScaleB(1d, -64), 1));
    }

    static bool Accept(Vector3 candidate, out Vector3 seated) { seated = candidate; return true; }
}
