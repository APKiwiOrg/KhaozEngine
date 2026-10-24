namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Exact binary32 orientation. Each input is a signed integer in units of 2^-149.
    // Wide fallback: six coordinate products fit below 2^557. Each magnitude sum uses 576 bits.
    // No float filter, optional double capability, epsilon or source-coordinate modification is involved.
    const string WaterOpticsPredicatesGlsl = WaterOpticsNarrowPredicatesGlsl + @"
struct OpticsWide { uint limb[18]; };

void opticsWideAdd(inout OpticsWide sum, int word, uint value) {
    [[dont_unroll]] for (int i = word; i < 18; i++) {
        uint old = sum.limb[i];
        sum.limb[i] = old+value;
        value = sum.limb[i] < old ? 1u : 0u;
        if (value == 0u) break;
    }
}

void opticsWideProduct(inout OpticsWide positive, inout OpticsWide negative, uint a, uint b, bool minus) {
    uint ae = (a >> 23u) & 255u, be = (b >> 23u) & 255u;
    uint am = (a & 0x7fffffu) | (ae == 0u ? 0u : 0x800000u);
    uint bm = (b & 0x7fffffu) | (be == 0u ? 0u : 0x800000u);
    if (am == 0u || bm == 0u) return;
    int shift = int(max(ae,1u)+max(be,1u))-2;
    int word = shift/32;
    uint offset = uint(shift%32);
    uvec2 p = opticsProduct24(am,bm);
    uint low = p.x << offset;
    uint middle = offset == 0u ? p.y : (p.y << offset) | (p.x >> (32u-offset));
    uint high = offset == 0u ? 0u : p.y >> (32u-offset);
    bool neg = minus != (((a ^ b) & 0x80000000u) != 0u);
    if (neg) {
        opticsWideAdd(negative,word,low);
        opticsWideAdd(negative,word+1,middle);
        opticsWideAdd(negative,word+2,high);
    } else {
        opticsWideAdd(positive,word,low);
        opticsWideAdd(positive,word+1,middle);
        opticsWideAdd(positive,word+2,high);
    }
}

int opticsOrientWide(uvec2 a, uvec2 b, uvec2 p) {
    OpticsWide positive, negative;
    for (int i = 0; i < 18; i++) { positive.limb[i] = 0u; negative.limb[i] = 0u; }
    // Keep one product body through exhaustive inlining, with the original accumulator order.
    [[dont_unroll]] for (int term = 0; term < 6; term++) {
        uint left, right;
        bool minus;
        if (term == 0) { left = b.x; right = p.y; minus = false; }
        else if (term == 1) { left = b.x; right = a.y; minus = true; }
        else if (term == 2) { left = a.x; right = p.y; minus = true; }
        else if (term == 3) { left = b.y; right = p.x; minus = true; }
        else if (term == 4) { left = b.y; right = a.x; minus = false; }
        else { left = a.y; right = p.x; minus = false; }
        opticsWideProduct(positive,negative,left,right,minus);
    }
    for (int i = 17; i >= 0; i--) {
        if (positive.limb[i] > negative.limb[i]) return 1;
        if (positive.limb[i] < negative.limb[i]) return -1;
    }
    return 0;
}

int opticsOrient(uvec2 a, uvec2 b, uvec2 p) {
    int baseShift = opticsOrientBaseShift(a,b,p);
    if (baseShift >= 0) return opticsOrientNarrow(a,b,p,baseShift);
    return opticsOrientWide(a,b,p);
}
";
}
