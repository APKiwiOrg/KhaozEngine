using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The actual R8 coverage rejection mask, at the internal size the resolve samples it.</summary>
internal sealed record CoverageMaskReadback(int Width, int Height, byte[] Values);

/// <summary>A host-only current-surface control: the source colour and depth the resolve would gather.</summary>
internal sealed record CoverageHostFootprint(CoverageTargetReadback Colour, CoverageTargetReadback Depth, Vector2 Jitter);

/// <summary>
/// Proves the coverage rejection mask covers every display pixel whose reconstruction footprint the host surface
/// change touched. The before and after controls draw the host alone, at the same frame, pose, camera and jitter,
/// in its previous and its new coverage identity, so equipment, camera motion and stable pose changes contribute
/// nothing. A display pixel is required when any texel of the exact 3x3 the resolve gathers differs by more than one
/// colour byte or the R32 depth tolerance. The resolve reads the mask at that 3x3's centre texel.
/// </summary>
internal static class CoverageMaskCompleteness
{
    internal sealed record Result(int Required, int SurfaceWitnesses, int EdgeWitnesses, int Missing,
        (int X, int Y)? FirstSurface, List<object> MissingPixels)
    {
        internal int RemovedWitnesses { get; init; }
    }

    internal static Result Assert(CoverageHostFootprint before, CoverageHostFootprint after, CoverageMaskReadback mask,
        int displayWidth, int displayHeight, Vector2 jitter, string artifact)
    {
        var result = Evaluate(before, after, mask, displayWidth, displayHeight, jitter);
        CoverageArtifacts.WriteJson($"{artifact}.mask-completeness.json", new
        {
            Artifact = artifact,
            result.Required,
            result.SurfaceWitnesses,
            result.EdgeWitnesses,
            result.RemovedWitnesses,
            result.Missing,
            result.MissingPixels,
        });
        Console.WriteLine($"coverage mask-completeness={artifact} required={result.Required} surface={result.SurfaceWitnesses} "
            + $"edge={result.EdgeWitnesses} removed={result.RemovedWitnesses} missing={result.Missing}");
        Xunit.Assert.True(result.Missing == 0, $"Incomplete coverage mask {result.Missing}: {artifact}");
        return result;
    }

    internal static Result Evaluate(CoverageHostFootprint before, CoverageHostFootprint after, CoverageMaskReadback mask,
        int displayWidth, int displayHeight, Vector2 jitter)
    {
        var colour = after.Colour;
        Xunit.Assert.Equal((colour.Width, colour.Height), (before.Colour.Width, before.Colour.Height));
        Xunit.Assert.Equal((colour.Width, colour.Height), (after.Depth.Width, after.Depth.Height));
        Xunit.Assert.Equal((colour.Width, colour.Height), (mask.Width, mask.Height));
        Xunit.Assert.Equal(jitter, before.Jitter);
        Xunit.Assert.Equal(jitter, after.Jitter);
        int texels = colour.Width * colour.Height;
        var changed = new bool[texels];
        var surface = new bool[texels];
        for (int t = 0; t < texels; t++)
        {
            bool colourChanged = false;
            for (int c = 0; c < 3; c++)
                colourChanged |= MathF.Abs(before.Colour.Values[t * colour.Channels + c] - colour.Values[t * colour.Channels + c]) > CoveragePixelProof.OpaqueColourTolerance;
            bool depthChanged = MathF.Abs(before.Depth.Values[t] - after.Depth.Values[t]) > CoveragePixelProof.DepthTolerance;
            changed[t] = colourChanged || depthChanged;
            // The collar and recessed core: skin in both identities at a moved depth.
            surface[t] = depthChanged && Host(before.Colour, t) && Host(colour, t);
        }
        int required = 0, surfaceWitnesses = 0, edgeWitnesses = 0, removedWitnesses = 0, missing = 0;
        (int, int)? firstSurface = null;
        var missingPixels = new List<object>();
        for (int y = 0; y < displayHeight; y++)
            for (int x = 0; x < displayWidth; x++)
            {
                var (cx, cy) = CoverageGhostEnvelope.CentreTexel(x, y, displayWidth, displayHeight, colour.Width, colour.Height, jitter);
                bool touched = false;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        touched |= changed[Math.Clamp(cy + dy, 0, colour.Height - 1) * colour.Width + Math.Clamp(cx + dx, 0, colour.Width - 1)];
                if (!touched) continue;
                required++;
                int centre = cy * colour.Width + cx;
                if (surface[centre])
                {
                    surfaceWitnesses++;
                    firstSurface ??= (x, y);
                }
                if (MathF.Abs(before.Depth.Values[centre] - .02f) > 1e-6f
                    && MathF.Abs(after.Depth.Values[centre] - .02f) <= 1e-6f) removedWitnesses++;
                if (!changed[centre]) edgeWitnesses++;
                if (mask.Values[centre] != 0) continue;
                missing++;
                missingPixels.Add(new { x, y, cx, cy });
            }
        return new(required, surfaceWitnesses, edgeWitnesses, missing, firstSurface, missingPixels) { RemovedWitnesses = removedWitnesses };
    }

    static bool Host(CoverageTargetReadback colour, int t) =>
        colour.Values[t * colour.Channels] > colour.Values[t * colour.Channels + 2] * 1.4f;
}
