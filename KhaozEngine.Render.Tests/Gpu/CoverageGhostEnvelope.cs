using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The no-ghost colour oracle at the temporal resolve's own output, before target outlines are composed.
/// <para>The resolve (<c>ShaderSources.TemporalAccumulate</c>) gathers the exact 3x3 around
/// <c>floor((pixel + 0.5) / display * internal + jitter)</c>, clamped to the target. In luma-weighted YCoCg, rounded to
/// half as <c>temporalPrepared</c> stores it, it clamps the Lanczos reconstruction, the display-kernel colour and the
/// moving-share colour to that 3x3's range, and clips history into its variance box, mean plus or minus gamma
/// deviations, which is widest at <see cref="TemporalResolveTuning.GammaStill"/>. Every blend of those lies in the
/// per-channel hull of the range and that box. A locked thin feature can retain only the luma supported by the independently bounded history
/// sampled through the source Catmull-Rom filter and its four-texel clamp. Chroma always uses the current hull. The output is <c>fromWeighted(max(yCoCgToRgb(resolved), 0))</c>, so its weighted
/// RGB lies in the hull's RGB intervals clamped at zero.</para>
/// <para>The 3x3 comes from the current-only control's source, so a stale colour the clip could not produce from the
/// current input fails wherever it lies, interior or edge, whatever its material class.</para>
/// </summary>
internal static class CoverageGhostEnvelope
{
    // Inputs are rounded to half exactly as the shader rounds them. The stored half output differs from the resolved
    // value by at most 2^-11 of a weighted value below one, inside the unchanged half temporal colour tolerance.
    internal const float Tolerance = .001f;
    const float BoxPadding = 1.0e-5f;
    const float Background = .02f;

    internal sealed record HeldBound(int X, int Y, Vector3 Lo, Vector3 Hi, Vector2 PriorY);

    internal sealed record Result(int Witnesses, int HostWitnesses, int PlateWitnesses, int EdgePixels, int HeldPixels,
        int Violations, List<object> Residuals)
    {
        internal HeldBound? WidestHeld { get; init; }
        internal CoverageLocalHistory History { get; init; } = null!;
        internal int EligibleHistory { get; init; }
        internal int OwnReprojected { get; init; }
        internal int RejectedHistory { get; init; }
        internal int HeldWithoutPrior { get; init; }
        internal int HeldLumaOutside { get; init; }
        internal float HeldLumaExcess { get; init; }

    }

    internal static Result Assert(CoverageTargetReadback output, CoverageTargetReadback? state,
        CoverageTargetReadback source, CoverageTargetReadback depth, Vector2 jitter, string artifact,
        bool requireHost, bool requirePlate, CoverageHistoryInputs? history = null, CoverageTargetReadback? reset = null)
    {
        var result = Evaluate(output, state, source, depth, jitter, history, reset);
        result.History.Write(artifact);
        CoverageArtifacts.WriteJson($"{artifact}.ghost-envelope.json", new
        {
            Artifact = artifact,
            result.Witnesses,
            result.HostWitnesses,
            result.PlateWitnesses,
            result.EdgePixels,
            result.HeldPixels,
            result.EligibleHistory,
            result.OwnReprojected,
            result.RejectedHistory,
            result.HeldWithoutPrior,
            result.HeldLumaOutside,
            result.HeldLumaExcess,
            result.Violations,
            Tolerance,
            BankWidth = result.History.Width,
            BankHeight = result.History.Height,
            BankChannels = new[] { "minR", "minG", "minB", "maxR", "maxG", "maxB", "possibleLock", "eligibility", "minWeightedY", "maxWeightedY" },
            Eligibility = "0 current only, 1 prior nearest, 2 prior own, -1 rejected",
            ConditionedInputs = "captured current motion and depth, resolve view matrices and history-valid flag",
            result.Residuals,
        });
        Console.WriteLine($"coverage ghost-envelope={artifact} witnesses={result.Witnesses} host={result.HostWitnesses} "
            + $"plate={result.PlateWitnesses} edge={result.EdgePixels} held={result.HeldPixels} eligible={result.EligibleHistory} own={result.OwnReprojected} rejected={result.RejectedHistory} unsupportedHeld={result.HeldWithoutPrior} heldLumaOutside={result.HeldLumaOutside} "
            + $"heldLumaExcess={result.HeldLumaExcess} violations={result.Violations}");
        Xunit.Assert.True(result.Witnesses > 100, $"No homogeneous colour witness: {artifact}");
        if (requireHost) Xunit.Assert.True(result.HostWitnesses > 100, $"No protected colour interior: {artifact}");
        if (requirePlate) Xunit.Assert.True(result.PlateWitnesses > 100, $"No interior plate colour proof: {artifact}");
        Xunit.Assert.True(result.Violations == 0, $"Ghost colour witnesses {result.Violations}: {artifact}");
        return result;
    }

