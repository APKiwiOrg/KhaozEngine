using System;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Ends the fragment at every discard in an emitted MSL entry point. GLSL's <c>discard</c> terminates the
    /// invocation, and SPIRV-Cross emits it as a bare <c>discard_fragment()</c>, so the rest of <c>main0</c> kept
    /// running on the discarded fragment. On Apple GPUs that left stale memory in every colour attachment of the
    /// discarded pixels once the shader grew, reproducibly and with Metal shader validation clean (issue 1411).
    /// Returning at the discard restores the source's semantics and also skips the shading that was wasted.
    /// A discard inside a helper function cannot return from <c>main0</c> and is left alone.
    /// <c>MslDiscardTerminationTests</c> pins that no shipped program has one.
    /// </summary>
    internal static class MslDiscardTermination
    {
        const string Discard = "discard_fragment();";
        const string ValueEntry = "fragment main0_out main0(";
        const string VoidEntry = "fragment void main0(";

        /// <summary>The fragment source with each discard in its entry point followed by a return.</summary>
        internal static string Apply(string fragmentSource)
        {
            int entry = fragmentSource.IndexOf(ValueEntry, StringComparison.Ordinal);
            string terminate = "{ discard_fragment(); return out; }";
            if (entry < 0)
            {
                entry = fragmentSource.IndexOf(VoidEntry, StringComparison.Ordinal);
                terminate = "{ discard_fragment(); return; }";
            }
            if (entry < 0) return fragmentSource;
            return string.Concat(fragmentSource.AsSpan(0, entry),
                fragmentSource[entry..].Replace(Discard, terminate, StringComparison.Ordinal));
        }
    }
}
