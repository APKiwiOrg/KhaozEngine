using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// The file envelope binds the detached Task2 inventory to the producing execution.
internal sealed record RendererPerfCompilerFile(string Schema, string UnitId, string ManifestSha256,
    string WorkloadSha, RendererPerfCompilerInventory Inventory);
internal static partial class RendererPerfCompilerJson
{
    internal const string Schema = "khaozengine.renderer-compiler-inventory/1";
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(RendererPerfManifest.JsonOptions)
        { TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RequireMembers } } };
        AddValues(options);
        return options;
    }
    static void RequireMembers(JsonTypeInfo type)
    {
        if (type.Kind == JsonTypeInfoKind.Object)
            foreach (var property in type.Properties) property.IsRequired = true;
    }
    internal static byte[] Serialize(RendererPerfCompilerInventory inventory, int maximumBytes)
    {
        Validate(inventory);
        return Encode(inventory, maximumBytes);
    }
    internal static RendererPerfCompilerInventory Parse(ReadOnlySpan<byte> utf8, int maximumBytes)
    {
        using var document = RendererPerfManifest.ReadDocument(utf8, maximumBytes);
        Preflight(document.RootElement);
        var result = document.RootElement.Deserialize<RendererPerfCompilerInventory>(Options) ?? throw new JsonException("Missing inventory.");
        Validate(result);
        return result;
    }
    internal static RendererPerfCompilerFile ReadFile(ReadOnlySpan<byte> utf8, int maximumBytes)
    {
        using var document = RendererPerfManifest.ReadDocument(utf8, maximumBytes);
        Preflight(document.RootElement.GetProperty("inventory"));
        var result = document.RootElement.Deserialize<RendererPerfCompilerFile>(Options) ?? throw new JsonException("Missing compiler file.");
        if (result.Schema != Schema) throw new JsonException("Unknown compiler file schema.");
        RendererPerfManifest.Hash(result.ManifestSha256, 64); RendererPerfManifest.Hash(result.WorkloadSha, 40);
        Validate(result.Inventory);
        return result;
    }
    internal static byte[] Encode<T>(T value, int maximumBytes)
    {
        using var stream = new BoundedStream(maximumBytes);
        JsonSerializer.Serialize(stream, value, Options);
        return stream.ToArray();
    }
    internal static void Validate(RendererPerfCompilerInventory inventory)
    {
        if (inventory.Schema != Schema || inventory.Complete != (inventory.Reason is null)) throw new JsonException("Invalid inventory schema or completeness.");
        var reasons = ImmutableArray.CreateBuilder<string>();
        RendererPerfConstructionInventoryChecks.Validate(inventory, reasons);
        if (reasons.Count != 0) throw new JsonException(string.Join(" ", reasons));
        foreach (var recipe in inventory.Recipes)
            if (recipe.RecipeSha256 != RendererPerfConstructionEncoding.Digest(recipe, inventory.Shaders, inventory.Layouts))
                throw new JsonException("Canonical recipe hash disagrees with retained values.");
        foreach (var occurrence in inventory.Occurrences)
            RendererPerfCompilerValidation.Pipeline(new(occurrence.Production, KhaozEngine.Gpu.GpuDiagnosticStatus.Unsupported, "Construction witness only.", []));
    }
    static void Preflight(JsonElement root)
    {
        foreach (var (name, maximum) in new[] { ("operations", 43), ("factoryEvents", 44032), ("shaders", 44032),
            ("layouts", 44032), ("recipes", 1024), ("occurrences", 11008) })
            if (root.GetProperty(name).GetArrayLength() > maximum) throw new JsonException("Inventory count exceeds " + name + " capacity.");
    }
    sealed class BoundedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (checked(Length + count) > maximum) throw new JsonException("Compiler JSON exceeds its byte capacity.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (checked(Length + buffer.Length) > maximum) throw new JsonException("Compiler JSON exceeds its byte capacity.");
            base.Write(buffer);
        }
    }
}
