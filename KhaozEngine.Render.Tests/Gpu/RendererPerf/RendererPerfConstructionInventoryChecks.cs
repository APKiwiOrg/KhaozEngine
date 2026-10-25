using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfConstructionInventoryChecks
{
    internal static void Validate(RendererPerfCompilerInventory i, ImmutableArray<string>.Builder reasons)
    {
        RendererPerfConstructionAdmission.Count(i.Operations.Length, 43, "operations");
        RendererPerfConstructionAdmission.Count(i.FactoryEvents.Length, 44032, "factory attempts");
        RendererPerfConstructionAdmission.Count(i.Shaders.Length, 44032, "shaders");
        RendererPerfConstructionAdmission.Count(i.Layouts.Length, 44032, "layouts");
        RendererPerfConstructionAdmission.Count(i.Occurrences.Length, 11008, "pipeline occurrences");
        RendererPerfConstructionAdmission.Count(i.Recipes.Length, 1024, "unique recipes");
        var budget = new RendererPerfConstructionAdmission();
        long descriptor = 0, source = 0, evidence = checked(94 + RendererPerfConstructionAdmission.Text(i.Schema, 1024));
        foreach (string op in i.Operations) evidence = checked(evidence + RendererPerfConstructionAdmission.Text(op));
        foreach (var shader in i.Shaders)
        {
            if (shader.Stages.IsDefaultOrEmpty || shader.Stages.Length > 2) throw new ArgumentException("Invalid captured stage count.");
            descriptor = checked(descriptor + 8);
            foreach (var s in shader.Stages)
            {
                source = checked(source + RendererPerfConstructionAdmission.Text(s.Source, 1048576));
                descriptor = checked(descriptor + RendererPerfConstructionAdmission.Text(s.Stage)
                    + RendererPerfConstructionAdmission.Text(s.EntryPoint) + 12);
            }
        }
        foreach (var layout in i.Layouts)
        {
            RendererPerfConstructionAdmission.Count(layout.Elements.Length, 128, "layout elements"); descriptor = checked(descriptor + 8);
            foreach (var e in layout.Elements)
            {
                if (!Enum.IsDefined(e.Kind) || (e.Stages & ~(GpuShaderStages.Vertex | GpuShaderStages.Fragment | GpuShaderStages.Compute)) != 0)
                    throw new ArgumentException("Unknown captured layout enum.");
                descriptor = checked(descriptor + RendererPerfConstructionAdmission.Text(e.Name) + 9);
            }
        }
        var digests = new Dictionary<string, int>(1024, StringComparer.Ordinal);
        foreach (var r in i.Recipes)
        {
            RendererPerfConstructionAdmission.Count(r.LayoutIds.Length, 32, "layout sets");
            if ((uint)r.ShaderId >= (uint)i.Shaders.Length || r.LayoutIds.Any(id => (uint)id >= (uint)i.Layouts.Length))
                throw new ArgumentException("Foreign captured graph reference.");
            descriptor = checked(descriptor + 85 + 4L * r.LayoutIds.Length + RendererPerfConstructionAdmission.Digest(r.CodeGenerationSha256));
            if (r.Graphics is { } g)
            {
                RendererPerfConstructionAdmission.Count(g.VertexLayouts.Length, 32, "vertex streams");
                RendererPerfConstructionAdmission.Count(g.Colour.Length, 8, "color attachments");
                RendererPerfConstructionAdmission.Count(g.BlendAttachments.Length, 8, "blend attachments");
                foreach (var v in g.VertexLayouts)
                {
                    RendererPerfConstructionAdmission.Count(v.Elements.Length, 64, "vertex elements");
                    foreach (var e in v.Elements) _ = RendererPerfConstructionAdmission.Text(e.Name);
                }
                using var count = new CountStream();
                using (var w = new BinaryWriter(count, RendererPerfConstructionEncoding.Utf8, true)) RendererPerfConstructionEncoding.Graphics(w, g);
                descriptor = checked(descriptor + count.Length);
            }
            byte[] bytes = RendererPerfConstructionEncoding.Bytes(r, i.Shaders, i.Layouts);
            if (digests.TryGetValue(r.RecipeSha256, out var existing))
                reasons.Add(RendererPerfConstructionEncoding.Bytes(i.Recipes[existing], i.Shaders, i.Layouts).AsSpan().SequenceEqual(bytes) ? "Duplicate full recipes were not canonicalized." : "Recipe digest collision has unequal values.");
            else digests.Add(r.RecipeSha256, r.Id);
        }
        foreach (var e in i.FactoryEvents)
            evidence = checked(evidence + 18 + (e.OutputId.HasValue ? 4 : 0)
                + (e.Failure is null ? 0 : RendererPerfConstructionAdmission.Text(e.Failure, 16777216)));
        foreach (var o in i.Occurrences)
            evidence = checked(evidence + 33 + (o.FrameIndex.HasValue ? 4 : 0) + RendererPerfConstructionAdmission.Witness(o.Production));
        if (i.Reason is not null) evidence = checked(evidence + RendererPerfConstructionAdmission.Text(i.Reason, 16777216));
        budget.Reserve(source, descriptor, evidence);
        if (!i.FactoryEvents.SequenceEqual(i.FactoryEvents.OrderBy(e => e.OperationOrdinal).ThenBy(e => e.Ordinal)))
            reasons.Add("Factory event order is not canonical.");
        CheckTable(RendererPerfFactoryKind.Shader, i.Shaders.Length);
        CheckTable(RendererPerfFactoryKind.Layout, i.Layouts.Length);
        if (!i.FactoryEvents.Where(e => e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
            .Select(e => e.InputId).Distinct().SequenceEqual(Enumerable.Range(0, i.Recipes.Length))) reasons.Add("Recipe IDs are not in first-creation order.");
        if (!i.FactoryEvents.Where(e => e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
            .Where(e => e.OutputId.HasValue).Select(e => e.OutputId!.Value).SequenceEqual(Enumerable.Range(0, i.Occurrences.Length)))
            reasons.Add("Occurrence IDs are not in successful factory order.");
        void CheckTable(RendererPerfFactoryKind kind, int count)
        {
            if (!i.FactoryEvents.Where(e => e.Kind == kind).Select(e => e.InputId).SequenceEqual(Enumerable.Range(0, count)))
                reasons.Add("Shader/layout table has foreign, repeated or unordered factory inputs.");
        }
        foreach (var e in i.FactoryEvents.Where(e => e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline))
        {
            if ((uint)e.InputId >= (uint)i.Recipes.Length) continue;
            var r = i.Recipes[e.InputId];
            var earlier = i.FactoryEvents.Where(other => other.OperationOrdinal == e.OperationOrdinal && other.Ordinal < e.Ordinal && other.OutputId.HasValue);
            if (!earlier.Any(other => other.Kind == RendererPerfFactoryKind.Shader && (uint)other.InputId < (uint)i.Shaders.Length
                && i.Shaders[other.InputId].Stages.SequenceEqual(i.Shaders[r.ShaderId].Stages)))
                reasons.Add("Pipeline source has no prior successful operation-local shader factory.");
            foreach (int id in r.LayoutIds)
                if (!earlier.Any(other => other.Kind == RendererPerfFactoryKind.Layout && (uint)other.InputId < (uint)i.Layouts.Length
                    && i.Layouts[other.InputId].Elements.SequenceEqual(i.Layouts[id].Elements)))
                    reasons.Add("Pipeline layout has no prior successful operation-local layout factory.");
        }
    }
    sealed class CountStream : Stream
    {
        long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _length = checked(_length + count);
        public override void Write(ReadOnlySpan<byte> buffer) => _length = checked(_length + buffer.Length);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
