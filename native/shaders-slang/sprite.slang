// Slang port of SpriteBatch.VertSrc and SpriteBatch.FragSrc (KhaozEngine.Render2D/SpriteBatch.cs), with the
// same math, vertex input locations and set and binding numbers. build-slang.sh compiles it.

struct ViewProjection
{
    float4x4 ViewProj;
};

[[vk::binding(0, 1)]] ConstantBuffer<ViewProjection> Vp;
[[vk::binding(0, 0)]] Texture2D<float4> Tex;
[[vk::binding(1, 0)]] SamplerState Samp;

struct VertexInput
{
    [[vk::location(0)]] float2 LocalPos;
    [[vk::location(1)]] float2 Uv;
    [[vk::location(2)]] float4 Color;
    [[vk::location(3)]] float2 Local;
    [[vk::location(4)]] float4 Shape;
    [[vk::location(5)]] float2 Mode;
};

// The varyings are written out twice because slangc 2026.23 crashes emitting Metal for a vertex output that
// nests a struct of two or more varyings, and the fragment input leaves out the position it does not read.
struct VertexOutput
{
    float4 Position : SV_Position;
    [[vk::location(0)]] float2 Uv;
    [[vk::location(1)]] float4 Color;
    [[vk::location(2)]] float2 Local;
    [[vk::location(3)]] float4 Shape;
    [[vk::location(4)]] float2 Mode;
};

struct FragmentInput
{
    [[vk::location(0)]] float2 Uv;
    [[vk::location(1)]] float4 Color;
    [[vk::location(2)]] float2 Local;
    [[vk::location(3)]] float4 Shape;
    [[vk::location(4)]] float2 Mode;
};

[shader("vertex")]
VertexOutput vertexMain(VertexInput input)
{
    VertexOutput output;
    output.Position = mul(Vp.ViewProj, float4(input.LocalPos, 0.0, 1.0));
    output.Uv = input.Uv;
    output.Color = input.Color;
    output.Local = input.Local;
    output.Shape = input.Shape;
    output.Mode = input.Mode;
    return output;
}

[shader("fragment")]
float4 fragmentMain(FragmentInput input) : SV_Target0
{
    float4 base = Tex.Sample(Samp, input.Uv) * input.Color;
    float2 b = input.Shape.xy;
    float r = input.Shape.z;
    float soft = input.Shape.w;
    float stroke = input.Mode.x;
    float2 q = abs(input.Local) - b + r;
    float d = min(max(q.x, q.y), 0.0) + length(max(q, float2(0.0))) - r;
    float dStroke = abs(d) - stroke * 0.5;
    d = stroke > 0.0 ? dStroke : d;
    // fwidth runs outside the select so the derivative stays in uniform control flow, as in the GLSL.
    float derivative = max(fwidth(d), 1e-4);
    float aa = soft > 0.0 ? soft : derivative;
    float cov = clamp(0.5 - d / aa, 0.0, 1.0);
    if (input.Mode.y < 0.5)
    {
        return base;
    }
    base.a *= cov;
    return base;
}
