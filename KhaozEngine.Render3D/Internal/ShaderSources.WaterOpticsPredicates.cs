namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Exact binary32 orientation. Each input is a signed integer in units of 2^-149.
    // Wide fallback: six coordinate products fit below 2^557. Each magnitude sum streams 576 bits.
    // No float filter, optional double capability, epsilon or source-coordinate modification is involved.
    const string WaterOpticsPredicatesGlsl = WaterOpticsNarrowPredicatesGlsl + @"
int opticsOrientWide(uvec2 a, uvec2 b, uvec2 p) {
    uvec4 products[6];
    // Cache each exact product once, in the original signed operand order.
    [[dont_unroll]] for (int term = 0; term < 6; term++) {
        uint left, right;
        bool minus;
        if (term == 0) { left = b.x; right = p.y; minus = false; }
        else if (term == 1) { left = b.x; right = a.y; minus = true; }
        else if (term == 2) { left = a.x; right = p.y; minus = true; }
        else if (term == 3) { left = b.y; right = p.x; minus = true; }
        else if (term == 4) { left = b.y; right = a.x; minus = false; }
        else { left = a.y; right = p.x; minus = false; }
        uint ae = (left >> 23u) & 255u, be = (right >> 23u) & 255u;
        uint am = (left & 0x7fffffu) | (ae == 0u ? 0u : 0x800000u);
        uint bm = (right & 0x7fffffu) | (be == 0u ? 0u : 0x800000u);
        uint shift = max(ae,1u)+max(be,1u)-2u;
        uint offset = shift & 31u;
        // Safe even if selections evaluate both operands. No shift by 32.
        uint spill = (32u-offset) & 31u;
        uvec2 product = opticsProduct24(am,bm);
        uint low = product.x << offset;
        uint middle = (product.y << offset) | (offset == 0u ? 0u : product.x >> spill);
        uint high = offset == 0u ? 0u : product.y >> spill;
        bool neg = minus != (((left ^ right) & 0x80000000u) != 0u);
        products[term] = uvec4(low,middle,high,(shift >> 5u) | (neg ? 16u : 0u));
    }
    uint positiveCarry = 0u, negativeCarry = 0u;
    int comparison = 0;
    // Normalize both unsigned sums in base 2^32. Each outgoing carry is at most 5.
    [[dont_unroll]] for (int word = 0; word < 18; word++) {
        uint positive = positiveCarry, negative = negativeCarry;
        uint positiveOverflow = 0u, negativeOverflow = 0u;
        [[dont_unroll]] for (int term = 0; term < 6; term++) {
            uvec4 product = products[term];
            int delta = word-int(product.w & 15u);
            uint digit = delta == 0 ? product.x : delta == 1 ? product.y : delta == 2 ? product.z : 0u;
            if ((product.w & 16u) != 0u) {
                uint next = negative+digit;
                negativeOverflow += next < negative ? 1u : 0u;
                negative = next;
            } else {
                uint next = positive+digit;
                positiveOverflow += next < positive ? 1u : 0u;
                positive = next;
            }
        }
        positiveCarry = positiveOverflow;
        negativeCarry = negativeOverflow;
        // Higher normalized words dominate every lower difference. Never return early.
        if (positive != negative) comparison = positive > negative ? 1 : -1;
    }
    // Six magnitudes total below 2^557, so both final carries are zero after 576 bits.
    return comparison;
}

int opticsOrient(uvec2 a, uvec2 b, uvec2 p) {
    int baseShift = opticsOrientBaseShift(a,b,p);
    if (baseShift >= 0) return opticsOrientNarrow(a,b,p,baseShift);
    return opticsOrientWide(a,b,p);
}
";
}
