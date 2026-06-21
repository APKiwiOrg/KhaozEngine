using System.Runtime.CompilerServices;

namespace KhaozEngine.Determinism;

/// <summary>
/// Inline fixed-size buffer holding a platform <c>fenv_t</c> by value (no heap allocation). Sized to a
/// safe upper bound across the platforms we target (macOS arm64 = 16 bytes {fpsr,fpcr} as u64; macOS x64
/// = 16 bytes; Linux glibc arm64 = 8 bytes, x64 = 28 bytes; ucrt = 8 bytes) so the surrounding token
/// struct has a stable layout everywhere.
/// </summary>
[InlineArray(Size)]
internal struct FpEnvBuffer
{
    public const int Size = 64;
    private byte _e0;
}
