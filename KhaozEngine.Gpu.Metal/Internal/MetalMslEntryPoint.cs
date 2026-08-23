using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE ENTRY POINT'S NAME, READ OUT OF THE EMISSION, and since 18.0.0 that is the only thing this reads.
    ///
    /// <para>
    /// THE NAME IS READ RATHER THAN ASSUMED (M-S5). SPIRV-Cross renames the GLSL <c>main</c>, because <c>main</c>
    /// is reserved in MSL, and it emits <c>main0</c> today. The incumbent looked a function up by a name Veldrid
    /// supplied from a layer this backend does not have, so guessing <c>main0</c> here would be inheriting a
    /// convention through a gap rather than reading a fact. It is also what the cache discussion forced into the
    /// payload, and the cache that landed carries it: a hit skips the emission, so
    /// <see cref="MetalMslCacheEntry"/> holds the name this read, because there is no other source for it.
    /// </para>
    /// <para>
    /// THE ARGUMENT LIST IS NO LONGER PARSED, AND THAT IS WHAT ROW 10 DELETED (#693). Until 18.0.0 this type also
    /// read each argument's <c>[[buffer(n)]]</c> attribute, because the index a resource landed at was
    /// SPIRV-Cross's to choose and the engine's only way to learn it was the text.
    /// <c>MslIndexRemap</c> now authors those indices before the emission, so there is nothing left to
    /// discover and the walk that discovered it is gone. What survives here is the name, which SPIRV-Cross still
    /// chooses.
    /// </para>
    /// <para>
    /// THE CLOSING PARENTHESIS IS STILL MATCHED BY DEPTH, even though nothing inside it is read any more. The
    /// name is the last identifier BEFORE the opening parenthesis, so only the opening one is load-bearing, but
    /// finding the argument list at all is what proves the match is an entry point rather than a forward
    /// declaration or a comment.
    /// </para>
    /// <para>
    /// AND EVERY FAILURE HERE IS LOUD (2.2b, pin 1). An entry point that cannot be found, an argument list that
    /// does not open or does not close, and a name that cannot be read: each throws naming the program and the
    /// stage. A function looked up by a guessed name is exactly the gap M-S5 exists to close.
    /// </para>
    /// </summary>
    internal static class MetalMslEntryPoint
    {
        /// <summary>The MSL qualifier that opens the entry point for a stage. A trailing space is part of the
        /// token: it is what keeps <c>vertex</c> from matching inside an identifier.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A stage this has no keyword for. All three are listed,
        /// so this is a new <see cref="MetalShaderStage"/> member, and defaulting it to <c>kernel</c> would send
        /// the parse looking for the wrong qualifier and report the entry point as missing.</exception>
        internal static string KeywordFor(MetalShaderStage stage) => stage switch
        {
            MetalShaderStage.Vertex => "vertex ",
            MetalShaderStage.Fragment => "fragment ",
            MetalShaderStage.Compute => "kernel ",
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "this MetalShaderStage has no MSL entry-point keyword. A new stage is an engine change, and this "
                + "is one of the sites it has to visit."),
        };

        /// <summary>
        /// The entry point's declared NAME, which is what the device passes to <c>-newFunctionWithName:</c>.
        /// </summary>
        /// <param name="msl">The emitted MSL for one stage.</param>
        /// <param name="stage">Which stage's entry point to find.</param>
        /// <param name="label">A name for the program, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The stage's entry point is not present, its argument list
        /// does not open or does not close, or its name cannot be read.</exception>
        internal static string NameOf(string msl, MetalShaderStage stage, string label)
        {
            ArgumentNullException.ThrowIfNull(msl);

            string keyword = KeywordFor(stage);
            string where = $"{label} [{stage.ToString().ToLowerInvariant()}]";

            int start = msl.IndexOf(keyword, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new ShaderValidationException(
                    $"{where}: the emitted MSL declares no '{keyword.Trim()}' entry point. The native Metal "
                    + "backend reads the function name and the resource indices out of the emission, so there is "
                    + "nothing to create a library function from and nothing to bind against.");
            }

            int open = msl.IndexOf('(', start);
            if (open < 0)
            {
                throw new ShaderValidationException(
                    $"{where}: the '{keyword.Trim()}' entry point has no argument list at all, which no "
                    + "SPIRV-Cross emission produces, so the match is not an entry point at all and the name "
                    + "read out of it would be some other declaration's.");
            }

            int close = -1, depth = 0;
            for (int i = open; i < msl.Length; i++)
            {
                if (msl[i] == '(') depth++;
                else if (msl[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0)
            {
                throw new ShaderValidationException(
                    $"{where}: the '{keyword.Trim()}' entry point's argument list never closes. The emitted MSL "
                    + "is truncated or is not MSL.");
            }

            return ReadName(msl, start + keyword.Length, open, where);
        }

        // The declared name is the last identifier before the '(': "vertex main0_out main0(" names main0, and the
        // return type in between is why this scans backwards from the parenthesis rather than forwards from the
        // keyword.
        static string ReadName(string msl, int afterKeyword, int open, string where)
        {
            int end = open;
            while (end > afterKeyword && char.IsWhiteSpace(msl[end - 1])) end--;
            int begin = end;
            while (begin > afterKeyword && (char.IsLetterOrDigit(msl[begin - 1]) || msl[begin - 1] == '_')) begin--;

            string name = msl[begin..end];
            if (name.Length == 0)
            {
                throw new ShaderValidationException(
                    $"{where}: the entry point's name could not be read out of the emitted MSL. It is what the "
                    + "backend passes to -newFunctionWithName:, and M-S5 says it is READ rather than assumed to "
                    + "be main0, so an unreadable one is a stop rather than a default.");
            }
            return name;
        }
    }
}
