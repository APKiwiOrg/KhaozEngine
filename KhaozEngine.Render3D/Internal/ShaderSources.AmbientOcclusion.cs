namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    const string AmbientOcclusionGeometry = @"
layout(set=0, binding=0) uniform texture2D DeviceDepth;
layout(set=0, binding=1) uniform texture2D GeometryNormal;
layout(set=0, binding=2) uniform texture2D Diffuse;
layout(set=0, binding=3) uniform sampler Point;
layout(set=0, binding=4) uniform AoFrame {
    mat4 InverseProjection;
    mat4 View;
    vec4 ProjectionScale; // x, y in half-viewport pixels; z = absolute perspective w scale or 0 for ortho
    vec4 Params; // radius in metres, strength, depth clear marker
};
layout(location=0) out vec4 oColor;

ivec2 fullSize() { return textureSize(sampler2D(DeviceDepth, Point), 0); }
ivec2 guidePixel(ivec2 halfPixel) { return min(halfPixel * 2 + 1, fullSize() - 1); }
vec4 diffuseAt(ivec2 p) { return texelFetch(sampler2D(Diffuse, Point), p, 0); }
float depthAt(ivec2 p) { return texelFetch(sampler2D(DeviceDepth, Point), p, 0).r; }
bool covered(ivec2 p, float depth) { return depth != Params.z || diffuseAt(p).a > 0.5; }

vec3 viewPosition(ivec2 p, float depth) {
    // Top-left integer texture coordinates and the engine's raw zero-to-one projection.
    vec2 ndc = ((vec2(p) + 0.5) / vec2(fullSize()) * 2.0 - 1.0) * vec2(1.0, -1.0);
    vec4 v = InverseProjection * vec4(ndc, depth, 1.0);
    return v.xyz / v.w;
}

vec3 viewNormal(ivec2 p) {
    vec3 n = texelFetch(sampler2D(GeometryNormal, Point), p, 0).xyz * 2.0 - 1.0;
    return normalize(mat3(View) * n);
}
";

    internal const string AmbientOcclusionHorizon = "#version 450\n" + AmbientOcclusionGeometry + @"
const float HalfPi = 1.57079632679;

float arc(float normalAngle, float horizon) {
    return (cos(normalAngle) + 2.0 * horizon * sin(normalAngle) - cos(2.0 * horizon - normalAngle)) * 0.25;
}

float horizon(ivec2 p, vec3 center, vec3 n, vec3 toEye, vec2 ray, float pixels) {
    float cosine = -1.0;
    ivec2 end = fullSize();
    ivec2 previous = p;
    for (int step = 1; step <= 4; ++step) {
        // Quadratic spacing keeps contact samples close, without missing the outer radius.
        float t = float(step) * 0.25;
        ivec2 q = p + ivec2(round(ray * max(float(step), pixels * t * t)));
        if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, end))) break;
        if (all(equal(p, q)) || all(equal(previous, q))) continue;
        previous = q;
        float depth = depthAt(q);
        if (!covered(q, depth)) continue;
        vec3 delta = viewPosition(q, depth) - center;
        float distance = length(delta);
        // Small plane bias rejects quantized depth/normal error on the receiving plane.
        if (distance <= 0.0 || distance >= Params.x || dot(n, delta) <= max(0.01, distance * 0.025)) continue;
        float falloff = 1.0 - smoothstep(0.15, 1.0, distance / Params.x);
        cosine = max(cosine, mix(-1.0, clamp(dot(delta, toEye) / distance, -1.0, 1.0), falloff));
    }
    return acos(cosine);
}

