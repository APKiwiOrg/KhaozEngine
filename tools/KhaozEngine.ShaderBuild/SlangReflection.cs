using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.ShaderBuild;

/// <summary>Joins slangc's SPIR-V, Metal and HLSL reflection into one binding list keyed by parameter name.</summary>
public static class SlangReflection
{
    /// <summary>The highest Metal buffer index a resource may take, because vertex streams count down from buffer 30.</summary>
    public const int MaxMetalBufferIndex = 15;

    /// <summary>Returns the bindings sorted by set, then binding.</summary>
    public static IReadOnlyList<BundleBinding> Join(string spirvJson, string metalJson, string hlslJson)
    {
        string? current = null;
        try
        {
            using var spirv = JsonDocument.Parse(spirvJson);
            using var metal = JsonDocument.Parse(metalJson);
            using var hlsl = JsonDocument.Parse(hlslJson);
            var metalByName = ByName(metal);
            var hlslByName = ByName(hlsl);
            var bindings = new List<BundleBinding>();
            foreach (var (name, parameter) in ByName(spirv))
            {
                current = name;
                if (!metalByName.Remove(name, out var metalParameter) || !hlslByName.Remove(name, out var hlslParameter))
                    throw new FormatException("it is missing from the Metal or HLSL reflection");
                bindings.Add(JoinOne(name, parameter, metalParameter, hlslParameter));
            }
            current = metalByName.Keys.Concat(hlslByName.Keys).FirstOrDefault();
            if (current is not null)
                throw new FormatException("it is missing from the SPIR-V reflection");
            return bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding).ToList();
        }
        catch (Exception problem) when (problem is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw Refusal(ShaderBuildFailure.ManifestInvalid, current, problem.Message, problem);
        }
    }

    private static BundleBinding JoinOne(string name, JsonElement spirv, JsonElement metal, JsonElement hlsl)
    {
        var spirvBinding = spirv.GetProperty("binding");
        if (spirvBinding.GetProperty("kind").GetString() != "descriptorTableSlot")
            throw new FormatException($"its SPIR-V binding kind is '{spirvBinding.GetProperty("kind").GetString()}', not a descriptor slot");
        var set = spirvBinding.TryGetProperty("space", out var space) ? space.GetInt32() : 0;
        var binding = spirvBinding.GetProperty("index").GetInt32();

        var metalBinding = metal.GetProperty("binding");
        var hlslBinding = hlsl.GetProperty("binding");
        var kindName = metalBinding.GetProperty("kind").GetString();
        if (hlslBinding.GetProperty("kind").GetString() != kindName)
            throw new FormatException("its Metal and HLSL binding kinds differ");
        var kind = Kind(kindName, metal.GetProperty("type"));
        var metalIndex = metalBinding.GetProperty("index").GetInt32();
        if (kind == BundleResourceKind.UniformBuffer && metalIndex > MaxMetalBufferIndex)
            throw Refusal(
                ShaderBuildFailure.MetalIndexInStreamRange,
                name,
                $"its Metal buffer index {metalIndex} is above {MaxMetalBufferIndex}, inside the vertex stream range",
                null);
        return new BundleBinding(name, set, binding, kind, metalIndex, hlslBinding.GetProperty("index").GetInt32());
    }

    private static BundleResourceKind Kind(string? bindingKind, JsonElement type) => bindingKind switch
    {
        "constantBuffer" => BundleResourceKind.UniformBuffer,
        "samplerState" => BundleResourceKind.Sampler,
        "shaderResource" when IsReadOnlyTexture(type) => BundleResourceKind.TextureReadOnly,
        _ => throw new FormatException($"its binding kind '{bindingKind}' has no bundle resource kind"),
    };

    private static bool IsReadOnlyTexture(JsonElement type)
        => type.TryGetProperty("kind", out var kind) && kind.GetString() == "resource"
            && type.TryGetProperty("baseShape", out var shape) && shape.GetString()!.StartsWith("texture", StringComparison.Ordinal)
            && (!type.TryGetProperty("access", out var access) || access.GetString() == "read");

    private static Dictionary<string, JsonElement> ByName(JsonDocument reflection)
    {
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var parameter in reflection.RootElement.GetProperty("parameters").EnumerateArray())
            byName.Add(parameter.GetProperty("name").GetString()!, parameter);
        return byName;
    }

    private static ShaderBuildException Refusal(ShaderBuildFailure failure, string? parameter, string reason, Exception? inner)
        => new(failure, parameter is null ? $"The shader reflection cannot be read: {reason}." : $"Shader parameter '{parameter}' cannot be bound: {reason}.", innerException: inner)
        { Parameter = parameter };
}

/// <summary>The resource kinds a bundle binding takes, named as the engine's GpuResourceKind members.</summary>
public enum BundleResourceKind
{
    UniformBuffer,
    TextureReadOnly,
    Sampler,
}

/// <summary>One shader parameter's Vulkan set and binding, resource kind, Metal index and HLSL register.</summary>
public sealed record BundleBinding(string Name, int Set, int Binding, BundleResourceKind Kind, int MetalIndex, int HlslRegister);
