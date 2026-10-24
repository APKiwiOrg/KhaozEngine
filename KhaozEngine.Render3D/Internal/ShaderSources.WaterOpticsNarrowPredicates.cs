namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Six products are each below 2^48. After removing the common shift, span <= 77
    // bounds either unsigned magnitude sum by 6*2^(48+77) < 2^128.
    // Wider domains keep the original 18-word predicate. No floating-point filter is used.
    const string WaterOpticsNarrowPredicatesGlsl = @"
// 512 is outside the finite product-shift range 0..506 and marks an exact zero product.
int opticsProductShift(uint a, uint b) {
    if ((a & 0x7fffffffu) == 0u || (b & 0x7fffffffu) == 0u) return 512;
    return int(max((a >> 23u) & 255u,1u)+max((b >> 23u) & 255u,1u))-2;
}

int opticsOrientBaseShift(uvec2 a, uvec2 b, uvec2 p) {
    ivec3 x = ivec3(opticsProductShift(b.x,p.y),opticsProductShift(b.x,a.y),opticsProductShift(a.x,p.y));
    ivec3 y = ivec3(opticsProductShift(b.y,p.x),opticsProductShift(b.y,a.x),opticsProductShift(a.y,p.x));
    ivec3 lo = min(x,y), hi = max(x & ivec3(511),y & ivec3(511));
    int smallest = min(lo.x,min(lo.y,lo.z)), largest = max(hi.x,max(hi.y,hi.z));
    if (smallest == 512) return 0;
    return largest-smallest <= 77 ? smallest : -1;
}

void opticsNarrowAdd(inout uvec4 sum, uvec4 value) {
    uvec4 joined = sum+value;
    uvec4 carry = uvec4(lessThan(joined,sum));
    joined.y += carry.x;
    carry.y |= joined.y < carry.x ? 1u : 0u;
    joined.z += carry.y;
    carry.z |= joined.z < carry.y ? 1u : 0u;
    joined.w += carry.z;
    sum = joined;
}

void opticsNarrowProduct(inout uvec4 positive, inout uvec4 negative, uint a, uint b, bool minus, int baseShift) {
    uint ae = (a >> 23u) & 255u, be = (b >> 23u) & 255u;
    uint am = (a & 0x7fffffu) | (ae == 0u ? 0u : 0x800000u);
    uint bm = (b & 0x7fffffu) | (be == 0u ? 0u : 0x800000u);
    if (am == 0u || bm == 0u) return;
    int shift = int(max(ae,1u)+max(be,1u))-2-baseShift;
    int word = shift/32;
    uint offset = uint(shift%32);
    uvec2 product = opticsProduct24(am,bm);
    uint low = product.x << offset;
    uint middle = offset == 0u ? product.y : (product.y << offset) | (product.x >> (32u-offset));
    uint high = offset == 0u ? 0u : product.y >> (32u-offset);
    // word is 0, 1 or 2. In word 2, offset <= 13 makes high exactly zero.
    uvec4 value = word == 0 ? uvec4(low,middle,high,0u)
        : word == 1 ? uvec4(0u,low,middle,high) : uvec4(0u,0u,low,middle);
    bool neg = minus != (((a ^ b) & 0x80000000u) != 0u);
    if (neg) opticsNarrowAdd(negative,value);
    else opticsNarrowAdd(positive,value);
}

int opticsOrientNarrow(uvec2 a, uvec2 b, uvec2 p, int baseShift) {
    uvec4 positive = uvec4(0u), negative = uvec4(0u);
    opticsNarrowProduct(positive,negative,b.x,p.y,false,baseShift);
    opticsNarrowProduct(positive,negative,b.x,a.y,true,baseShift);
    opticsNarrowProduct(positive,negative,a.x,p.y,true,baseShift);
    opticsNarrowProduct(positive,negative,b.y,p.x,true,baseShift);
    opticsNarrowProduct(positive,negative,b.y,a.x,false,baseShift);
    opticsNarrowProduct(positive,negative,a.y,p.x,false,baseShift);
    for (int i = 3; i >= 0; i--) {
        if (positive[i] > negative[i]) return 1;
        if (positive[i] < negative[i]) return -1;
    }
    return 0;
}
";
}
