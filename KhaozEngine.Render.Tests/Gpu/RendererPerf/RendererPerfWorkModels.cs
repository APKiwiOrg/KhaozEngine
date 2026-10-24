using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Recipe-owned logical work, never an assertion of exact off-chip traffic or shader invocations.
internal static class RendererPerfWorkModels
{
    internal static (long ReadBytes, long WriteBytes) TextureAccess(uint w, uint h, uint mips, uint layers,
        uint samples, GpuPixelFormat format, uint reads, uint writes)
    {
        long bytes = RendererPerfFloorModel.TextureBytes(w, h, mips, layers, samples, format);
        return (checked(bytes * reads), checked(bytes * writes));
    }
    // Bilinear reduction model, four logical source taps and one destination write per reduced texel.
    // Native reduction/filter choice and cache traffic still require evidence before a gap is complete.
    internal static (long ReadBytes, long WriteBytes) MipTraffic(uint w, uint h, uint mips, uint layers, GpuPixelFormat format)
    {
        _ = RendererPerfFloorModel.TextureBytes(w, h, mips, layers, 1, format);
        long writes = 0;
        for (uint mip = 1; mip < mips; mip++)
            writes = checked(writes + RendererPerfFloorModel.TextureBytes(Math.Max(1, w >> (int)mip),
                Math.Max(1, h >> (int)mip), 1, layers, 1, format));
        return (checked(writes * 4), writes);
    }
    internal static ImmutableArray<RendererPassWorkModel> ForRegistry(RendererPerfPassDefinition[] passes, string recipe)
        => [.. passes.Select(p => Missing(p.Id, p.Occurrence, p.DeviceOnly,
            passes.Any(child => child.Parent >= 0 && passes[child.Parent].Id == p.Id && passes[child.Parent].Occurrence == p.Occurrence), recipe))];

