#version 450
layout(set = 0, binding = 0) uniform texture2D depthImage;
layout(set = 0, binding = 1) uniform sampler pointSampler;
layout(location = 0) out vec4 color;
void main() {
    float depth = texelFetch(sampler2D(depthImage, pointSampler), ivec2(gl_FragCoord.xy), 0).r;
    color = vec4(depth, 0.0, 0.0, 1.0);
}