    internal static Result Evaluate(CoverageTargetReadback output, CoverageTargetReadback? state,
        CoverageTargetReadback source, CoverageTargetReadback depth, Vector2 jitter, CoverageHistoryInputs? history = null, CoverageTargetReadback? reset = null)
    {
        Xunit.Assert.Equal((source.Width, source.Height), (depth.Width, depth.Height));
        int texels = source.Width * source.Height;
        var prepared = new Vector3[texels];
        var kind = new int[texels];
        var controlY = new Vector2[texels];
        for (int t = 0; t < texels; t++)
        {
            var colour = new Vector3(source.Values[t * source.Channels], source.Values[t * source.Channels + 1],
                source.Values[t * source.Channels + 2]);
            var ycc = YCoCg(TemporalResolveMath.ToWeighted(Vector3.Clamp(colour, Vector3.Zero, new Vector3(65504f))));
            prepared[t] = new(Half(ycc.X), Half(ycc.Y), Half(ycc.Z));
            var yBound = CoverageLocalHistory.ControlY(colour);
            controlY[t] = new(Half(yBound.X), Half(yBound.Y));
            kind[t] = MathF.Abs(depth.Values[t] - Background) < 1e-6f ? -1
                : colour.X > colour.Z * 1.4f ? 1 : colour.Z > colour.X * 2f ? 2 : 0;
        }
        int witnesses = 0, host = 0, plate = 0, edge = 0, held = 0, violations = 0, heldLumaOutside = 0;
        float heldLumaExcess = 0;
        int eligible = 0, own = 0, rejected = 0, unsupportedHeld = 0;
        var next = new CoverageLocalHistory(output.Width, output.Height);
        HeldBound? widest = null;
        var residuals = new List<object>();
        for (int y = 0; y < output.Height; y++)
            for (int x = 0; x < output.Width; x++)
            {
                var (cx, cy) = CentreTexel(x, y, output.Width, output.Height, source.Width, source.Height, jitter);
                Vector3 min = new(float.MaxValue), max = new(float.MinValue), sum = Vector3.Zero, squares = Vector3.Zero;
                int centreKind = kind[cy * source.Width + cx];
                bool homogeneous = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int t = Math.Clamp(cy + dy, 0, source.Height - 1) * source.Width + Math.Clamp(cx + dx, 0, source.Width - 1);
                        Vector3 ycc = prepared[t];
                        min = Vector3.Min(min, ycc);
                        max = Vector3.Max(max, ycc);
                        sum += ycc;
                        squares += ycc * ycc;
                        homogeneous &= kind[t] == centreKind;
                    }
                Vector3 mean = sum / 9f;
                Vector3 deviation = Vector3.SquareRoot(Vector3.Max(squares / 9f - mean * mean, Vector3.Zero));
                Vector3 lo = Vector3.Min(min, mean - TemporalResolveTuning.GammaStill * deviation - new Vector3(BoxPadding));
                Vector3 hi = Vector3.Max(max, mean + TemporalResolveTuning.GammaStill * deviation + new Vector3(BoxPadding));
                if (homogeneous)
                {
                    witnesses++;
                    if (centreKind == 1) host++;
                    if (centreKind == 2) plate++;
                }
                else edge++;

                int i = y * output.Width + x;
                bool ridge = PossibleRidge(cx, cy, source.Width, source.Height, controlY);
                bool rejectedHere = history?.Mask is { } mask && mask.Values[cy * mask.Width + cx] != 0;
                if (rejectedHere) rejected++;
                bool readable = history?.Previous is not null && history.Resolve.Jitter.W > .5f && !rejectedHere;
                Vector2 priorY = default;
                float possibleLock = ridge ? 1 : 0;
                bool supported = false;
                CoverageHistoryReprojection.Site site = default;
                if (readable)
                {
                    site = CoverageHistoryReprojection.At(x, y, cx, cy, depth, history!);
                    if (site.Own) own++;
                    if (site.OnScreen)
                    {
                        var sample = history!.Previous!.Sample(site.Uv);
                        possibleLock = Math.Max(possibleLock, Math.Max(0, sample.PossibleLock - TemporalResolveTuning.LockDecay));
                        supported = possibleLock > 0;
                        if (supported) { priorY = history.Previous.SampleY(site.Uv, sample.Colour); eligible++; }
                    }
                }
                Vector3 supportedLo = lo, supportedHi = hi;
                if (supported)
                {
                    supportedLo.X = Math.Min(lo.X, priorY.X);
                    supportedHi.X = Math.Max(hi.X, priorY.Y);
                }
                // This bank is calculated before inspecting tested colour or lock state. Source rules can reduce
                // history weight, lock and hold, so their convex hull is a conservative independently derived bound.
                bool resetSeed = reset is not null && history is not null && (rejectedHere || history.Resolve.Jitter.W < .5f);
                Vector3 resetColour = reset is null ? default : new(reset.Values[i * reset.Channels],
                    reset.Values[i * reset.Channels + 1], reset.Values[i * reset.Channels + 2]);
                if (resetSeed) next.StoreReset(i, resetColour, possibleLock);
                else next.Store(i, supportedLo, supportedHi, possibleLock);
                next.Eligibility[i] = rejectedHere ? -1 : supported ? site.Own ? 2 : 1 : 0;
                var resolved = new Vector3(output.Values[i * output.Channels], output.Values[i * output.Channels + 1],
                    output.Values[i * output.Channels + 2]);
                Vector3 weighted = TemporalResolveMath.ToWeighted(resolved);
                bool locked = state is not null && StoredLock(state.Values[i * state.Channels + 1]) > 0;
                Vector3 checkedLo = lo, checkedHi = hi;
                if (locked)
                {
                    held++;
                    if (supported)
                    {
                        checkedLo = supportedLo; checkedHi = supportedHi;
                        if (widest is null || priorY.Y > widest.PriorY.Y) widest = new(x, y, checkedLo, checkedHi, priorY);
                    }
                    else unsupportedHeld++;
                    float luma = YCoCg(weighted).X;
                    float excess = Math.Max(0, Math.Max(lo.X - luma, luma - hi.X));
                    if (excess > Tolerance) heldLumaOutside++;
                    heldLumaExcess = Math.Max(heldLumaExcess, excess);
                    residuals.Add(new
                    {
                        x,
                        y,
                        kind = "held-local-bound",
                        supported,
                        ridge,
                        site.Own,
                        previousUv = new[] { site.Uv.X, site.Uv.Y },
                        priorY = new[] { priorY.X, priorY.Y },
                        possibleLock,
                        excess,
                        lo = Array(checkedLo),
                        hi = Array(checkedHi)
                    });
                }
                bool inside = (!resetSeed || Within(resolved, resetColour, resetColour))
                    && Within(weighted, RgbMin(checkedLo, checkedHi), RgbMax(checkedLo, checkedHi))
                    && CoverageClampedColour.Contains(weighted, checkedLo, checkedHi, Tolerance);
                if (inside) continue;
                violations++;
                residuals.Add(new
                {
                    x,
                    y,
                    kind = "violation",
                    cx,
                    cy,
                    homogeneous,
                    centreKind,
                    resolved = Array(resolved),
                    weighted = Array(weighted),
                    lo = Array(lo),
                    hi = Array(hi)
                });
            }
        return new(witnesses, host, plate, edge, held, violations, residuals)
        {
            History = next,
            WidestHeld = widest,
            EligibleHistory = eligible,
            OwnReprojected = own,
            RejectedHistory = rejected,
            HeldWithoutPrior = unsupportedHeld,
            HeldLumaOutside = heldLumaOutside,
            HeldLumaExcess = heldLumaExcess,
        };
    }

    internal static (int X, int Y) CentreTexel(int x, int y, int displayWidth, int displayHeight,
        int internalWidth, int internalHeight, Vector2 jitter) =>
        (Math.Clamp((int)MathF.Floor((x + .5f) / displayWidth * internalWidth + jitter.X), 0, internalWidth - 1),
         Math.Clamp((int)MathF.Floor((y + .5f) / displayHeight * internalHeight + jitter.Y), 0, internalHeight - 1));

    static Vector3 RgbMin(Vector3 lo, Vector3 hi) =>
        Vector3.Max(new Vector3(lo.X + lo.Y - hi.Z, lo.X + lo.Z, lo.X - hi.Y - hi.Z), Vector3.Zero);

    static Vector3 RgbMax(Vector3 lo, Vector3 hi) =>
        Vector3.Max(new Vector3(hi.X + hi.Y - lo.Z, hi.X + hi.Z, hi.X - lo.Y - lo.Z), Vector3.Zero);

    // Possible ridge under the independently measured one-byte source-input contract. This only bounds lifetime
    // from above. Motion, reactive release, depth refusal and held-from-farther can shorten that lifetime.
    static bool PossibleRidge(int cx, int cy, int width, int height, Vector2[] y)
    {
        Vector2 At(int dx, int dy) => y[Math.Clamp(cy + dy, 0, height - 1) * width + Math.Clamp(cx + dx, 0, width - 1)];
        Vector2 c = At(0, 0);
        float threshold = Math.Max(TemporalResolveTuning.LockRidgeAbsolute, TemporalResolveTuning.LockRidgeRelative * c.X);
        bool Pair(Vector2 a, Vector2 b) => c.Y - Math.Max(a.X, b.X) > threshold || Math.Min(a.Y, b.Y) - c.X > threshold;
        return Pair(At(-1, 0), At(1, 0)) || Pair(At(0, -1), At(0, 1))
            || Pair(At(-1, -1), At(1, 1)) || Pair(At(1, -1), At(-1, 1));
    }

    static bool Within(Vector3 value, Vector3 lo, Vector3 hi) =>
        value.X >= lo.X - Tolerance && value.X <= hi.X + Tolerance
        && value.Y >= lo.Y - Tolerance && value.Y <= hi.Y + Tolerance
        && value.Z >= lo.Z - Tolerance && value.Z <= hi.Z + Tolerance;

    static float[] Array(Vector3 v) => [v.X, v.Y, v.Z];

    static Vector3 YCoCg(Vector3 c) => new(.25f * c.X + .5f * c.Y + .25f * c.Z, .5f * c.X - .5f * c.Z,
        -.25f * c.X + .5f * c.Y - .25f * c.Z);

    // temporalStoredLock: a moved, band or followed mark lies below minus one half and carries the lock as its
    // remainder over two.
    static float StoredLock(float stored)
    {
        if (!(stored < -.5f)) return stored;
        float value = -1f - stored;
        return value - 2f * MathF.Floor(value / 2f);
    }

    // temporalHalf: round to the nearest half, ties to even, and flush below 2^-14 to a signed zero.
    static float Half(float value)
    {
        uint bits = BitConverter.SingleToUInt32Bits(value);
        uint rounded = (bits + 0xfffu + ((bits >> 13) & 1u)) & 0xffffe000u;
        uint small = (uint)((int)((bits & 0x7fffffffu) - 0x38800000u) >> 31);
        return BitConverter.UInt32BitsToSingle(rounded & (~small | 0x80000000u));
    }
}
