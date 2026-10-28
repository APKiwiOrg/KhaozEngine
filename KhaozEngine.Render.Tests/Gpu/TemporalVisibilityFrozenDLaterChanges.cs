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
/// half restored. A later change adds its own entry the same way.
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

    /// <summary>The shimmer record's GLSL helpers, spliced in before the accumulation.</summary>
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

    /// <summary>The shimmer record in the history state (taa-still-detail Task 4).</summary>
    internal static readonly Change ShimmerRecord = new(nameof(ShimmerRecord),
        [ShimmerHelpers, PixelFields, RecordDeclaration, FourChannelFetch, RecordReprojection, HistoryFetch,
            HistoryInClipSpace, RecordStep, RecordResult],
        [RecordStore]);

    /// <summary>Every production change after D, undone in this order.</summary>
    internal static readonly Change[] Changes = [ShimmerRecord];

    /// <summary>The program with every change in <see cref="Changes"/> undone. A program with none of a change's pieces
    /// keeps that change's text, and one with none at all comes back as the same instance.</summary>
    internal static string Restore(string program)
    {
        foreach (Change change in Changes) program = Restore(program, change);
        return program;
    }

    static string Restore(string program, Change change)
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
