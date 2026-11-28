using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace KhaozEngine.Gpu;

/// <summary>The compiled shader programs one assembly embeds as a JSON bundle.</summary>
public sealed class GpuShaderBundle : IGpuShaderProgramSource
{
    /// <summary>The bundle's format name.</summary>
    public const string Format = "khaoz-shader-bundle";

    /// <summary>The only bundle format version this reader accepts.</summary>
    public const int FormatVersion = 1;

    readonly Dictionary<string, Dictionary<string, GpuShaderProgram>> _byId;

    GpuShaderBundle(string slangVersion, ImmutableArray<GpuShaderProgram> programs,
        Dictionary<string, Dictionary<string, GpuShaderProgram>> byId)
    {
        SlangVersion = slangVersion;
        Programs = programs;
        _byId = byId;
    }

    /// <summary>The slangc version that compiled the bundle.</summary>
    public string SlangVersion { get; }

    /// <summary>Every program in the bundle.</summary>
    public IReadOnlyList<GpuShaderProgram> Programs { get; }

    /// <summary>Reads the bundle an assembly embeds as its <c>&lt;AssemblyName&gt;.khaoz.shaders.json</c> resource.</summary>
    public static GpuShaderBundle FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var resource = assembly.GetName().Name + ".khaoz.shaders.json";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new ShaderValidationException(ShaderValidationRefusal.BundleMissing,
                $"Assembly '{assembly.GetName().Name}' embeds no shader bundle resource '{resource}'.")
            { Label = resource };
        return Load(stream, resource);
    }

    /// <summary>Reads a UTF-8 JSON bundle, refusing another format or version and any malformed field.</summary>
    public static GpuShaderBundle Load(Stream utf8Json, string origin)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        ArgumentNullException.ThrowIfNull(origin);
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            RequireFormat(root, origin);
            var programs = ImmutableArray.CreateBuilder<GpuShaderProgram>();
            var byId = new Dictionary<string, Dictionary<string, GpuShaderProgram>>(StringComparer.Ordinal);
            foreach (var element in Field(root, "programs", JsonValueKind.Array).EnumerateArray())
            {
                var program = ReadProgram(element);
                if (!byId.TryGetValue(program.Id, out var variants))
                    byId[program.Id] = variants = new Dictionary<string, GpuShaderProgram>(StringComparer.Ordinal);
                if (!variants.TryAdd(program.Variant, program))
                    throw new FormatException($"Program '{program.Id}' variant '{program.Variant}' appears twice.");
                programs.Add(program);
            }
            return new GpuShaderBundle(Text(root, "slangc"), programs.ToImmutable(), byId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw new ShaderValidationException(ShaderValidationRefusal.BundleCorrupt,
                $"Shader bundle '{origin}' is malformed: {ex.Message}", ex) { Label = origin };
        }
    }

    /// <inheritdoc/>
    public GpuShaderProgram Get(string id, string variant = "")
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(variant);
        if (!_byId.TryGetValue(id, out var variants))
            throw new ShaderValidationException(ShaderValidationRefusal.BundleProgramUnknown,
                $"The shader bundle has no program '{id}'.") { Label = id };
        return variants.TryGetValue(variant, out var program)
            ? program
            : throw new ShaderValidationException(ShaderValidationRefusal.BundleVariantUnknown,
                $"Shader program '{id}' has no variant '{variant}'.") { Label = id };
    }

    static void RequireFormat(JsonElement root, string origin)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("The bundle root is not an object.");
        var format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : (int?)null;
        if (format != Format || version != FormatVersion)
            throw new ShaderValidationException(ShaderValidationRefusal.BundleFormatUnsupported,
                $"Shader bundle '{origin}' is format '{format}' version {version?.ToString() ?? "unknown"}, but this reader accepts only '{Format}' version {FormatVersion}.")
            { Label = origin, FormatVersion = version };
    }

    static GpuShaderProgram ReadProgram(JsonElement element)
    {
        var bindings = new List<GpuShaderBinding>();
        foreach (var b in Field(element, "bindings", JsonValueKind.Array).EnumerateArray())
            bindings.Add(new GpuShaderBinding(Text(b, "name"), Index(b, "set"), Index(b, "binding"),
                Kind(Text(b, "kind")), Index(b, "metal"), Index(b, "hlsl")));
        var precise = Field(element, "vertexPreciseMath", JsonValueKind.True, JsonValueKind.False).GetBoolean();
        return new GpuShaderProgram(Text(element, "id"), Text(element, "variant"), precise,
            ReadStage(Field(element, "vertex", JsonValueKind.Object)),
            ReadStage(Field(element, "fragment", JsonValueKind.Object)), bindings);
    }

    static GpuShaderStageCode ReadStage(JsonElement element)
        => new(Text(element, "entryPoint"), Convert.FromBase64String(Text(element, "spirv")),
            Text(element, "msl"), Text(element, "hlsl"));

    static GpuResourceKind Kind(string name)
        => Enum.IsDefined(typeof(GpuResourceKind), name)
            ? Enum.Parse<GpuResourceKind>(name)
            : throw new FormatException($"Resource kind '{name}' is not a GpuResourceKind member name.");

    static int Index(JsonElement element, string name)
        => Field(element, name, JsonValueKind.Number).TryGetInt32(out var value) && value >= 0
            ? value
            : throw new FormatException($"Field '{name}' is not a non-negative 32-bit integer.");

    static string Text(JsonElement element, string name) => Field(element, name, JsonValueKind.String).GetString()!;

    static JsonElement Field(JsonElement element, string name, params JsonValueKind[] kinds)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            throw new FormatException($"Field '{name}' is missing.");
        return Array.IndexOf(kinds, value.ValueKind) >= 0
            ? value
            : throw new FormatException($"Field '{name}' is {value.ValueKind}, not {string.Join(" or ", kinds)}.");
    }
}
