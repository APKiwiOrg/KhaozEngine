using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// GPU value structs deliberately have no serialization dependency. Their wire constructors live here.
internal static partial class RendererPerfCompilerJson
{
    sealed record Vector(float X, float Y, float Z, float W);
    sealed record Layout(string Name, GpuResourceKind Kind, GpuShaderStages Stages, bool Dynamic);
    sealed record Vertex(string Name, GpuVertexElementFormat Format);
    sealed record Blend(bool BlendEnabled, GpuBlendFactor SourceColorFactor, GpuBlendFactor DestinationColorFactor,
        GpuBlendFunction ColorFunction, GpuBlendFactor SourceAlphaFactor, GpuBlendFactor DestinationAlphaFactor, GpuBlendFunction AlphaFunction);
    sealed record Depth(bool DepthTestEnabled, bool DepthWriteEnabled, GpuComparison Comparison);
    sealed record Raster(GpuFaceCull CullMode, GpuPolygonFill FillMode, GpuFrontFace FrontFace, bool DepthClipEnabled, bool ScissorTestEnabled);
    static void AddValues(JsonSerializerOptions o)
    {
        o.Converters.Add(new ValueConverter<Vector4, Vector>(v => new(v.X, v.Y, v.Z, v.W), v => new(v.X, v.Y, v.Z, v.W)));
        o.Converters.Add(new ValueConverter<GpuResourceLayoutElement, Layout>(v => new(v.Name, v.Kind, v.Stages, v.Dynamic), v => new(v.Name, v.Kind, v.Stages, v.Dynamic)));
        o.Converters.Add(new ValueConverter<GpuVertexElement, Vertex>(v => new(v.Name, v.Format), v => new(v.Name, v.Format)));
        o.Converters.Add(new ValueConverter<GpuBlendAttachment, Blend>(v => new(v.BlendEnabled, v.SourceColorFactor, v.DestinationColorFactor,
            v.ColorFunction, v.SourceAlphaFactor, v.DestinationAlphaFactor, v.AlphaFunction), v => new(v.BlendEnabled, v.SourceColorFactor,
            v.DestinationColorFactor, v.ColorFunction, v.SourceAlphaFactor, v.DestinationAlphaFactor, v.AlphaFunction)));
        o.Converters.Add(new ValueConverter<GpuDepthStencilState, Depth>(v => new(v.DepthTestEnabled, v.DepthWriteEnabled, v.Comparison),
            v => new(v.DepthTestEnabled, v.DepthWriteEnabled, v.Comparison)));
        o.Converters.Add(new ValueConverter<GpuRasterizerState, Raster>(v => new(v.CullMode, v.FillMode, v.FrontFace, v.DepthClipEnabled, v.ScissorTestEnabled),
            v => new(v.CullMode, v.FillMode, v.FrontFace, v.DepthClipEnabled, v.ScissorTestEnabled)));
    }
    sealed class ValueConverter<T, TWire>(Func<T, TWire> encode, Func<TWire, T> decode) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => decode(JsonSerializer.Deserialize<TWire>(ref reader, options) ?? throw new JsonException("Missing GPU value."));
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, encode(value), options);
    }
}
