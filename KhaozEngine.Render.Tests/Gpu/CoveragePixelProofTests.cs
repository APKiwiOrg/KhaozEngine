using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Pure regressions for the native coverage oracles, on small synthetic targets with no device.</summary>
public sealed class CoveragePixelProofTests
{
    const int W = 40, H = 30;
    static readonly Vector3 Plate = new Vector3(46, 255, 255) / 255f, Skin = new Vector3(255, 160, 80) / 255f;

    [Fact]
    public void Partial_same_class_skin_history_is_a_ghost()
    {
        // A quarter of skin keeps the plate class (B > 2R) yet moves every channel well beyond one byte.
        Vector3 ghost = Vector3.Lerp(Plate, Skin, .25f);
        Assert.True(ghost.Z > 2 * ghost.X);
        Assert.True(MathF.Abs(ghost.X - Plate.X) * 255 > 50);
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        var output = Output(source, (x, y) => x is >= 6 and < 10 && y is >= 12 and < 16 ? ghost : null);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => Envelope(output, null, source, Depth((_, _) => false)));
        Assert.StartsWith("Ghost colour witnesses 16:", failure.Message);
    }

    [Fact]
    public void Original_skin_on_a_plate_interior_is_a_ghost()
    {
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        var output = Output(source, (x, y) => x == 8 && y == 14 ? Skin : null);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => Envelope(output, null, source, Depth((_, _) => false)));
        Assert.StartsWith("Ghost colour witnesses 1:", failure.Message);
    }

    [Fact]
    public void Edge_reconstruction_inside_the_current_neighbourhood_passes()
    {
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        Vector3 blend = TemporalResolveMath.FromWeighted(Vector3.Lerp(TemporalResolveMath.ToWeighted(Plate),
            TemporalResolveMath.ToWeighted(Skin), .5f));
        var output = Output(source, (x, _) => x is W / 2 - 1 or W / 2 ? blend : null);
        var result = Envelope(output, null, source, Depth((_, _) => false));
        Assert.Equal(0, result.Violations);
        Assert.Equal(2 * H, result.EdgePixels);
    }

    [Fact]
    public void Quantized_background_is_not_a_plate_witness()
    {
        // The clear colour resolving one byte down meets the plate ratio. Depth, not colour, marks it background.
        Vector3 quantized = new Vector3(4, 38, 9) / 255f;
        Assert.True(quantized.Z > 2 * quantized.X);
        var source = Source((x, _) => x < W / 2 ? quantized : Skin);
        var depth = Depth((x, _) => x < W / 2);
        Assert.Equal(0, CoverageGhostEnvelope.Evaluate(source, null, source, depth, Vector2.Zero).PlateWitnesses);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => Envelope(source, null, source, depth));
        Assert.StartsWith("No interior plate colour proof", failure.Message);
    }

    [Fact]
    public void A_held_history_pixel_is_rejected_when_the_current_surface_is_empty()
    {
        Vector3 background = new Vector3(5, 38, 9) / 255f;
        var source = Source((_, _) => background);
        Vector3 ycc = YCoCg(TemporalResolveMath.ToWeighted(background));
        Vector3 stale = TemporalResolveMath.FromWeighted(Rgb(ycc + new Vector3(.1f, 0, 0)));
        var output = Output(source, (x, y) => x == 8 && y == 14 ? stale : null);
        var held = State((x, y) => x == 8 && y == 14 ? 1f : 0f);
        var result = CoverageGhostEnvelope.Evaluate(output, held, source, Depth((_, _) => true), Vector2.Zero);
        Assert.Equal(1, result.Violations);
    }

    [Fact]
    public void A_zero_clamped_held_pixel_is_not_skipped()
    {
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        var output = Output(source, (x, y) => x == 8 && y == 14 ? new Vector3(0, 1, 0) : null);
        var held = State((x, y) => x == 8 && y == 14 ? 1f : 0f);
        Assert.Equal(1, CoverageGhostEnvelope.Evaluate(output, held, source, Depth((_, _) => false), Vector2.Zero).Violations);
    }

    [Fact]
    public void A_held_thin_feature_uses_only_its_independent_local_history()
    {
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        Vector3 ycc = YCoCg(TemporalResolveMath.ToWeighted(Plate));
        Vector3 brighter = TemporalResolveMath.FromWeighted(Rgb(ycc + new Vector3(.05f, 0, 0)));
        bool Line(int x, int y) => x == 8 && y is >= 12 and < 16;
        var prior = Output(source, (x, y) => Line(x, y) ? brighter : null);
        var depth = Depth((_, _) => false);
        var bank = CoverageGhostEnvelope.Evaluate(prior, null, prior, depth, Vector2.Zero).History;
        var input = History(bank);
        var held = State((x, y) => Line(x, y) ? 1f : 0f);
        Assert.Equal(0, CoverageGhostEnvelope.Evaluate(prior, held, source, depth, Vector2.Zero, input).Violations);
        Assert.Equal(4, CoverageGhostEnvelope.Evaluate(prior, held, source, depth, Vector2.Zero).Violations);
        // Even valid held history cannot carry a quarter of skin into the plate's current chroma.
        var ghost = Output(source, (x, y) => Line(x, y) ? Vector3.Lerp(Plate, Skin, .25f) : null);
        Assert.Equal(4, CoverageGhostEnvelope.Evaluate(ghost, held, source, depth, Vector2.Zero, input).Violations);
        // A brighter sample with the exact current chroma also fails beyond this pixel's supported prior luma.
        var tooBright = Output(source, (x, y) => Line(x, y)
            ? TemporalResolveMath.FromWeighted(Rgb(ycc + new Vector3(.2f, 0, 0))) : null);
        Assert.Equal(4, CoverageGhostEnvelope.Evaluate(tooBright, held, source, depth, Vector2.Zero, input).Violations);
        // Clearing identity or a nonzero rejection mask makes even the legitimate prior colour invalid.
        Assert.Equal(4, CoverageGhostEnvelope.Evaluate(prior, held, source, depth, Vector2.Zero,
            input with { Mask = Mask(Line) }).Violations);
        var invalid = input.Resolve;
        invalid.Jitter.W = 0;
        Assert.Equal(4, CoverageGhostEnvelope.Evaluate(prior, held, source, depth, Vector2.Zero,
            input with { Resolve = invalid }).Violations);
    }

    [Fact]
    public void Unrelated_bright_history_does_not_support_a_same_chroma_ghost()
    {
        var source = Source((x, _) => x < W / 2 ? Plate : Skin);
        Vector3 ycc = YCoCg(TemporalResolveMath.ToWeighted(Plate));
        Vector3 brighter = TemporalResolveMath.FromWeighted(Rgb(ycc + new Vector3(.1f, 0, 0)));
        var distant = Output(source, (x, y) => x == 30 && y is >= 12 and < 16 ? brighter : null);
        var depth = Depth((_, _) => false);
        var bank = CoverageGhostEnvelope.Evaluate(distant, null, distant, depth, Vector2.Zero).History;
        var ghost = Output(source, (x, y) => x == 8 && y == 14 ? brighter : null);
        var held = State((x, y) => x == 8 && y == 14 ? 1f : 0f);
        Assert.Equal(1, CoverageGhostEnvelope.Evaluate(ghost, held, source, depth, Vector2.Zero, History(bank)).Violations);
        // The next bank cannot learn from that tested contamination.
        var cleanBank = CoverageGhostEnvelope.Evaluate(source, held, source, depth, Vector2.Zero, History(bank)).History;
        var testedBank = CoverageGhostEnvelope.Evaluate(ghost, held, source, depth, Vector2.Zero, History(bank)).History;
        Assert.Equal(cleanBank.Pixels, testedBank.Pixels);
        Assert.Equal(cleanBank.PossibleLocks, testedBank.PossibleLocks);
        Assert.Equal(cleanBank.Luma, testedBank.Luma);
    }

    [Fact]
    public void Joint_chroma_rejects_an_impossible_independent_rgb_combination()
    {
        Vector3 ycc = YCoCg(TemporalResolveMath.ToWeighted(Plate));
        Vector3 lo = new(.1f, ycc.Y - 1e-5f, ycc.Z - 1e-5f);
        Vector3 hi = new(.8f, ycc.Y + 1e-5f, ycc.Z + 1e-5f);
        Vector3 ghost = TemporalResolveMath.ToWeighted(Vector3.Lerp(Plate, Skin, .25f));
        Vector3 rgbLo = Vector3.Max(new Vector3(lo.X + lo.Y - hi.Z, lo.X + lo.Z, lo.X - hi.Y - hi.Z), Vector3.Zero);
        Vector3 rgbHi = Vector3.Max(new Vector3(hi.X + hi.Y - lo.Z, hi.X + hi.Z, hi.X - lo.Y - lo.Z), Vector3.Zero);
        Assert.Equal(ghost, Vector3.Clamp(ghost, rgbLo, rgbHi));
        Assert.False(CoverageClampedColour.Contains(ghost, lo, hi, CoverageGhostEnvelope.Tolerance));
    }

    [Fact]
    public void Zero_clamp_feasibility_checks_all_channels_together()
    {
        Vector3 lo = new(.2f - 1e-5f, -.3f - 1e-5f, -1e-5f);
        Vector3 hi = new(.2f + 1e-5f, -.3f + 1e-5f, 1e-5f);
        Assert.True(CoverageClampedColour.Contains(new(0, .2f, .5f), lo, hi, CoverageGhostEnvelope.Tolerance));
        Assert.False(CoverageClampedColour.Contains(new(0, .2f, .55f), lo, hi, CoverageGhostEnvelope.Tolerance));
    }

    [Fact]
    public void Joint_history_does_not_invent_a_denominator_floor_crossing()
    {
        // The first native wrapping witness: independent RGB maxima cross one, the actual joint luma stays below .918.
        Vector3 lo = new(.021073539f, -.3093422f, -.0602459f), hi = new(.8080025f, .3570581f, .13741808f);
        var joint = CoverageJointHistory.Bounds(lo, hi);
        Assert.InRange(joint.Rgb.Max.X, 0, 13);
        Assert.InRange(joint.Rgb.Max.Y, 0, 13);
        Assert.InRange(joint.Rgb.Max.Z, 0, 13);
        var bank = new CoverageLocalHistory(W, H);
        bank.Store(14 * W + 8, lo, hi, 1);
        Vector2 uv = new((8.5f) / W, (14.5f) / H);
        Assert.Equal(Vector3.Zero, CoverageHistoryDisplacement.Bound(bank, uv, 14 * W + 8));
        var sample = bank.Sample(uv);
        var y = bank.SampleY(uv, sample.Colour);
        Assert.InRange(y.Y, 0, .9f);
        Assert.True(CoverageLocalHistory.WeightedY(sample.Colour).Y > y.Y);
    }

    [Fact]
    public void An_incomplete_mask_misses_a_changed_collar_band()
    {
        // The collar moves inward in a band of skin. The resolve's 3x3 makes its neighbours required too.
        bool Body(int x, int y) => x is >= 8 and < 32 && y is >= 6 and < 24;
        var colour = Source((x, y) => Body(x, y) ? Skin : new Vector3(5, 38, 9) / 255f);
        var before = new CoverageHostFootprint(colour, DepthValues((x, y) => Body(x, y) ? .5f : .02f), Vector2.Zero);
        var after = new CoverageHostFootprint(colour, DepthValues((x, y) => !Body(x, y) ? .02f
            : x is >= 14 and < 17 ? .52f : .5f), Vector2.Zero);
        var full = Mask((x, y) => x is >= 12 and < 19 && y is >= 5 and < 25);
        var complete = CoverageMaskCompleteness.Assert(before, after, full, W, H, Vector2.Zero, "synthetic-complete");
        // Columns 13 to 17 over rows 5 to 24 are required, the moved columns 14 to 16 over rows 6 to 23 are surface.
        Assert.Equal(5 * 20, complete.Required);
        Assert.Equal(3 * 18, complete.SurfaceWitnesses);
        Assert.Equal(5 * 20 - 3 * 18, complete.EdgeWitnesses);
        var band = Mask((x, y) => x is >= 12 and < 19 && x != 15 && y is >= 5 and < 25);
        var failure = Assert.Throws<Xunit.Sdk.TrueException>(() =>
            CoverageMaskCompleteness.Assert(before, after, band, W, H, Vector2.Zero, "synthetic-band"));
        Assert.StartsWith("Incomplete coverage mask 20:", failure.Message);
    }

    [Fact]
    public void Raw_evidence_needs_its_own_explicit_directory()
    {
        Assert.NotEqual("KE_GOLDEN_EVIDENCE_DIR", CoverageArtifacts.DirectoryVariable);
        Assert.Null(CoverageArtifacts.Resolve(null));
        Assert.Null(CoverageArtifacts.Resolve(""));
        Assert.Null(CoverageArtifacts.Resolve("  "));
        Assert.Equal("/evidence", CoverageArtifacts.Resolve("/evidence"));
    }

    static CoverageGhostEnvelope.Result Envelope(CoverageTargetReadback output, CoverageTargetReadback? state,
        CoverageTargetReadback source, CoverageTargetReadback depth) =>
        CoverageGhostEnvelope.Assert(output, state, source, depth, Vector2.Zero, "synthetic", requireHost: true, requirePlate: true);

    static CoverageTargetReadback Source(Func<int, int, Vector3> colour)
    {
        var values = new float[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                Vector3 c = colour(x, y);
                int i = (y * W + x) * 4;
                (values[i], values[i + 1], values[i + 2], values[i + 3]) = (c.X, c.Y, c.Z, 1);
            }
        return new(W, H, 4, values);
    }

    static CoverageTargetReadback Output(CoverageTargetReadback source, Func<int, int, Vector3?> replace)
    {
        var values = (float[])source.Values.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (replace(x, y) is { } c)
                {
                    int i = (y * W + x) * 4;
                    (values[i], values[i + 1], values[i + 2]) = (c.X, c.Y, c.Z);
                }
        return source with { Values = values };
    }

    static CoverageTargetReadback Depth(Func<int, int, bool> background) =>
        DepthValues((x, y) => background(x, y) ? .02f : .5f);

    static CoverageTargetReadback DepthValues(Func<int, int, float> depth)
    {
        var values = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) values[y * W + x] = depth(x, y);
        return new(W, H, 1, values);
    }

    static CoverageTargetReadback State(Func<int, int, float> lockValue)
    {
        var values = new float[W * H * 2];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) values[(y * W + x) * 2 + 1] = lockValue(x, y);
        return new(W, H, 2, values);
    }

    static CoverageMaskReadback Mask(Func<int, int, bool> rejected)
    {
        var values = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++) values[y * W + x] = rejected(x, y) ? (byte)255 : (byte)0;
        return new(W, H, values);
    }

    static CoverageHistoryInputs History(CoverageLocalHistory bank) => new(bank,
        new(new Vector2[W * H], W, H), new TemporalResolveUniforms
        {
            CurrentToPrevious = Matrix4x4.Identity,
            PreviousProjection = Matrix4x4.Identity,
            BackgroundToPrevious = Matrix4x4.Identity,
            CurrentDepth = new(0, 0, 1, 0),
            Sizes = new(W, H, W, H),
            Jitter = new(0, 0, 1, 1),
            Params = new(0, 1, 0, 0),
        }, null);

    static Vector3 YCoCg(Vector3 c) => new(.25f * c.X + .5f * c.Y + .25f * c.Z, .5f * c.X - .5f * c.Z,
        -.25f * c.X + .5f * c.Y - .25f * c.Z);

    static Vector3 Rgb(Vector3 c) => new(c.X + c.Y - c.Z, c.X + c.Z, c.X - c.Y - c.Z);
}
