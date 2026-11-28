using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KhaozEngine.ShaderBuild;

/// <summary>Writes the bundle JSON the engine's GpuShaderBundle reads, byte for byte the same on every host.</summary>
public static class BundleWriter
{
    public const string Format = "khaoz-shader-bundle";
    public const int FormatVersion = 1;

    /// <summary>Returns UTF-8 JSON with LF line endings and two-space indents, programs by id then variant.</summary>
    public static byte[] Write(string slangcVersion, IEnumerable<BundleProgram> programs)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            // The bundle is never embedded in HTML, so MSL and HLSL operators stay readable instead of < escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var json = new Utf8JsonWriter(buffer, options))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteNumber("version", FormatVersion);
            json.WriteString("slangc", slangcVersion);
            json.WriteStartArray("programs");
            foreach (var program in programs.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Variant, StringComparer.Ordinal))
                WriteProgram(json, program);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteProgram(Utf8JsonWriter json, BundleProgram program)
    {
        json.WriteStartObject();
        json.WriteString("id", program.Id);
        json.WriteString("variant", program.Variant);
        json.WriteBoolean("vertexPreciseMath", program.VertexPreciseMath);
        json.WriteStartArray("bindings");
        foreach (var binding in program.Bindings.OrderBy(b => b.Set).ThenBy(b => b.Binding))
        {
            json.WriteStartObject();
            json.WriteString("name", binding.Name);
            json.WriteNumber("set", binding.Set);
            json.WriteNumber("binding", binding.Binding);
            json.WriteString("kind", binding.Kind.ToString());
            json.WriteNumber("metal", binding.MetalIndex);
            json.WriteNumber("hlsl", binding.HlslRegister);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        WriteStage(json, "vertex", program.Vertex);
        WriteStage(json, "fragment", program.Fragment);
        json.WriteEndObject();
    }

    private static void WriteStage(Utf8JsonWriter json, string name, BundleStage stage)
    {
        json.WriteStartObject(name);
        json.WriteString("entryPoint", stage.EntryPoint);
        json.WriteBase64String("spirv", stage.Spirv);
        json.WriteString("msl", stage.Msl);
        json.WriteString("hlsl", stage.Hlsl);
        json.WriteEndObject();
    }
}

/// <summary>One compiled program variant as the bundle records it.</summary>
public sealed record BundleProgram(
    string Id, string Variant, bool VertexPreciseMath, IReadOnlyList<BundleBinding> Bindings, BundleStage Vertex, BundleStage Fragment);

/// <summary>One stage's declared entry point and its SPIR-V, MSL and HLSL code.</summary>
public sealed record BundleStage(string EntryPoint, byte[] Spirv, string Msl, string Hlsl);
