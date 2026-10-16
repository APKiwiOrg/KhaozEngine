using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageHistoryNegative
{
    internal static void AssertPartialSkin(CoverageCapture actual, CoverageCapture control, CoverageHostFootprint before)
    {
        var source = control.ReconstructionColour!;
        var output = actual.ResolvedColour!;
        var cold = control.ResolvedColour!;
        var mask = actual.RejectionMask!;
        for (int y = 1; y < output.Height - 1; y++)
            for (int x = 1; x < output.Width - 1; x++)
            {
                var (cx, cy) = CoverageGhostEnvelope.CentreTexel(x, y, output.Width, output.Height,
                    source.Width, source.Height, actual.ResolveJitter);
                if (mask.Values[cy * mask.Width + cx] == 0) continue;
                bool interior = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int t = Math.Clamp(cy + dy, 0, source.Height - 1) * source.Width + Math.Clamp(cx + dx, 0, source.Width - 1);
                        interior &= source.Values[t * source.Channels + 2] > source.Values[t * source.Channels] * 2
                            && Math.Abs(control.Depth.Values[t] - .02f) > 1e-6f;
                    }
                if (!interior) continue;
                int texel = cy * source.Width + cx, at = (y * output.Width + x) * output.Channels;
                int oldAt = texel * before.Colour.Channels;
                Vector3 skin = new(before.Colour.Values[oldAt], before.Colour.Values[oldAt + 1], before.Colour.Values[oldAt + 2]);
                Vector3 plate = new(cold.Values[at], cold.Values[at + 1], cold.Values[at + 2]);
                if (!(skin.X > skin.Z * 1.4f && plate.Z > plate.X * 2)) continue;
                Vector3 ghost = Vector3.Lerp(plate, skin, .25f);
                if (!(ghost.Z > ghost.X * 2 && ghost.X - plate.X > 50f / 255)) continue;
                var values = (float[])output.Values.Clone();
                (values[at], values[at + 1], values[at + 2]) = (ghost.X, ghost.Y, ghost.Z);
                var input = new CoverageHistoryInputs(null, actual.Motion!.Value, actual.ResolveUniforms, mask);
                var result = CoverageGhostEnvelope.Evaluate(output with { Values = values }, actual.ResolvedState,
                    source, control.Depth, actual.ResolveJitter, input, cold);
                Assert.Equal(1, result.Violations);
                CoverageArtifacts.WriteRaw($"{actual.Artifact}.partial-skin-negative.f32", values);
                CoverageArtifacts.WriteJson($"{actual.Artifact}.partial-skin-negative.json", new
                {
                    x,
                    y,
                    cx,
                    cy,
                    Blend = .25f,
                    Rejection = mask.Values[texel],
                    Plate = new[] { plate.X, plate.Y, plate.Z },
                    Skin = new[] { skin.X, skin.Y, skin.Z },
                    Injected = new[] { ghost.X, ghost.Y, ghost.Z },
                    result.Violations,
                    result.Residuals,
                });
                Console.WriteLine($"coverage partial-skin-negative={actual.Artifact} pixel={x},{y} blend=.25 "
                    + $"sameClass=true redDeltaBytes={(ghost.X - plate.X) * 255} rejection={mask.Values[texel]} violations={result.Violations}");
                return;
            }
        Assert.Fail($"No masked same-class partial-skin interior witness: {actual.Artifact}");
    }

    internal static void AssertWidest(CoverageGhostEnvelope.Result positive, CoverageCapture actual, CoverageCapture control,
        CoverageHistoryInputs input)
    {
        var bound = Assert.IsType<CoverageGhostEnvelope.HeldBound>(positive.WidestHeld);
        var output = actual.ResolvedColour!;
        int at = (bound.Y * output.Width + bound.X) * output.Channels;
        var cold = control.ResolvedColour!;
        Vector3 current = TemporalResolveMath.ToWeighted(new(cold.Values[at], cold.Values[at + 1], cold.Values[at + 2]));
        float co = .5f * current.X - .5f * current.Z, cg = -.25f * current.X + .5f * current.Y - .25f * current.Z;
        Vector3 weighted = CoverageJointHistory.Rgb(new(bound.Hi.X + .05f, co, cg));
        Assert.True(weighted.X > 0 && weighted.Y > 0 && weighted.Z > 0
            && Vector3.Dot(weighted, CoverageJointHistory.Rec709) < 1 - 1e-4f,
            $"Local luma bound is too broad for a finite bright same-chroma negative: {actual.Artifact} Y={bound.Hi.X}");
        Vector3 ghost = TemporalResolveMath.FromWeighted(weighted);
        var values = (float[])output.Values.Clone();
        (values[at], values[at + 1], values[at + 2]) = (ghost.X, ghost.Y, ghost.Z);
        var result = CoverageGhostEnvelope.Evaluate(output with { Values = values }, actual.ResolvedState,
            control.ReconstructionColour!, control.Depth, actual.ResolveJitter, input, cold);
        Assert.Equal(1, result.Violations);
        CoverageArtifacts.WriteRaw($"{actual.Artifact}.widest-held-bright-negative.f32", values);
        CoverageArtifacts.WriteJson($"{actual.Artifact}.widest-held-bright-negative.json", new
        {
            bound.X,
            bound.Y,
            PriorY = new[] { bound.PriorY.X, bound.PriorY.Y },
            AllowedY = new[] { bound.Lo.X, bound.Hi.X },
            InjectedY = bound.Hi.X + .05f,
            IndependentCurrent = new[] { cold.Values[at], cold.Values[at + 1], cold.Values[at + 2] },
            InjectedRgb = new[] { ghost.X, ghost.Y, ghost.Z },
            result.Violations,
            result.Residuals,
        });
        Console.WriteLine($"coverage widest-held-negative={actual.Artifact} pixel={bound.X},{bound.Y} "
            + $"priorY={bound.PriorY.X},{bound.PriorY.Y} injectedY={bound.Hi.X + .05f} violations={result.Violations}");
    }
}
