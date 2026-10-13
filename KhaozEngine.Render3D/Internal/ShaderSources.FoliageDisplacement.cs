namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Current and previous state share one std140 block for colour, motion and depth evaluation.
    public const string FoliageUniformBlockGlsl = @"layout(set=1, binding=0) uniform Foliage {
    vec4 FocusRadius;
    vec4 Density;
    vec4 FadeWind;
    vec4 WindTime;
    vec4 Interactors[4];
    vec4 Strengths;
    vec4 WindFade;          // x = blade pixels where wind stops, y = metres per pixel now, z = the same last frame
    vec4 PrevFocus;         // xyz = last frame's focus, w = last frame's wind time
    vec4 PrevInteractors[4];
    vec4 PrevStrengths;
};
";

    // All foliage passes evaluate the same fade, rebasing and profile-controlled deformation.
    public const string FoliageDisplacementGlsl = @"vec3 foliageWorld(vec3 focus, float windTime, float metresPerPixel, mat4 fadeViewProj, bool previous,
                  out mat4 Model, out bool rejected) {
    float rank = FoliageParameters.x;
    float distanceToFocus = length(IModel3.xz - focus.xz);
    float cutoff = FocusRadius.w;
    float fadeStart = max(0.0, cutoff - WindTime.w);
    float inner = Density.z - Density.w;
    if (Density.x > Density.y && rank >= Density.y) {
        cutoff = inner + (Density.x - rank) / (Density.x - Density.y) * Density.w;
        fadeStart = max(inner, cutoff - min(FadeWind.x, Density.w));
    }
    float fade = cutoff > fadeStart ? clamp((distanceToFocus - fadeStart) / (cutoff - fadeStart), 0.0, 1.0) : 0.0;
    float heightFade = 1.0 - fade * fade * (3.0 - 2.0 * fade);
    rejected = rank >= Density.x || distanceToFocus > cutoff || heightFade <= 0.0001;
    Model = mat4(IModel0, IModel1, IModel2, IModel3);
    Model[3].xyz -= RenderOrigin.xyz;
    Model[3].xyz += IModel1.xyz * FoliageParameters.y * (1.0 - heightFade);
    Model[1].xyz *= max(heightFade, 0.00001);
    vec4 world = Model * vec4(Position, 1.0);
    float invHeight = FoliageParameters.z;
    float bladeHeight = invHeight > 0.0 ? length(IModel1.xyz) / invHeight * heightFade : 0.0;
    float rootWeight = clamp((Position.y - FoliageParameters.y) * invHeight, 0.0, 1.0);
    rootWeight *= rootWeight;
    float phase = dot(IModel3.xz, WindTime.xy) * FadeWind.w - windTime * FadeWind.z;
    vec3 root = (Model * vec4(0.0, FoliageParameters.y, 0.0, 1.0)).xyz;
    vec2 bend = WindTime.xy * (sin(phase) * 0.7 + sin(phase * 0.43 + 1.7) * 0.3) * FadeWind.y * bladeHeight * FoliageMotion.z;
    if (WindFade.x > 0.0) {
        // Sub-pixel sway only flips samples between foliage and ground. Fade wind before interactor pushes.
        // Clip w is unjittered view depth. A zero pixel scale keeps full wind, as does a disabled fade.
        float fadeHeight = max((fadeViewProj * vec4(root, 1.0)).w, 0.0) * metresPerPixel * WindFade.x;
        bend *= fadeHeight > 0.0 ? smoothstep(1.0, 2.0, bladeHeight / fadeHeight) : 1.0;
    }
    for (int i = 0; i < 4; i++) {
        vec4 interactor = previous ? PrevInteractors[i] : Interactors[i];
        float strength = previous ? PrevStrengths[i] : Strengths[i];
        if (interactor.w <= 0.0 || strength <= 0.0) continue;
        vec3 delta = root - (interactor.xyz - RenderOrigin.xyz);
        float falloff = 1.0 - smoothstep(0.0, interactor.w, length(delta));
        bend += delta.xz / max(length(delta.xz), 0.05) * falloff * strength * bladeHeight * FoliageMotion.x;
    }
    float bendLength = length(bend);
    float cap = bladeHeight * FoliageMotion.y;
    if (bendLength > cap && bendLength > 0.0) bend *= cap / bendLength;
    world.xz += bend * rootWeight;
    world.y -= (bladeHeight - sqrt(max(0.0, bladeHeight * bladeHeight - dot(bend, bend)))) * rootWeight;
    return world.xyz;
}
";
}
