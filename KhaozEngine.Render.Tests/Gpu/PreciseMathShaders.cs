namespace KhaozEngine.Tests.Gpu
{
    internal static class PreciseMathShaders
    {
        // Each row is (operation, left word, right word, result word). A null result means any NaN.
        internal static readonly (uint Op, uint Left, uint Right, uint? Result)[] IeeeRows =
        {
            (2, 0u, 0x80000000u, 0x80000000u),
            (2, 0x80000000u, 0u, 0x80000000u),
            (2, 0x80000000u, 0x80000000u, 0u),
            (2, 0xbf800000u, 0u, 0x80000000u),
            (2, 0x80800000u, 0x00800000u, 0x80000000u),
            (0, 0x80000000u, 0x80000000u, 0x80000000u),
            (0, 0u, 0x80000000u, 0u),
            (0, 0x3f800001u, 0xbf800001u, 0u),
            (1, 0x80000000u, 0u, 0x80000000u),
            (0, 0x7fc12345u, 0x3f800000u, null),
            (1, 0x7fc12345u, 0x3f800000u, null),
            (2, 0x7fc12345u, 0x3f800000u, null),
            (0, 0x3f800000u, 0xffc12345u, null),
            (1, 0x3f800000u, 0xffc12345u, null),
            (2, 0x3f800000u, 0xffc12345u, null),
            (1, 0x7f800000u, 0x7f800000u, null),
            (2, 0x7f800000u, 0x40000000u, 0x7f800000u),
            (2, 0x7f800000u, 0xc0000000u, 0xff800000u),
            (2, 0xff800000u, 0xc0000000u, 0x7f800000u),
        };

        internal const string IeeeCompute = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
void main() {
    uint row = gl_GlobalInvocationID.x;
    uint op = Inputs[row * 3u];
    float a = uintBitsToFloat(Inputs[row * 3u + 1u]);
    float b = uintBitsToFloat(Inputs[row * 3u + 2u]);
    precise float result;
    if (op == 0u) result = a + b;
    else if (op == 1u) result = a - b;
    else result = a * b;
    Results[row] = floatBitsToUint(result);
}";

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
