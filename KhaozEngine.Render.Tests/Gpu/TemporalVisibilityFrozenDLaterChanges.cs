using System;
using System.Linq;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// PRODUCTION CHANGES AFTER D, UNDONE. Each production change to the resolve programs since D shipped at 583a3ae7b that
/// lies outside D's frozen pieces (<see cref="TemporalVisibilityFrozenD"/> and
/// <see cref="TemporalVisibilityFrozenDMask"/>) is one <see cref="Change"/> in <see cref="Changes"/>: each production
/// text exactly as it stands, and the text D shipped in its place. <see cref="TemporalVisibilityFrozenD.ToD"/> undoes them before its own rewrite, so D prime and
/// shipped D stay the programs <see cref="TemporalVisibilityFrozenDMask.ProgramSha256"/> pins. A program carries a
/// change whole or not at all: every <see cref="Change.Core"/> piece once and each <see cref="Change.Optional"/> piece
/// at most once, or none of them. Anything else throws, so a production edit to a piece fails here instead of leaving D
/// half restored. A later change adds its own entry the same way, ahead of the changes it was written over, and its
/// pieces may hold an earlier change's production text, which its undo gives back.
/// <para>D writes no shimmer record. Its programs store (confidence, lock, 0, 1) in the four-channel state target and
/// read only the first two channels back, so a comparison against D holds the first two state channels alone
/// (<see cref="TemporalHistorySample.WithoutShimmerRecord"/>).</para>
/// </summary>
internal static class TemporalVisibilityFrozenDLaterChanges
{
    /// <summary>One production piece and the text D shipped in its place, empty where D had nothing.</summary>
    internal sealed record Undo(string Name, string Production, string D);

    /// <summary>One production change: the pieces every program that carries it holds once each, and the pieces only
    /// some of those programs hold, such as an entry point's store, which the debug views and counts lack.</summary>
    internal sealed record Change(string Name, Undo[] Core, Undo[] Optional);

    /// <summary>The shimmer record's and score's GLSL helpers, spliced in before the accumulation.</summary>
    internal static readonly Undo ShimmerHelpers = new(nameof(ShimmerHelpers), ShaderSources.TemporalShimmerGlsl, "");

    /// <summary>The record and score fields on TemporalPixel.</summary>
    internal static readonly Undo PixelFields = new(nameof(PixelFields),
        @"// drift and swing are the shimmer record (TemporalShimmerGlsl) the history state stores, shimmer the score read from it.
struct TemporalPixel {
    vec3 color; float confidence; float stability; float moved; float disocclusion; float reactive; float clip;
    float alpha; float drift; float swing; float shimmer;
};
",
        @"struct TemporalPixel {
    vec3 color; float confidence; float stability; float moved; float disocclusion; float reactive; float clip;
    float alpha;
};
");

    /// <summary>The reprojected record's declaration beside the reprojected state.</summary>
    internal static readonly Undo RecordDeclaration = new(nameof(RecordDeclaration),
        @"    // carriedFollowed whether that was the followed surface's own. The shimmer record in blue and alpha is blended by
    // the same weights.
    vec2 fetchedState = vec2(0.0);
    vec2 fetchedRecord = vec2(0.0);
",
        @"    // carriedFollowed whether that was the followed surface's own.
    vec2 fetchedState = vec2(0.0);
");

    /// <summary>The state footprint fetched in four channels.</summary>
    internal static readonly Undo FourChannelFetch = new(nameof(FourChannelFetch),
        @"        vec4 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0);
        vec4 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0);
        vec4 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0);
        vec4 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0);
",
        @"        vec2 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0).rg;
        vec2 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0).rg;
        vec2 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0).rg;
        vec2 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0).rg;
");

    /// <summary>The record blended by the state's bilinear weights.</summary>
    internal static readonly Undo RecordReprojection = new(nameof(RecordReprojection),
        @"        fetchedRecord = vec2(dot(bilinear, vec4(s00.z, s10.z, s01.z, s11.z)),
            dot(bilinear, vec4(s00.w, s10.w, s01.w, s11.w)));
