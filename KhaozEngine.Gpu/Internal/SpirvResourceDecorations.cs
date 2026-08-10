using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>One resource variable's descriptor decorations, read straight out of a SPIR-V module.</summary>
    /// <param name="Id">The variable's SPIR-V result id, which is also the number SPIRV-Cross uses when it has to
    /// invent a name for the variable (<c>_70</c>).</param>
    /// <param name="Set">The <c>DescriptorSet</c> decoration.</param>
    /// <param name="Binding">The <c>Binding</c> decoration.</param>
    internal readonly record struct SpirvResourceDecoration(uint Id, uint Set, uint Binding);

    /// <summary>
    /// Every <c>(id, set, binding)</c> triple a SPIR-V module declares, read by walking its decoration
    /// instructions. THE KEY THE NATIVE METAL BACKEND'S BINDING TABLE JOINS ON (decision M-B1 as re-adjudicated in
    /// section 2.2b of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>).
    ///
    /// <para>
    /// WHY THIS IS SOUND WHERE A NAME JOIN IS NOT. Decorations are not debug information. <c>OpDecorate</c>
    /// carries the semantics the shader declared and survives everything, where <c>OpName</c> is stripped the
    /// moment debug info is off, which <see cref="SpirvFrontEndPin"/>'s <c>Debug</c> turns off for every
    /// engine-owned emission. So the key this reads is present under exactly the pin the shipped path uses, which
    /// is the whole difference from the key the name join needed. Section 2.2a measured the alternative: zero
    /// exact name joins over 159 emitted arguments, because every texture and sampler element reflects with an
    /// EMPTY name, buffer elements reflect as <c>{blockType}_{instance}</c> while the argument is named for the
    /// instance alone, and the reflection is computed once for a pair while each stage renumbers its ids
    /// independently. This key measured 159 of 159 with no failure class of any size.
    /// </para>
    /// <para>
    /// PER STAGE, ALWAYS. Each stage's ids are read out of THAT STAGE'S OWN module. The independent renumbering
    /// that killed the name join is the mechanism here rather than the hazard, and reading a pair's shared
    /// reflection instead is precisely the mistake that produced the refuted join (2.2b, pin 2).
    /// </para>
    /// <para>
    /// NOTHING HERE NEEDS THE TYPE GRAPH. A variable is a resource variable if and only if it carries BOTH a
    /// <c>DescriptorSet</c> and a <c>Binding</c> decoration, so the walk never has to resolve
    /// <c>OpVariable</c>'s storage class or chase a pointer type to decide what it is looking at. That keeps it
    /// the same shape and roughly the same size as <see cref="SpirvLocalSize"/>, which hand-walks the same
    /// instruction stream for the one execution mode it needs, and which is why it lives here beside it.
    /// </para>
    /// <para>
    /// IT WAS A MEASUREMENT AND IS NOW A MECHANISM. Row 1 wrote this in the test project, deliberately, with a
    /// header saying that promoting it here is the move if the re-adjudication took the id join. It did (2.2b),
    /// so this is that promotion. <c>MetalMslIdJoinSpikeTests</c> still reads it, and still asserts the same
    /// properties, which is what keeps the ruling's evidence live rather than historical.
    /// </para>
    /// </summary>
    internal static class SpirvResourceDecorations
    {
        const uint Magic = 0x07230203;
        const int HeaderWords = 5;
        const uint OpDecorate = 71;
        const uint DecorationBinding = 33;
        const uint DecorationDescriptorSet = 34;

        /// <summary>Every id in the module carrying both a <c>DescriptorSet</c> and a <c>Binding</c>
        /// decoration, keyed by id.</summary>
        /// <param name="spirv">The module bytes, little-endian words as every glslang emission is.</param>
        /// <param name="label">A name for the module, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The bytes are not a SPIR-V module, or the instruction
        /// stream is truncated.</exception>
        internal static IReadOnlyDictionary<uint, SpirvResourceDecoration> Read(byte[] spirv, string label)
        {
            ArgumentNullException.ThrowIfNull(spirv);
            if (spirv.Length < HeaderWords * 4 || spirv.Length % 4 != 0)
                throw new ShaderValidationException($"{label}: not a SPIR-V module (length {spirv.Length}).");

            uint[] words = ToWords(spirv);
            if (words[0] != Magic)
                throw new ShaderValidationException(
                    $"{label}: not a SPIR-V module (magic 0x{words[0]:X8}, expected 0x{Magic:X8}).");

            var sets = new Dictionary<uint, uint>();
            var bindings = new Dictionary<uint, uint>();

            int i = HeaderWords;
            while (i < words.Length)
            {
                uint opcode = words[i] & 0xFFFFu;
                int wordCount = (int)(words[i] >> 16);
                // A zero word count would not advance, so a malformed module cannot spin here.
                if (wordCount <= 0 || i + wordCount > words.Length)
                    throw new ShaderValidationException($"{label}: truncated or malformed SPIR-V instruction stream.");

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
