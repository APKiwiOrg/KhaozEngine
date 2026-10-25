#version 450
layout(local_size_x = 1) in;
layout(std430, set = 0, binding = 0) readonly buffer InputWords { uint inputWords[]; };
layout(std430, set = 0, binding = 1) buffer OutputWords { uint outputWords[]; };
void main() { outputWords[0] = inputWords[0] + 1u; }