",
        @"");

    /// <summary>The record in the finite check, and the history's luma and change moved up beside the fetch.</summary>
    internal static readonly Undo HistoryFetch = new(nameof(HistoryFetch),
        @"    vec2 historyRecord = vec2(0.0);
    if (useHistory) {
        vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        bool finite = all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
            && all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)))
            && all(greaterThan(fetchedRecord, vec2(-HalfMax))) && all(lessThan(fetchedRecord, vec2(HalfMax)));
        if (finite) {
            history = max(fetched, vec4(0.0));
            historyState = clamp(fetchedState, vec2(0.0), vec2(1.0));
            historyRecord = fetchedRecord;
        } else {
            useHistory = false;
        }
    }
    // The history in the clip's space, and this frame's luma less it, the change the shimmer record averages.
    vec3 historyYcc = rgbToYCoCg(toWeighted(history.rgb));
    float lumaChange = current.x - historyYcc.x;
",
        @"    if (useHistory) {
        vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        bool finite = all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
            && all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)));
        if (finite) {
            history = max(fetched, vec4(0.0));
            historyState = clamp(fetchedState, vec2(0.0), vec2(1.0));
        } else {
            useHistory = false;
        }
    }
");

    /// <summary>Where D took the history's luma, before the box.</summary>
    internal static readonly Undo HistoryInClipSpace = new(nameof(HistoryInClipSpace),
        @"    vec3 boxMax = mean + gamma * deviation;
    vec3 boxCentre = 0.5 * (boxMin + boxMax);
",
        @"    vec3 boxMax = mean + gamma * deviation;
    vec3 historyYcc = rgbToYCoCg(toWeighted(history.rgb));
    vec3 boxCentre = 0.5 * (boxMin + boxMax);
");

    /// <summary>The record's step after the shading release.</summary>
    internal static readonly Undo RecordStep = new(nameof(RecordStep),
        @"    // The shimmer record steps where the pixel kept its history and its shading, and starts empty elsewhere.
    vec2 shimmerRecord = useHistory && !released ? temporalShimmerStep(historyRecord, lumaChange) : vec2(0.0);
",
        @"");

    /// <summary>The record and score written to the result.</summary>
    internal static readonly Undo RecordResult = new(nameof(RecordResult),
        @"    result.drift = shimmerRecord.x;
    result.swing = shimmerRecord.y;
    result.shimmer = 0.0;
",
        @"");

    /// <summary>Both entry points' state store with the record in blue and alpha.</summary>
    internal static readonly Undo RecordStore = new(nameof(RecordStore),
        "oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), p.drift, p.swing);",
        "oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), 0.0, 1.0);");

    /// <summary>The shimmer score's tuning constants, in every program that carries the tuning block, the split's
    /// preparing pass included, which carries none of the score's other pieces.</summary>
    internal static readonly Undo ScoreConstants = new(nameof(ScoreConstants),
        @"const float ShimmerFloor = 0.03125;
const float ShimmerStillPixels = 0.25;
const float ShimmerChangeBand = 3.0;
const float ShimmerClipGain = 0.35;
const float MaxStillAccumulation = 63.0;
const float VisibilityShimmerGate = 0.1;
",
        @"");

    /// <summary>The stored confidence read back up to the still cap, not 1.</summary>
    internal static readonly Undo StillConfidenceRead = new(nameof(StillConfidenceRead),
        @"            historyState = clamp(fetchedState, vec2(0.0), vec2(MaxStillAccumulation / MaxAccumulation, 1.0));
",
        @"            historyState = clamp(fetchedState, vec2(0.0), vec2(1.0));
");

    /// <summary>The motion cap alone where the accumulated weight was taken before the score.</summary>
    internal static readonly Undo MotionCapAlone = new(nameof(MotionCapAlone),
        @"    // Step 8, first half: the accumulated sample weight's cap, lower as motion grows. The weight itself waits for the
    // shimmer score, after the shading release.
    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
",
        @"    // Step 8, first half: the accumulated sample weight, capped lower as motion grows and cut by reactive content.
    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
    float accumulated = useHistory
        ? min(historyState.x * MaxAccumulation, motionCap) * (1.0 - reactive * ReactiveStrength)
        : 0.0;
");

    /// <summary>The box's excess, which only the final clip read, left out now that the clip has its own set.</summary>
    internal static readonly Undo BoxExcess = new(nameof(BoxExcess),
        @"    vec3 fromCentre = historyYcc - boxCentre;

",
        @"    vec3 fromCentre = historyYcc - boxCentre;
    vec3 excess = abs(fromCentre) / boxExtent;

");

    /// <summary>The score, the still cap's accumulated weight and the widened final clip after the record step, back
    /// to the shading release's weight drop and the box clip.</summary>
    internal static readonly Undo ScoreClip = new(nameof(ScoreClip),
        @"    // The shimmer record steps where the pixel kept its history and its shading, and starts empty elsewhere.
    vec2 shimmerRecord = useHistory && !released ? temporalShimmerStep(historyRecord, lumaChange) : vec2(0.0);
    // The score from the stepped record and this frame's change (temporalShimmerScore). It is none on a moving pixel,
    // so a moving pixel keeps its motion cap, and none on a released or fresh one, whose record is empty.
    float shimmer = temporalShimmerScore(shimmerRecord.x, shimmerRecord.y, lumaChange, motionPixels, reactive);
#ifdef TemporalShimmerVisibility
    // Where the exported key-shadow visibility moved since last frame a shadow is passing, which is real however the
    // luma alternates, so the pixel scores none: where the visibility release fired on its 3x3, or where the centre
    // texel's own visibility moved (temporalShimmerVisibilityChanged), read only where the score holds and no release
    // fired. A shadow reaching a neighbour column first releases the lock before the centre texel changes.
    if (shimmer > 0.0 && (visibilityReleased || temporalShimmerVisibilityChanged(uv, centreTexel, previousUv)))
        shimmer = 0.0;
#endif

    // Step 8, first half, done: a still pixel that only shimmers carries up to MaxStillAccumulation, so its current
    // weight's floor and the sawtooth each jitter hit leaves fall about four times lower. A score of none keeps the
    // motion cap exactly, so a one-way change accumulates as before. Cut by reactive content, and dropped where the
    // shading release fired.
    float cap = mix(motionCap, MaxStillAccumulation, shimmer);
    float accumulated = useHistory && !released
        ? min(historyState.x * MaxAccumulation, cap) * (1.0 - reactive * ReactiveStrength)
        : 0.0;

    // Step 5's final clip: the box's luma extent widened by ShimmerClipGain swings times the score on both sides, so a
    // still pixel's history inside its own shimmer band survives the frames the jitter misses the feature, and history
    // past the band clips as before. The shading release above judged the unwidened box.
    float shimmerReach = ShimmerClipGain * shimmer * shimmerRecord.y;
    vec3 clipMin = vec3(boxMin.x - shimmerReach, boxMin.yz);
    vec3 clipMax = vec3(boxMax.x + shimmerReach, boxMax.yz);
    vec3 clipCentre = 0.5 * (clipMin + clipMax);
    vec3 clipExtent = 0.5 * (clipMax - clipMin) + vec3(1.0e-5);
    vec3 clipFrom = historyYcc - clipCentre;
    vec3 clipExcess = abs(clipFrom) / clipExtent;
    clipExcess.x *= 1.0 - hold;
    float clipScale = max(clipExcess.x, max(clipExcess.y, clipExcess.z));
    vec3 clipped = clipScale > 1.0 ? clipCentre + clipFrom / clipScale : historyYcc;
",
        @"    if (released) accumulated = 0.0;
    // The shimmer record steps where the pixel kept its history and its shading, and starts empty elsewhere.
    vec2 shimmerRecord = useHistory && !released ? temporalShimmerStep(historyRecord, lumaChange) : vec2(0.0);
    excess.x *= 1.0 - hold;
    float clipScale = max(excess.x, max(excess.y, excess.z));
    vec3 clipped = clipScale > 1.0 ? boxCentre + fromCentre / clipScale : historyYcc;
");

    /// <summary>The score's shadow evidence, spliced in after the visibility helpers.</summary>
    internal static readonly Undo ShadowEvidence = new(nameof(ShadowEvidence), ShaderSources.TemporalShimmerVisibilityGlsl,
        "");

    /// <summary>The display share read with the confidence clamped to a share.</summary>
    internal static readonly Undo DisplayShareRead = new(nameof(DisplayShareRead),
        @"    float displayShare = temporalDisplayShare(useHistory && !released, min(historyState.x, 1.0), motionPixels,
        reactive);
",
        @"    float displayShare = temporalDisplayShare(useHistory && !released, historyState.x, motionPixels, reactive);
");

    /// <summary>The current weight's floor on a still shimmering pixel.</summary>
    internal static readonly Undo CurrentWeightFloor = new(nameof(CurrentWeightFloor),
        @"    // sampleWeight / (MaxAccumulation + sampleWeight), about one in sixteen, or toward one in 64 on a still pixel that
    // only shimmers, raised wherever reactive content or a disocclusion removed accumulated weight.
",
        @"    // sampleWeight / (MaxAccumulation + sampleWeight), about one in sixteen, raised wherever reactive content or a
    // disocclusion removed accumulated weight.
");

    /// <summary>The stored confidence capped by the still cap.</summary>
    internal static readonly Undo StillConfidenceStore = new(nameof(StillConfidenceStore),
        @"        : min(accumulated + sampleWeight, cap) / MaxAccumulation;
",
        @"        : min(accumulated + sampleWeight, motionCap) / MaxAccumulation;
");

    /// <summary>The score written to the result.</summary>
    internal static readonly Undo ScoreResult = new(nameof(ScoreResult),
        @"    result.shimmer = shimmer;
",
        @"    result.shimmer = 0.0;
");

    /// <summary>The shimmer score's tuning (taa-still-detail Task 5). Its own change, since the split's preparing pass
    /// carries the tuning block without the accumulation.</summary>
    internal static readonly Change ShimmerTuning = new(nameof(ShimmerTuning), [ScoreConstants], []);

    /// <summary>The shimmer score, its shadow evidence, the wider luma clip and the longer still history
    /// (taa-still-detail Task 5). Its undo gives back <see cref="ShimmerRecord"/>'s production text, so it comes before
    /// that change in <see cref="Changes"/>.</summary>
    internal static readonly Change ShimmerScore = new(nameof(ShimmerScore),
        [StillConfidenceRead, MotionCapAlone, BoxExcess, ScoreClip, ShadowEvidence, DisplayShareRead,
            CurrentWeightFloor, StillConfidenceStore, ScoreResult],
        []);

    /// <summary>The shimmer record in the history state (taa-still-detail Task 4).</summary>
    internal static readonly Change ShimmerRecord = new(nameof(ShimmerRecord),
        [ShimmerHelpers, PixelFields, RecordDeclaration, FourChannelFetch, RecordReprojection, HistoryFetch,
            HistoryInClipSpace, RecordStep, RecordResult],
        [RecordStore]);

    /// <summary>The precision-safe clip, restored before the shimmer clip it replaces.</summary>
    internal static readonly Undo PrecisionClip = new(nameof(PrecisionClip),
        @"    vec3 clipCentre = 0.5 * (clipMin + clipMax);
    vec3 clipExtent = 0.5 * (clipMax - clipMin) + vec3(1.0e-5);
    // Use the shimmer score's still-pixel test. Moving pixels need the coupled clip to discard stale colour.
    if (motionPixels < ShimmerStillPixels) {
        // Binary16 spacing at the largest RGB channel bounds every channel's truncation error, including subnormals.
        float largestHistory = max(history.r, max(history.g, history.b));
        float halfStep = max(uintBitsToFloat(floatBitsToUint(largestHistory) & 0x7f800000u) / 1024.0,
            0.000000059604644775390625);
        // Each chroma row has unit absolute coefficient sum. Include the weighting denominator's rounding change.
        float weightedStep = halfStep / max(1.0 + temporalLuma(history.rgb) - halfStep, 1.0e-4);
        vec2 chromaFloor = ChromaPrecisionFloorSteps * weightedStep * (vec2(1.0) + abs(historyYcc.yz));
        clipExtent.yz = max(clipExtent.yz, chromaFloor);
    }
    vec3 clipFrom = historyYcc - clipCentre;
    vec3 clipExcess = abs(clipFrom) / clipExtent;
    clipExcess.x *= 1.0 - hold;
    float clipScale = max(clipExcess.x, max(clipExcess.y, clipExcess.z));
    vec3 clipped = clipScale > 1.0 ? clipCentre + clipFrom / clipScale : historyYcc;
",
        @"    vec3 clipCentre = 0.5 * (clipMin + clipMax);
    vec3 clipExtent = 0.5 * (clipMax - clipMin) + vec3(1.0e-5);
    vec3 clipFrom = historyYcc - clipCentre;
    vec3 clipExcess = abs(clipFrom) / clipExtent;
    clipExcess.x *= 1.0 - hold;
    float clipScale = max(clipExcess.x, max(clipExcess.y, clipExcess.z));
    vec3 clipped = clipScale > 1.0 ? clipCentre + clipFrom / clipScale : historyYcc;
");

    internal static readonly Change PrecisionClipChange = new(nameof(PrecisionClipChange), [PrecisionClip], []);

    // The preparing pass carries tuning without the clip, so its undo is an independent whole change.
    internal static readonly Undo PrecisionFloorConstant = new(nameof(PrecisionFloorConstant),
        "const float ChromaPrecisionFloorSteps = 4.0;\n", "");
    internal static readonly Change PrecisionFloorTuning = new(nameof(PrecisionFloorTuning), [PrecisionFloorConstant], []);

    /// <summary>Every production change after D, undone in this order, the latest first, so each change finds the
    /// production text it was written against.</summary>
    internal static readonly Change[] Changes = [PrecisionFloorTuning, PrecisionClipChange, ShimmerTuning, ShimmerScore, ShimmerRecord];

    /// <summary>The program with every change in <see cref="Changes"/> undone. A program with none of a change's pieces
    /// keeps that change's text, and one with none at all comes back as the same instance.</summary>
    internal static string Restore(string program)
    {
        foreach (Change change in Changes) program = Restore(program, change);
        return program;
    }

    /// <summary>The program with one change undone, under the same whole-or-none rule.</summary>
    internal static string Restore(string program, Change change)
    {
        int[] core = change.Core.Select(u => Count(program, u.Production)).ToArray();
        int[] optional = change.Optional.Select(u => Count(program, u.Production)).ToArray();
        if (core.All(n => n == 0) && optional.All(n => n == 0)) return program;
        if (core.Any(n => n != 1) || optional.Any(n => n > 1))
            throw new InvalidOperationException($"the D restore of {change.Name} found core pieces "
                + $"[{string.Join(", ", core)}] and optional pieces [{string.Join(", ", optional)}], each core piece "
                + "once and each optional piece at most once, or none at all, expected");
        foreach (Undo undo in change.Core.Concat(change.Optional))
            program = program.Replace(undo.Production, undo.D, StringComparison.Ordinal);
        return program;
    }

    static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
