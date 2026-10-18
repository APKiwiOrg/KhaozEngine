namespace KhaozEngine.Tests.Gpu
{
    internal static class PreciseMathShaders
    {
        internal const string Compute = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
void main() {
    float a = uintBitsToFloat(Inputs[0]);
    float b = uintBitsToFloat(Inputs[1]);
    float c = uintBitsToFloat(Inputs[2]);
    precise float p = a * b;
    precise float q = p + c;
    Results[0] = floatBitsToUint(q);
}";

        internal const string Vertex = @"#version 450
layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
layout(location = 0) flat out uint Result;
void main() {
    float a = uintBitsToFloat(Inputs[0]);
    float b = uintBitsToFloat(Inputs[1]);
    float c = uintBitsToFloat(Inputs[2]);
    precise float p = a * b;
    precise float q = p + c;
    Result = floatBitsToUint(q);
    vec2 corners[3] = vec2[](vec2(-1, -1), vec2(3, -1), vec2(-1, 3));
    gl_Position = vec4(corners[gl_VertexIndex], 0, 1);
}";

        internal const string Fragment = @"#version 450
layout(location = 0) flat in uint Result;
layout(location = 0) out vec4 Colour;
void main() { Colour = Result == 0u ? vec4(0, 1, 0, 1) : vec4(1, 0, 0, 1); }";
    }
}
