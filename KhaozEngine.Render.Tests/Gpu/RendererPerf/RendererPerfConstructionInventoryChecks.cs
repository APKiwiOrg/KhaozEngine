using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        var content = new RendererPerfConstructionContent(budget);
        if (!i.Shaders.Select(s => s.Id).SequenceEqual(Enumerable.Range(0, i.Shaders.Length))
            || !i.Layouts.Select(l => l.Id).SequenceEqual(Enumerable.Range(0, i.Layouts.Length)))
            throw new ArgumentException("Graph IDs are not dense first references.");
        long evidence = checked(94 + RendererPerfConstructionAdmission.Text(i.Schema, 1024));
        foreach (string op in i.Operations) evidence = checked(evidence + RendererPerfConstructionAdmission.Text(op));
        foreach (var shader in i.Shaders)
        {
            if (shader.Stages.IsDefaultOrEmpty || shader.Stages.Length > 2) throw new ArgumentException("Invalid captured stage count.");
            foreach (var s in shader.Stages)
            {
                _ = content.Source(s.Source);
                _ = RendererPerfConstructionAdmission.Text(s.Stage);
                _ = RendererPerfConstructionAdmission.Text(s.EntryPoint);
            }
            if (content.Shader(shader).Id != shader.Id) reasons.Add("Duplicate shader descriptors were not canonicalized.");
        }
        foreach (var layout in i.Layouts)
        {
            RendererPerfConstructionAdmission.Count(layout.Elements.Length, 128, "layout elements");
            foreach (var e in layout.Elements)
            {
                if (!Enum.IsDefined(e.Kind) || (e.Stages & ~(GpuShaderStages.Vertex | GpuShaderStages.Fragment | GpuShaderStages.Compute)) != 0)
                    throw new ArgumentException("Unknown captured layout enum.");
                _ = RendererPerfConstructionAdmission.Text(e.Name);
            }
            if (content.Layout(layout).Id != layout.Id) reasons.Add("Duplicate layout descriptors were not canonicalized.");
        }
        var digests = new Dictionary<string, int>(1024, StringComparer.Ordinal);
        foreach (var r in i.Recipes)
        {
            RendererPerfConstructionAdmission.Count(r.LayoutIds.Length, 32, "layout sets");
            if ((uint)r.ShaderId >= (uint)i.Shaders.Length || r.LayoutIds.Any(id => (uint)id >= (uint)i.Layouts.Length))
                throw new ArgumentException("Foreign captured graph reference.");
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
            }
            _ = content.Pipeline(r, i.Shaders, i.Layouts);
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
        budget.Reserve(0, 0, evidence);
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
            if (!i.FactoryEvents.Where(e => e.Kind == kind).Select(e => e.InputId).Distinct().SequenceEqual(Enumerable.Range(0, count)))
                reasons.Add("Shader/layout table has foreign or unordered first factory inputs.");
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
}
