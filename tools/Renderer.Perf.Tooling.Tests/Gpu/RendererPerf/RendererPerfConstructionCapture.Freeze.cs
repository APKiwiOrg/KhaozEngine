using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed partial class RendererPerfConstructionCapture
{
    internal RendererPerfCompilerInventory Freeze() => Freeze(null);

    internal RendererPerfCompilerInventory Freeze(Func<RendererPerfPipelineRecipe, ImmutableArray<RendererPerfShaderRecipe>,
        ImmutableArray<RendererPerfLayoutRecipe>, string>? digest)
    {
        if (_frozen is not null) return _frozen;
        if (_operation >= 0) throw new InvalidOperationException("End the operation before freezing construction evidence.");
        if (!_attempts.SequenceEqual(_forwarded)) Refuse("Independent wrapper forwarding counts disagree.");
        if (_nextOperation != _operations.Length) Refuse("Not every scheduled construction operation completed.");
        var shaders = Canonical(_shaders, _shaderCount, (s, id) => s with { Id = id }, out var shaderRemap);
        var layouts = Canonical(_layouts, _layoutCount, (l, id) => l with { Id = id }, out var layoutRemap);
        var recipes = ImmutableArray.CreateBuilder<RendererPerfPipelineRecipe>();
        var buckets = new Dictionary<string, List<int>>(RendererPerfConstructionAdmission.UniqueRecipes, StringComparer.Ordinal);
        int[] remap = new int[_recipeCount]; Array.Fill(remap, -1);
        for (int i = 0; i < _recipeCount; i++)
        {
            var original = _recipes[i]!;
            var recipe = original with { ShaderId = shaderRemap[original.ShaderId], LayoutIds = [.. original.LayoutIds.Select(id => layoutRemap[id])] };
            try
            {
                byte[] value = RendererPerfConstructionEncoding.Bytes(recipe, shaders, layouts);
                string hash = digest is null ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(value)) : digest(recipe, shaders, layouts);
                if (!KhaozEngine.Gpu.Internal.GpuDiagnosticIdentityEncoding.ValidHash(hash)) throw new ArgumentException("Invalid recipe digest.");
                if (buckets.TryGetValue(hash, out var candidates))
                {
                    foreach (int candidate in candidates)
                        if (value.AsSpan().SequenceEqual(RendererPerfConstructionEncoding.Bytes(recipes[candidate], shaders, layouts))) { remap[i] = candidate; break; }
                    if (remap[i] >= 0) continue;
                    Refuse("Construction recipe digest collision has unequal values.");
                }
                if (recipes.Count == RendererPerfConstructionAdmission.UniqueRecipes)
                { Refuse("Construction unique recipe capacity exceeded."); continue; }
                int id = recipes.Count; remap[i] = id;
                recipes.Add(recipe with { Id = id, RecipeSha256 = hash });
                if (candidates is null) { candidates = []; buckets.Add(hash, candidates); }
                candidates.Add(id);
            }
            catch (Exception e) { Refuse(e); }
        }
        var occurrences = ImmutableArray.CreateBuilder<RendererPerfPipelineOccurrence>();
        int[] occurrenceRemap = new int[_occurrenceCount]; Array.Fill(occurrenceRemap, -1);
        for (int i = 0; i < _occurrenceCount; i++)
        {
            var row = _occurrences[i]!;
            if (remap[row.RecipeId] < 0) continue;
            occurrenceRemap[i] = occurrences.Count;
            occurrences.Add(row with { Id = occurrences.Count, RecipeId = remap[row.RecipeId], BindCount = _binds[i] });
        }
        var events = ImmutableArray.CreateBuilder<RendererPerfFactoryEvent>();
        for (int i = 0; i < _eventCount; i++)
        {
            if (_events[i] is not { } e) continue;
            if (e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
            {
                if (remap[e.InputId] < 0 || e.OutputId is { } output && occurrenceRemap[output] < 0) { _omitted = checked(_omitted + 1); continue; }
                e = e with { InputId = remap[e.InputId], OutputId = e.OutputId is { } id ? occurrenceRemap[id] : null };
            }
            else
            {
                int[] graphRemap = e.Kind == RendererPerfFactoryKind.Shader ? shaderRemap : layoutRemap;
                e = e with { InputId = graphRemap[e.InputId], OutputId = e.OutputId is { } id ? graphRemap[id] : null };
            }
            events.Add(e);
        }
        var frozenOccurrences = occurrences.ToImmutable();
        var counts = new RendererPerfCompilerCounts(checked(_forwarded[0] + _forwarded[1] + _forwarded[2] + _forwarded[3]),
            new(_attempts[2], _attempts[3]), new(_successes[2], _successes[3]),
            new(frozenOccurrences.Count(o => o.Kind == RendererPerfPipelineKind.Graphics), frozenOccurrences.Count(o => o.Kind == RendererPerfPipelineKind.Compute)),
            new(0, 0), new(0, 0), recipes.Count, frozenOccurrences.Select(o => o.RecipeId).Distinct().Count(),
            frozenOccurrences.Count(o => o.BindCount > 0), 0, 0, _omitted);
        return _frozen = new(RendererPerfCompilerJson.Schema, _operations, events.ToImmutable(), shaders, layouts,
            recipes.ToImmutable(), frozenOccurrences, counts, Failure is null, Failure);
    }

    static ImmutableArray<T> Canonical<T>(T?[] inputs, int count, Func<T, int, T> identify, out int[] remap) where T : class
    {
        var rows = ImmutableArray.CreateBuilder<T>();
        var ids = new Dictionary<T, int>(ReferenceEqualityComparer.Instance);
        remap = new int[count];
        for (int n = 0; n < count; n++)
        {
            var row = inputs[n]!;
            if (!ids.TryGetValue(row, out int id))
            { id = rows.Count; ids.Add(row, id); rows.Add(identify(row, id)); }
            remap[n] = id;
        }
        return rows.ToImmutable();
    }
}
