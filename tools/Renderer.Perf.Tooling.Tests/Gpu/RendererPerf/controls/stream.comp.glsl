#version 450
layout(local_size_x = 256) in;
layout(std430, set = 0, binding = 0) readonly buffer InputWords { uint inputWords[]; };
layout(std430, set = 0, binding = 1) buffer OutputWords { uint outputWords[]; };
void main() {
    uint i = gl_GlobalInvocationID.x + gl_GlobalInvocationID.y * 262144u;
    outputWords[i] = inputWords[i] ^ 0xa5a5a5a5u;
}
