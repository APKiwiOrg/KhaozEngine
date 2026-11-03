using System;

namespace KhaozEngine.Tests.Gpu;

internal static class CandidateGateSource
{
    internal static string WithoutGates(string source) => source
        .Replace(@"        if (Params.w > 0.5 && all(equal(Sizes.xy, Sizes.zw))) {
            kernels.displayX[i] = kernels.x[i];
            kernels.displayY[i] = kernels.y[i];
        } else {
            kernels.displayX[i] = toAfloat(lanczos2(toSample.x * displayOverInternal));
            kernels.displayY[i] = toAfloat(lanczos2(toSample.y * displayOverInternal));
        }
".ReplaceLineEndings("\n"), @"        kernels.displayX[i] = toAfloat(lanczos2(toSample.x * displayOverInternal));
        kernels.displayY[i] = toAfloat(lanczos2(toSample.y * displayOverInternal));
".ReplaceLineEndings("\n"), StringComparison.Ordinal)
        .Replace(@"    bool evaluateSpecular = (int(IndirectParams.w) & 1) == 0 || specStrength != 0.0;
    vec3 V = vec3(0.0);
    float spec = 0.0;
    if (evaluateSpecular) {
        V = normalize(CameraPos.xyz - worldPos);
        vec3 H = normalize(-normalize(LightDir.xyz) + V);
        spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey) * keyShadow;
    }
".ReplaceLineEndings("\n"), @"    vec3 V = normalize(CameraPos.xyz - worldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey) * keyShadow;
".ReplaceLineEndings("\n"), StringComparison.Ordinal)
        .Replace(@"        float sp = 0.0;
        if (evaluateSpecular) {
            vec3 Hp = normalize(L + V);
            sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
        }
".ReplaceLineEndings("\n"), @"        vec3 Hp = normalize(L + V);
        float sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
".ReplaceLineEndings("\n"), StringComparison.Ordinal)
        .Replace(@"    bool reuseSlots = (int(IndirectParams.w) & 2) != 0;
    vec3 materialSamples[5];
    vec3 albedo = vec3(0.0);
    for (int L = 0; L < 5; L++) {
        float wl = w[L];
        if (wl <= 0.001) continue;
        int sl = slot[L];
        int reuse = -1;
        if (reuseSlots) {
            for (int P = 0; P < L; P++) {
                if (w[P] > 0.001 && slot[P] == sl) { reuse = P; break; }
            }
        }
        vec3 c;
        if (reuse >= 0) {
            c = materialSamples[reuse];
        } else {
            c = sampleMaterial(sl, wpAbs, dWx, dWy, tw, tri);
            // The material's steep companion, a property of the MATERIAL whatever slot role it fills here. Its own
            // companion is never followed. A material with none, or a slope below its band, takes no extra tap.
            vec4 sb = SteepBand[sl];
            if (sb.x > 0.5) {
                float s = 1.0 - smoothstep(sb.y, sb.z, ny);
                if (s > 0.0) {
                    c = mix(c, sampleMaterial(clamp(int(sb.x - 0.5), 0, maxSlot), wpAbs, dWx, dWy, tw, tri), s);
                }
            }
        }
        if (reuseSlots) materialSamples[L] = c;
        albedo += wl * c;
    }
".ReplaceLineEndings("\n"), @"    vec3 albedo = vec3(0.0);
    for (int L = 0; L < 5; L++) {
        float wl = w[L];
        if (wl <= 0.001) continue;
        int sl = slot[L];
        vec3 c = sampleMaterial(sl, wpAbs, dWx, dWy, tw, tri);
        // The material's steep companion, a property of the MATERIAL whatever slot role it fills here. Its own
        // companion is never followed. A material with none, or a slope below its band, takes no extra tap.
        vec4 sb = SteepBand[sl];
        if (sb.x > 0.5) {
            float s = 1.0 - smoothstep(sb.y, sb.z, ny);
            if (s > 0.0) {
                c = mix(c, sampleMaterial(clamp(int(sb.x - 0.5), 0, maxSlot), wpAbs, dWx, dWy, tw, tri), s);
            }
        }
        albedo += wl * c;
    }
".ReplaceLineEndings("\n"), StringComparison.Ordinal);
}
