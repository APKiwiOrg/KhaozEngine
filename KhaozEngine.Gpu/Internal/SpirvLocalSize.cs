using System;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Reads a compute module's workgroup size straight out of its SPIR-V, so the engine never asks a
    /// caller to repeat the shader's own <c>layout(local_size_x = ...)</c> in C#.
    ///
    /// This exists because of a silent-failure shape in the layer it was written against, the Veldrid stack the
    /// engine ran on until 18.0.0. Its <c>ComputePipelineDescription</c> carried <c>ThreadGroupSizeX/Y/Z</c> and
    /// validated nothing against the shader, and only ONE backend read them: Metal, where they became the
    /// <c>threadsPerThreadgroup</c> argument of <c>dispatchThreadGroups</c> (MSL does not carry the workgroup
    /// size the way SPIR-V does). Vulkan and Direct3D11 ignored them entirely and took the size from the module.
    /// So a description that disagreed with the shader was invisible on two backends and produced WRONG RESULTS
    /// on the third, with no error anywhere. And <c>Veldrid.SPIRV</c> did not report the size back either: its
    /// <c>ComputeCompilationResult</c> carried only the cross-compiled source and a resource-layout reflection.
    /// The three native backends took the size from this parse from the day each shipped, so the bug class has
    /// no home left, and the parse is what keeps it that way.
    ///
    /// Parsing the one execution mode out of the module is a few lines and removes the whole class of bug, so the
    /// engine's <see cref="IGpuComputeShader"/> exposes the size it read rather than trusting a caller-supplied
    /// copy.</summary>
    internal static class SpirvLocalSize
    {
        const uint Magic = 0x07230203;
        const int HeaderWords = 5;
        const uint OpExecutionMode = 16;
        const uint OpExecutionModeId = 331;
        const uint ExecutionModeLocalSize = 17;
        const uint ExecutionModeLocalSizeId = 38;

        /// <summary>Reads the <c>LocalSize</c> execution mode out of a SPIR-V module. Throws
        /// <see cref="ShaderValidationException"/> when the bytes are not a SPIR-V module or are truncated.
        ///
        /// A source that declares no <c>layout(local_size_x = ...)</c> does NOT reach the final throw: GLSL's
        /// default workgroup size is 1x1x1 and glslang emits an explicit <c>LocalSize 1 1 1</c> for it, so this
        /// returns (1, 1, 1) and the shader runs one invocation per group. That path is pinned by
        /// <c>SpirvLocalSizeTests.AnOmittedLayoutYieldsTheGlslDefaultOfOneByOneByOne</c>. The two remaining throws
        /// are defensive against a module this engine did not compile, or a toolchain that stops emitting the
        /// default - reachable in principle, not through the seam's own compile path.</summary>
        public static (uint X, uint Y, uint Z) Parse(byte[] spirv, string label)
        {
            if (spirv is null) throw new ArgumentNullException(nameof(spirv));
            if (spirv.Length < HeaderWords * 4 || spirv.Length % 4 != 0)
                throw new ShaderValidationException($"{label}: not a SPIR-V module (length {spirv.Length}).");

            uint[] words = ToWords(spirv);
            if (words[0] != Magic)
                throw new ShaderValidationException(
                    $"{label}: not a SPIR-V module (magic 0x{words[0]:X8}, expected 0x{Magic:X8}).");

            bool sawLocalSizeId = false;
            int i = HeaderWords;
            while (i < words.Length)
            {
                uint opcode = words[i] & 0xFFFFu;
                int wordCount = (int)(words[i] >> 16);
                // A zero word count would not advance, so a malformed module cannot spin here.
                if (wordCount <= 0 || i + wordCount > words.Length)
                    throw new ShaderValidationException($"{label}: truncated or malformed SPIR-V instruction stream.");

                // OpExecutionMode: <entryPoint id> <mode> <literals...>. LocalSize carries x/y/z literals.
                if (opcode == OpExecutionMode && wordCount >= 6 && words[i + 2] == ExecutionModeLocalSize)
                    return (words[i + 3], words[i + 4], words[i + 5]);

                // OpExecutionModeId + LocalSizeId means the size comes from specialization constants, which are
                // resolvable only by evaluating the constant graph. Flag it rather than silently defaulting.
                // Defensive today: the seam does not expose specialization constants for compute at all (they are
                // mis-marshalled a layer down, see issue #312), so nothing it compiles can emit LocalSizeId.
                if (opcode == OpExecutionModeId && wordCount >= 6 && words[i + 2] == ExecutionModeLocalSizeId)
                    sawLocalSizeId = true;

                i += wordCount;
            }

            throw new ShaderValidationException(sawLocalSizeId
                ? $"{label}: workgroup size comes from specialization constants (LocalSizeId), which the engine " +
                  "cannot resolve. Declare a literal layout(local_size_x = ...) instead."
                : $"{label}: no layout(local_size_x = ...) workgroup-size declaration found. A compute shader must " +
                  "declare one.");
        }

        static uint[] ToWords(byte[] spirv)
        {
            var words = new uint[spirv.Length / 4];
            // SPIR-V words are little-endian in every module glslang / shaderc emits, and the magic check above
            // rejects a byte-reversed module before this is used.
            for (int w = 0; w < words.Length; w++)
            {
                int b = w * 4;
                words[w] = (uint)(spirv[b] | (spirv[b + 1] << 8) | (spirv[b + 2] << 16) | (spirv[b + 3] << 24));
            }
            return words;
        }
    }
}
