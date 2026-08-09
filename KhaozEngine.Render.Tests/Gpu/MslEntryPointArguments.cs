using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One emitted entry-point argument: which Metal index space it landed in, at what index, and the
    /// name the cross-compiler gave it.</summary>
    /// <param name="Space">The Metal index space, one of <c>buffer</c>, <c>texture</c> or <c>sampler</c>.</param>
    /// <param name="Index">The index the cross-compiler chose within that space.</param>
    /// <param name="Name">The declared argument name, which for an unnamed resource is SPIRV-Cross's
    /// <c>_&lt;id&gt;</c>.</param>
    public readonly record struct EmittedArgument(string Space, int Index, string Name);

    /// <summary>
    /// The one parse of an emitted MSL entry point's resource arguments, shared by both row-1 join spikes
    /// (<see cref="MetalMslNameJoinSpikeTests"/> and <see cref="MetalMslIdJoinSpikeTests"/>) so the two
    /// measurements are taken over exactly the same census and a difference between them is a difference in the
    /// JOIN rather than in the counting.
    ///
    /// <para>
    /// The closing parenthesis is matched by DEPTH rather than taken as the first one, because every argument
    /// carries an attribute of its own and a naive scan stops inside <c>[[buffer(0)]]</c> and sees a single
    /// argument. That is the same walk <c>ShaderValidation.CheckMslBufferSlots</c> already ships, and row 9
    /// promotes into the binding path.
    /// </para>
    /// </summary>
    public static class MslEntryPointArguments
    {
        /// <summary>The entry point's resource arguments, in declaration order.</summary>
        /// <param name="msl">The emitted MSL for one stage.</param>
        /// <param name="entryKeyword">The keyword that opens the entry point: <c>vertex </c>, <c>fragment </c>
        /// or <c>kernel </c>.</param>
        public static List<EmittedArgument> Parse(string msl, string entryKeyword)
        {
            var arguments = new List<EmittedArgument>();
            int start = msl.IndexOf(entryKeyword, StringComparison.Ordinal);
            if (start < 0) return arguments;
            int open = msl.IndexOf('(', start);
            if (open < 0) return arguments;

            int close = -1, depth = 0;
            for (int i = open; i < msl.Length; i++)
            {
                if (msl[i] == '(') depth++;
                else if (msl[i] == ')' && --depth == 0) { close = i; break; }
            }
            if (close < 0) return arguments;

            foreach (string raw in msl.Substring(open + 1, close - open - 1).Split(','))
            {
                string argument = raw.Trim();
                foreach (string space in new[] { "buffer", "texture", "sampler" })
                {
                    string marker = "[[" + space + "(";
                    int at = argument.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0) continue;
                    int numberStart = at + marker.Length;
                    int numberEnd = argument.IndexOf(')', numberStart);
                    if (numberEnd < 0) continue;
                    if (!int.TryParse(argument.AsSpan(numberStart, numberEnd - numberStart), out int index)) continue;

                    // The declared name is the last identifier before the attribute, past any reference or
                    // pointer punctuation: "constant _68& _70 [[buffer(0)]]" names _70.
                    string declaration = argument[..at].TrimEnd();
                    int split = declaration.LastIndexOfAny(new[] { ' ', '&', '*' });
                    arguments.Add(new EmittedArgument(space, index,
                        split >= 0 ? declaration[(split + 1)..] : declaration));
                    break;
                }
            }
            return arguments;
        }

        /// <summary>The SPIR-V id an argument name carries, for the id-keyed join. SPIRV-Cross names an unnamed
        /// variable <c>_&lt;id&gt;</c>, so an argument named <c>_70</c> is variable 70 IN THAT STAGE'S MODULE.
        /// Returns false for any other name shape, which is what a named resource would produce.</summary>
        public static bool TryReadId(string argumentName, out uint id)
        {
            id = 0;
            if (argumentName.Length < 2 || argumentName[0] != '_') return false;
            return uint.TryParse(argumentName.AsSpan(1), out id);
        }
    }
}
