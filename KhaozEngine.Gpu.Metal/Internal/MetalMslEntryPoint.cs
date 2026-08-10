using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>One resource argument of an emitted MSL entry point: which index space it landed in, at what
    /// index, and the name the cross-compiler gave it.</summary>
    /// <param name="Space">The Metal index space the attribute named.</param>
    /// <param name="Index">The index the cross-compiler chose within that space.</param>
    /// <param name="Name">The declared argument name, which for a resource with no debug name is SPIRV-Cross's
    /// <c>_&lt;id&gt;</c>.</param>
    internal readonly record struct MetalMslArgument(MetalIndexSpace Space, int Index, string Name);

    /// <summary>
    /// THE ENTRY POINT AS THE EMISSION ACTUALLY WROTE IT: its NAME, and its resource arguments with the indices
    /// SPIRV-Cross chose. Everything the native Metal backend knows about where a resource went comes through
    /// here, because Metal has no binding decorations and the CPU-side count the incumbent uses is the authority
    /// this backend removes (M-B1, section 8.2).
    ///
    /// <para>
    /// THE NAME IS READ RATHER THAN ASSUMED (M-S5). SPIRV-Cross renames the GLSL <c>main</c>, because <c>main</c>
    /// is reserved in MSL, and it emits <c>main0</c> today. The incumbent looks a function up by a name Veldrid
    /// supplies from a layer this backend does not have, so guessing <c>main0</c> here would be inheriting a
    /// convention through a gap rather than reading a fact. It is also what the <c>.metallib</c>-shaped cache
    /// discussion forced into the payload: a cache hit that skipped the emission would have no other way to know
    /// the function's name.
    /// </para>
    /// <para>
    /// THE CLOSING PARENTHESIS IS MATCHED BY DEPTH, never taken as the first one. Every argument carries an
    /// attribute of its own, so a naive scan stops inside <c>[[buffer(0)]]</c> and sees a single argument. That is
    /// the exact failure <c>ShaderValidation.CheckMslBufferSlots</c> already documents and already solves, and
    /// 2.2 counted the existence of that walk as an asset when it ruled the table is READ off the emission. This
    /// is that walk, promoted from a compute-only diagnostic into the binding path.
    /// </para>
    /// <para>
    /// AND EVERY FAILURE HERE IS LOUD (2.2b, pin 1). An entry point that cannot be found, an argument list that
    /// does not close: each throws naming the program and the stage. There is no path from a malformed emission
    /// to an empty argument list that a later count could fill in, because a silent fallback to counting is
    /// precisely the mechanism this backend exists not to reproduce.
    /// </para>
    /// </summary>
    internal static class MetalMslEntryPoint
    {
        /// <summary>The MSL qualifier that opens the entry point for a stage. A trailing space is part of the
        /// token: it is what keeps <c>vertex</c> from matching inside an identifier.</summary>
        internal static string KeywordFor(MetalShaderStage stage) => stage switch
        {
            MetalShaderStage.Vertex => "vertex ",
            MetalShaderStage.Fragment => "fragment ",
            _ => "kernel ",
        };

        /// <summary>
        /// The entry point's declared NAME and its resource arguments, in declaration order.
        /// </summary>
        /// <param name="msl">The emitted MSL for one stage.</param>
        /// <param name="stage">Which stage's entry point to find.</param>
        /// <param name="label">A name for the program, included in any error message.</param>
        /// <exception cref="ShaderValidationException">The stage's entry point is not present, its argument list
        /// does not close, or its name cannot be read.</exception>
        internal static (string Name, List<MetalMslArgument> Arguments) Parse(string msl, MetalShaderStage stage,
            string label)
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
                    + "SPIRV-Cross emission produces. Treating this as zero arguments would bind nothing and "
                    + "render an untextured frame with no error, so it is a stop.");
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

            return (ReadName(msl, start + keyword.Length, open, where), ReadArguments(msl, open, close));
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

        static List<MetalMslArgument> ReadArguments(string msl, int open, int close)
        {
            var arguments = new List<MetalMslArgument>();

            foreach (string raw in msl.Substring(open + 1, close - open - 1).Split(','))
            {
                string argument = raw.Trim();
                foreach (MetalIndexSpace space in Spaces)
                {
                    string marker = "[[" + space.Word() + "(";
                    int at = argument.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;

                    int numberStart = at + marker.Length;
                    int numberEnd = argument.IndexOf(')', numberStart);
                    if (numberEnd < 0) continue;
                    if (!int.TryParse(argument.AsSpan(numberStart, numberEnd - numberStart), out int index)) continue;

                    // The declared name is the last identifier before the attribute, past any reference or
                    // pointer punctuation: "constant _68& _70 [[buffer(0)]]" names _70.
                    string declaration = argument[..at].TrimEnd();
                    int split = declaration.LastIndexOfAny(NameBreaks);
                    arguments.Add(new MetalMslArgument(space, index,
                        split >= 0 ? declaration[(split + 1)..] : declaration));
                    break;
                }
            }

            return arguments;
        }

        // Allocated once rather than per call: this runs per stage per program at load, and both arrays are
        // read-only by use.
        static readonly MetalIndexSpace[] Spaces =
            { MetalIndexSpace.Buffer, MetalIndexSpace.Texture, MetalIndexSpace.Sampler };

        static readonly char[] NameBreaks = { ' ', '&', '*' };

        /// <summary>The SPIR-V id an argument name carries, which is the key the table joins on. SPIRV-Cross
        /// names a variable with no debug name <c>_&lt;id&gt;</c>, so an argument named <c>_70</c> is variable 70
        /// IN THAT STAGE'S OWN MODULE. False for any other shape, which is what a resource carrying a real name
        /// would produce, and the caller turns that into a throw rather than a fallback.</summary>
        internal static bool TryReadId(string argumentName, out uint id)
        {
            id = 0;
            if (argumentName is null || argumentName.Length < 2 || argumentName[0] != '_') return false;
            return uint.TryParse(argumentName.AsSpan(1), out id);
        }
    }
}
