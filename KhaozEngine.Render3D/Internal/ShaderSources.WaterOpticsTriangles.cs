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
    [[dont_unroll]] for (int i = 0; i < 3; i++) {
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
    // Fixed ordered interpolation shares one body per operation after exhaustive inlining.
    // Slots 0..5 are dx,dz,ex,ez,px,pz, 6..7 temporaries, 8..10 det,bn,cn,
    // 11..14 wb,wc,db,dc and 15 near/result. Every read follows its producing step.
    uint r[16];
    [[dont_unroll]] for (uint step = 0u; step < 23u; step++) {
        uint left = 0u, right = 0u;
        int operation = 0, destination = int(step);
        switch (step) {
            case 0u: left = b.x; right = a.x^0x80000000u; break;
            case 1u: left = b.y; right = a.y^0x80000000u; break;
            case 2u: left = c.x; right = a.x^0x80000000u; break;
            case 3u: left = c.y; right = a.y^0x80000000u; break;
            case 4u: left = p.x; right = a.x^0x80000000u; break;
            case 5u: left = p.y; right = a.y^0x80000000u; break;
            case 6u: left = r[0]; right = r[3]; operation = 1; destination = 6; break;
            case 7u: left = r[1]; right = r[2]; operation = 1; destination = 7; break;
            case 8u: left = r[6]; right = r[7]^0x80000000u; destination = 8; break;
            case 9u: left = r[4]; right = r[3]; operation = 1; destination = 6; break;
            case 10u: left = r[5]; right = r[2]; operation = 1; destination = 7; break;
            case 11u: left = r[6]; right = r[7]^0x80000000u; destination = 9; break;
            case 12u: left = r[0]; right = r[5]; operation = 1; destination = 6; break;
            case 13u: left = r[1]; right = r[4]; operation = 1; destination = 7; break;
            case 14u: left = r[6]; right = r[7]^0x80000000u; destination = 10; break;
            case 15u: left = r[9]; right = r[8]; operation = 2; destination = 11; break;
            case 16u: left = r[10]; right = r[8]; operation = 2; destination = 12; break;
            case 17u: left = b.z; right = a.z^0x80000000u; destination = 13; break;
            case 18u: left = c.z; right = a.z^0x80000000u; destination = 14; break;
            case 19u: left = r[11]; right = r[13]; operation = 1; destination = 6; break;
            case 20u: left = a.z; right = r[6]; destination = 15; break;
            case 21u: left = r[12]; right = r[14]; operation = 1; destination = 6; break;
            case 22u: left = r[15]; right = r[6]; destination = 15; break;
        }
        uint result;
        if (operation == 0) result = opticsAdd(left,right);
        else if (operation == 1) result = opticsMul(left,right);
        else result = opticsDiv(left,right);
        // Literal destinations let the compiler carry independent scalar registers through the loop.
        switch (destination) {
            case 0: r[0] = result; break;
            case 1: r[1] = result; break;
            case 2: r[2] = result; break;
            case 3: r[3] = result; break;
            case 4: r[4] = result; break;
            case 5: r[5] = result; break;
            case 6: r[6] = result; break;
            case 7: r[7] = result; break;
            case 8: r[8] = result; break;
            case 9: r[9] = result; break;
            case 10: r[10] = result; break;
            case 11: r[11] = result; break;
            case 12: r[12] = result; break;
            case 13: r[13] = result; break;
            case 14: r[14] = result; break;
            case 15: r[15] = result; break;
        }
    }
    return r[15];
}

uint opticsUnit(uint value) {
    return (value & 0x80000000u) != 0u ? 0u : min(value,0x3f800000u);
}

bool tryOpticsTriangle(vec2 local, out float depth) {
    uvec2 p = floatBitsToUint(local);
    int columns = opticsAddress(1), rows = opticsAddress(2);
    // The reciprocal and product each add at most u/(1-u) relative error. Together with the
    // bin product this is inside the constructor's outward 4u bin support and one-ULP expansion.
    uint mapped[2];
    int column = 0, row = 0;
    // The first two rounded/clamped products feed the last two, in the original u,v,column,row order.
    [[dont_unroll]] for (int step = 0; step < 4; step++) {
        uint left, right;
        if (step == 0) { left = p.x; right = floatBitsToUint(OpticsRect.z); }
        else if (step == 1) { left = p.y; right = floatBitsToUint(OpticsRect.w); }
        else if (step == 2) { left = mapped[0]; right = floatBitsToUint(float(columns)); }
        else { left = mapped[1]; right = floatBitsToUint(float(rows)); }
        uint result = opticsMul(left,right);
        if (step < 2) mapped[step] = opticsUnit(result);
        else if (step == 2) column = min(int(uintBitsToFloat(result)),columns-1);
        else row = min(int(uintBitsToFloat(result)),rows-1);
    }
    int bin = 16+2*(row*columns+column);
    int first = opticsAddress(bin), count = opticsAddress(bin+1);
    int triangles = opticsAddress(4), edges = opticsAddress(5);
    [[dont_unroll]] for (int i = 0; i < 32; i++) {
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
