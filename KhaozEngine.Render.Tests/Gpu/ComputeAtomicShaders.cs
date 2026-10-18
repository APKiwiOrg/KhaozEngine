namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Unsigned storage-word atomic fixtures shared by device and real compiler conformance tests.</summary>
    internal static class ComputeAtomicShaders
    {
        internal const string UnsignedOrMax = @"#version 450
layout(local_size_x = 64) in;
layout(std430, set = 0, binding = 0) buffer WordBuf { uint Words[]; };
void main() {
    uint i = gl_GlobalInvocationID.x;
    atomicOr(Words[0], 1u << (i & 31u));
    atomicMax(Words[1], 0x80000000u | i);
}";

        internal const string ReturnOldValue = @"#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) buffer WordBuf { uint Words[]; };
void main() {
    Words[0] = atomicOr(Words[2], 4u);
    Words[1] = atomicMax(Words[2], 0x80000000u);
}";
    }
}
