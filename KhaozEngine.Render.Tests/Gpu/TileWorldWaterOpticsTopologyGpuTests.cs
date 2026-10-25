using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;
using F = KhaozEngine.Tests.Gpu.TileWorldWaterTopologyFixtures;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// General topology acceptance beside the unchanged saddle fact. Each case renders through the shipped TileWorld
/// adapter with the saddle's camera rules, lighting and 0.20 m feather. Depth and hit expectations come from a
/// binary64 vertical-line oracle over the exact meshes the view drew. The image expectation is an independent field
/// built here from those drawn triangles. Every case carries a negative witness, a plausible wrong topology, that
/// must fail both checks, so neither check is vacuous.
/// </summary>
[Collection("GpuTimingSensitive")]
public sealed class TileWorldWaterOpticsTopologyGpuTests(TileWorldWaterTopologyScene fixture, ITestOutputHelper output)
    : IClassFixture<TileWorldWaterTopologyScene>
{
    // The TileWorld single budget, min(0.0001 m, TileSize / 1024), at 1 m tiles. Each field's certified bound must fit
    // it, and every GPU sample must lie within its own field's certified bound of the drawn-mesh oracle.
    const double DepthBudget = 0.0001;
    const double WitnessSeparation = 0.05;
    // The saddle's fixed RGB allowance, used unchanged as the equivalence threshold.
    const float EquivalentRgbTolerance = 0.04f;
    const float ParityRgbTolerance = 0.01f;

    [GpuFact]
    public void OtherDiagonalFollowsTheDrawnNwSeRidge() => Verify(F.OtherDiagonal);

    [GpuFact]
    public void AbsentOverlayRotationFollowsTheForcedDrawnDiagonal() => Verify(F.ForcedRotation);

    [GpuFact]
    public void EveryCornerCutRotationFollowsItsFourDrawnTriangles()
    {
        TileWaterTopologyCapture capture = Verify(F.CornerCuts);
        // Four tiles of four triangles each, so all four rotations were drawn as cuts.
        Assert.Equal(16, capture.DrawnWaterTriangles);
    }

    // Corner cuts whose mid-edge points sit on sloping edges shared with an uncut neighbour. The drawn bed is
    // continuous and single valued, so this ordinary content must publish and match the drawn triangles.
    [GpuFact]
    public void SlopedCornerCutTJunctionsPublishTheirDrawnTriangles()
    {
        TileWaterTopologyCapture capture = Verify(F.SlopedCornerCuts);
        Assert.Equal(16, capture.DrawnWaterTriangles);
    }

    // Feather subdivisions on a sloping shared edge with an ordinary non-dyadic height are T junctions too.
    [GpuFact]
    public void FeatherOverlaySubdivisionsFollowTheirDrawnTriangles()
    {
        TileWaterTopologyCapture capture = Verify(F.FeatherSubdivisions);
        Assert.True(capture.DrawnWaterTriangles > 64, $"Only {capture.DrawnWaterTriangles} drawn water triangles, so the feather did not subdivide.");
    }

    [GpuFact]
    public void DiagonalHalfOverlaysWithMaterialFollowTheirForcedDiagonals()
    {
        TileWaterTopologyCapture capture = Verify(F.DiagonalHalfOverlays);
        Assert.Equal(8, capture.DrawnWaterTriangles);
    }

    [GpuFact]
    public void CoarseInteriorAndTransitionFansFollowTheDrawnCoarseMesh() => Verify(F.CoarseInterior);

    [GpuFact]
    public void NeighbouringFullAndCoarseFieldsAgreeAcrossTheRegionBorder()
    {
        TileWaterTopologyCapture capture = Verify(F.FullCoarseBorder);
        var byPoint = capture.Admitted.GroupBy(d => (d.WorldX, d.WorldZ)).Where(g => g.Count() > 1).ToArray();
        Assert.NotEmpty(byPoint);
        foreach (var shared in byPoint)
        {
            Assert.True(shared.Key.WorldX == 64d, $"Only the region border is shared, not ({shared.Key.WorldX}, {shared.Key.WorldZ}).");
            float first = shared.First().GpuDepth;
            Assert.All(shared, d => Assert.True(Math.Abs(d.GpuDepth - first) <= 1e-6,
                $"Neighbouring fields disagree at ({d.WorldX}, {d.WorldZ}): {d.GpuDepth:R} and {first:R}."));
        }
        output.WriteLine($"{byPoint.Length} border points sampled by both neighbouring fields agree.");
    }

    [GpuFact]
    public void DistantOriginReducesToTheNearImageAndDepths()
    {
        TileWaterTopologyCapture near = Verify(F.NearBlock);
        TileWaterTopologyCapture distant = Verify(F.DistantBlock);
        Assert.True(distant.RenderOriginActive, "The distant frame must run with an active render origin.");
        Assert.Equal(new Vector3(64f * F.DistantRegionOffset, 0f, 0f), distant.RenderOrigin);
        Assert.Equal(new Vector3(64f * F.DistantRegionOffset, 0f, 0f) + near.Fields[0].Origin, distant.Fields[0].Origin);
        float authored = TileWaterTopologyCapture.MaximumRgbDistance(near.Images["authored"], distant.Images["authored"]);
        float opaque = TileWaterTopologyCapture.MaximumRgbDistance(near.Images["opaque"], distant.Images["opaque"]);
        float influence = TileWaterTopologyCapture.MaximumInfluenceDifference(near.Images["authored"], near.Images["opaque"],
            distant.Images["authored"], distant.Images["opaque"]);
        output.WriteLine($"Near and distant differ by at most {authored:R} RGB authored, {opaque:R} RGB opaque and "
            + $"{influence:R} in optical influence.");
        Assert.True(influence <= ParityRgbTolerance,
            $"The reduced distant water changes its opaque control differently from the near water, by {influence:R}.");
        var nearDepths = near.Admitted.ToDictionary(d => (d.WorldX + 64d * F.DistantRegionOffset, d.WorldZ), d => d.GpuDepth);
        int compared = 0;
        foreach (TileWaterDepthProbe d in distant.Admitted)
        {
            if (!nearDepths.TryGetValue((d.WorldX, d.WorldZ), out float expected)) continue;
            compared++;
            Assert.True(d.GpuDepth == expected, $"Distant depth {d.GpuDepth:R} at ({d.WorldX}, {d.WorldZ}) differs from near {expected:R}.");
        }
        Assert.True(compared > 50, $"Only {compared} distant probes had a near counterpart.");
    }

    [GpuFact]
    public void AgedTopologySceneMatchesFreshScene()
    {
        _ = fixture.Result(F.CornerCuts);
        TileWaterTopologyCapture aged = fixture.Capture(F.CornerCuts, "aged-" + F.CornerCuts.Name);
        TileWaterTopologyCapture fresh;
        using (var freshScene = new TileWorldWaterTopologyScene()) fresh = freshScene.Capture(F.CornerCuts, "fresh-" + F.CornerCuts.Name);
        aged.ThrowIfFailed();
        fresh.ThrowIfFailed();
        fixture.WriteSheet("aged-fresh-contactsheet", [aged.Images["authored"], fresh.Images["authored"]]);
        foreach (string image in new[] { "authored", "scene-depth", "opaque", "matched-control" })
        {
            float distance = TileWaterTopologyCapture.MaximumRgbDistance(aged.Images[image], fresh.Images[image]);
            output.WriteLine($"Aged and fresh {image} differ by at most {distance:R} RGB.");
            Assert.True(distance <= ParityRgbTolerance, $"Aged and fresh {image} differ by {distance:R}.");
        }
    }

    TileWaterTopologyCapture Verify(TileWaterTopologyCase c)
    {
        TileWaterTopologyCapture r = fixture.Result(c);
        output.WriteLine($"{c.Name} on {fixture.Backend} {fixture.DeviceName}: failures {r.DescribeFailures()}, "
            + $"fields {r.LiveFieldDelta}, uploads {r.UploadDelta}, texture bytes {r.LiveTextureByteDelta}, "
            + $"retained CPU bytes {r.LiveRetainedCpuByteDelta}, source bytes {r.SourceBytes.Resident}/{r.SourceBytes.Pending}, "
            + $"depth probes {r.Depths.Count}, oracle error {r.MaximumOracleError:R} ({r.MaximumBoundRatio:F3} of bound), witness deviation {r.MaximumWitnessDeviation:R}, "
            + $"control RGB {r.ControlMaximumRgb:R}, witness RGB {r.WitnessMaximumRgb:R}, "
            + $"deep visible {r.VisibleDeepProbes}/{r.ProjectedDeepProbes}, total {r.Milliseconds.GetValueOrDefault("Total"):F1} ms.");
        foreach (TileWaterFieldReport f in r.Fields)
            output.WriteLine($"  field origin {f.Origin} size {f.Size} bins {f.BinColumns}x{f.BinRows} (base {f.BaseColumns}x{f.BaseRows}, "
                + $"attempts {f.DerivedPackingAttempts}) bound {f.ReconstructionErrorBoundMetres:R} triangles {f.Triangles} K {f.MaximumBinOccupancy} texture "
                + $"{f.TextureWidth}x{f.TextureHeight} {f.TextureBytes} B retained {f.RetainedBytes} B order {f.SourceOrderMatched} {f.SourceOrderError}");
        r.ThrowIfFailed();
        Assert.True(r.Failures.Count == 0, $"{c.Name} was refused: {r.DescribeFailures()}.");
        Assert.Equal(c.ExpectedFields, r.LiveFieldDelta);
        if (!c.Profiled) Assert.Equal(c.ExpectedFields, r.UploadDelta);
        Assert.Equal(0, r.LiveFieldsAfterRelease);
        Assert.Equal(c.ExpectedFields, r.Fields.Count);
        Assert.True(r.SourceBytes.Resident > 0 && r.SourceBytes.Pending == 0,
            $"Retained source bytes {r.SourceBytes.Resident}/{r.SourceBytes.Pending}.");
        foreach (TileWaterFieldReport f in r.Fields)
        {
            // TileWorld fields store rest depth against the exact datum 0, so the binding reference Y is zero.
            Assert.Equal(0f, f.Origin.Y);
            Assert.True(f.SourceOrderError is null, f.SourceOrderError);
            Assert.Equal(f.Triangles, f.SourceOrderMatched);
            Assert.InRange(f.DerivedPackingAttempts, 1, 11);
            Assert.True(f.ReconstructionErrorBoundMetres > 0 && f.ReconstructionErrorBoundMetres <= DepthBudget,
                $"Certified bound {f.ReconstructionErrorBoundMetres:R} m is outside the {DepthBudget} m budget.");
        }

        TileWaterDepthProbe[] admitted = r.Admitted.ToArray();
        Assert.True(admitted.Length > 8, $"{c.Name} sampled only {admitted.Length} admitted points.");
        TileWaterDepthProbe[] missed = admitted.Where(d => !d.Hit).ToArray();
        Assert.True(missed.Length == 0, $"{c.Name}: {missed.Length} admitted points missed, first at "
            + $"({missed.FirstOrDefault().WorldX}, {missed.FirstOrDefault().WorldZ}).");
        TileWaterDepthProbe worst = admitted.MaxBy(d => Math.Abs(d.GpuDepth - d.OracleDepth) / d.Bound);
        Assert.True(Math.Abs(worst.GpuDepth - worst.OracleDepth) <= worst.Bound,
            $"{c.Name} at ({worst.WorldX}, {worst.WorldZ}): GPU depth {worst.GpuDepth:R}, drawn-mesh oracle "
            + $"{worst.OracleDepth:R}, certified bound {worst.Bound:R}.");
        Assert.All(r.Depths.Where(d => d.Kind == "miss"), d => Assert.False(d.Hit, "A point outside the field reported a hit."));
        Assert.True(r.MaximumWitnessDeviation > WitnessSeparation,
            $"{c.Name} witness {c.Witness} deviates by only {r.MaximumWitnessDeviation:R} m, so the depth check is vacuous.");

        Assert.True(r.ControlMaximumRgb <= EquivalentRgbTolerance,
            $"{c.Name} authored image differs from the independent drawn-triangle field by {r.ControlMaximumRgb:R} RGB.");
        Assert.True(r.WitnessMaximumRgb > EquivalentRgbTolerance,
            $"{c.Name} witness {c.Witness} image differs by only {r.WitnessMaximumRgb:R} RGB, so the image check is vacuous.");
        Assert.True(r.VisibleDeepProbes > 0, $"{c.Name} shows no visible water over {r.ProjectedDeepProbes} deep points.");
        return r;
    }
}
