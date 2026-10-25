#version 450
layout(location = 0) out vec4 color;
layout(location = 1) out vec4 normal;
layout(location = 2) out float depth;
layout(location = 3) out vec2 motion;
void main() {
    color = vec4(0.25, 0.5, 0.75, 0.5);
    normal = vec4(0.25, 0.5, 0.75, 1.0);
    depth = 0.5;
    motion = vec2(0.125, 0.25);
}
