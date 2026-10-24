namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Shared verbatim by water and the independent diagnostic readback harness.
    internal const string WaterOpticsTriangleGlsl = WaterOpticsBinary32Glsl + WaterOpticsPredicatesGlsl + @"
uint opticsScalar(int address) {
    int width = int(OpticsParams.z);
    return floatBitsToUint(texelFetch(sampler2D(OpticsTex,OpticsSamp),ivec2(address%width,address/width),0).r);
}

int opticsAddress(int address) { return int(uintBitsToFloat(opticsScalar(address))); }

bool opticsContains(int triangle, int edges, uvec2 p) {
    for (int i = 0; i < 3; i++) {
        int reference = int(uintBitsToFloat(opticsScalar(triangle+9+i)));
        int edge = edges+4*(abs(reference)-1);
        int sign = opticsOrient(uvec2(opticsScalar(edge),opticsScalar(edge+1)),
            uvec2(opticsScalar(edge+2),opticsScalar(edge+3)),p);
        if (reference > 0 ? sign < 0 : sign > 0) return false;
    }
    return true;
}

uint opticsInterpolate(int triangle, uvec2 p) {
    uvec3 a = uvec3(opticsScalar(triangle),opticsScalar(triangle+1),opticsScalar(triangle+2));
    uvec3 b = uvec3(opticsScalar(triangle+3),opticsScalar(triangle+4),opticsScalar(triangle+5));
    uvec3 c = uvec3(opticsScalar(triangle+6),opticsScalar(triangle+7),opticsScalar(triangle+8));
    if ((a.z == b.z && a.z == c.z) || ((a.z | b.z | c.z) & 0x7fffffffu) == 0u) return a.z;
    uint dx = opticsAdd(b.x,a.x^0x80000000u), dz = opticsAdd(b.y,a.y^0x80000000u);
    uint ex = opticsAdd(c.x,a.x^0x80000000u), ez = opticsAdd(c.y,a.y^0x80000000u);
    uint px = opticsAdd(p.x,a.x^0x80000000u), pz = opticsAdd(p.y,a.y^0x80000000u);
    uint determinant = opticsAdd(opticsMul(dx,ez),opticsMul(dz,ex)^0x80000000u);
    uint bn = opticsAdd(opticsMul(px,ez),opticsMul(pz,ex)^0x80000000u);
    uint cn = opticsAdd(opticsMul(dx,pz),opticsMul(dz,px)^0x80000000u);
    uint wb = opticsDiv(bn,determinant), wc = opticsDiv(cn,determinant);
    uint db = opticsAdd(b.z,a.z^0x80000000u), dc = opticsAdd(c.z,a.z^0x80000000u);
    uint near = opticsAdd(a.z,opticsMul(wb,db));
    return opticsAdd(near,opticsMul(wc,dc));
}

uint opticsUnit(uint value) {
    return (value & 0x80000000u) != 0u ? 0u : min(value,0x3f800000u);
}

bool tryOpticsTriangle(vec2 local, out float depth) {
    uvec2 p = floatBitsToUint(local);
    int columns = opticsAddress(1), rows = opticsAddress(2);
    // The reciprocal and product each add at most u/(1-u) relative error. Together with the
    // bin product this is inside the constructor's outward 4u bin support and one-ULP expansion.
    uint u = opticsUnit(opticsMul(p.x,floatBitsToUint(OpticsRect.z)));
    uint v = opticsUnit(opticsMul(p.y,floatBitsToUint(OpticsRect.w)));
    int column = min(int(uintBitsToFloat(opticsMul(u,floatBitsToUint(float(columns))))),columns-1);
    int row = min(int(uintBitsToFloat(opticsMul(v,floatBitsToUint(float(rows))))),rows-1);
    int bin = 16+2*(row*columns+column);
    int first = opticsAddress(bin), count = opticsAddress(bin+1);
    int triangles = opticsAddress(4), edges = opticsAddress(5);
    for (int i = 0; i < 32; i++) {
        if (i >= count) break;
        int triangle = triangles+12*opticsAddress(first+i);
        if (!opticsContains(triangle,edges,p)) continue;
        depth = uintBitsToFloat(opticsInterpolate(triangle,p));
        return true;
    }
    depth = 0.0;
    return false;
}
";
}
