using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One resource variable's descriptor decorations, read straight out of a SPIR-V module.</summary>
    /// <param name="Id">The variable's SPIR-V result id, which is also the number SPIRV-Cross uses when it has to
    /// invent a name for the variable (<c>_70</c>).</param>
    /// <param name="Set">The <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The <c>Binding</c> decoration.</param>
    public readonly record struct SpirvResourceDecoration(uint Id, uint Set, uint Binding);

    /// <summary>
    /// Reads every <c>(id, set, binding)</c> triple out of a SPIR-V module by walking its decoration
    /// instructions, for the id-keyed join spike in <see cref="MetalMslIdJoinSpikeTests"/>.
    ///
    /// <para>
    /// WHY THIS IS SOUND WHERE A NAME JOIN IS NOT. Decorations are not debug information. <c>OpDecorate</c>
    /// carries the semantics the shader declared and survives everything, where <c>OpName</c> is stripped the
    /// moment debug info is off, which <c>SpirvFrontEndPin.Debug</c> turns off for every engine-owned emission.
    /// So the key this reads is present under exactly the pin the shipped path uses, which is the whole
    /// difference from the key the name join needed.
    /// </para>
    /// <para>
    /// NOTHING HERE NEEDS THE TYPE GRAPH. A variable is a resource variable if and only if it carries BOTH a
    /// <c>DescriptorSet</c> and a <c>Binding</c> decoration, so the walk never has to resolve
    /// <c>OpVariable</c>'s storage class or chase a pointer type to decide what it is looking at. That keeps it
    /// the same shape and roughly the same size as the precedent the engine already ships,
    /// <c>KhaozEngine.Gpu/Internal/SpirvLocalSize.cs</c>, which hand-walks the same instruction stream for the
    /// one execution mode it needs.
    /// </para>
    /// <para>
    /// It lives in the test project rather than in <c>KhaozEngine.Gpu</c> because it is a MEASUREMENT, not a
    /// mechanism. If the re-adjudication of M-B1 that section 2.2a asks for takes the id join, promoting this
    /// into the engine beside <c>SpirvLocalSize</c> is the move, and until then shipping it would be shipping an
    /// unused public surface.
    /// </para>
    /// </summary>
    public static class SpirvResourceDecorations
    {
        const uint Magic = 0x07230203;
        const int HeaderWords = 5;
        const uint OpDecorate = 71;
        const uint DecorationBinding = 33;
        const uint DecorationDescriptorSet = 34;

        /// <summary>Every id in the module carrying both a <c>DescriptorSet</c> and a <c>Binding</c>
        /// decoration, keyed by id.</summary>
        /// <param name="spirv">The module bytes, little-endian words as every glslang emission is.</param>
        /// <exception cref="ArgumentException">The bytes are not a SPIR-V module, or the instruction stream is
        /// truncated.</exception>
        public static IReadOnlyDictionary<uint, SpirvResourceDecoration> Read(byte[] spirv)
        {
            ArgumentNullException.ThrowIfNull(spirv);
            if (spirv.Length < HeaderWords * 4 || spirv.Length % 4 != 0)
                throw new ArgumentException($"not a SPIR-V module (length {spirv.Length}).", nameof(spirv));

            uint[] words = ToWords(spirv);
            if (words[0] != Magic)
                throw new ArgumentException(
                    $"not a SPIR-V module (magic 0x{words[0]:X8}, expected 0x{Magic:X8}).", nameof(spirv));

            var sets = new Dictionary<uint, uint>();
            var bindings = new Dictionary<uint, uint>();

            int i = HeaderWords;
            while (i < words.Length)
            {
                uint opcode = words[i] & 0xFFFFu;
                int wordCount = (int)(words[i] >> 16);
                // A zero word count would not advance, so a malformed module cannot spin here.
                if (wordCount <= 0 || i + wordCount > words.Length)
                    throw new ArgumentException("truncated or malformed SPIR-V instruction stream.", nameof(spirv));

                // OpDecorate: <target id> <decoration> <literals...>. Both decorations this cares about carry
                // exactly one literal, so a four-word instruction is the shape and anything else is not one.
                if (opcode == OpDecorate && wordCount == 4)
                {
                    uint target = words[i + 1];
                    uint decoration = words[i + 2];
                    if (decoration == DecorationDescriptorSet) sets[target] = words[i + 3];
                    else if (decoration == DecorationBinding) bindings[target] = words[i + 3];
                }

                i += wordCount;
            }

            var result = new Dictionary<uint, SpirvResourceDecoration>();
            foreach ((uint id, uint set) in sets)
                if (bindings.TryGetValue(id, out uint binding))
                    result[id] = new SpirvResourceDecoration(id, set, binding);
            return result;
        }

        static uint[] ToWords(byte[] spirv)
        {
            var words = new uint[spirv.Length / 4];
            for (int w = 0; w < words.Length; w++)
            {
                int b = w * 4;
                words[w] = (uint)(spirv[b] | (spirv[b + 1] << 8) | (spirv[b + 2] << 16) | (spirv[b + 3] << 24));
            }
            return words;
        }
    }
}
