namespace KhaozEngine.Render3D.Internal;

internal static partial class ShaderSources
{
    // Triangle-only IEEE binary32, nearest-even. All arithmetic on significands is uint32.
    // Native shader division has a weaker accuracy contract, and Metal fast math is global.
    // Keeping these operations in integers also preserves subnormal inputs and intermediate roundings.
    // The field certificate guarantees finite operands and nonzero divisors. This is not a NaN/Inf library.
    const string WaterOpticsBinary32Glsl = @"
uint opticsJam(uint v, int distance) {
    if (distance <= 0) return v;
    if (distance >= 32) return v == 0u ? 0u : 1u;
    return (v >> uint(distance)) | ((v << uint(32-distance)) == 0u ? 0u : 1u);
}

// Normalized 24-bit significand and biased exponent. Zero is handled by callers.
uint opticsSignificand(uint bits, out int exponent) {
    exponent = int((bits >> 23u) & 255u);
    uint m = bits & 0x7fffffu;
    if (exponent != 0) return m | 0x800000u;
    int shift = 23-findMSB(m);
    exponent = 1-shift;
    return m << uint(shift);
}

// sig has its leading bit at 26, plus guard, round and sticky in its low three bits.
uint opticsRound(uint sign, int exponent, uint sig) {
    if (exponent <= 0) {
        sig = opticsJam(sig, 1-exponent);
        exponent = 1;
    }
    uint tail = sig & 7u;
    uint m = sig >> 3u;
    if (tail > 4u || (tail == 4u && (m & 1u) != 0u)) m++;
    if (m >= 0x1000000u) { m >>= 1u; exponent++; }
    if (exponent >= 255) return sign | 0x7f800000u;
    if (m < 0x800000u) exponent = 0;
    return sign | (uint(exponent) << 23u) | (m & 0x7fffffu);
}

uint opticsAdd(uint a, uint b) {
    uint am = a & 0x7fffffffu, bm = b & 0x7fffffffu;
    if (am == 0u && bm == 0u) return a & b & 0x80000000u;
    if (am == 0u) return b;
    if (bm == 0u) return a;
    if (am < bm) { uint swap = a; a = b; b = swap; }
    uint sign = a & 0x80000000u;
    int ae, be;
    uint x = opticsSignificand(a,ae) << 3u;
    uint bmSignificand = opticsSignificand(b,be);
    uint y = opticsJam(bmSignificand << 3u,ae-be);
    uint sig;
    if (((a ^ b) & 0x80000000u) == 0u) {
        sig = x+y;
        if ((sig & 0x8000000u) != 0u) { sig = opticsJam(sig,1); ae++; }
    } else {
        sig = x-y;
        if (sig == 0u) return 0u;
        int shift = 26-findMSB(sig);
        sig <<= uint(shift);
        ae -= shift;
    }
    return opticsRound(sign,ae,sig);
}

// Exact 24 by 24 bit product. The cross-term is below 2^25, so none of these uints overflow
// except the intentional low-word addition, whose carry is retained separately.
uvec2 opticsProduct24(uint a, uint b) {
    uint low = (a & 65535u)*(b & 65535u);
    uint cross = (a & 65535u)*(b >> 16u)+(a >> 16u)*(b & 65535u);
    uint joined = low+(cross << 16u);
    uint high = (a >> 16u)*(b >> 16u)+(cross >> 16u)+(joined < low ? 1u : 0u);
    return uvec2(joined,high);
}

uint opticsMul(uint a, uint b) {
    uint sign = (a ^ b) & 0x80000000u;
    if ((a & 0x7fffffffu) == 0u || (b & 0x7fffffffu) == 0u) return sign;
    int ae, be;
    uint am = opticsSignificand(a,ae), bm = opticsSignificand(b,be);
    uvec2 p = opticsProduct24(am,bm);
    int extra = (p.y & 32768u) == 0u ? 0 : 1;
    uint shift = uint(20+extra);
    uint sig = (p.x >> shift) | (p.y << (32u-shift));
    if ((p.x << (32u-shift)) != 0u) sig |= 1u;
    return opticsRound(sign,ae+be-127+extra,sig);
}

uint opticsDiv(uint a, uint b) {
    uint sign = (a ^ b) & 0x80000000u;
    if ((b & 0x7fffffffu) == 0u) return sign | 0x7f800000u;
    if ((a & 0x7fffffffu) == 0u) return sign;
    int ae, be;
    uint remainder = opticsSignificand(a,ae), divisor = opticsSignificand(b,be);
    int exponent = ae-be+127;
    if (remainder < divisor) { remainder <<= 1u; exponent--; }
    uint sig = 0u;
    // Restoring division emits the 24 significand bits and three rounding bits.
    // remainder is below 2*divisor, hence below 2^25 throughout.
    [[dont_unroll]] for (int bit = 0; bit < 27; bit++) {
        sig <<= 1u;
        if (remainder >= divisor) { remainder -= divisor; sig |= 1u; }
        if (bit != 26) remainder <<= 1u;
    }
    if (remainder != 0u) sig |= 1u;
    return opticsRound(sign,exponent,sig);
}
";
}
