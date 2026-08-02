using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// ONE RECORDED COMMAND, and it is exactly 32 bytes: an opcode, a reference index, and six payload words.
    /// Section 5.1 of <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> sizes the stream against this
    /// number (roughly 10k ops per frame is 320 KB of a reused array), so the size is part of the design rather
    /// than an implementation detail, and <c>D3D11CommandStreamTests</c> asserts it.
    /// <para>
    /// Everything here is a value. No field holds a managed reference, which is what makes truncating the stream
    /// a single integer write with no write barriers and no garbage: a resource argument is an INDEX into the
    /// owning list's <see cref="D3D11ReferenceList"/>, and that list is what keeps the resource alive for the
    /// recording's lifetime.
    /// </para>
    /// <para>
    /// SIX payload words is one more than any command currently needs (the widest are five). Growth is therefore
    /// a new word inside the existing 32 bytes rather than a bigger struct, and if a command ever needs a seventh
    /// the answer is the payload arena, not a wider op.
    /// </para>
    /// </summary>
    internal readonly struct D3D11Op
    {
        /// <summary>Which command this is.</summary>
        public readonly D3D11OpCode Code;

        /// <summary>The op's PRIMARY resource argument, as an index into the owning stream's reference list, or
        /// <see cref="D3D11ReferenceList.NoReference"/> when the command takes none. A command with a second
        /// resource (a copy's destination, a resolve's target) carries that one as an index in a payload word,
        /// because a reference index is just an integer.</summary>
        public readonly int Reference;

        /// <summary>Payload word 0.</summary>
        public readonly uint Arg0;
        /// <summary>Payload word 1.</summary>
        public readonly uint Arg1;
        /// <summary>Payload word 2.</summary>
        public readonly uint Arg2;
        /// <summary>Payload word 3.</summary>
        public readonly uint Arg3;
        /// <summary>Payload word 4.</summary>
        public readonly uint Arg4;
        /// <summary>Payload word 5. Unused by every command today, kept so the struct stays at its designed
        /// size while the backend grows.</summary>
        public readonly uint Arg5;

        /// <summary>Build an op. Everything past the opcode is optional so a call site names only the words it
        /// actually uses, which is what keeps the encoder readable against the seam it mirrors.</summary>
        public D3D11Op(D3D11OpCode code, int reference = D3D11ReferenceList.NoReference,
            uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0, uint a4 = 0, uint a5 = 0)
        {
            Code = code; Reference = reference;
            Arg0 = a0; Arg1 = a1; Arg2 = a2; Arg3 = a3; Arg4 = a4; Arg5 = a5;
        }

        /// <summary>A float payload word, bit-exact. Used for the clear colour and the clear depth, the only
        /// non-integer arguments on the seam.</summary>
        public static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

        /// <summary>Read a float payload word back, bit-exact. The inverse of <see cref="Bits(float)"/>, so a
        /// clear colour survives the round trip without a rounding step of its own.</summary>
        public static float Float(uint word) => BitConverter.UInt32BitsToSingle(word);

        /// <summary>A signed payload word (the one on the seam is <c>DrawIndexed</c>'s vertex offset, which is
        /// deliberately signed).</summary>
        public static uint Signed(int value) => unchecked((uint)value);

        /// <summary>Read a signed payload word back.</summary>
        public static int Signed(uint word) => unchecked((int)word);

        /// <summary>Pack a mip level and an array layer into one payload word, sixteen bits each. Only the
        /// subresource copy needs this, and only because it carries two resources plus six numbers. Both fit
        /// with room to spare: D3D11 caps a 2D array at 2048 layers and a mip chain at 32 levels.</summary>
        public static uint PackSubresource(uint mipLevel, uint arrayLayer)
        {
            if (mipLevel > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                    "A mip level past 65535 cannot be recorded. D3D11 caps a mip chain far below this, so a value "
                    + "this large is a caller bug rather than a limit of the command stream.");
            if (arrayLayer > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(arrayLayer), arrayLayer,
                    "An array layer past 65535 cannot be recorded. D3D11 caps a 2D texture array at 2048 layers, "
                    + "so a value this large is a caller bug rather than a limit of the command stream.");
            return (mipLevel << 16) | arrayLayer;
        }

        /// <summary>The mip level out of a word packed by <see cref="PackSubresource"/>.</summary>
        public static uint MipOf(uint packed) => packed >> 16;

        /// <summary>The array layer out of a word packed by <see cref="PackSubresource"/>.</summary>
        public static uint LayerOf(uint packed) => packed & 0xFFFFu;
    }
}