    internal static ImmutableArray<RendererPassWorkModel> ForFrame(RendererPerfFrameSample frame, string recipe)
    {
        var inactive = frame.Passes.GroupBy(pass => pass.Id, StringComparer.Ordinal)
            .Where(group => group.All(pass => pass.Duration.Status == RendererPerfMetricStatus.NotExecuted))
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var models = ImmutableArray.CreateBuilder<RendererPassWorkModel>(frame.Passes.Length);
        foreach (var pass in frame.Passes)
        {
            bool parent = frame.Passes.Any(child => child.ParentId == pass.Id && child.ParentOccurrence == pass.Occurrence);
            var model = Missing(pass.Id, pass.Occurrence, pass.DeviceOnly, parent, recipe);
            var components = model.Components.ToBuilder();
            var missing = model.MissingEvidence.ToBuilder();
            string prefix = $"logical.pass.{pass.Id}.{pass.Occurrence}.";
            double? Counter(string name)
            {
                if (!frame.Metrics.TryGetValue(prefix + name, out var metric)) return null;
                if (metric.Status != RendererPerfMetricStatus.Available || metric.Value is null)
                {
                    missing.Add(metric.Reason ?? $"Missing {name} counter evidence.");
                    if (frame.Metrics.TryGetValue(prefix + name + ".known", out var known)) return known.Value;
                    return null;
                }
                return metric.Value;
            }
            foreach (string count in new[] { "Draws", "IndexedDraws", "Dispatches", "DispatchInvocations", "DispatchGroups",
                "Vertices", "Indices", "Instances", "Triangles", "MipGenerations", "ColorClears", "DepthClears",
                "BufferUploadBytes", "TextureUploadBytes", "BufferCopies", "TextureCopies", "TextureSubresourceCopies", "Resolves" })
                if (Counter(count) is { } value) components[count] = Exact(value,
                    count.EndsWith("Bytes", StringComparison.Ordinal) ? "bytes" : "count", "exclusive logical counter in " + recipe);
            double? copied = Counter("CopyBytes"), resolvedRead = Counter("ResolveReadBytes"), resolvedWrite = Counter("ResolveWriteBytes");
            if (!parent && !pass.DeviceOnly && (copied is not null || resolvedRead is not null || resolvedWrite is not null))
            {
                double reads = (copied ?? 0) + (resolvedRead ?? 0), writes = (copied ?? 0) + (resolvedWrite ?? 0);
                bool transferOnly = pass.Id.StartsWith("copy.", StringComparison.Ordinal)
                    || pass.Id is "ocean.mip-copy" or "outline.mask-resolve" or "outline.visible-resolve";
                bool completeTransfers = transferOnly && copied is not null && resolvedRead is not null && resolvedWrite is not null && Counter("UnknownCopyBytes") == 0;
                components["readBytes"] = new(reads, completeTransfers ? reads : null, "bytes",
                    "known copy source payload plus resolve source samples", completeTransfers ? "exact" : "estimated",
                    completeTransfers ? null : "Other shader traffic or transfer extents remain unknown.");
                components["writeBytes"] = new(writes, completeTransfers ? writes : null, "bytes",
                    "known copy destination payload plus resolve destination", completeTransfers ? "exact" : "estimated",
                    completeTransfers ? null : "Other shader traffic or transfer extents remain unknown.");
                // Only transfer-only owners with every counter present can claim a complete logical model.
                if (transferOnly && completeTransfers && Counter("Draws") == 0 && Counter("IndexedDraws") == 0
                    && Counter("Dispatches") == 0 && Counter("ColorClears") == 0 && Counter("DepthClears") == 0)
                    missing.Clear();
            }
            if (Counter("Vertices") is { } vertices && Counter("Indices") is { } indices)
                components["vertexInvocations"] = new(0, vertices + indices, "invocations",
                    "submitted vertices and indices, post-transform reuse is unmeasured", "estimated", null);
            if (pass.Duration.Status == RendererPerfMetricStatus.NotExecuted)
            {
                components["execution"] = Exact(0, "count", "NotExecuted cached or inactive registry occurrence");
                missing.Clear();
            }
            AddRecordingFacts(frame, frame.Passes.IndexOf(pass) + 1, components);
            // Copy-only minima cannot fill unknown shader/alpha/cache traffic in a mixed owner.
            models.Add(model with
            {
                Components = components.ToImmutable(),
                SerialDependencies = [.. model.SerialDependencies.Where(dependency => !inactive.Contains(dependency))],
                MissingEvidence = missing.Distinct().ToImmutableArray()
            });
        }
        return models.MoveToImmutable();
    }
    static void AddRecordingFacts(RendererPerfFrameSample frame, int owner,
        ImmutableDictionary<string, RendererPerfWorkRange>.Builder components)
    {
        foreach (var row in frame.Metrics.Where(pair => pair.Key.EndsWith(".owner", StringComparison.Ordinal)
            && (pair.Key.StartsWith("target.", StringComparison.Ordinal) || pair.Key.StartsWith("dispatch.", StringComparison.Ordinal))
            && pair.Value.Status == RendererPerfMetricStatus.Available && pair.Value.Value == owner))
        {
            string prefix = row.Key[..^5];
            foreach (var pair in frame.Metrics.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Key != row.Key))
                components[pair.Key] = pair.Value.Status == RendererPerfMetricStatus.Available && pair.Value.Value is { } value
                    ? Exact(value, pair.Value.Unit, pair.Value.Source)
                    : new(null, null, pair.Value.Unit, pair.Value.Source, "unknown", pair.Value.Reason ?? "Requested metadata is unavailable.");
            if (!prefix.StartsWith("target.", StringComparison.Ordinal)) continue;
            uint? Dimension(string key) => frame.Metrics.TryGetValue(prefix + key, out var metric)
                && metric.Status == RendererPerfMetricStatus.Available && metric.Value is >= 1 and <= uint.MaxValue
                && metric.Value == Math.Truncate(metric.Value.Value) ? (uint)metric.Value : null;
            uint? width = Dimension("width"), height = Dimension("height"), samples = Dimension("samples");
            if (width is null || height is null || samples is null) continue;
            long payload = 0;
            bool complete = frame.Metrics.ContainsKey(prefix + "depth");
            foreach (var format in frame.Metrics.Where(pair => pair.Key.StartsWith(prefix + "colour", StringComparison.Ordinal) || pair.Key == prefix + "depth"))
            {
                if (format.Value.Status == RendererPerfMetricStatus.NotExecuted) continue;
                if (format.Value.Status != RendererPerfMetricStatus.Available || format.Value.Value is not { } value
                    || value < 0 || value > int.MaxValue || value != Math.Truncate(value) || !Enum.IsDefined((GpuPixelFormat)(int)value))
                { complete = false; continue; }
                payload = checked(payload + RendererPerfFloorModel.TextureBytes(width.Value, height.Value, 1, 1, samples.Value, (GpuPixelFormat)(int)value));
            }
            components[prefix + "payloadBytes"] = new(payload, complete ? payload : null, "bytes",
                "logical attached subresource payload including format and samples, not memory traffic", complete ? "exact" : "estimated",
                complete ? null : "Some attachment formats are unavailable.");
        }
    }

    static RendererPassWorkModel Missing(string id, int occurrence, bool device, bool parent, string recipe)
    {
        string reason = device ? "CPU-only device owner, no GPU interval or GPU floor. Host CPU identity and empty-host observer overhead are required."
            : parent ? "Inclusive parent. Assess exclusive children without adding parent work or nested GPU durations."
            : Reason(id);
        var components = ImmutableDictionary<string, RendererPerfWorkRange>.Empty
            .Add("readBytes", new(null, null, "bytes", recipe, "unknown", reason))
            .Add("writeBytes", new(null, null, "bytes", recipe, "unknown", reason))
            .Add("gpuEligible", Exact(device ? 0 : 1, "count", "frozen registry ownership"));
        return new(id, occurrence, components, Dependencies(id), [reason]);
    }
    static ImmutableArray<string> Dependencies(string id) => id switch
    {
        // The current column reads the prior frame's row set. This frame's next-row writes the other set.
        // Cross-frame dependencies cannot be expressed by these same-frame pass IDs.
        "ocean.mip-copy" => ["ocean.column"],
        "ocean.mips" => ["ocean.mip-copy"],
        "water.surface" => ["ocean.column", "ocean.mips"],
        "temporal.resolve" or "temporal.accumulate" => ["copy.opaque-temporal", "temporal.outline-inputs"],
        "post.bloom.horizontal" => ["post.bloom.bright"],
        "post.bloom.vertical" => ["post.bloom.horizontal"],
        "post.bloom.composite" => ["post.bloom.vertical"],
        _ => []
    };
    internal static string Reason(string id)
    {
        if (id.StartsWith("device.", StringComparison.Ordinal)) return "CPU-only owner requires hosted CPU and empty-host/observer overhead, without a GPU bound.";
        if (id.StartsWith("shadow.", StringComparison.Ordinal)) return "Shadow vertex/depth invocation and alpha/dissolve ranges, bound buffers, depth traffic and matched depth throughput are not measured.";
        if (id.StartsWith("opaque.", StringComparison.Ordinal)) return "Geometry vertex/fragment invocation, splat/tile texture taps, alpha overdraw and fused motion MRT traffic require route evidence.";
        if (id.StartsWith("ocean.", StringComparison.Ordinal) || id == "startup.ocean-prime") return "FFT butterfly operations, bound storage/image extents and mip/filter/cache traffic require per-dispatch evidence. Prime is startup only.";
        if (id.StartsWith("water", StringComparison.Ordinal)) return "Water vertex/fragment invocations and reflection/refraction/depth/FFT texture traffic are unknown.";
        if (id.StartsWith("copy.", StringComparison.Ordinal)) return "Transfer resource format, mip, layer and sample extents or complete copy/resolve byte counters are missing.";
        if (id.StartsWith("post.", StringComparison.Ordinal) || id.StartsWith("temporal.", StringComparison.Ordinal)) return "Fullscreen source texture formats, tap and history/depth ranges, destination samples and matching fill/dependency evidence are missing.";
        if (id.StartsWith("outline.", StringComparison.Ordinal)) return "Outline group mask coverage, texture mip/resolve traffic and fused growth/composite invocations require evidence.";
        if (id.StartsWith("effects.", StringComparison.Ordinal) || id.StartsWith("display.", StringComparison.Ordinal) || id.StartsWith("ui.", StringComparison.Ordinal)) return "Pixel/vertex/blend coverage, texture sampling and depth/alpha overdraw ranges are unknown for this effect or UI run.";
        if (id.StartsWith("atmosphere.", StringComparison.Ordinal) || id.StartsWith("background.", StringComparison.Ordinal)) return "Atmosphere integration/texture taps or sky/star invocations require shader and cached-LUT evidence.";
        if (id.StartsWith("geometry.", StringComparison.Ordinal)) return "Host upload bytes do not specify native staging/copy traffic or GPU shader work. Upload route evidence is required.";
        return $"Pass {id} requires explicit resource, invocation and operation evidence before assigning a hardware reference.";
    }
    static RendererPerfWorkRange Exact(double value, string unit, string source) => new(value, value, unit, source, "exact", null);
}
