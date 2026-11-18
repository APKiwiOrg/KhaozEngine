namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // The mean and four fetches remain float32 locals. Only the completed children cross a target boundary.
    internal const string TemporalDetailChildrenGlsl = @"
void detailChildren(inout vec4 r, vec4 mean, vec4 a, vec4 b, vec4 c, vec4 d, float retention,
    out vec4 child0, out vec4 child1, out vec4 child2, out vec4 child3) {
    child0 = r; child1 = r; child2 = r; child3 = r;
    if (!(retention > 0.0) || !detailFinite4(r) || !detailFinite4(mean) || !detailFinite4(a) || !detailFinite4(b)
        || !detailFinite4(c) || !detailFinite4(d) || any(lessThan(r, vec4(0.0)))
        || any(greaterThan(r, vec4(65504.0)))) return;
    vec4 storedR = detailStoredResult(r);
    vec4 red = detailChannel(storedR.r, vec4(a.r,b.r,c.r,d.r), mean.r, retention);
    vec4 green = detailChannel(storedR.g, vec4(a.g,b.g,c.g,d.g), mean.g, retention);
    vec4 blue = detailChannel(storedR.b, vec4(a.b,b.b,c.b,d.b), mean.b, retention);
    vec4 alpha = detailChannel(storedR.a, vec4(a.a,b.a,c.a,d.a), mean.a, retention);
    // Nothing survived transport. Keep one raw value for all five same-format conversions.
    if (all(equal(red, vec4(storedR.r))) && all(equal(green, vec4(storedR.g)))
        && all(equal(blue, vec4(storedR.b))) && all(equal(alpha, vec4(storedR.a)))) return;
    r = storedR;
    child0 = vec4(red.x, green.x, blue.x, alpha.x);
    child1 = vec4(red.y, green.y, blue.y, alpha.y);
    child2 = vec4(red.z, green.z, blue.z, alpha.z);
    child3 = vec4(red.w, green.w, blue.w, alpha.w);
}";
}