void main() {
    ivec2 p = guidePixel(ivec2(gl_FragCoord.xy));
    float depth = depthAt(p);
    oColor = vec4(0.0);
    if (!covered(p, depth)) return;

    vec3 center = viewPosition(p, depth);
    vec3 n = viewNormal(p);
    bool ortho = ProjectionScale.z == 0.0;
    vec3 toEye = ortho ? vec3(0.0, 0.0, 1.0) : normalize(-center);
    vec2 radiusPixels = min(vec2(fullSize()) * 2.0,
        ProjectionScale.xy * (Params.x / (ortho ? 1.0 : max(1e-4, -center.z * ProjectionScale.z))));
    if (max(radiusPixels.x, radiusPixels.y) < 1.0) return;
    float visibility = 0.0, unoccluded = 0.0;

    // Four unoriented screen axes integrate both sides of each slice about the view vector.
    // Fixed axes avoid an independent frame noise sequence before the engine's temporal resolve.
    for (int i = 0; i < 4; ++i) {
        float angle = float(i) * 0.78539816339;
        vec2 axis = vec2(cos(angle), sin(angle));
        vec2 pixelRay = axis * radiusPixels;
        float pixels = length(pixelRay);
        vec2 ray = pixelRay / max(pixels, 1e-5);
        vec3 t = vec3(axis.x, -axis.y, 0.0);
        t = normalize(t - toEye * dot(t, toEye));
        vec2 projected = vec2(dot(n, toEye), dot(n, t));
        float lengthN = length(projected);
        if (lengthN < 1e-4) continue;
        float normalAngle = atan(projected.y, max(0.0001, projected.x));
        float lo = normalAngle - HalfPi, hi = normalAngle + HalfPi;
        float hLo = clamp(-horizon(p, center, n, toEye, -ray, pixels), lo, hi);
        float hHi = clamp(horizon(p, center, n, toEye, ray, pixels), lo, hi);
        unoccluded += lengthN * (arc(normalAngle, lo) + arc(normalAngle, hi));
        visibility += lengthN * (arc(normalAngle, hLo) + arc(normalAngle, hHi));
    }
    oColor.r = clamp(1.0 - visibility / max(unoccluded, 1e-4), 0.0, 1.0);
}";

    internal const string AmbientOcclusionComposite = "#version 450\n" + AmbientOcclusionGeometry + @"
layout(set=0, binding=5) uniform texture2D Occlusion;

void main() {
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec4 diffuse = diffuseAt(pixel);
    if (diffuse.a < 0.5 || all(equal(diffuse.rgb, vec3(0.0)))) discard;
    vec3 p = viewPosition(pixel, depthAt(pixel));
    vec3 normal = viewNormal(pixel);
    ivec2 size = textureSize(sampler2D(Occlusion, Point), 0);
    float total = 0.0, sum = 0.0;

    // Joint filter and upsample. Compare view-space distances against the receiver's plane
    // so a sloped surface stays connected, but skip unrelated layers and normal discontinuities.
    ivec2 halfPixel = pixel / 2;
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x) {
        ivec2 q = halfPixel + ivec2(x, y);
        if (any(lessThan(q, ivec2(0))) || any(greaterThanEqual(q, size))) continue;
        ivec2 guide = guidePixel(q);
        float depth = depthAt(guide);
        if (!covered(guide, depth)) continue;
        vec3 delta = viewPosition(guide, depth) - p;
        float plane = clamp(1.0 - abs(dot(delta, normal)) / max(0.025, 0.12 * Params.x), 0.0, 1.0);
        float range = clamp(1.0 - length(delta) / Params.x, 0.0, 1.0);
        float alignment = max(0.0, dot(normal, viewNormal(guide)));
        vec2 d = vec2(guide - pixel);
        float w = exp2(-0.13 * dot(d, d)) * plane * plane * range * pow(alignment, 8.0);
        sum += w * texelFetch(sampler2D(Occlusion, Point), q, 0).r;
        total += w;
    }
    // Keep confidence in the filter near isolated thin surfaces. No matched samples means zero loss.
    float loss = sum / max(total, 0.1);
    oColor = vec4(diffuse.rgb * (Params.y * loss), 0.0);
}";
}
