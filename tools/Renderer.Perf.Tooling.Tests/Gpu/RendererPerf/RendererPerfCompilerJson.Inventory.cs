using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static partial class RendererPerfCompilerJson
{
    sealed record SourceValue(string Sha256, string Source);
    sealed record StageReference(string Stage, string EntryPoint, string SourceSha256, uint LocalX, uint LocalY, uint LocalZ);
    sealed record ShaderReference(int Id, ImmutableArray<StageReference> Stages);
    sealed record InventoryValue(string Schema, ImmutableArray<string> Operations,
        ImmutableArray<RendererPerfFactoryEvent> FactoryEvents, ImmutableArray<SourceValue> Sources,
        ImmutableArray<ShaderReference> Shaders, ImmutableArray<RendererPerfLayoutRecipe> Layouts,
        ImmutableArray<RendererPerfPipelineRecipe> Recipes, ImmutableArray<RendererPerfPipelineOccurrence> Occurrences,
        RendererPerfCompilerCounts Counts, bool Complete, string? Reason);

    // Only the wire shape changes. Replay and canonical recipe identities still consume exact sources.
    sealed class InventoryConverter : JsonConverter<RendererPerfCompilerInventory>
    {
        public override RendererPerfCompilerInventory Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var wire = JsonSerializer.Deserialize<InventoryValue>(ref reader, options) ?? throw new JsonException("Missing inventory.");
            if (wire.Schema != Schema) throw new JsonException("Unknown compiler inventory schema.");
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            var content = new RendererPerfConstructionContent(new());
            foreach (var s in wire.Sources)
            {
                string source = content.Source(s.Source);
                if (s.Sha256 != RendererPerfConstructionContent.SourceHash(source) || !sources.TryAdd(s.Sha256, source))
                    throw new JsonException("Source table hash is invalid or duplicated.");
            }
            var used = new HashSet<string>(StringComparer.Ordinal);
            var order = new List<string>();
            var shaders = wire.Shaders.Select(s => new RendererPerfShaderRecipe(s.Id, [.. s.Stages.Select(stage =>
            {
                if (!sources.TryGetValue(stage.SourceSha256, out string? source)) throw new JsonException("Foreign stage source reference.");
                if (used.Add(stage.SourceSha256)) order.Add(stage.SourceSha256);
                return new RendererPerfStageSource(stage.Stage, stage.EntryPoint, source, stage.LocalX, stage.LocalY, stage.LocalZ);
            })])).ToImmutableArray();
            if (!order.SequenceEqual(wire.Sources.Select(s => s.Sha256)))
                throw new JsonException("Source table is unused or not in first-reference order.");
            return new(wire.Schema, wire.Operations, wire.FactoryEvents, shaders, wire.Layouts, wire.Recipes,
                wire.Occurrences, wire.Counts, wire.Complete, wire.Reason);
        }

        public override void Write(Utf8JsonWriter writer, RendererPerfCompilerInventory inventory, JsonSerializerOptions options)
        {
            var sources = ImmutableArray.CreateBuilder<SourceValue>();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var shaders = inventory.Shaders.Select(s => new ShaderReference(s.Id, [.. s.Stages.Select(stage =>
            {
                if (!hashes.TryGetValue(stage.Source, out string? hash))
                {
                    hash = RendererPerfConstructionContent.SourceHash(stage.Source);
                    hashes.Add(stage.Source, hash); sources.Add(new(hash, stage.Source));
                }
                return new StageReference(stage.Stage, stage.EntryPoint, hash, stage.LocalX, stage.LocalY, stage.LocalZ);
            })])).ToImmutableArray();
            JsonSerializer.Serialize(writer, new InventoryValue(inventory.Schema, inventory.Operations, inventory.FactoryEvents,
                sources.ToImmutable(), shaders, inventory.Layouts, inventory.Recipes, inventory.Occurrences,
                inventory.Counts, inventory.Complete, inventory.Reason), options);
        }
    }
}
